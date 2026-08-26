namespace Expzip.Inspection;

/// <summary>
/// 見つかった事柄を集めて、報告に出せる並びに整える (#57)。
/// </summary>
/// <remarks>
/// 同じ種類が何万件も出る書庫がありうる (すべてのファイルが実行形式、など)。
/// そのまま並べると重い事柄が埋もれるため、種類ごとに出す数を区切り、
/// 残りは「ほかに N 件」の1行にまとめる。
/// </remarks>
internal sealed class FindingCollector
{
    /// <summary>1つの種類につき並べる上限。</summary>
    private const int PerIssueLimit = 200;

    private readonly List<InspectionFinding> _findings = [];

    private readonly Dictionary<InspectionIssue, int> _counts = [];

    /// <summary>見つかった事柄を1件加える。</summary>
    /// <param name="issue">事柄の種類。</param>
    /// <param name="target">書庫内のパス。書庫そのものなら空。</param>
    /// <param name="detail">文言に差し込む補足。</param>
    public void Add(InspectionIssue issue, string target, string? detail = null)
    {
        _counts.TryGetValue(issue, out var seen);
        _counts[issue] = seen + 1;

        if (seen < PerIssueLimit)
        {
            _findings.Add(new InspectionFinding(issue, target, detail));
        }
    }

    /// <summary>報告に載せる並びを作る。</summary>
    public IReadOnlyList<InspectionFinding> Build()
    {
        foreach (var (issue, count) in _counts)
        {
            if (count > PerIssueLimit)
            {
                _findings.Add(new InspectionFinding(
                    issue, string.Empty, Extra: count - PerIssueLimit));
            }
        }

        // 重いものから。同じ重さなら検査の系統ごとにまとめ、その中は見つけた順。
        // 「ほかに N 件」は代表の行から離れないよう、同じ種類の末尾に置く
        return _findings
            .OrderByDescending(static f => f.Severity)
            .ThenBy(static f => f.Check)
            .ThenBy(static f => f.Issue)
            .ThenBy(static f => f.Extra > 0 ? 1 : 0)
            .ToList();
    }
}
