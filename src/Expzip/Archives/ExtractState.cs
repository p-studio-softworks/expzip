using System.IO;
using SharpCompress.Common;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>展開の途中経過。ZIP 側と同じ結果の形にまとめる。</summary>
/// <remarks>
/// 書庫の外へ書き出そうとするエントリを弾き (Zip Slip 対策)、出所の印を引き継ぎ、
/// 中断したら書きかけを消す。7z / tar (#19) と NSIS (#68) で共通に使う。
/// </remarks>
internal sealed class ExtractState(
    string destinationRoot,
    bool overwrite,
    string? zoneIdentifier,
    string? basePath,
    IProgress<ExtractProgress>? progress)
{
    private readonly List<string> _rejected = [];
    private readonly List<(string Name, string Reason)> _failed = [];
    private long _doneBytes;
    private int _extracted;
    private int _skipped;

    public long TotalBytes { get; set; }

    public bool Cancelled { get; private set; }

    /// <summary>合言葉が要る書庫は、理由を自前の文言で出す (#20)。</summary>
    private static string Explain(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException
            ? Strings.PasswordNotSupported
            : Strings.Reason(ex);

    /// <summary>1件を書き出す。</summary>
    public void Write(
        string key, long size, DateTime? lastWriteTime,
        Func<Stream> openEntry, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Cancelled = true;
            return;
        }

        // 名前の形は形式ごとに違う (tar は先頭に `./` が付く)。
        // 一覧の組み立てと同じ整え方をしてから、展開先を決める
        var relative = ArchivePath.ToSafeRelativePath(
            ArchiveExtractor.StripBase(ArchiveTreeBuilder.Trim(key), basePath));

        if (relative is null)
        {
            _rejected.Add(key);
            return;
        }

        var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));

        // 書庫の外へ書き出そうとするエントリを弾く (Zip Slip 対策)
        if (!ArchiveExtractor.IsInside(destinationRoot, target))
        {
            _rejected.Add(key);
            return;
        }

        try
        {
            if (File.Exists(target) && !overwrite)
            {
                _skipped++;
                Advance(size, key);
                return;
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            using (var source = openEntry())
            using (var destination = new FileStream(
                       target, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                CancellableCopy.CopyContent(source, destination, cancellationToken);
            }

            ArchiveExtractor.ApplyStamp(target, lastWriteTime ?? default, zoneIdentifier);
            _extracted++;
        }
        catch (OperationCanceledException)
        {
            // 書きかけのファイルは中身が途中までしかない。見た目は正常な
            // ファイルとして残るため、何も残らないより悪い
            ArchiveExtractor.TryDelete(target);
            Cancelled = true;
            return;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                   or SharpCompress.Common.CryptographicException)
        {
            // パスワード付きの書庫。一覧は読めるが中身は取り出せない
            ArchiveExtractor.TryDelete(target);
            _failed.Add((key, Explain(ex)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or PathTooLongException or InvalidDataException
                                   or SharpCompressException)
        {
            // 読めなかった分の書きかけを残さない。中身が途中までのファイルは、
            // 見た目が正常なだけに何も残らないより悪い (#66、#167)
            ArchiveExtractor.TryDelete(target);

            // 1件の失敗で全体を止めない。まとめて報告する
            _failed.Add((key, Strings.Reason(ex)));
        }

        Advance(size, key);
    }

    /// <summary>書庫ごと読めなかったことを記録する。</summary>
    public void Fail(string name, string reason) => _failed.Add((name, reason));

    public ExtractResult ToResult()
        => new(_extracted, _skipped, _rejected, _failed, Cancelled);

    private void Advance(long size, string key)
    {
        _doneBytes += size;
        progress?.Report(new ExtractProgress(_doneBytes, TotalBytes, key));
    }
}
