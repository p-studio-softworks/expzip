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
    /// <summary>受け取るルールの上限。</summary>
    /// <remarks>
    /// <para>
    /// 当初は 8 にしていたが、**実地では少なすぎた** (#78)。121ファイルの書庫から
    /// 5件しか得られず、その5件では書庫の間違いにほとんど気付けなかった。
    /// ルールが少なければ、違反の見落としがそのぶん増える。
    /// </para>
    /// <para>
    /// 並べすぎると人が見られなくなるのは変わらないが、**外すのは人にできて、
    /// 挙がらなかったものを足すのは人にできない。**多めに出して選ばせる。
    /// 頼み方 (<c>RulePromptSystem</c>) に書いた上限と揃えること。
    /// </para>
    /// </remarks>
    private const int MaxRules = 20;

    /// <summary>答えの長さの上限。</summary>
    /// <remarks>
    /// **考えてから答えるモデルは、考えた分もこの予算から引く** (#76)。
    /// 2000 では、考え終えた時点で尽きて答えが空になりうる。
    /// <see cref="MaxRules"/> を 20 に上げたぶん、答え自体も長くなる (#78)。
    /// 日本語の説明と根拠が付くので、1件あたりを厚めに見込む。
    /// </remarks>
    private const int AnswerLimit = 12000;

    /// <summary>お手本から決まりを推定する。</summary>
    public static async Task<RuleEstimate> EstimateAsync(
        AiOptions options, ArchiveContents sample, ArchiveDigest digest,
        CancellationToken cancellationToken = default)
    {
        var answer = await AiClient.AskAsync(
            options, Strings.RulePromptSystem, digest.Text, AnswerLimit, cancellationToken);

        if (!answer.Ok)
        {
            return new RuleEstimate([], 0, [], answer.Message);
        }

        var read = Parse(answer.Text);

        // 「決まりは無い」と「答えを読み取れなかった」は別物。混ぜない
        if (!read.Understood)
        {
            return new RuleEstimate([], 0, [], Strings.RuleUnreadable);
        }

        // お手本自身に当て直す。破っているもの、当てる先が無いものは採らない。
        // **どちらだったかは残す** (#80)。意味がまるで違うため
        var kept = new List<ArchiveRule>();
        var dropped = new List<RuleDrop>();

        foreach (var rule in read.Rules)
        {
            var result = RuleChecker.Check(rule, sample);

            if (result.Applied == 0)
            {
                // 当てる先が無い。お手本が破っているのではなく、こちらが
                // 確かめようがなかった。語彙で書けないことを AI が値に押し込むと、
                // だいたいここに落ちる
                dropped.Add(new RuleDrop(rule, DropReason.NothingToCheck));
            }
            else if (result.Satisfied)
            {
                kept.Add(rule);
            }
            else
            {
                dropped.Add(new RuleDrop(rule, DropReason.Broken));
            }
        }

        // **AI が指した場所の中身は、こちらで数え上げて補う** (#84)。
        // AI は代表例で済ませることがあり、それに気付く手立てが無い
        kept.AddRange(RuleFiller.Fill(kept, sample));

        return new RuleEstimate(kept, read.Unusable, dropped, string.Empty);
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

                var kind = RuleWords.ToKind(Read(item, "kind"));

                if (kind is null)
                {
                    unusable++;
                    continue;
                }

                var rule = ArchiveRule.TryCreate(
                    kind.Value, RuleWords.ToScope(Read(item, "scope")), Read(item, "value"),
                    Read(item, "description"), Read(item, "evidence"),
                    where: Read(item, "where"));

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

}

/// <summary>推定した結果 (#25)。</summary>
/// <param name="Rules">お手本に当て直しても通ったルール。</param>
/// <param name="Unusable">形にならず読み取れなかった数。</param>
/// <param name="Dropped">当て直して採らなかった候補と、その訳 (#80)。</param>
/// <param name="Message">尋ねられなかったときに、人に見せる一文。</param>
internal readonly record struct RuleEstimate(
    IReadOnlyList<ArchiveRule> Rules, int Unusable, IReadOnlyList<RuleDrop> Dropped,
    string Message)
{
    /// <summary>尋ねて答えが返ってきたか。</summary>
    public bool Ok => Message.Length == 0;

    /// <summary>数え上げて補った数 (#84)。</summary>
    public int Filled => Rules.Count(r => r.Source == RuleSource.Filled);

    /// <summary>採らなかった数。</summary>
    public int Rejected => Dropped.Count;

    /// <summary>お手本自身が破っていた数。AI の読み違い。</summary>
    public int Broken => Dropped.Count(d => d.Reason == DropReason.Broken);

    /// <summary>
    /// 当てる先が無く、確かめようがなかった数。
    /// </summary>
    /// <remarks>
    /// **これが多いときは、AI の間違いではなくこちらの語彙が足りていない** (#80)。
    /// 書けないことを値に押し込まれると、当たる先が無くなってここに来る。
    /// </remarks>
    public int Unchecked => Dropped.Count(d => d.Reason == DropReason.NothingToCheck);
}

/// <summary>採らなかった候補と、その訳 (#80)。</summary>
/// <remarks>
/// 捨てた数だけ知らせても、何が捨てられたのかは分からない。**人が確かめるための
/// 画面で、9件のうち8件を黙って捨てるのは筋が悪い。**中身も残して見せる。
/// </remarks>
internal readonly record struct RuleDrop(ArchiveRule Rule, DropReason Reason);

/// <summary>採らなかった訳 (#80)。</summary>
internal enum DropReason
{
    /// <summary>お手本自身が破っていた。</summary>
    Broken,

    /// <summary>当てる先が無く、確かめようがなかった。</summary>
    NothingToCheck,
}
