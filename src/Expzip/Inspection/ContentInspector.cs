using System.IO;
using Expzip.Archives;
using Expzip.Localization;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;
using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

// 標準ライブラリにも同じ名前の型があるため、こちら側の名前をはっきりさせる
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Expzip.Inspection;

/// <summary>
/// 書庫の中身を1度だけ読み通し、CRCの照合 (#54) とマルウェア検査 (#56) を同時に行う。
/// </summary>
/// <remarks>
/// <para>
/// どちらも「エントリの中身そのもの」を必要とする。別々に行うと書庫を二度読むことに
/// なるため、1回の読み出しに相乗りさせている。検査の中でいちばん時間がかかるのは
/// ここなので、進捗と中断もここが本番になる。
/// </para>
/// <para>
/// 7z はまとめて圧縮されている (ソリッド) ため、必ず先頭から順に読む。1件ずつ開くと
/// そのたびに同じ塊を復号し直すことになる (実測で370倍。#19)。
/// </para>
/// </remarks>
internal static class ContentInspector
{
    /// <summary>まとめて読む大きさ。マルウェア検査に渡さない場合に使う。</summary>
    private const int ChunkSize = 128 * 1024;

    /// <summary>中身を読み通した結果の数え上げ。</summary>
    /// <param name="Checked">中身まで読んで確かめたファイルの数。</param>
    /// <param name="Scanned">マルウェア検査に渡せたファイルの数。</param>
    public readonly record struct Counts(int Checked, int Scanned);

    public static Counts Inspect(InspectionContext context)
    {
        context.BeginPhase(InspectionPhase.Contents, 0.88);

        var pass = new Pass(context.Contents.TotalLength, context.Contents.FileCount);

        ScanArchiveItself(context);

        if (ArchiveFormats.IsZipBased(context.Contents.Format))
        {
            InspectZip(context, pass);
        }
        else if (context.Contents.Format == ArchiveFormat.Pe)
        {
            InspectPe(context, pass);
        }
        else if (context.Contents.Format == ArchiveFormat.Nsis)
        {
            InspectNsis(context, pass);
        }
        else if (context.Contents.Format is ArchiveFormat.Cab or ArchiveFormat.CabExe)
        {
            InspectCab(context, pass);
        }
        else if (context.Contents.Format == ArchiveFormat.Msi)
        {
            InspectMsi(context, pass);
        }
        else if (context.Contents.Format == ArchiveFormat.Burn)
        {
            InspectBurn(context, pass);
        }
        else if (context.Contents.Format == ArchiveFormat.MsiExe)
        {
            InspectMsiExe(context, pass);
        }
        else
        {
            InspectSharp(context, pass);
        }

        context.Advance(1, string.Empty);
        return new Counts(pass.Checked, pass.Scanned);
    }

    /// <summary>
    /// 書庫ファイルそのものを1回だけ対策ソフトに渡す (#56)。
    /// </summary>
    /// <remarks>
    /// 対策ソフト側は書庫を自分で開いて中を見る。入れ子の書庫 (zip の中の zip) は
    /// こちらでは開けないため、ここで見てもらう。実測で、ZIP のバイト列をそのまま
    /// 渡した場合も中の検体が検出された。
    /// </remarks>
    private static void ScanArchiveItself(InspectionContext context)
    {
        if (context.Scanner is not { } scanner)
        {
            return;
        }

        var name = Path.GetFileName(context.ArchivePath);

        try
        {
            var length = new FileInfo(context.ArchivePath).Length;

            if (length > AmsiScanner.SizeLimit)
            {
                context.Findings.Add(InspectionIssue.TooLargeToScan, string.Empty, Megabytes(length));
                return;
            }

            context.Advance(0, name);
            var bytes = File.ReadAllBytes(context.ArchivePath);

            if (scanner.Scan(bytes, bytes.Length, name, context.Cancellation))
            {
                context.Findings.Add(InspectionIssue.MalwareDetected, string.Empty);
            }
        }
        catch (OutOfMemoryException)
        {
            context.Findings.Add(InspectionIssue.TooLargeToScan, string.Empty);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
        }
    }

