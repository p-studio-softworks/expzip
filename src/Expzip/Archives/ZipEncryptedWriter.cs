using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;

using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Expzip.Archives;

/// <summary>
/// パスワード付きZIPを書き換える (#20)。
/// </summary>
/// <remarks>
/// <para>
/// SharpZipLib は書庫を作るときにだけ AES で暗号化できる。既にある書庫へ
/// 暗号化したエントリを足すことはできない (「Creation of AES encrypted entries is
/// not supported」)。そのため、**どの操作も書庫を作り直す**形になる。
/// </para>
/// <para>
/// 作り直しでは、残すエントリを一度復号してから暗号化し直す。暗号化されていない
/// ZIP の追加や削除が「触らないエントリをそのまま持ち越す」のに比べて費用は高いが、
/// パスワード付きの書庫を書き換えるにはこれしかない。
/// </para>
/// <para>
/// 元の書庫を直接書き換えず、作業用ファイルに書いてから差し替えるのは
/// <see cref="ZipArchiveWriter"/> と同じ。途中で失敗しても元の書庫は無傷で残る。
/// </para>
/// </remarks>
internal static class ZipEncryptedWriter
{
    /// <summary>WinZip AES の鍵長。フェーズ3で作るのは256ビットのみ。</summary>
    private const int AesKeySize = 256;

    /// <summary>ファイルを追加する。引数の意味は <see cref="ZipArchiveWriter.Add"/> と同じ。</summary>
    public static AddResult Add(
        string archivePath,
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        bool replaceExisting,
        string password,
        CompressionLevel compressionLevel,
        IProgress<AddProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = ZipArchiveWriter.BuildPlan(sourcePaths, destinationFolder);
        var additions = plan.Where(static p => !p.IsDirectory).ToList();
        var totalBytes = additions.Sum(static p => p.Length);
        long doneBytes = 0;

        var added = 0;
        var replaced = 0;
        var skipped = 0;
        var failed = new List<(string, string)>();

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var result = Rewrite(
            archivePath, password, compressionLevel, cancellationToken,
            keep: name =>
            {
                // 同じ名前が来る場合、置き換えるなら古い方を落とす
                var collides = additions.Any(
                    p => string.Equals(p.EntryName, name, StringComparison.OrdinalIgnoreCase));

                if (!collides)
                {
                    return name;
                }

                if (replaceExisting)
                {
                    taken.Add(name);
                    return null;
                }

                taken.Add(name);
                return name;
            },
            append: writer =>
            {
                foreach (var item in plan)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (item.IsDirectory)
                    {
                        writer.WriteFolder(item.EntryName);
                        continue;
                    }

                    var existed = taken.Contains(item.EntryName);
                    if (existed && !replaceExisting)
                    {
                        skipped++;
                        doneBytes += item.Length;
                        continue;
                    }

                    try
                    {
                        using var source = File.OpenRead(item.SourcePath);
                        writer.WriteFile(item.EntryName, source, ReadLastWriteTime(item.SourcePath));

                        if (existed)
                        {
                            replaced++;
                        }
                        else
                        {
                            added++;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException
                                               or PathTooLongException)
                    {
                        // 1件の失敗で全体を止めない。まとめて報告する
                        failed.Add((item.SourcePath, ex.Message));
                    }

                    doneBytes += item.Length;
                    progress?.Report(new AddProgress(doneBytes, totalBytes, item.EntryName));
                }
            });

        return result
            ? new AddResult(added, replaced, skipped, failed, Cancelled: false)
            : new AddResult(0, 0, 0, failed, Cancelled: true);
    }

    /// <summary>エントリを削除する。引数の意味は <see cref="ZipArchiveWriter.Delete"/> と同じ。</summary>
    public static DeleteResult Delete(
        string archivePath,
        IReadOnlySet<string> fileEntryNames,
        IReadOnlyList<string> folderPaths,
        string password,
        CancellationToken cancellationToken)
    {
        var deleted = 0;

        var result = Rewrite(
            archivePath, password, CompressionLevel.Optimal, cancellationToken,
            keep: name =>
            {
                if (!ZipArchiveWriter.ShouldDelete(name, fileEntryNames, folderPaths))
                {
                    return name;
                }

                deleted++;
                return null;
            });

        return result ? new DeleteResult(deleted, false) : new DeleteResult(0, true);
    }

    /// <summary>名前の変更と移動。引数の意味は <see cref="ZipArchiveWriter.Move"/> と同じ。</summary>
    public static RenameResult Move(
        string archivePath,
        IReadOnlyList<PathChange> changes,
        string password,
        CompressionLevel compressionLevel,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var renamed = 0;

        var result = Rewrite(
            archivePath, password, compressionLevel, cancellationToken,
            keep: name =>
            {
                if (ZipArchiveWriter.MapAny(name, changes) is not { } mapped)
                {
                    return name;
                }

                renamed++;
                progress?.Report(renamed);
                return mapped;
            });

        return result ? new RenameResult(renamed, false) : new RenameResult(0, true);
    }

