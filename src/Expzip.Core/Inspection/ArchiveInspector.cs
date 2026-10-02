using System.Diagnostics;
using Expzip.Archives;

namespace Expzip.Inspection;

/// <summary>
/// 書庫をまとめて調べて1枚の報告にする (#53)。
/// </summary>
/// <remarks>
/// <para>
/// 検査の種類でコマンドを分けない。利用者が知りたいのは「この書庫は安全に開けるか」の
/// 一点で、構造とマルウェアの区別は実装側の都合でしかない。
/// </para>
/// <para>
/// <b>起動は手動のみ</b>。書庫を開くたびには走らせない。書庫を丸ごと読むため、
/// 開く速さを損なうため。開いた時点で出す「パスが通常ではない項目」の警告 (#36) は
/// これまでどおり別に出す。
/// </para>
/// <para>
/// <b>駆除・隔離・削除はしない</b>。報告して終わりにする。書庫の中身をどうするかは
/// 利用者が決めること。
/// </para>
/// </remarks>
internal static class ArchiveInspector
{
    /// <summary>開いている書庫を調べる。</summary>
    /// <param name="contents">調べる書庫の内容。</param>
    /// <param name="password">合言葉。要らない書庫や、入力を断られた場合は null。</param>
    /// <param name="progress">経過の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    public static InspectionReport Inspect(
        ArchiveContents contents,
        string? password,
        IProgress<InspectProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var clock = Stopwatch.StartNew();

        // 対策ソフトが AMSI を提供していない環境では開けない。
        // その場合はマルウェア検査だけを見送り、他の検査は行う (#56)
        using var scanner = AmsiScanner.TryCreate();

        var context = new InspectionContext(contents, password, scanner, progress, cancellationToken);
        var counts = default(ContentInspector.Counts);
        var cancelled = false;

        try
        {
            StructureInspector.Inspect(context);
            SafetyInspector.Inspect(context);
            counts = ContentInspector.Inspect(context);
        }
        catch (OperationCanceledException)
        {
            // 中断は失敗ではない。中断したことを報告に載せて返す。
            // 途中までの結果は画面には出さない (#161)
            cancelled = true;
        }

        return new InspectionReport
        {
            ArchivePath = contents.FilePath,
            Findings = context.Findings.Build(),
            Cancelled = cancelled || context.Stopped,
            FileCount = contents.FileCount,
            ContentsChecked = counts.Checked,
            MalwareScanned = counts.Scanned,
            Malware = scanner is null ? MalwareStatus.Unavailable : MalwareStatus.Ran,
            Elapsed = clock.Elapsed,
        };
    }
}