    private static void InspectZip(InspectionContext context, Pass pass)
    {
        SharpZipFile zip;

        try
        {
            // 自己解凍書庫は先頭にプログラムが付いている。その分を隠して渡す (#32)
            zip = new SharpZipFile(ZipPrefix.Open(context.ArchivePath), leaveOpen: false)
            {
                Password = context.Password,
                StringCodec = StringCodec.FromEncoding(ZipArchiveReader.EntryNameEncoding),
            };
        }
        catch (Exception ex) when (ex is ZipException or IOException or InvalidDataException
                                   or UnauthorizedAccessException or NotSupportedException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
            return;
        }

        // 標準の実装が扱えないエントリ用 (#66、#67)。要るまで開かない
        using var fallback = new ZipMethodFallback(context.ArchivePath, context.Password);

        using (zip)
        {
            foreach (ZipEntry entry in zip)
            {
                if (context.Stopped)
                {
                    return;
                }

                if (!entry.IsFile)
                {
                    continue;
                }

                var name = ArchiveTreeBuilder.Trim(
                    ArchiveFormats.EntryName(context.Contents.Format, entry.Name));
                var size = Math.Max(0, entry.Size);
                pass.Entry(context, name);

                // 合言葉が分からない項目は中身を見られない。
                // 黙って飛ばさず、調べられなかったことを報告する (#56)
                if (entry.IsCrypted && context.Password is null)
                {
                    context.Findings.Add(InspectionIssue.EncryptedNotChecked, name);
                    pass.Bytes(context, size, name);
                    continue;
                }

                // SharpZipLib が扱えない方式 (LZMA、PPMd、Deflate64) や鍵長 (AES-192) は
                // 開き直して読む。読めれば CRC まで確かめられるので、
                // 「検査できなかった」ではなく本当の判定を出せる (#66、#67)
                Read(context, pass, name, size, ZipEncryption.ExpectedCrc(entry),
                    () => fallback.Open(entry.Name, () => zip.GetInputStream(entry)));
            }
        }
    }

    /// <summary>exe / dll の区画と部品を 1 つずつ読む (#183)。CRC は持たない。</summary>
    private static void InspectPe(InspectionContext context, Pass pass)
    {
        try
        {
            var items = PeReader.ReadLayout(context.ArchivePath, context.Cancellation);
            using var source = ArchiveFile.OpenRead(context.ArchivePath);

            foreach (var item in items)
            {
                if (context.Stopped)
                {
                    return;
                }

                var name = ArchiveTreeBuilder.Trim(item.Path);
                pass.Entry(context, name);
                Read(context, pass, name, item.Length, -1, () => item.Open(source));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
        }
    }

    /// <summary>NSIS インストーラーの中身を 1 つずつ読む (#68)。CRC は持たない。</summary>
    /// <remarks>
    /// 取り出しと同じ道筋で読む。以前は tar の読み方に回っていて、壊れていない
    /// インストーラーでも「中身を読み出せませんでした」と出し、中身を検査していなかった。
    /// </remarks>
    private static void InspectNsis(InspectionContext context, Pass pass)
    {
        try
        {
            var layout = NsisReader.ReadLayout(context.ArchivePath, context.Cancellation);

            NsisExtractor.Visit(context.ArchivePath, layout, layout.Files, (file, size, open) =>
            {
                var name = ArchiveTreeBuilder.Trim(NsisReader.ToArchivePath(file.Name));
                pass.Entry(context, name);
                Read(context, pass, name, size, -1, open);
            }, context.Cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
        }
    }

    /// <summary>CAB と、CAB が入った exe の中身を 1 つずつ読む (#182)。</summary>
    /// <remarks>
    /// CAB は塊ごとに照合の値を持っていて、cabinet.dll が展開しながら確かめる。
    /// 合わなければ読み取りが失敗し、書庫そのものの問題として報告する。
    /// </remarks>
    private static void InspectCab(InspectionContext context, Pass pass)
    {
        try
        {
            CabReader.Visit(context.ArchivePath, null, (entry, open) =>
            {
                var name = ArchiveTreeBuilder.Trim(entry.Name);
                pass.Entry(context, name);
                Read(context, pass, name, entry.Length, -1, open);
            }, context.Cancellation, context.Contents.Format);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex) || ex is OutOfMemoryException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
        }
    }

    /// <summary>MSI の中身を 1 つずつ読む (#182)。取り出しと同じ道筋で、CAB から読む。</summary>
    private static void InspectMsi(InspectionContext context, Pass pass)
    {
        try
        {
            MsiReader.Visit(context.ArchivePath, null, (file, _, open) =>
            {
                var name = ArchiveTreeBuilder.Trim(file.Path);
                pass.Entry(context, name);
                Read(context, pass, name, file.Length, -1, open);
            }, context.Cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex) || ex is OutOfMemoryException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
        }
    }

    /// <summary>WiX Burn でまとめた exe の中身を 1 つずつ読む (#182)。入れ物の CAB から読む。</summary>
    private static void InspectBurn(InspectionContext context, Pass pass)
    {
        try
        {
            BurnReader.Visit(context.ArchivePath, null, (payload, _, open) =>
            {
                var name = ArchiveTreeBuilder.Trim(payload.Path);
                pass.Entry(context, name);
                Read(context, pass, name, payload.Length, -1, open);
            }, context.Cancellation);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex) || ex is OutOfMemoryException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
        }
    }

