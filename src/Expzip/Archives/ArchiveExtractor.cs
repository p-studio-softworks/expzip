using System.IO;
using System.IO.Compression;

namespace Expzip.Archives;

/// <summary>展開の進捗。</summary>
/// <param name="DoneBytes">展開済みのバイト数。</param>
/// <param name="TotalBytes">展開する合計バイト数。</param>
/// <param name="CurrentName">いま処理しているファイル名。</param>
internal readonly record struct ExtractProgress(long DoneBytes, long TotalBytes, string CurrentName)
{
    /// <summary>0〜100 の進捗率。</summary>
    public double Percent => TotalBytes <= 0 ? 100 : (double)DoneBytes / TotalBytes * 100.0;
}

/// <summary>展開の結果。</summary>
/// <param name="Extracted">展開したファイル数。</param>
/// <param name="Skipped">既存ファイルを上書きせず飛ばした数。</param>
/// <param name="Rejected">安全でないパスとして拒否したエントリ名。</param>
/// <param name="Failed">書き出しに失敗したエントリ名とその理由。</param>
internal sealed record ExtractResult(
    int Extracted,
    int Skipped,
    IReadOnlyList<string> Rejected,
    IReadOnlyList<(string Name, string Reason)> Failed);

/// <summary>書庫から実ファイルへ展開する。</summary>
internal static class ArchiveExtractor
{
    /// <summary>
    /// 指定したエントリを展開する。
    /// </summary>
    /// <param name="archivePath">書庫ファイルのパス。</param>
    /// <param name="sourceNames">
    /// 展開するエントリの <see cref="ArchiveEntry.SourceName"/>。
    /// <see langword="null"/> を渡すと書庫全体を展開する。
    /// </param>
    /// <param name="destinationDirectory">展開先フォルダ。</param>
    /// <param name="overwrite">既存のファイルを上書きするかどうか。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    public static ExtractResult Extract(
        string archivePath,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        bool overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken)
    {
        // 展開先の正規化。これを基準に、書庫外へ書き出そうとするエントリを弾く
        var destinationRoot = Path.GetFullPath(destinationDirectory);

        using var stream = File.OpenRead(archivePath);
        using var zip = new ZipArchive(
            stream, ZipArchiveMode.Read, leaveOpen: false, ZipArchiveReader.EntryNameEncoding);

        var targets = zip.Entries
            .Where(e => !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'))
            .Where(e => sourceNames is null || sourceNames.Contains(e.FullName))
            .ToList();

        var totalBytes = targets.Sum(static e => e.Length);
        long doneBytes = 0;

        var extracted = 0;
        var skipped = 0;
        var rejected = new List<string>();
        var failed = new List<(string, string)>();

        // 進捗通知が多すぎると UI 側が詰まるため、1%刻みに間引く
        var reportStep = Math.Max(1, totalBytes / 100);
        long nextReport = 0;

        foreach (var entry in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relative = ToSafeRelativePath(entry.FullName);
            if (relative is null)
            {
                rejected.Add(entry.FullName);
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));

            // Zip Slip 対策。`../` を含むエントリで展開先の外に書き出されるのを防ぐ。
            // Path.GetFullPath で解決したうえで、展開先の配下にあることを確認する。
            if (!IsInside(destinationRoot, target))
            {
                rejected.Add(entry.FullName);
                continue;
            }

            try
            {
                if (File.Exists(target) && !overwrite)
                {
                    skipped++;
                    doneBytes += entry.Length;
                    continue;
                }

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var source = entry.Open())
                using (var destination = new FileStream(
                    target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(destination);
                }

                TryPreserveTimestamp(entry, target);
                extracted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or PathTooLongException or InvalidDataException)
            {
                // 1件の失敗で全体を止めない。まとめて報告する
                failed.Add((entry.FullName, ex.Message));
            }

            doneBytes += entry.Length;

            if (progress is not null && (doneBytes >= nextReport || ReferenceEquals(entry, targets[^1])))
            {
                nextReport = doneBytes + reportStep;
                progress.Report(new ExtractProgress(doneBytes, totalBytes, entry.FullName));
            }
        }

        return new ExtractResult(extracted, skipped, rejected, failed);
    }

    /// <summary>
    /// エントリ名を展開先からの相対パスに変換する。
    /// 絶対パスやドライブ指定など、そのまま結合すると展開先を離れてしまう形は拒否する。
    /// </summary>
    /// <remarks>
    /// 先頭の <c>/</c> は拒否せず取り除く。<c>/foo.txt</c> は展開先を起点とした
    /// <c>foo.txt</c> として扱われ、展開先の外には出ないため危険ではない。
    /// 多くのアーカイバも同じ扱いをする。一方 <c>../</c> を含む経路とドライブ指定は、
    /// 展開先の外を指しうるので拒否する。
    /// </remarks>
    /// <returns>安全な相対パス。扱えない場合は <see langword="null"/>。</returns>
    private static string? ToSafeRelativePath(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').TrimStart('/');

        if (normalized.Length == 0)
        {
            return null;
        }

        // "C:/..." のようなドライブ指定や UNC パスは相対パスとして扱えない
        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            return null;
        }

        return normalized.Replace('/', Path.DirectorySeparatorChar);
    }

    /// <summary>解決済みのパスが指定フォルダの配下にあるか。</summary>
    private static bool IsInside(string root, string candidate)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 展開したファイルに書庫内の更新日時を反映する。
    /// 書庫によっては範囲外の日付が入っており設定できないことがあるが、
    /// 展開自体は成功しているので失敗しても無視する。
    /// </summary>
    private static void TryPreserveTimestamp(ZipArchiveEntry entry, string path)
    {
        try
        {
            File.SetLastWriteTime(path, entry.LastWriteTime.LocalDateTime);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException
                                   or UnauthorizedAccessException)
        {
        }
    }
}
