namespace Expzip.Inspection;

/// <summary>マルウェア検査を行えたかどうか (#56)。</summary>
internal enum MalwareStatus
{
    /// <summary>行った。</summary>
    Ran,

    /// <summary>
    /// AMSI を提供する対策ソフトが居ないため行えなかった。
    /// 黙って省かず、報告にはっきり出す (#57)。
    /// </summary>
    Unavailable,
}

/// <summary>検査1回分の結果 (#53)。</summary>
internal sealed class InspectionReport
{
    /// <summary>検査した書庫のパス。結果の行から一覧へ飛ぶときに使う。</summary>
    public required string ArchivePath { get; init; }

    /// <summary>見つかった事柄。重い順に並んでいる。</summary>
    public required IReadOnlyList<InspectionFinding> Findings { get; init; }

    /// <summary>途中で中断されたか。</summary>
    public bool Cancelled { get; init; }

    /// <summary>書庫に入っているファイルの数。</summary>
    public int FileCount { get; init; }

    /// <summary>中身まで読んで確かめたファイルの数。</summary>
    public int ContentsChecked { get; init; }

    /// <summary>マルウェア検査に渡せたファイルの数。</summary>
    public int MalwareScanned { get; init; }

    /// <summary>マルウェア検査の可否。</summary>
    public MalwareStatus Malware { get; init; }

    /// <summary>検査にかかった時間。</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>いちばん重い事柄。何も無ければ「問題なし」。</summary>
    public InspectionSeverity Worst => Findings.Count == 0
        ? InspectionSeverity.Ok
        : Findings.Max(static f => f.Severity);

    /// <summary>危険と判定した件数。省いた分も数に入れる。</summary>
    public int DangerCount => Count(InspectionSeverity.Danger);

    /// <summary>注意と判定した件数。省いた分も数に入れる。</summary>
    public int WarningCount => Count(InspectionSeverity.Warning);

    private int Count(InspectionSeverity severity) => Findings
        .Where(f => f.Severity == severity)
        .Sum(static f => f.Extra > 0 ? f.Extra : 1);
}