    /// <summary>MSI が入った exe の、中の MSI を 1 つずつ読む (#182)。CRC は持たない。</summary>
    /// <remarks>中の MSI の中身までは見ない。書庫の中の書庫と同じく、開いた先で検査する。</remarks>
    private static void InspectMsiExe(InspectionContext context, Pass pass)
    {
        try
        {
            var items = MsiExeReader.Find(context.ArchivePath, cancellationToken: context.Cancellation);
            using var source = ArchiveFile.OpenRead(context.ArchivePath);

            foreach (var item in items)
            {
                if (context.Stopped)
                {
                    return;
                }

                pass.Entry(context, item.Name);
                Read(context, pass, item.Name, item.Length, -1, () => MsiExeReader.Slice(source, item));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
        }
    }

    private static void InspectSharp(InspectionContext context, Pass pass)
    {
        IArchive? archive = null;
        Stream? file = null;
        IReader? reader = null;

        try
        {
            if (context.Contents.Format == ArchiveFormat.SevenZip)
            {
                archive = SharpArchiveAccess.OpenSevenZip(context.ArchivePath, context.Password);
                reader = archive.ExtractAllEntries();
            }
            else
            {
                file = ArchiveFile.OpenRead(context.ArchivePath);
                reader = SharpArchiveAccess.OpenTarReader(file);
            }

            while (reader.MoveToNextEntry())
            {
                if (context.Stopped)
                {
                    return;
                }

                var entry = reader.Entry;
                if (entry.IsDirectory || entry.Key is not { } key)
                {
                    continue;
                }

                var name = ArchiveTreeBuilder.Trim(key);
                var size = Math.Max(0, entry.Size);
                pass.Entry(context, name);

                if (entry.IsEncrypted && context.Password is null)
                {
                    context.Findings.Add(InspectionIssue.EncryptedNotChecked, name);
                    pass.Bytes(context, size, name);
                    continue;
                }

                // tar はCRCを持たない。7z は持つ (0 は「無い」の意味で使われる)
                Read(context, pass, name, size, entry.Crc == 0 ? -1 : entry.Crc,
                    reader.OpenEntryStream);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            // 塊の復号に失敗すると、取り出しの手前で例外になる。
            // どのエントリとは言えないため、書庫そのものの問題として報告する
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
        }
        finally
        {
            reader?.Dispose();
            archive?.Dispose();
            file?.Dispose();
        }
    }

    /// <summary>1件を読み通して、CRCを照合し、対策ソフトに渡す。</summary>
    private static void Read(
        InspectionContext context, Pass pass, string name, long size, long expectedCrc,
        Func<Stream> open)
    {
        var scanner = context.Scanner;

        // 上限を超えるものは分割せずに「検査できず」とする。分割すると署名が
        // 境目で切れて見落とすため (#56)
        var scan = scanner is not null && size > 0 && size <= AmsiScanner.SizeLimit;

        if (scanner is not null && size > AmsiScanner.SizeLimit)
        {
            context.Findings.Add(InspectionIssue.TooLargeToScan, name, Megabytes(size));
        }

        var whole = scan ? new byte[size] : null;

        // 大きさが読むまで分からないもの (size が -1。NSIS の塊ごとの圧縮) は、
        // 読みながら溜めて、上限に収まれば検査に渡す
        var growing = scanner is not null && size < 0 ? new MemoryStream() : null;
        long total = 0;

        var crc = new Crc32();
        var filled = 0;

        try
        {
            using var stream = open();

            var buffer = whole ?? pass.Chunk;
            int read;

            while ((read = stream.Read(
                       buffer,
                       whole is null ? 0 : filled,
                       whole is null ? buffer.Length : whole.Length - filled)) > 0)
            {
                crc.Update(new ArraySegment<byte>(buffer, whole is null ? 0 : filled, read));
                filled += read;
                total += read;

                if (growing is not null)
                {
                    if (total <= AmsiScanner.SizeLimit)
                    {
                        growing.Write(buffer, 0, read);
                    }
                    else
                    {
                        growing.Dispose();
                        growing = null;
                    }
                }

                pass.Bytes(context, read, name);
                context.Cancellation.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex) || ex is ZipException or OutOfMemoryException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, name, Strings.Reason(ex));
            return;
        }

        pass.Checked++;

        if (expectedCrc >= 0 && crc.Value != expectedCrc)
        {
            context.Findings.Add(InspectionIssue.CrcMismatch, name);
        }

        if (size < 0 && scanner is not null && total > AmsiScanner.SizeLimit)
        {
            context.Findings.Add(InspectionIssue.TooLargeToScan, name, Megabytes(total));
        }

        using (growing)
        {
            // 大きさが先に分かったものは whole に、読んでから分かったものは growing に溜まっている
            var (data, length) = growing is not null
                ? (growing.GetBuffer(), (int)growing.Length)
                : (whole, filled);

            if (data is null || length == 0 || scanner is null)
            {
                return;
            }

            pass.Scanned++;

            if (scanner.Scan(data, length, name, context.Cancellation))
            {
                context.Findings.Add(InspectionIssue.MalwareDetected, name);
            }
        }
    }

    /// <summary>大きさを MB で表した文字。上限を超えたことを伝えるのに使う。</summary>
    private static string Megabytes(long bytes) => $"{bytes / (1024.0 * 1024.0):N0} MB";

    private static bool IsReadFailure(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException
            or InvalidFormatException or ArchiveOperationException
            or IOException or InvalidDataException or NotSupportedException;

    /// <summary>読み通しの途中経過。</summary>
    /// <param name="totalBytes">書庫に入っている中身の合計 (展開後)。</param>
    /// <param name="fileCount">書庫に入っているファイルの数。</param>
    private sealed class Pass(long totalBytes, int fileCount)
    {
        /// <summary>
        /// 1件あたりの固定費を、読むバイト数に置き換えた見積もり。
        /// </summary>
        /// <remarks>
        /// マルウェア検査は実測で1件あたり約0.8ms、大きなバッファでは約700MB/秒。
        /// つまり1件を相手にするだけで、0.5MB ほど読むのと同じだけかかる。
        /// 小さなファイルが何万件も入った書庫は、合計しても数MBにしかならないため、
        /// 読んだバイト数だけで測ると進み具合がほとんど動かないまま何分も待つことになる。
        /// </remarks>
        private const long EntryCost = 512 * 1024;

        private readonly long _total = Math.Max(1, totalBytes + ((long)fileCount * EntryCost));

        private long _done;

        /// <summary>マルウェア検査に渡さない場合に使い回す読み取り用の場所。</summary>
        public byte[] Chunk { get; } = new byte[ChunkSize];

        /// <summary>中身まで読んで確かめたファイルの数。</summary>
        public int Checked { get; set; }

        /// <summary>マルウェア検査に渡せたファイルの数。</summary>
        public int Scanned { get; set; }

        /// <summary>1件に取り掛かったことを数える。</summary>
        public void Entry(InspectionContext context, string name)
            => Report(context, EntryCost, name);

        /// <summary>読んだ分を数える。調べられなかった分もここを通す。</summary>
        public void Bytes(InspectionContext context, long bytes, string name)
            => Report(context, bytes, name);

        private void Report(InspectionContext context, long work, string name)
        {
            _done += work;

            // 途中で 1 に達すると区切りの終わりと見分けが付かなくなる。
            // 見積もりなので、実際より進むことはありうる
            context.Advance(Math.Min(0.999, _done / (double)_total), name);
        }
    }
}
