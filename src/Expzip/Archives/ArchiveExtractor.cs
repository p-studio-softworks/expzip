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
/// <param name="Cancelled">利用者の操作で中断した場合は true。</param>
internal sealed record ExtractResult(
    int Extracted,
    int Skipped,
    IReadOnlyList<string> Rejected,
    IReadOnlyList<(string Name, string Reason)> Failed,
    bool Cancelled);

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
    /// <param name="zoneIdentifier">
    /// 書き出したファイルに引き継ぐ出所の印 (#12)。<see langword="null"/> なら何もしない。
    /// </param>
    /// <param name="basePath">
    /// 書き出し先を決める際に、書庫内パスの先頭から取り除くフォルダ (#48)。
    /// 例えば <c>資料/画像</c> を選んで展開する場合に <c>資料</c> を渡すと、
    /// 展開先には <c>画像\…</c> が並ぶ。<see langword="null"/> なら書庫のルートからの
    /// 階層をそのまま作る。
    /// </param>
    /// <param name="format">書庫の形式 (#19)。ZIP 以外は SharpCompress 側へ回す。</param>
    /// <param name="password">パスワード付きZIPの合言葉 (#20)。要らない書庫では <see langword="null"/>。</param>
    public static ExtractResult Extract(
        string archivePath,
        ArchiveFormat format,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        bool overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null,
        string? password = null)
        => format != ArchiveFormat.Zip
            ? SharpArchiveExtractor.Extract(archivePath, format, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath)
            : password is null
                ? ExtractZip(archivePath, sourceNames, destinationDirectory, overwrite,
                    progress, cancellationToken, zoneIdentifier, basePath)
                : ZipEncryption.Extract(archivePath, password, sourceNames, destinationDirectory,
                    overwrite, progress, cancellationToken, zoneIdentifier, basePath);

    private static ExtractResult ExtractZip(
        string archivePath,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        bool overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier,
        string? basePath)
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

        var cancelled = false;

        foreach (var entry in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var relative = ArchivePath.ToSafeRelativePath(StripBase(entry.FullName, basePath));
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
                    CancellableCopy.Copy(source, destination, cancellationToken);
                }

                TryPreserveTimestamp(entry, target);

                // 書庫に出所の印が付いていた場合は、書き出したファイルにも引き継ぐ。
                // 印が消えると SmartScreen や保護ビューが働かなくなる (#12)
                MarkOfTheWeb.TryApply(target, zoneIdentifier);

                extracted++;
            }
            catch (OperationCanceledException)
            {
                // 書きかけのファイルは中身が途中までしかない。見た目は正常な
                // ファイルとして残るため、何も残らないより悪い。消してから中断する。
                // ここに来る時点で using は抜けており、ファイルは閉じられている。
                TryDelete(target);
                cancelled = true;
                break;
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

        return new ExtractResult(extracted, skipped, rejected, failed, cancelled);
    }

    /// <summary>
    /// 書庫内パスの先頭から、指定のフォルダを取り除く。
    /// 選んだフォルダを展開先の最上位にするために使う (#48)。
    /// </summary>
    public static string StripBase(string entryName, string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return entryName;
        }

        var normalized = ArchivePath.Normalize(entryName);
        var prefix = basePath + "/";

        return normalized.StartsWith(prefix, StringComparison.Ordinal)
            ? normalized[prefix.Length..]
            : normalized;
    }

    /// <summary>中断時の後始末。消せなくても中断自体は成立するので握りつぶす。</summary>
    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>解決済みのパスが指定フォルダの配下にあるか。</summary>
    internal static bool IsInside(string root, string candidate)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 書き出したファイルに、書庫が持っていた更新日時と出所の印を移す (#12, #19)。
    /// </summary>
    internal static void ApplyStamp(string target, DateTime lastWriteTime, string? zoneIdentifier)
    {
        try
        {
            if (lastWriteTime != default)
            {
                File.SetLastWriteTime(target, lastWriteTime);
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException
                                   or UnauthorizedAccessException)
        {
        }

        // 書庫に出所の印が付いていた場合は、書き出したファイルにも引き継ぐ。
        // 印が消えると SmartScreen や保護ビューが働かなくなる (#12)
        MarkOfTheWeb.TryApply(target, zoneIdentifier);
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
