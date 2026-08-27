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
/// そのたびに同じ塊を復号し直すことになる (#19 の実測で370倍)。
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

        if (context.Contents.Format == ArchiveFormat.Zip)
        {
            InspectZip(context, pass);
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

            if (scanner.Scan(bytes, bytes.Length, name))
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
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, ex.Message);
        }
    }

    private static void InspectZip(InspectionContext context, Pass pass)
    {
        SharpZipFile zip;

        try
        {
            zip = new SharpZipFile(context.ArchivePath)
            {
                Password = context.Password,
                StringCodec = StringCodec.FromEncoding(ZipArchiveReader.EntryNameEncoding),
            };
        }
        catch (Exception ex) when (ex is ZipException or IOException or InvalidDataException
                                   or UnauthorizedAccessException or NotSupportedException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, ex.Message);
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

                var name = ArchiveTreeBuilder.Trim(entry.Name);
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
                Read(context, pass, name, size, ExpectedCrc(entry),
                    () => fallback.Open(entry.Name, () => zip.GetInputStream(entry)));
            }
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
                file = File.OpenRead(context.ArchivePath);
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
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, ex.Message);
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
            context.Findings.Add(InspectionIssue.Unreadable, name, ex.Message);
            return;
        }

        pass.Checked++;

        if (expectedCrc >= 0 && crc.Value != expectedCrc)
        {
            context.Findings.Add(InspectionIssue.CrcMismatch, name);
        }

        if (whole is null || scanner is null)
        {
            return;
        }

        pass.Scanned++;

        if (scanner.Scan(whole, filled, name))
        {
            context.Findings.Add(InspectionIssue.MalwareDetected, name);
        }
    }

    /// <summary>
    /// 突き合わせに使う CRC。分からない場合は -1。
    /// </summary>
    /// <remarks>
    /// WinZip AES (AE-2) は仕様として CRC の欄を 0 で書く。復号しないと元の値が
    /// 分からないため、書庫には残さない決まりになっている。そのまま突き合わせると
    /// パスワード付き書庫のすべてが「壊れている」ことになってしまう。
    /// </remarks>
    private static long ExpectedCrc(ZipEntry entry)
        => entry.AESKeySize > 0 && entry.Crc == 0 ? -1 : entry.Crc;

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
