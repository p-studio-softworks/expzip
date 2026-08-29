using Expzip.Archives;

namespace Expzip.Ai;

/// <summary>
/// 保存した決まりを、いま開いている書庫に当てた結果 (#27、仕様書 11.3節の4)。
/// </summary>
/// <remarks>
/// <para>
/// **AI は使わない。**当てはめは <see cref="RuleChecker"/> が決まった手順で行う
/// (#25)。ここでやるのは、当てた結果を**画面から引きやすい形に並べ替える**こと。
/// 一覧は項目ごとに「この項目は決まりに合っているか」を知りたいので、
/// 決まりごとの結果を項目ごとに引き直す。
/// </para>
/// <para>
/// **外した決まりは当てない** (#26)。使わないことにしたものを当てると、
/// 外した意味が無くなる。
/// </para>
/// <para>
/// **「必ずある」が無い場合は、指させる項目が無い。**一覧に印を付けられないため、
/// 別に持って報告のほうで出す。印が付かないことを「問題なし」と読ませない。
/// </para>
/// </remarks>
internal sealed class RuleAudit
{
    private RuleAudit(
        string learnedFrom,
        DateTimeOffset learnedAt,
        IReadOnlyList<RuleResult> results,
        Dictionary<string, List<ArchiveRule>> broken,
        List<ArchiveRule> unmet)
    {
        LearnedFrom = learnedFrom;
        LearnedAt = learnedAt;
        Results = results;
        Broken = broken;
        Unmet = unmet;
    }

    /// <summary>お手本にした書庫の名前。</summary>
    public string LearnedFrom { get; }

    /// <summary>決まりを保存した日時。</summary>
    public DateTimeOffset LearnedAt { get; }

    /// <summary>当てた決まりごとの結果。報告に出す。</summary>
    public IReadOnlyList<RuleResult> Results { get; }

    /// <summary>決まりに合っていない項目。書庫内パスから、破っている決まりを引く。</summary>
    public IReadOnlyDictionary<string, List<ArchiveRule>> Broken { get; }

    /// <summary>「必ずある」はずのものが無かった決まり。指させる項目が無い。</summary>
    public IReadOnlyList<ArchiveRule> Unmet { get; }

    /// <summary>当てた決まりの数。</summary>
    public int RuleCount => Results.Count;

    /// <summary>決まりに合っていない項目の数。</summary>
    public int BrokenCount => Broken.Count;

    /// <summary>合っていないものが1つも無いか。</summary>
    public bool Clean => Broken.Count == 0 && Unmet.Count == 0;

    /// <summary>保存された決まりを読んで当てる。決まりが無ければ <see langword="null"/>。</summary>
    public static RuleAudit? FromStore(ArchiveContents contents)
    {
        if (RuleStore.Load() is not { } book)
        {
            return null;
        }

        var rules = book.Rules.Where(static entry => entry.Enabled)
            .Select(static entry => entry.Rule).ToList();

        return rules.Count == 0
            ? null
            : Run(rules, contents, book.LearnedFrom, book.LearnedAt);
    }

    /// <summary>決まりを当てる。</summary>
    public static RuleAudit Run(
        IReadOnlyList<ArchiveRule> rules, ArchiveContents contents,
        string learnedFrom = "", DateTimeOffset learnedAt = default)
    {
        var results = RuleChecker.CheckAll(rules, contents);

        // 書庫内パスの大文字小文字は、一覧の見え方と同じく区別しない
        var broken = new Dictionary<string, List<ArchiveRule>>(StringComparer.OrdinalIgnoreCase);
        var unmet = new List<ArchiveRule>();

        foreach (var result in results)
        {
            if (result.Satisfied)
            {
                continue;
            }

            if (result.Violations.Count == 0)
            {
                // 「必ずある」が無い。指させる項目が無いので、別に持つ
                unmet.Add(result.Rule);
                continue;
            }

            foreach (var path in result.Violations)
            {
                if (!broken.TryGetValue(path, out var list))
                {
                    broken[path] = list = [];
                }

                list.Add(result.Rule);
            }
        }

        return new RuleAudit(learnedFrom, learnedAt, results, broken, unmet);
    }

    /// <summary>その項目が破っている決まり。破っていなければ <see langword="null"/>。</summary>
    public IReadOnlyList<ArchiveRule>? Breaks(string fullPath)
        => Broken.TryGetValue(fullPath, out var rules) ? rules : null;
}
