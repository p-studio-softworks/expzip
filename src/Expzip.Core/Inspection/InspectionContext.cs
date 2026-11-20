using System.Diagnostics;
using Expzip.Archives;

namespace Expzip.Inspection;

/// <summary>
/// 検査の進み方の区切り (#53)。
/// </summary>
/// <remarks>
/// <see cref="InspectionCheck"/> とは別にしてある。CRCの照合 (#54) とマルウェア検査 (#56)
/// は同じ読み出しに相乗りするため、経過の上ではひとまとまりの「中身」になる。
/// </remarks>
internal enum InspectionPhase
{
    /// <summary>ヘッダと索引の照合。</summary>
    Structure,

    /// <summary>名前と大きさだけで分かる検査。</summary>
    Safety,

    /// <summary>中身を読んでの照合とマルウェア検査。</summary>
    Contents,
}

/// <summary>検査の経過 (#53)。</summary>
/// <param name="Percent">全体の進み具合 (0〜100)。</param>
/// <param name="Phase">いま行っているところ。</param>
/// <param name="CurrentName">いま見ている項目の名前。</param>
internal readonly record struct InspectProgress(
    double Percent, InspectionPhase Phase, string CurrentName);

/// <summary>中身から読んだリンクの指す先 (#391)。</summary>
/// <param name="Name">中身を読んだ項目の名前。一覧の項目と同じものかを確かめるのに使う。</param>
/// <param name="Target">指す先。読めなかった、長すぎる、壊れているときは <see langword="null"/>。</param>
/// <param name="Locked">パスワードが分からず読めなかった。</param>
internal sealed record LinkRead(string Name, string? Target, bool Locked = false);

/// <summary>
/// 検査1回分に要るものをまとめたもの。各検査はこれを受け取り、見つけたものをここへ入れる。
/// </summary>
/// <remarks>
/// 進捗は「いまの区切りの中で何割まで来たか」だけを渡せばよいようにしてある。
/// 区切りごとの重みの配分は <see cref="BeginPhase"/> で決め、全体に対する割合への
/// 変換はここで引き受ける。各検査が全体の進み具合を知る必要はない。
/// </remarks>
internal sealed class InspectionContext(
    ArchiveContents contents,
    string? password,
    AmsiScanner? scanner,
    IProgress<InspectProgress>? progress,
    CancellationToken cancellationToken)
{
    /// <summary>
    /// 経過を知らせる間隔。
    /// </summary>
    /// <remarks>
    /// 間引きをここに置くのは、検査ごとに書くと必ず抜けが出るため。実際、
    /// 構造の検査は1件ごとに出していて、5万件の書庫では5万回になっていた。
    /// 間隔を件数やバイト数で区切らないのは、書庫の作りによって出すぎたり
    /// 出なさすぎたりするため。
    /// </remarks>
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(100);

    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private readonly Lock _lock = new();

    private TimeSpan _reportedAt = TimeSpan.MinValue;

    private double _base;

    private double _span;

    private InspectionPhase _phase;

    /// <summary>検査する書庫の内容。</summary>
    public ArchiveContents Contents => contents;

    /// <summary>書庫ファイルのパス。</summary>
    public string ArchivePath => contents.FilePath;

    /// <summary>合言葉。要らない書庫や、入力を断られた場合は <see langword="null"/>。</summary>
    public string? Password => password;

    /// <summary>マルウェア検査の受け口。使えない環境では <see langword="null"/> (#56)。</summary>
    public AmsiScanner? Scanner => scanner;

    /// <summary>中断の合図。</summary>
    public CancellationToken Cancellation => cancellationToken;

    /// <summary>見つかったものの置き場。</summary>
    public FindingCollector Findings { get; } = new();

    /// <summary>
    /// 索引が 2 つある書庫だと知らせたか (#396)。構造の検査と中身の検査はファイルを別々に開くので、
    /// あいだに置き換わったときは中身の検査が知らせる。2 回は出さない。
    /// </summary>
    public bool SplitDirectoryReported { get; set; }

    /// <summary>索引が 2 つある書庫だと、まだ知らせていなければ知らせる (#396)。</summary>
    public void ReportSplitDirectory()
    {
        lock (Findings)
        {
            if (!SplitDirectoryReported)
            {
                SplitDirectoryReported = true;
                Findings.Add(InspectionIssue.SplitDirectory, string.Empty);
            }
        }
    }

    /// <summary>
    /// 中身から読んだリンクの指す先 (#391)。項目の番号 (<see cref="SequenceItem.Index"/>) から引く。
    /// 中身の検査が読み通すついでに入れ、<see cref="LinkInspector"/> が使う。書庫を 2 回読まないため。
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<int, LinkRead> LinkTargets { get; } = new();

    /// <summary>
    /// <see cref="LinkTargets"/> に取っておく指す先の文字数の合計の上限 (#391)。リンクが何万個もある書庫で、
    /// 検査の終わりまで抱えるメモリーが膨らまないようにする (おおよそ 32 MB)。
    /// </summary>
    private const long LinkTargetBudget = 16L * 1024 * 1024;

    private long _linkTargetChars;

    /// <summary>
    /// 指す先を取っておけるか。上限を超えたら <see langword="false"/> で、そのリンクは確かめられないとする (黙って通さない)。
    /// </summary>
    public bool TryKeepLinkTarget(int length) => Interlocked.Add(ref _linkTargetChars, length) <= LinkTargetBudget;

    /// <summary>中断が要求されているか。</summary>
    public bool Stopped => cancellationToken.IsCancellationRequested;

    /// <summary>次の区切りに移る。<paramref name="span"/> は全体に対する重み (0〜1)。</summary>
    public void BeginPhase(InspectionPhase phase, double span)
    {
        _base += _span;
        _span = span;
        _phase = phase;
        Advance(0, string.Empty);
    }

    /// <summary>
    /// いまの区切りの中での進み具合 (0〜1) を伝える。
    /// </summary>
    /// <remarks>
    /// 呼びすぎても構わない。間隔を空けるのはこちらの仕事にしてある。
    /// ただし区切りの始まりと終わりだけは、間隔によらず必ず伝える。
    /// </remarks>
    public void Advance(double fraction, string currentName)
    {
        if (progress is null)
        {
            return;
        }

        // 中身の検査は並列に進むので、どの作業からも呼ばれる (#199)
        lock (_lock)
        {
            var edge = fraction is <= 0 or >= 1;
            var now = _clock.Elapsed;

            if (!edge && now - _reportedAt < ReportInterval)
            {
                return;
            }

            _reportedAt = now;
            progress.Report(new InspectProgress(
                Math.Clamp(_base + (_span * fraction), 0, 1) * 100, _phase, currentName));
        }
    }
}
