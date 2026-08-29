using System.Text.Json;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ai;

/// <summary>
/// お手本書庫から、作り方の決まりを推定する (#25、仕様書 11.3節)。
/// </summary>
/// <remarks>
/// <para>
/// 手順は3つ。**送る形を作り**(<see cref="ArchiveDigest"/>)、**尋ね**、
/// **返ってきた決まりをお手本自身に当て直す**。
/// </para>
/// <para>
/// **当て直しがこの機能の要**になる。お手本から読み取ったはずの決まりを、
/// そのお手本が破っているなら、それは読み取りではなく思い付きなので採らない。
/// AI に「自信の度合い」を書かせる手もあるが、その数字を確かめる方法が無い。
/// お手本に当てるほうは、こちら側で確かめられる (<see cref="RuleChecker"/>)。
/// </para>
/// <para>
/// **当てる先が無い決まりも採らない。**フォルダが1つも無い書庫で
/// 「フォルダ名はこの形」と言われても、そう言えた根拠がお手本に無い。
/// </para>
/// </remarks>
internal static class RuleEstimator
{
    /// <summary>受け取る決まりの上限。</summary>
    /// <remarks>
    /// 多ければよいものではない。確かなものから数件で足りるし、あとで人が
    /// 一つずつ見て直す (#26) ことを考えると、並べすぎると見られなくなる。
    /// </remarks>
    private const int MaxRules = 8;

    /// <summary>答えの長さの上限。決まり8件を書くには十分。</summary>
    private const int AnswerLimit = 2000;

    /// <summary>お手本から決まりを推定する。</summary>
    public static async Task<RuleEstimate> EstimateAsync(
        AiOptions options, ArchiveContents sample, ArchiveDigest digest,
        CancellationToken cancellationToken = default)
    {
        var answer = await AiClient.AskAsync(
            options, Strings.RulePromptSystem, digest.Text, AnswerLimit, cancellationToken);

        if (!answer.Ok)
        {
            return new RuleEstimate([], 0, 0, answer.Message);
        }

        var read = Parse(answer.Text);

        // 「決まりは無い」と「答えを読み取れなかった」は別物。混ぜない
        if (!read.Understood)
        {
            return new RuleEstimate([], 0, 0, Strings.RuleUnreadable);
        }

        // お手本自身に当て直す。破っているもの、当てる先が無いものは採らない
        var kept = new List<ArchiveRule>();
        var rejected = 0;

        foreach (var rule in read.Rules)
        {
            var result = RuleChecker.Check(rule, sample);

            if (result.Satisfied && result.Applied > 0)
            {
                kept.Add(rule);
            }
            else
            {
                rejected++;
            }
        }

        return new RuleEstimate(kept, read.Unusable, rejected, string.Empty);
    }

    /// <summary>返ってきた文章から決まりを読み取る。</summary>
    /// <remarks>
    /// <para>
    /// **緩く読む。**答えの形を指定せずに頼んでいる (<see cref="AiClient.AskAsync"/>)
    /// ため、<c>```json</c> で囲ってきたり、前後に一言添えてきたりする。
    /// 最初の <c>{</c> から最後の <c>}</c> までを JSON として読む。
    /// </para>
    /// <para>
    /// 読めなかった項目は数えて返す。黙って減らすと、8件挙げたと言われたのに
    /// 3件しか出ない理由が誰にも分からなくなる。
    /// </para>
    /// </remarks>
    private static (bool Understood, List<ArchiveRule> Rules, int Unusable) Parse(string text)
    {
        var rules = new List<ArchiveRule>();
        var unusable = 0;

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return (false, rules, 0);
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text[start..(end + 1)]);
        }
        catch (JsonException)
        {
            return (false, rules, 0);
        }

        using (document)
        {
            if (!document.RootElement.TryGetProperty("rules", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return (false, rules, 0);
            }

            foreach (var item in list.EnumerateArray())
            {
                if (rules.Count >= MaxRules)
                {
                    break;
                }

                if (item.ValueKind != JsonValueKind.Object)
                {
                    unusable++;
                    continue;
                }

                var kind = ToKind(Read(item, "kind"));

                if (kind is null)
                {
                    unusable++;
                    continue;
                }

                var rule = ArchiveRule.TryCreate(
                    kind.Value, ToScope(Read(item, "scope")), Read(item, "value"),
                    Read(item, "description"), Read(item, "evidence"));

                if (rule is null)
                {
                    unusable++;
                }
                else
                {
                    rules.Add(rule);
                }
            }
        }

        return (true, rules, unusable);
    }

    private static string Read(JsonElement item, string name)
        => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static RuleKind? ToKind(string text) => text.Trim().ToLowerInvariant() switch
    {
        "required_entry" => RuleKind.RequiredEntry,
        "required_folder" => RuleKind.RequiredFolder,
        "forbidden_extension" => RuleKind.ForbiddenExtension,
        "forbidden_name" => RuleKind.ForbiddenName,
        "name_pattern" => RuleKind.NamePattern,
        _ => null,
    };

    /// <summary>当てはめる先。読み取れなければ、いちばん広いものにする。</summary>
    private static RuleScope ToScope(string text) => text.Trim().ToLowerInvariant() switch
    {
        "root" => RuleScope.Root,
        "folders" => RuleScope.Folders,
        "files" => RuleScope.Files,
        _ => RuleScope.All,
    };
}

/// <summary>推定した結果 (#25)。</summary>
/// <param name="Rules">お手本に当て直しても通った決まり。</param>
/// <param name="Unusable">形にならず読み取れなかった数。</param>
/// <param name="Rejected">お手本自身が満たさず、採らなかった数。</param>
/// <param name="Message">尋ねられなかったときに、人に見せる一文。</param>
internal readonly record struct RuleEstimate(
    IReadOnlyList<ArchiveRule> Rules, int Unusable, int Rejected, string Message)
{
    /// <summary>尋ねて答えが返ってきたか。</summary>
    public bool Ok => Message.Length == 0;
}
