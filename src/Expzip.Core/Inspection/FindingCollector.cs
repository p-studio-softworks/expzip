namespace Expzip.Inspection;

/// <summary>
/// 見つかった事柄を集めて、報告に出せる並びに整える (#57)。
/// </summary>
/// <remarks>
/// <para>
/// 同じ種類が何万件も出る書庫がありうる (すべてのファイルが実行形式、など)。
/// そのまま並べると重い事柄が埋もれるため、種類ごとに出す数を区切り、
/// 残りは「ほかに N 件」の1行にまとめる。
/// </para>
/// <para>
/// 中身の検査は並列に進む (#199)。終わる順はまちまちになるので、エントリの事柄には
/// 書庫の中での順番を添えてもらい、並べるときにその順に直す。順番を添えない事柄
/// (書庫そのものの事柄や、構造と安全性の検査の事柄) は、加えた順に、エントリの事柄より前に並ぶ。
/// </para>
/// </remarks>
internal sealed class FindingCollector
{
    /// <summary>1つの種類につき並べる上限。</summary>
    private const int PerIssueLimit = 200;

    private readonly Lock _lock = new();

    private readonly List<(bool InEntry, long Order, InspectionFinding Finding)> _findings = [];

    private long _sequence;

    /// <summary>見つかった事柄を1件加える。</summary>
    /// <param name="issue">事柄の種類。</param>
    /// <param name="target">書庫内のパス。書庫そのものなら空。</param>
    /// <param name="detail">文言に差し込む補足。</param>
    public void Add(InspectionIssue issue, string target, string? detail = null)
    {
        lock (_lock)
        {
            _findings.Add((false, _sequence++, new InspectionFinding(issue, target, detail)));
        }
    }

    /// <summary>中身の検査で、エントリについて見つかった事柄を1件加える。</summary>
    /// <param name="order">書庫の中でのエントリの順番。並べるときにこの順に直す。</param>
    /// <inheritdoc cref="Add(InspectionIssue, string, string?)"/>
    public void Add(InspectionIssue issue, string target, string? detail, long order)
    {
        lock (_lock)
        {
            _findings.Add((true, order, new InspectionFinding(issue, target, detail)));
        }
    }

    /// <summary>報告に載せる並びを作る。</summary>
    public IReadOnlyList<InspectionFinding> Build()
    {
        lock (_lock)
        {
            var shown = new List<(bool InEntry, long Order, InspectionFinding Finding)>();

            // 上限に数えるのは、見つかった順ではなく書庫の順で前のもの
            foreach (var group in _findings.GroupBy(static f => f.Finding.Issue))
            {
                var ordered = group.OrderBy(static f => f.InEntry).ThenBy(static f => f.Order).ToList();
                shown.AddRange(ordered.Take(PerIssueLimit));

                if (ordered.Count > PerIssueLimit)
                {
                    shown.Add((true, long.MaxValue, new InspectionFinding(
                        group.Key, string.Empty, Extra: ordered.Count - PerIssueLimit)));
                }
            }

            // 重いものから。同じ重さなら検査の系統ごとにまとめ、その中は書庫の順。
            // 「ほかに N 件」は代表の行から離れないよう、同じ種類の末尾に置く
            return shown
                .OrderByDescending(static f => f.Finding.Severity)
                .ThenBy(static f => f.Finding.Check)
                .ThenBy(static f => f.Finding.Issue)
                .ThenBy(static f => f.Finding.Extra > 0 ? 1 : 0)
                .ThenBy(static f => f.InEntry)
                .ThenBy(static f => f.Order)
                .Select(static f => f.Finding)
                .ToList();
        }
    }
}