    /// <summary>
    /// 空のフォルダを作る。引数の意味は <see cref="ZipArchiveWriter.CreateFolder"/> と同じ。
    /// </summary>
    public static bool CreateFolder(string archivePath, string folderPath, string password)
    {
        var entryName = ArchivePath.Normalize(folderPath) + "/";
        var taken = false;

        var result = Rewrite(
            archivePath, password, CompressionLevel.Optimal, CancellationToken.None,
            keep: name =>
            {
                // 同じ名前のフォルダが既にあるか、その配下に何かあるか
                taken |= ArchivePath.Normalize(name)
                    .StartsWith(entryName, StringComparison.OrdinalIgnoreCase);

                return name;
            },
            append: writer =>
            {
                if (!taken)
                {
                    writer.WriteFolder(entryName);
                }
            });

        return result && !taken;
    }

    /// <summary>
    /// 書庫を作り直す。
    /// </summary>
    /// <param name="keep">
    /// 残すエントリを決める。元の名前を渡し、そのまま残すなら同じ名前、
    /// 名前を変えるなら新しい名前、落とすなら <see langword="null"/> を返す。
    /// </param>
    /// <param name="append">作り直しの最後に足すもの。</param>
    /// <returns>書き換えられた場合は true。中断した場合は false。</returns>
    private static bool Rewrite(
        string archivePath,
        string password,
        CompressionLevel compressionLevel,
        CancellationToken cancellationToken,
        Func<string, string?> keep,
        Action<EncryptedZipWriter>? append = null)
    {
        var temp = archivePath + ZipArchiveWriter.TempSuffix;

        try
        {
            using (var source = OpenSharp(archivePath, password))
            using (var stream = File.Create(temp))
            using (var output = new ZipOutputStream(stream))
            {
                output.SetLevel(ToSharpLevel(compressionLevel));
                output.Password = password;
                output.UseZip64 = UseZip64.Dynamic;

                var writer = new EncryptedZipWriter(output);

                foreach (ZipEntry entry in source)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (keep(entry.Name) is not { } name)
                    {
                        continue;
                    }

                    if (entry.IsDirectory)
                    {
                        writer.WriteFolder(name);
                        continue;
                    }

                    // 残すエントリも一度復号して暗号化し直す。SharpZipLib には
                    // 暗号化されたままのデータを移す手立てが無い
                    using var content = source.GetInputStream(entry);
                    writer.WriteFile(name, content, entry.DateTime);
                }

                append?.Invoke(writer);
            }

            File.Move(temp, archivePath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            // 中断したときは差し替えない。元の書庫はそのまま
            return false;
        }
        finally
        {
            ZipArchiveWriter.TryDelete(temp);
        }
    }

    /// <summary>暗号化しながら書き出す。</summary>
    private sealed class EncryptedZipWriter(ZipOutputStream output)
    {
        public void WriteFolder(string entryName)
        {
            var name = entryName.TrimEnd('/') + "/";
            output.PutNextEntry(new ZipEntry(name) { DateTime = DateTime.Now, IsUnicodeText = true });
            output.CloseEntry();
        }

        public void WriteFile(string entryName, Stream content, DateTime lastWriteTime)
        {
            output.PutNextEntry(new ZipEntry(entryName)
            {
                DateTime = lastWriteTime,
                AESKeySize = AesKeySize,
                IsUnicodeText = true,
                CompressionMethod = CompressionMethod.Deflated,
            });

            content.CopyTo(output);
            output.CloseEntry();
        }
    }

    /// <summary>読み書きの両方で、一覧と同じ名前の読み方を使う。</summary>
    private static SharpZipFile OpenSharp(string path, string password)
        => new(path)
        {
            Password = password,
            StringCodec = StringCodec.FromEncoding(ZipArchiveReader.EntryNameEncoding),
        };

    /// <summary>圧縮の強さを SharpZipLib の 0〜9 に読み替える。</summary>
    private static int ToSharpLevel(CompressionLevel level) => level switch
    {
        CompressionLevel.NoCompression => 0,
        CompressionLevel.Fastest => 1,
        CompressionLevel.SmallestSize => 9,
        _ => 6,
    };

    /// <summary>元ファイルの更新日時。読めない場合は現在時刻。</summary>
    private static DateTime ReadLastWriteTime(string path)
    {
        try
        {
            return File.GetLastWriteTime(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException)
        {
            return DateTime.Now;
        }
    }
}
