using System.Buffers;
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
/// <para>
/// <b>CPU のコアを使い切る (#199)。</b> 対策ソフトの判定は、読み終えた中身を
/// <see cref="ScanPool"/> に回してコアの数だけ並べて行う。書庫そのものの判定は、エントリの検査と
/// 同時に裏で進める。ZIP は、取り出しと CRC の照合も作業ごとに書庫を開き直して並べる。
/// 判定は終わる順がまちまちなので、見つかった事柄にはエントリの順番を添え、報告では書庫の順に並べ直す。
/// </para>
/// </remarks>
internal static class ContentInspector
{
    /// <summary>まとめて読む大きさ。判定に回さない場合に使う。</summary>
    private const int ChunkSize = 128 * 1024;

    /// <summary>中身を読み通した結果の数え上げ。</summary>
    /// <param name="Checked">中身まで読んで確かめたファイルの数。</param>
    /// <param name="Scanned">マルウェア検査に渡せたファイルの数。</param>
    public readonly record struct Counts(int Checked, int Scanned);

    public static Counts Inspect(InspectionContext context)
    {
        context.BeginPhase(InspectionPhase.Contents, 0.88);

        // 判定はコアの数だけ並べて行う (#199)。対策ソフトが使えなければ立てない
        using var pool = context.Scanner is { } scanner ? new ScanPool(scanner, context.Cancellation) : null;
        var pass = new Pass(context.Contents.TotalLength, context.Contents.FileCount, pool);

        // 書庫そのものの判定は、エントリの検査と互いに関係が無い。待ち合わせずに裏で進める (#199)。
        // 1 回の呼び出しで分けられず、検査全体の時間をほとんど決めるので、真っ先に始める
        var itself = Task.Factory.StartNew(
            () => ScanArchiveItself(context, pool), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);

        try
        {
            InspectEntries(context, pass);

            // 取り出し終えても、判定がまだ走っていることがある。書庫そのものの判定は特に長く、
            // 小さなファイル 20,000 個の書庫で 30 秒ほどかかる。その間は書庫の名前を出しておく。
            // 経過の知らせは間引かれるので、1 回だけでは出ないことがある
            var archiveName = Path.GetFileName(context.ArchivePath);
            while (Task.WhenAny(itself, Task.Delay(200)).GetAwaiter().GetResult() != itself)
            {
                context.Cancellation.ThrowIfCancellationRequested();
                context.Advance(0.999, archiveName);
            }

            itself.GetAwaiter().GetResult();

            if (pool is not null)
            {
                foreach (var (order, name) in pool.Finish())
                {
                    context.Findings.Add(InspectionIssue.MalwareDetected, name, null, order);
                }
            }
        }
        finally
        {
            // 中断や失敗のときも、裏の判定が受け口を使い終えるのを待ってから戻る。
            // 判定を待つのはすぐやめるので、ここで長く止まることはない
            try
            {
                itself.Wait();
            }
            catch (AggregateException)
            {
            }
        }

        context.Advance(1, string.Empty);
        return new Counts(pass.Checked, pass.Scanned);
    }

    /// <summary>形式ごとの読み方で、エントリを 1 つずつ取り出して調べる。</summary>
    private static void InspectEntries(InspectionContext context, Pass pass)
    {
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
    }

