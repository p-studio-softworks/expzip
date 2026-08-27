using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;

// 標準ライブラリにも同じ名前の型があるため、こちら側の名前をはっきりさせる
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Expzip.Archives;

/// <summary>パスワード付きZIPの扱い (#20)。</summary>
/// <remarks>
/// <para>
/// <see cref="System.IO.Compression"/> は暗号化されたZIPを復号できない。一覧
/// (エントリ名や大きさ) は読めるが、中身を開こうとすると「対応していない圧縮方式」
/// として例外になる。AES暗号化のエントリが圧縮方式99として記録されているため。
/// </para>
/// <para>
/// そこで読み書きの両方に対応している SharpZipLib (MIT) を、暗号化された書庫の
/// ときだけ使う。判定と展開の約束は通常のZIPと揃える。
/// </para>
/// </remarks>
internal static class ZipEncryption
{
    /// <summary>
    /// 書庫が暗号化されているかを調べる。
    /// </summary>
    /// <remarks>
    /// 中央ディレクトリをもう一度なめることになるため、いつでも行うわけにはいかない
    /// (30万件で0.3秒)。標準ライブラリで開けなかったときだけ呼ぶこと。
    /// </remarks>
    public static ZipEncryptionInfo Inspect(string path)
    {
        try
        {
            using var zip = OpenSharp(path);

            var files = 0;
            var encrypted = 0;
            var aes = false;

            foreach (ZipEntry entry in zip)
            {
                // フォルダのエントリは暗号化されない。数に入れると
                // 「一部だけ暗号化」と見誤る
                if (!entry.IsFile)
                {
                    continue;
                }

                files++;

                if (!entry.IsCrypted)
                {
                    continue;
                }

                encrypted++;
                aes |= entry.AESKeySize > 0;
            }

            return new ZipEncryptionInfo(encrypted > 0, encrypted, files, aes);
        }
        catch (Exception ex) when (ex is ZipException or IOException or UnauthorizedAccessException
                                   or InvalidDataException or NotSupportedException)
        {
            return new ZipEncryptionInfo(false, 0, 0, false);
        }
    }

    /// <summary>
    /// 暗号化されているエントリの名前を集める (#20)。
    /// </summary>
    /// <remarks>
    /// 中央ディレクトリをもう一度なめるうえ、エントリ名をもう一組抱えることになる。
    /// 一部だけが暗号化された書庫 (<see cref="ZipEncryptionInfo.IsPartial"/>) のときだけ
    /// 呼ぶこと。書庫まるごと暗号化されている場合は、名前を引き当てるまでもない。
    /// </remarks>
    public static HashSet<string> CollectEncryptedNames(string path)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            using var zip = OpenSharp(path);

