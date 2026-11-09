using System.IO;
using SharpCompress.Common;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>展開の途中経過。ZIP 側と同じ結果の形にまとめる。</summary>
/// <remarks>
/// 書庫の外へ書き出そうとするエントリを弾き (Zip Slip 対策)、MotW を引き継ぐ。
/// 失敗や中断では書きかけだけを消し、同じ名前の既にあるファイルは残す (#320)。
/// 7z / tar (#19) と NSIS (#68) で共通に使う。
/// </remarks>
internal sealed class ExtractState(
    string destinationRoot,
    ExtractOverwrite overwrite,
    string? zoneIdentifier,
    string? basePath,
    IProgress<ExtractProgress>? progress)
{
    private readonly List<string> _rejected = [];
    private readonly List<(string Name, string Reason)> _failed = [];
    private long _doneBytes;
    private readonly List<string> _skippedNames = [];
    private int _extracted;
    private readonly MarkWatch _marks = new();

    public long TotalBytes { get; set; }

    public bool Cancelled { get; private set; }

    /// <summary>合言葉が要る書庫は、理由を自前の文言で出す (#20)。</summary>
    private static string Explain(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException
            ? Strings.PasswordNotSupported
            : Strings.Reason(ex);

    /// <summary>1件を書き出す。</summary>
    /// <param name="expectedCrc">
    /// 書庫に書かれた CRC。持たない形式 (tar、NSIS) では -1 (#168)。
    /// </param>
    /// <param name="sizeIsExact"><paramref name="size"/> が中身の大きさと一致するはずの形式か (#320)。</param>
    public void Write(
        string key, long size, DateTime? lastWriteTime,
        Func<Stream> openEntry, CancellationToken cancellationToken, long expectedCrc = -1, bool sizeIsExact = false)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            Cancelled = true;
            return;
        }

        // 名前の形は形式ごとに違う (tar は先頭に `./` が付く)。
        // 上書きの確認と同じ決め方で展開先を決め、書庫の外へ書き出そうとするエントリを弾く (Zip Slip 対策)
        var target = ArchiveExtractor.TargetFor(destinationRoot, key, basePath);
        if (target is null)
        {
            _rejected.Add(key);
            return;
        }

        try
        {
            var replace = overwrite.Allows(target);
            if (!replace && File.Exists(target))
            {
                _skippedNames.Add(key);
                Advance(size, key);
                return;
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var source = Open(openEntry);
            try
            {
                using var file = AtomicFile.CreateForExtraction(target, replace, zoneIdentifier);
                CancellableCopy.CopyContent(
                    source, file.Stream, cancellationToken, expectedCrc, sizeIsExact ? size : -1);

                // 閉じるのに失敗したものは、壊れている。置き換える前に確かめる
                Close(source);

                if (file.Commit(lastWriteTime))
                {
                    _extracted++;
                    _marks.Committed(file, target);
                    overwrite.Created(target);
                }
                else
                {
                    _skippedNames.Add(key);
                }
            }
            finally
            {
                Close(source);
            }
        }
        catch (OperationCanceledException)
        {
            // 書きかけは AtomicFile が消す。同じ名前の既にあるファイルには触れていない (#320)
            Cancelled = true;
            return;
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException
                                   or SharpCompress.Common.CryptographicException)
        {
            // パスワード付きの書庫。一覧は読めるが中身は取り出せない
            _failed.Add((key, Explain(ex)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or PathTooLongException or InvalidDataException
                                   or SharpCompressException)
        {
            // 読めなかったものは残さない。中身が途中までのファイルは、
            // 見た目が正常なだけに何も残らないより悪い (#66、#167)。
            // 1件の失敗で全体を止めない。まとめて報告する
            _failed.Add((key, Strings.Reason(ex)));
        }

        Advance(size, key);
    }

    /// <summary>中身を開く。開く時点で壊れに気付くものも、読むときと同じに扱う (#294)。</summary>
    private static Stream Open(Func<Stream> openEntry)
    {
        try
        {
            return openEntry();
        }
        catch (Exception ex) when (CancellableCopy.IsLibraryFailure(ex))
        {
            throw new DamagedDataException(ex);
        }
    }

    /// <summary>中身を閉じる。読み残しを読み飛ばす処理が、壊れたデータで失敗することがある (#294)。</summary>
    private static void Close(Stream source)
    {
        try
        {
            source.Dispose();
        }
        catch (Exception ex) when (CancellableCopy.IsLibraryFailure(ex))
        {
            throw new DamagedDataException(ex);
        }
    }

    /// <summary>書庫ごと読めなかったことを記録する。</summary>
    public void Fail(string name, string reason) => _failed.Add((name, reason));

    public ExtractResult ToResult()
        => new(_extracted, _skippedNames.Count, _rejected, _failed, Cancelled, _marks.Lost, _skippedNames);

    private void Advance(long size, string key)
    {
        _doneBytes += size;
        progress?.Report(new ExtractProgress(_doneBytes, TotalBytes, key));
    }
}