    /// <summary>
    /// 書庫ファイルそのものを1回だけ対策ソフトに渡す (#56)。
    /// </summary>
    /// <remarks>
    /// 対策ソフト側は書庫を自分で開いて中を見る。入れ子の書庫 (zip の中の zip) は
    /// こちらでは開けないため、ここで見てもらう。実測で、ZIP のバイト列をそのまま
    /// 渡した場合も中の検体が検出された。
    /// </remarks>
    private static void ScanArchiveItself(InspectionContext context, ScanPool? pool)
    {
        if (context.Scanner is not { } scanner || pool is null)
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

            // エントリの中身と同じ上限の中で抱える
            pool.Reserve(length);
            try
            {
                var bytes = File.ReadAllBytes(context.ArchivePath);

                if (scanner.Scan(bytes, bytes.Length, name, context.Cancellation))
                {
                    context.Findings.Add(InspectionIssue.MalwareDetected, string.Empty);
                }
            }
            finally
            {
                pool.Release(length);
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

    /// <summary>
    /// ZIP の中身を読む。作業ごとに書庫を開き直し、コアの数だけ並べて読む (#199)。
    /// </summary>
    /// <remarks>
    /// 取り出しと CRC の照合も CPU の仕事で、文書 402 個 (473 MB) で 1.8 秒かかっていた。
    /// 書庫が HDD にあるときは 1 本のまま。読む場所があちこちに飛び、逆に遅くなる (#195 の展開と同じ)。
    /// </remarks>
    private static void InspectZip(InspectionContext context, Pass pass)
    {
        SharpZipFile zip;

        try
        {
            zip = OpenZip(context);
        }
        catch (Exception ex) when (ex is ZipException or IOException or InvalidDataException
                                   or UnauthorizedAccessException or NotSupportedException)
        {
            context.Findings.Add(InspectionIssue.Unreadable, string.Empty, Strings.Reason(ex));
            return;
        }

        using (zip)
        {
            var count = (int)zip.Count;
            var lanes = count >= 2 && Environment.ProcessorCount >= 2
                        && SeekPenalty.Of(context.ArchivePath) == false
                ? Math.Min(Environment.ProcessorCount, count)
                : 1;
            var cursor = -1;

            // 1 本の作業分。空いているエントリを順に取っていく
            void Run(SharpZipFile own)
            {
                // 標準の実装が扱えないエントリ用 (#66、#67)。要るまで開かない
                using var fallback = new ZipMethodFallback(context.ArchivePath, context.Password);

                int index;
                while ((index = Interlocked.Increment(ref cursor)) < count)
                {
                    if (context.Stopped)
                    {
                        return;
                    }

                    InspectZipEntry(context, pass, own, own[index], index, fallback);
                }
            }

            if (lanes == 1)
            {
                Run(zip);
                return;
            }

            var all = Enumerable.Range(0, lanes).Select(lane => Task.Factory.StartNew(() =>
            {
                if (lane == 0)
                {
                    Run(zip);
                    return;
                }

                // 書庫は作業ごとに開く。ひとつの書庫を複数から同時に読むことはできない
                using var own = OpenZip(context);
                Run(own);
            }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

            // すべて終わるのを待ってから、最初の失敗をそのまま投げる
            Task.WhenAll(all).GetAwaiter().GetResult();
        }
    }

    /// <summary>検査のために ZIP を開く。</summary>
    private static SharpZipFile OpenZip(InspectionContext context)
        // 自己解凍書庫は先頭にプログラムが付いている。その分を隠して渡す (#32)
        => new(ZipPrefix.Open(context.ArchivePath), leaveOpen: false)
        {
            Password = context.Password,
            StringCodec = StringCodec.FromEncoding(ZipArchiveReader.EntryNameEncoding),
        };

    private static void InspectZipEntry(
        InspectionContext context, Pass pass, SharpZipFile zip, ZipEntry entry, int order,
        ZipMethodFallback fallback)
    {
        if (!entry.IsFile)
        {
            return;
        }

        var name = ArchiveTreeBuilder.Trim(
            ArchiveFormats.EntryName(context.Contents.Format, entry.Name));
        var size = Math.Max(0, entry.Size);
        pass.Entry(context, name);

        // 合言葉が分からない項目は中身を見られない。
        // 黙って飛ばさず、調べられなかったことを報告する (#56)
        if (entry.IsCrypted && context.Password is null)
        {
            context.Findings.Add(InspectionIssue.EncryptedNotChecked, name, null, order);
            pass.Bytes(context, size, name);
            return;
        }

        // SharpZipLib が扱えない方式 (LZMA、PPMd、Deflate64) や鍵長 (AES-192) は
        // 開き直して読む。読めれば CRC まで確かめられるので、
        // 「検査できなかった」ではなく本当の判定を出せる (#66、#67)
        Read(context, pass, order, name, size, ZipEncryption.ExpectedCrc(entry),
            () => fallback.Open(entry.Name, () => zip.GetInputStream(entry)));
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
                var order = pass.Entry(context, name);
                Read(context, pass, order, name, item.Length, -1, () => item.Open(source));
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
                var order = pass.Entry(context, name);
                Read(context, pass, order, name, size, -1, open);
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
                var order = pass.Entry(context, name);
                Read(context, pass, order, name, entry.Length, -1, open);
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
                var order = pass.Entry(context, name);
                Read(context, pass, order, name, file.Length, -1, open);
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
                var order = pass.Entry(context, name);
                Read(context, pass, order, name, payload.Length, -1, open);
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

                var order = pass.Entry(context, item.Name);
                Read(context, pass, order, item.Name, item.Length, -1, () => MsiExeReader.Slice(source, item));
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
                var order = pass.Entry(context, name);

                if (entry.IsEncrypted && context.Password is null)
                {
                    context.Findings.Add(InspectionIssue.EncryptedNotChecked, name, null, order);
                    pass.Bytes(context, size, name);
                    continue;
                }

                // tar はCRCを持たない。7z は持つ (0 は「無い」の意味で使われる)
                Read(context, pass, order, name, size, entry.Crc == 0 ? -1 : entry.Crc,
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

    /// <summary>
    /// 1件を読み通して、CRCを照合し、対策ソフトの判定に回す。
    /// </summary>
    /// <remarks>
    /// 判定の結果は待たない。判定は <see cref="ScanPool"/> が並べて行い、検出されたものは
    /// 最後にまとめて報告に載せる (#199)。並列で読む ZIP では、どの作業からも呼ばれる。
    /// </remarks>
    /// <param name="order">書庫の中でのエントリの順番。報告をこの順に並べる。</param>
    private static void Read(
        InspectionContext context, Pass pass, long order, string name, long size, long expectedCrc,
        Func<Stream> open)
    {
        var pool = pass.Pool;

        // 上限を超えるものは分割せずに「検査できず」とする。分割すると署名が
        // 境目で切れて見落とすため (#56)
        var scan = pool is not null && size > 0 && size <= AmsiScanner.SizeLimit;

        if (pool is not null && size > AmsiScanner.SizeLimit)
        {
            context.Findings.Add(InspectionIssue.TooLargeToScan, name, Megabytes(size), order);
        }

        // 判定に回す中身の置き場。読む前に確保し、上限を超えるなら判定が進むのを待つ
        long reserved = 0;
        if (scan)
        {
            pool!.Reserve(size);
            reserved = size;
        }

        byte[]? whole = null;

        // 大きさが読むまで分からないもの (size が -1。NSIS の塊ごとの圧縮) は、
        // 読みながら溜めて、上限に収まれば検査に渡す
        var growing = pool is not null && size < 0 ? new MemoryStream() : null;
        long total = 0;

        var crc = new Crc32();
        var filled = 0;
        var chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);

        try
        {
            whole = scan ? new byte[size] : null;
            using var stream = open();

            var buffer = whole ?? chunk;
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
            pool?.Release(reserved);
            throw;
        }
        catch (Exception ex) when (IsReadFailure(ex) || ex is ZipException or OutOfMemoryException)
        {
            pool?.Release(reserved);
            context.Findings.Add(InspectionIssue.Unreadable, name, Strings.Reason(ex), order);
            return;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        pass.CountChecked();

        if (expectedCrc >= 0 && crc.Value != expectedCrc)
        {
            context.Findings.Add(InspectionIssue.CrcMismatch, name, null, order);
        }

        if (size < 0 && pool is not null && total > AmsiScanner.SizeLimit)
        {
            context.Findings.Add(InspectionIssue.TooLargeToScan, name, Megabytes(total), order);
        }

        // 大きさが先に分かったものは whole に、読んでから分かったものは growing に溜まっている。
        // 置き場は判定が終わると返される
        var (data, length) = growing is not null
            ? (growing.GetBuffer(), (int)growing.Length)
            : (whole, filled);
        growing?.Dispose();

        if (pool is null || data is null || length == 0)
        {
            pool?.Release(reserved);
            return;
        }

        if (growing is not null)
        {
            // 読み終えてから大きさが分かった。読んでいる間は上限の外で抱えていた
            pool.Reserve(length);
            reserved = length;
        }

        pass.CountScanned();
        pool.Post(order, name, data, length, reserved);
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
    /// <param name="pool">判定に回す先。対策ソフトが使えない環境では <see langword="null"/>。</param>
    /// <remarks>並列で読む ZIP では、どの作業からも呼ばれる (#199)。</remarks>
    private sealed class Pass(long totalBytes, int fileCount, ScanPool? pool)
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

        private long _order = -1;

        private int _checked;

        private int _scanned;

        /// <summary>判定に回す先。対策ソフトが使えない環境では <see langword="null"/>。</summary>
        public ScanPool? Pool => pool;

        /// <summary>中身まで読んで確かめたファイルの数。</summary>
        public int Checked => Volatile.Read(ref _checked);

        /// <summary>マルウェア検査に渡せたファイルの数。</summary>
        public int Scanned => Volatile.Read(ref _scanned);

        public void CountChecked() => Interlocked.Increment(ref _checked);

        public void CountScanned() => Interlocked.Increment(ref _scanned);

        /// <summary>1件に取り掛かったことを数える。</summary>
        /// <returns>取り掛かった順の番号。報告を書庫の順に並べるのに使う。</returns>
        public long Entry(InspectionContext context, string name)
        {
            Report(context, EntryCost, name);
            return Interlocked.Increment(ref _order);
        }

        /// <summary>読んだ分を数える。調べられなかった分もここを通す。</summary>
        public void Bytes(InspectionContext context, long bytes, string name)
            => Report(context, bytes, name);

        private void Report(InspectionContext context, long work, string name)
        {
            var done = Interlocked.Add(ref _done, work);

            // 途中で 1 に達すると区切りの終わりと見分けが付かなくなる。
            // 見積もりなので、実際より進むことはありうる
            context.Advance(Math.Min(0.999, done / (double)_total), name);
        }
    }
}