            foreach (ZipEntry entry in zip)
            {
                if (entry.IsFile && entry.IsCrypted)
                {
                    names.Add(ArchiveTreeBuilder.Trim(entry.Name));
                }
            }
        }
        catch (Exception ex) when (ex is ZipException or IOException or UnauthorizedAccessException
                                   or InvalidDataException or NotSupportedException)
        {
            // 調べられなければ印を付けないだけ。書庫は開けている
        }

        return names;
    }

    /// <summary>
    /// 標準ライブラリで中身を読めるかどうかを、先頭の1件だけ試して確かめる。
    /// </summary>
    /// <remarks>
    /// 全件を調べると大きな書庫で費用がかさむ。暗号化はふつう書庫全体に掛かるため、
    /// 1件で足りる。混在している書庫は、実際に読めなかった時点で気付ける。
    /// </remarks>
    public static bool CanReadContent(ZipArchive zip)
    {
        var first = zip.Entries.FirstOrDefault(
            static e => e.Length > 0 && !e.FullName.EndsWith('/') && !e.FullName.EndsWith('\\'));

        if (first is null)
        {
            return true;
        }

        try
        {
            using var stream = first.Open();
            return true;
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>パスワードが合っているかを確かめる。</summary>
    public static bool TestPassword(string path, string password)
    {
        try
        {
            using var zip = OpenSharp(path, password);

            var entry = zip.Cast<ZipEntry>().FirstOrDefault(e => e.IsFile && e.Size > 0);
            if (entry is null)
            {
                return true;
            }

            // AES は復号の前に検査用の値を照合するため、開けた時点で合っている。
            // 旧方式 (ZipCrypto) は1バイト読むまで分からないので、実際に読む
            using var stream = zip.GetInputStream(entry);
            return stream.ReadByte() >= 0;
        }
        catch (Exception ex) when (ex is ZipException or IOException or InvalidDataException
                                   or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// パスワード付きZIPから展開する。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。
    /// </summary>
    public static ExtractResult Extract(
        string archivePath,
        string password,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        bool overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null)
    {
        var destinationRoot = Path.GetFullPath(destinationDirectory);

        var extracted = 0;
        var skipped = 0;
        var rejected = new List<string>();
        var failed = new List<(string, string)>();
        var cancelled = false;

        using var zip = OpenSharp(archivePath, password);

        // SharpZipLib が扱えない鍵長 (AES-192) のエントリ用 (#67)。要るまで開かない
        using var fallback = new ZipMethodFallback(archivePath, password);

        var targets = zip.Cast<ZipEntry>()
            .Where(e => e.IsFile)
            .Where(e => sourceNames is null || sourceNames.Contains(e.Name))
            .ToList();

        var totalBytes = targets.Sum(static e => Math.Max(0, e.Size));
        long doneBytes = 0;

        foreach (var entry in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var relative = ArchivePath.ToSafeRelativePath(
                ArchiveExtractor.StripBase(ArchiveTreeBuilder.Trim(entry.Name), basePath));

            if (relative is null)
            {
                rejected.Add(entry.Name);
                continue;
            }

            var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));

            // 書庫の外へ書き出そうとするエントリを弾く (Zip Slip 対策)
            if (!ArchiveExtractor.IsInside(destinationRoot, target))
            {
                rejected.Add(entry.Name);
                continue;
            }

            try
            {
                if (File.Exists(target) && !overwrite)
                {
                    skipped++;
                    doneBytes += Math.Max(0, entry.Size);
                    continue;
                }

                var directory = Path.GetDirectoryName(target);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                using (var source = fallback.Open(entry.Name, () => zip.GetInputStream(entry)))
                using (var destination = new FileStream(
                           target, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    CancellableCopy.Copy(source, destination, cancellationToken);
                }

                ArchiveExtractor.ApplyStamp(target, entry.DateTime, zoneIdentifier);
                extracted++;
            }
            catch (OperationCanceledException)
            {
                ArchiveExtractor.TryDelete(target);
                cancelled = true;
                break;
            }
            catch (Exception ex) when (ex is ZipException or IOException or UnauthorizedAccessException
                                       or ArgumentException or NotSupportedException
                                       or PathTooLongException or InvalidDataException)
            {
                failed.Add((entry.Name, ex.Message));
            }

            doneBytes += Math.Max(0, entry.Size);
            progress?.Report(new ExtractProgress(doneBytes, totalBytes, entry.Name));
        }

        return new ExtractResult(extracted, skipped, rejected, failed, cancelled);
    }

    /// <summary>
    /// SharpZipLib 側の書庫を、一覧と同じ名前の読み方で開く。
    /// </summary>
    /// <remarks>
    /// EFSフラグが立たないエントリ名の解釈に、ZIP の一覧で使っているのと同じ
    /// 判定器 (#13) を渡す。こうしておかないと、従来の日本語書庫で名前が食い違い、
    /// 取り出す対象を引き当てられなくなる。
    /// </remarks>
    private static SharpZipFile OpenSharp(string path, string? password = null)
        => new(path)
        {
            Password = password,
            StringCodec = StringCodec.FromEncoding(ZipArchiveReader.EntryNameEncoding),
        };
}

/// <summary>書庫の暗号化の状態。</summary>
/// <param name="IsEncrypted">暗号化されたエントリを含むか。</param>
/// <param name="EncryptedCount">暗号化されたエントリの数。</param>
/// <param name="FileCount">フォルダを除いたエントリの数。</param>
/// <param name="UsesAes">AES で暗号化されているか。false の場合は旧方式 (ZipCrypto)。</param>
internal readonly record struct ZipEncryptionInfo(
    bool IsEncrypted, int EncryptedCount, int FileCount, bool UsesAes)
{
    /// <summary>
    /// 一部のエントリだけが暗号化されているか (#20)。
    /// この場合だけ、どのエントリが保護されているかを名前で引き当てる必要がある。
    /// </summary>
    public bool IsPartial => EncryptedCount > 0 && EncryptedCount < FileCount;
}
