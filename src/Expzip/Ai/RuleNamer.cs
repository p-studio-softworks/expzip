using System.Text;
using System.Text.Json;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ai;

/// <summary>
/// 名前を付け直すとき、その案を AI に出させる (#28、仕様書 11.3節の5)。
/// </summary>
/// <remarks>
/// <para>
/// **付け直す名前は、こちら側では作れない。**「フォルダ名は `日付_案件名` の形」と
/// いう決まりに対して、`ゴミ` をどう直すのが妥当かは、正規表現からは出てこない。
/// 案を出すところだけを AI に頼む。
/// </para>
/// <para>
/// **出てきた案は、こちら側で必ず確かめる** (<see cref="RuleFixer.WhyNot"/>)。
/// 決まりの形に合わない案、別の決まりを破る案、既にある名前は採らない。
/// #25 で決まりを当て直したのと同じ考え方で、**確かめられるものは確かめる**。
/// </para>
/// <para>
/// 送るのは**いまの名前と、満たすべき形**だけ。ファイルの中身は送らない
/// (仕様書 11.4節、確定方針)。
/// </para>
/// </remarks>
internal static class RuleNamer
{
    /// <summary>答えの長さの上限。</summary>
    private const int AnswerLimit = 1500;

    /// <summary>一度に頼む数の上限。多いと当てずっぽうが増える。</summary>
    private const int MaxItems = 20;

    /// <summary>名前の案を出してもらい、確かめたものだけを入れる。</summary>
    /// <returns>入れられた数と、人に見せる一文。</returns>
    public static async Task<NamingResult> SuggestAsync(
        AiOptions options, IReadOnlyList<RuleFix> fixes, RuleAudit audit,
        ArchiveContents contents, CancellationToken cancellationToken = default)
    {
        var wanted = fixes.Where(static fix => fix.Kind == RuleFixKind.Rename)
            .Take(MaxItems).ToList();

        if (wanted.Count == 0)
        {
            return new NamingResult(0, 0, Strings.RuleFixNothingToName);
        }

        var answer = await AiClient.AskAsync(
            options, Strings.RuleNamePrompt, Describe(wanted, audit), AnswerLimit,
            cancellationToken);

        if (!answer.Ok)
        {
            return new NamingResult(0, 0, answer.Message);
        }

        var names = Parse(answer.Text);

        if (names is null)
        {
            return new NamingResult(0, 0, Strings.RuleUnreadable);
        }

        var filled = 0;
        var refused = 0;

        foreach (var fix in wanted)
        {
            if (!names.TryGetValue(fix.Path, out var name))
            {
                continue;
            }

            // 出てきた案をそのまま入れない。確かめてから入れる
            if (RuleFixer.WhyNot(fix, name, audit, contents) is not null)
            {
                refused++;
                continue;
            }

            fix.NewName = name.Trim();
            filled++;
        }

        return new NamingResult(filled, refused, string.Empty);
    }

    /// <summary>頼む中身を組み立てる。いまの名前と、満たすべき形だけ。</summary>
    private static string Describe(IReadOnlyList<RuleFix> fixes, RuleAudit audit)
    {
        var builder = new StringBuilder();

        foreach (var fix in fixes)
        {
            builder.Append(fix.Path);
            builder.Append('\t');
            builder.Append(fix.IsFolder ? Strings.RuleNameFolder : Strings.RuleNameFile);
            builder.Append('\t');

            var pattern = fix.Rules.FirstOrDefault(
                static rule => rule.Kind == RuleKind.NamePattern);

            builder.Append(pattern?.Value ?? string.Empty);
            builder.Append('\t');
            builder.Append(pattern?.Description ?? string.Empty);
            builder.Append('\n');
        }

        // 使ってはいけない名前と拡張子も伝える。避けられるなら避けてもらう
        var banned = audit.Results.Select(static result => result.Rule)
            .Where(static rule => rule.Kind
                is RuleKind.ForbiddenExtension or RuleKind.ForbiddenName)
            .Select(static rule => rule.Value)
            .ToList();

        if (banned.Count > 0)
        {
            builder.Append('\n');
            builder.Append(Strings.RuleNameBanned(string.Join(", ", banned)));
            builder.Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>返ってきた文章から、パスと名前の組を読み取る。</summary>
    private static Dictionary<string, string>? Parse(string text)
    {
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');

        if (start < 0 || end <= start)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(text[start..(end + 1)]);

            if (!document.RootElement.TryGetProperty("names", out var list)
                || list.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("path", out var path)
                    || !item.TryGetProperty("name", out var name)
                    || path.ValueKind != JsonValueKind.String
                    || name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                found[path.GetString() ?? string.Empty] = name.GetString() ?? string.Empty;
            }

            return found;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

/// <summary>名前の案を入れた結果 (#28)。</summary>
/// <param name="Filled">確かめて入れられた数。</param>
/// <param name="Refused">案が出たが、確かめて採らなかった数。</param>
/// <param name="Message">頼めなかったときに、人に見せる一文。</param>
internal readonly record struct NamingResult(int Filled, int Refused, string Message)
{
    /// <summary>頼んで答えが返ってきたか。</summary>
    public bool Ok => Message.Length == 0;
}
