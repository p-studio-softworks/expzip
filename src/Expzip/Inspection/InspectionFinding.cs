namespace Expzip.Inspection;

/// <summary>
/// 検査で見つかった事柄の種類 (#53)。
/// </summary>
/// <remarks>
/// 文言そのものではなく種類を持つ。表示するときに <see cref="Localization.Strings"/> を
/// 通すため、検査が済んだあとに言語を切り替えても報告の文字が入れ替わる (#23)。
/// </remarks>
internal enum InspectionIssue
{
    // -------------------------------------------------------------- 構造 (#54)

    /// <summary>展開して照合したCRCが、書庫に書かれている値と合わない。</summary>
    CrcMismatch,

    /// <summary>中央ディレクトリとローカルヘッダの内容が食い違う。</summary>
    HeaderMismatch,

    /// <summary>書庫が途中で切れている。</summary>
    Truncated,

    /// <summary>書庫の末尾に、書庫ではないデータが続いている。</summary>
    TrailingData,

    /// <summary>対応していない圧縮方式で、中身を取り出せない。</summary>
    UnsupportedMethod,

    /// <summary>同じ名前のエントリが2つ以上ある。</summary>
    DuplicateName,

    /// <summary>中身を読み出せなかった。</summary>
    Unreadable,

    /// <summary>パスワードが分からないため、中身を調べられなかった。</summary>
    EncryptedNotChecked,

    // -------------------------------------------------------------- 安全性 (#55)

    /// <summary>展開すると展開先の外へ出るパス。</summary>
    EscapingPath,

    /// <summary>展開先の中には収まるが、通常の書庫にはあり得ない形のパス。</summary>
    SuspiciousPath,

    /// <summary>Windows が特別扱いする名前 (CON、PRN など)。</summary>
    ReservedName,

    /// <summary>末尾が空白かピリオドで、Windows では作れない名前。</summary>
    TrailingSpaceOrDot,

    /// <summary>制御文字を含む名前。</summary>
    ControlCharacter,

    /// <summary>Windows のファイル名に使えない文字を含む名前。</summary>
    InvalidCharacter,

    /// <summary>大文字小文字だけが違うエントリ。展開先で片方が失われる。</summary>
    CaseCollision,

    /// <summary>右横書き記号を含む名前。拡張子を偽装できる。</summary>
    BidiOverride,

    /// <summary>展開すると極端に大きくなるエントリ。</summary>
    HighRatio,

    /// <summary>書庫全体が極端に膨らむ。いわゆるZIP爆弾。</summary>
    ZipBomb,

    /// <summary>開くと実行されうる拡張子。</summary>
    ExecutableExtension,

    // -------------------------------------------------------------- マルウェア (#56)

    /// <summary>対策ソフトが検出した。</summary>
    MalwareDetected,

    /// <summary>大きすぎて一度に渡せず、マルウェア検査を行えなかった。</summary>
    TooLargeToScan,
}

/// <summary>検査で見つかった事柄の重さ (#57)。</summary>
internal enum InspectionSeverity
{
    /// <summary>問題なし。何も見つからなかったことを示す1行に使う。</summary>
    Ok,

    /// <summary>注意。中身を確かめてから扱ったほうがよい。</summary>
    Warning,

    /// <summary>危険。そのまま展開すると困ることが起きうる。</summary>
    Danger,
}

/// <summary>どの系統の検査で見つかったか (#53)。</summary>
internal enum InspectionCheck
{
    /// <summary>構造の検査 (#54)。</summary>
    Structure,

    /// <summary>安全性の検査 (#55)。</summary>
    Safety,

    /// <summary>マルウェア検査 (#56)。</summary>
    Malware,
}

/// <summary>検査で見つかった1件。</summary>
/// <param name="Issue">見つかった事柄の種類。</param>
/// <param name="Target">書庫内のパス。書庫そのものを指す場合は空。</param>
/// <param name="Detail">文言に差し込む補足。種類ごとに意味が違う。</param>
/// <param name="Extra">
/// 同じ種類がこれ以上あって省いた件数。0 より大きいときは、代表の1行ではなく
/// 「ほかに N 件」を表す行になる。
/// </param>
internal sealed record InspectionFinding(
    InspectionIssue Issue, string Target, string? Detail = null, int Extra = 0)
{
    /// <summary>この事柄の重さ。</summary>
    public InspectionSeverity Severity => InspectionIssues.SeverityOf(Issue);

    /// <summary>この事柄を見つけた検査。</summary>
    public InspectionCheck Check => InspectionIssues.CheckOf(Issue);
}

/// <summary>事柄の種類ごとの決めごと。</summary>
internal static class InspectionIssues
{
    /// <summary>
    /// 重さは種類で決まる。
    /// </summary>
    /// <remarks>
    /// 同じ現象でも程度によって重さを変えたいものは、種類そのものを分けてある
    /// (<see cref="InspectionIssue.HighRatio"/> と <see cref="InspectionIssue.ZipBomb"/> など)。
    /// 呼ぶ側が重さを決められるようにすると、同じ事柄が場所によって違う重さで出てしまう。
    /// </remarks>
    public static InspectionSeverity SeverityOf(InspectionIssue issue) => issue switch
    {
        InspectionIssue.CrcMismatch => InspectionSeverity.Danger,
        InspectionIssue.HeaderMismatch => InspectionSeverity.Danger,
        InspectionIssue.Truncated => InspectionSeverity.Danger,
        InspectionIssue.Unreadable => InspectionSeverity.Danger,
        InspectionIssue.EscapingPath => InspectionSeverity.Danger,
        InspectionIssue.BidiOverride => InspectionSeverity.Danger,
        InspectionIssue.ZipBomb => InspectionSeverity.Danger,
        InspectionIssue.MalwareDetected => InspectionSeverity.Danger,
        _ => InspectionSeverity.Warning,
    };

    /// <summary>どの検査が見つけたか。</summary>
    public static InspectionCheck CheckOf(InspectionIssue issue) => issue switch
    {
        InspectionIssue.MalwareDetected or InspectionIssue.TooLargeToScan
            => InspectionCheck.Malware,

        InspectionIssue.EscapingPath or InspectionIssue.SuspiciousPath
            or InspectionIssue.ReservedName or InspectionIssue.TrailingSpaceOrDot
            or InspectionIssue.ControlCharacter or InspectionIssue.InvalidCharacter
            or InspectionIssue.CaseCollision or InspectionIssue.BidiOverride
            or InspectionIssue.HighRatio or InspectionIssue.ZipBomb
            or InspectionIssue.ExecutableExtension
            => InspectionCheck.Safety,

        _ => InspectionCheck.Structure,
    };
}
