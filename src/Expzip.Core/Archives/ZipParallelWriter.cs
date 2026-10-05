using System.Buffers;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using Expzip.Localization;
using ICSharpCode.SharpZipLib.Checksum;
using ICSharpCode.SharpZipLib.Zip;
using PlanItem = Expzip.Archives.ZipArchiveWriter.PlanItem;

// 標準ライブラリにも同じ名前の型があるため、こちら側の名前をはっきりさせる
using SharpCompressionMethod = ICSharpCode.SharpZipLib.Zip.CompressionMethod;
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Expzip.Archives;

/// <summary>
/// ZIP 書庫へのファイルの追加を、CPU のコアを使い切って行う (#195)。
/// </summary>
/// <remarks>
/// <para>
/// <b>なぜ分けたか。</b> SharpZipLib の書き換え (<see cref="ZipUpdate"/>) は、ファイルを
/// 1 つずつ順に圧縮する。使うコアは 1 つだけで、20 コアの PC なら 19 コアが空く。
/// 圧縮はほとんどが CPU の仕事なので、ここを並列にすると大きく速くなる。
/// 文書のようなファイル 400 個 (計 459 MB) で、12.2 秒が 0.7 秒ほどになる。
/// 大きなファイルは区切りごとに分けて並列に圧縮する (#196、<see cref="PackInPieces"/>)。
/// 大きなファイル 1 つだけのときも、コアを使い切る。
/// </para>
/// <para>
/// <b>手順。</b> 各ファイルの圧縮をコアの数だけ同時に行い、書庫への書き込みは元の順に
/// 1 本で行う。SharpZipLib の書き換えには圧縮済みのデータを渡せないため、書庫は
/// <see cref="ZipOutputStream"/> で作り直す。元からある項目は、圧縮済みのデータを
/// そのまま写す。圧縮し直さないので、圧縮後の大きさも CRC も変わらない。
/// </para>
/// <para>
/// <b>元の書庫は最後まで触らない。</b> 作業用ファイルに書き終えてから差し替える。
/// 途中で中断や失敗が起きても、元の書庫はそのまま残る。
/// </para>
/// <para>
/// <b>写せない項目がある書庫は扱わない。</b> 暗号化された項目、Deflate と格納以外の
/// 方式の項目、UTF-8 として読めない名前 (古い日本語の書庫、#13)、先頭にプログラムが
/// 付いた書庫 (#32) は、<see langword="null"/> を返してこれまでの書き換えに任せる。
/// 写す途中で中身の食い違いに気づいた場合も同じ。
/// </para>
/// </remarks>
internal static class ZipParallelWriter
{
    /// <summary>
    /// 圧縮を終えて書き込みを待つデータの上限。これを超えないところまで先に圧縮しておく。
    /// </summary>
    private const long HoldLimit = 256L * 1024 * 1024;

    /// <summary>
    /// これより大きいファイルは、圧縮したデータをメモリーではなく一時ファイルに置く。
    /// </summary>
    private const long InMemoryLimit = 16L * 1024 * 1024;

    /// <summary>
    /// 一時ファイルに置いて書き込みを待つデータの上限。大きなファイルばかりのときに、
    /// 一時フォルダーのあるディスクを埋めないため。
    /// </summary>
    private const long SpillLimit = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// これより大きいファイルは、区切りごとに分けて並列に圧縮する (#196)。
    /// 一時ファイルに置く大きさと同じにしておく。
    /// </summary>
    private const long SplitThreshold = InMemoryLimit;

    /// <summary>
    /// 区切りの大きさ。小さいほど継ぎ目が増えて縮みにくくなる。512 MB のファイルで測ると、
    /// 1 本で圧縮したときより 0.14% (実行ファイル) から 0.54% (文書) 大きくなる。
    /// 2 MB にしても速さは変わらないが、16 MB のファイルを 8 つにしか分けられない。
    /// </summary>
    private const int PieceSize = 1024 * 1024;

    /// <summary>
    /// 中身の無い最後のブロック (固定ハフマン符号、最終の印付き)。
    /// ファイルの大きさが区切りのちょうど倍数のときに、データの終わりを示すのに使う。
    /// </summary>
    private static readonly byte[] EmptyFinalBlock = [0x03, 0x00];

    /// <summary>読み書きの単位。<see cref="Stream.CopyTo(Stream)"/> のデフォルトと同じ大きさ。</summary>
    private const int BufferSize = 81920;

    /// <summary>局所ヘッダーの署名。</summary>
    private const uint LocalSignature = 0x04034b50;

    /// <summary>局所ヘッダーの固定部の長さ。</summary>
    private const int LocalHeaderLength = 30;

    /// <summary>終端レコードの署名。</summary>
    private const uint EndSignature = 0x06054b50;

    /// <summary>終端レコードの固定部の長さ。</summary>
    private const int EndLength = 22;

    /// <summary>
    /// ディスク上のファイルやフォルダーを書庫に追加する。引数の意味は <see cref="ZipArchiveWriter.Add"/> と同じ。
    /// </summary>
    /// <returns>この方法で扱えない書庫なら <see langword="null"/>。</returns>
    public static AddResult? TryAdd(
        string archivePath,
        IReadOnlyList<PlanItem> plan,
        bool replaceExisting,
        IProgress<AddProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (ZipPrefix.Detect(archivePath) != 0)
        {
            return null;
        }

        var temp = archivePath + ZipArchiveWriter.TempSuffix;
        var failed = new List<(string, string)>();

        try
        {
            int added;
            int replaced;
            int skipped;

            using (var source = ZipUpdate.Open(archivePath))
            {
                var existing = source.Cast<ZipEntry>().ToList();
                if (!existing.All(CanCarry))
                {
                    return null;
                }

                // 置き換えと飛ばしは、書き始める前に決めておく
                var byName = new Dictionary<string, ZipEntry>(StringComparer.Ordinal);
                foreach (var entry in existing)
                {
                    byName.TryAdd(entry.Name, entry);
                }

                var dropped = new HashSet<ZipEntry>(ReferenceEqualityComparer.Instance);
                var incoming = new List<(PlanItem Item, bool Replaces)>();
                skipped = 0;

                foreach (var item in plan)
                {
                    if (byName.TryGetValue(item.EntryName, out var old))
                    {
                        if (!replaceExisting)
                        {
                            skipped++;
                            continue;
                        }

                        dropped.Add(old);
                    }

                    incoming.Add((item, old is not null));
                }

                cancellationToken.ThrowIfCancellationRequested();

                using var raw = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var verbatim = new VerbatimText();

                using var stream = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                using var output = new ZipOutputStream(stream, StringCodec.FromEncoding(verbatim))
                {
                    IsStreamOwner = false,
                    UseZip64 = UseZip64.Dynamic,

                    // 名前は書庫にあるとおりに写す。手を加えると、元の名前と変わってしまう
                    NameTransform = null,
                };

                if (!string.IsNullOrEmpty(source.ZipFileComment))
                {
                    // 書庫のコメントには文字コードの印が無い。元のバイト列のまま写す
                    if (ReadComment(raw) is { } comment)
                    {
                        verbatim.Remember(source.ZipFileComment, comment);
                    }

                    output.SetComment(source.ZipFileComment);
                }

                foreach (var entry in existing)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!dropped.Contains(entry))
                    {
                        Carry(raw, entry, output, cancellationToken);
                    }
                }

                var gauge = new Gauge(incoming.Sum(static x => x.Item.Length), progress);
                (added, replaced) = WriteIncoming(output, incoming, failed, gauge, cancellationToken);

                output.Finish();
            }

            // 書庫を閉じてから差し替える。開いたままでは置き換えられない
            File.Move(temp, archivePath, overwrite: true);
            return new AddResult(added, replaced, skipped, failed, Cancelled: false);
        }
        catch (OperationCanceledException)
        {
            return new AddResult(0, 0, 0, failed, Cancelled: true);
        }
        catch (Exception ex) when (ex is ZipException or CarryException)
        {
            // 元の書庫の中身が食い違っていた。これまでの書き換えなら扱える
            return null;
        }
        finally
        {
            ZipArchiveWriter.TryDelete(temp);
        }
    }

    /// <summary>圧縮済みのデータをそのまま写せる項目か。</summary>
    private static bool CanCarry(ZipEntry entry)
        => !entry.IsCrypted
           && entry.AESKeySize == 0
           && entry.CompressionMethod is SharpCompressionMethod.Stored or SharpCompressionMethod.Deflated;

    /// <summary>元の書庫の項目を、圧縮し直さずに写す。</summary>
    private static void Carry(Stream raw, ZipEntry entry, ZipOutputStream output, CancellationToken cancellationToken)
    {
        // 中身の位置は局所ヘッダーの後ろ。名前と追加情報の長さは、中央ディレクトリと
        // 違うことがあるので局所ヘッダーから読む
        Span<byte> header = stackalloc byte[LocalHeaderLength];
        raw.Position = entry.Offset;
        raw.ReadExactly(header);

        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != LocalSignature)
        {
            throw new CarryException();
        }

        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);

        if (!entry.IsUnicodeText)
        {
            // UTF-8 の印が無く、UTF-8 として読めない名前 (古い日本語の書庫、#13) は写せない。
            // ZipOutputStream は、印の無い名前を中央ディレクトリには必ず UTF-8 で書くので、
            // 局所ヘッダーと食い違い、ほかのツールでは化けてしまう。これまでの書き換えに任せる。
            // UTF-8 として読める名前 (英数字だけの名前など) は、書き戻しても同じバイト列になる
            var name = new byte[nameLength];
            raw.ReadExactly(name);

            if (!System.Text.Unicode.Utf8.IsValid(name))
            {
                throw new CarryException();
            }
        }

        raw.Position = entry.Offset + LocalHeaderLength + nameLength + extraLength;

        var copy = (ZipEntry)entry.Clone();

        if (entry.CompressionMethod == SharpCompressionMethod.Deflated)
        {
            output.PutNextPassthroughEntry(copy);
        }
        else
        {
            // 格納の項目は書き込みながら CRC が照合される
            output.PutNextEntry(copy);
        }

        CopyExactly(raw, output, entry.CompressedSize, cancellationToken);
        output.CloseEntry();
    }

    /// <summary>書庫のコメントを、バイト列のまま読む。見つからなければ <see langword="null"/>。</summary>
    private static byte[]? ReadComment(Stream raw)
    {
        // 終端レコードは書庫の末尾にあり、その後ろにコメントが続く
        var span = (int)Math.Min(raw.Length, EndLength + 0xFFFF);
        var tail = new byte[span];
        raw.Position = raw.Length - span;
        raw.ReadExactly(tail);

        for (var i = span - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == EndSignature
                && i + EndLength + BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) == span)
            {
                return tail[(i + EndLength)..];
            }
        }

        return null;
    }

    /// <summary>決まった量だけ写す。足りなければ書庫が途中で切れている。</summary>
    private static void CopyExactly(Stream source, Stream destination, long length, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            while (length > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, length));
                if (read == 0)
                {
                    throw new CarryException();
                }

                destination.Write(buffer, 0, read);
                length -= read;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 追加するファイルを並列に圧縮し、元の順に書き込む。
    /// </summary>
    /// <returns>新しく追加した数と、置き換えた数。</returns>
    private static (int Added, int Replaced) WriteIncoming(
        ZipOutputStream output,
        List<(PlanItem Item, bool Replaces)> incoming,
        List<(string, string)> failed,
        Gauge gauge,
        CancellationToken cancellationToken)
    {
        var added = 0;
        var replaced = 0;

        // 書き込みで失敗したときに、先に走らせた圧縮も止める
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = stop.Token;

        // 先に圧縮しておく分。コアの数の 2 倍まで走らせ、書き込みを待たせない
        var ahead = new Queue<(PlanItem Item, bool Replaces, Task<Packed> Task, Hold Held)>();
        var lanes = Environment.ProcessorCount * 2;
        var held = default(Hold);
        var next = 0;

        try
        {
            while (next < incoming.Count || ahead.Count > 0)
            {
                // 圧縮済みのデータを抱えすぎない。ただし 1 件も走っていなければ、大きくても始める
                while (next < incoming.Count && ahead.Count < lanes)
                {
                    var (item, replaces) = incoming[next];
                    var weight = Weigh(item);

                    if (ahead.Count > 0 && !(held + weight).Fits)
                    {
                        break;
                    }

                    held += weight;
                    next++;
                    ahead.Enqueue((item, replaces,
                        item.IsDirectory
                            ? Task.FromResult(Packed.Folder)
                            : Task.Run(() => Pack(item, gauge, token), token),
                        weight));
                }

                var (done, isReplacement, task, doneWeight) = ahead.Dequeue();

                // 中断のときは、ここで OperationCanceledException になる
                using var packed = task.GetAwaiter().GetResult();
                held -= doneWeight;

                if (packed.Error is { } reason)
                {
                    failed.Add((done.SourcePath, reason));
                    continue;
                }

                Write(output, done, packed, gauge, token);

                if (isReplacement)
                {
                    replaced++;
                }
                else
                {
                    added++;
                }
            }
        }
        finally
        {
            // 走っている圧縮を止め、抱えている一時ファイルを片付ける
            stop.Cancel();

            foreach (var (_, _, task, _) in ahead)
            {
                try
                {
                    task.GetAwaiter().GetResult().Dispose();
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException
                                           or UnauthorizedAccessException)
                {
                }
            }
        }

        return (added, replaced);
    }

    /// <summary>圧縮を終えてから書き込むまでに抱える量の見積もり。元の大きさを上限とみなす。</summary>
    private static Hold Weigh(PlanItem item)
        => item.IsDirectory ? default
            : item.Length > InMemoryLimit ? new Hold(0, item.Length)
            : new Hold(item.Length, 0);

    /// <summary>圧縮を終えて書き込みを待つデータの量。</summary>
    /// <param name="Memory">メモリーに置く分。</param>
    /// <param name="Disk">一時ファイルに置く分。</param>
    private readonly record struct Hold(long Memory, long Disk)
    {
        /// <summary>上限に収まっているか。</summary>
        public bool Fits => Memory <= HoldLimit && Disk <= SpillLimit;

        public static Hold operator +(Hold a, Hold b) => new(a.Memory + b.Memory, a.Disk + b.Disk);

        public static Hold operator -(Hold a, Hold b) => new(a.Memory - b.Memory, a.Disk - b.Disk);
    }

    /// <summary>1 件を圧縮する。書き込みはしない。</summary>
    private static Packed Pack(PlanItem item, Gauge gauge, CancellationToken cancellationToken)
    {
        // 読めるかを確かめ、あわせて圧縮するかどうかを決める (#38)
        var choice = CompressionChoice.Probe(item.SourcePath);
        if (choice.Error is { } reason)
        {
            return Packed.Failed(reason);
        }

        // 大きくて圧縮しないものは、書き込むときに直接読む。写しを作るだけ無駄になる
        if (choice.Method == SharpCompressionMethod.Stored && item.Length > InMemoryLimit)
        {
            return Packed.Direct;
        }

        Stream holder = item.Length > InMemoryLimit
            ? new FileStream(
                Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize,
                FileOptions.DeleteOnClose)
            : new MemoryStream((int)item.Length);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long size;
            long crcValue;

            using (var input = new FileStream(
                       item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize))
            {
                if (choice.Method == SharpCompressionMethod.Deflated && item.Length > SplitThreshold)
                {
                    // ほかに圧縮するファイルが無くても、コアを使い切る (#196)
                    (size, crcValue) = PackInPieces(input, holder, item.EntryName, gauge, cancellationToken);
                }
                else
                {
                    var crc = new Crc32();
                    size = 0;

                    using (var sink = choice.Method == SharpCompressionMethod.Deflated
                               ? new DeflateStream(holder, CompressionLevel.Optimal, leaveOpen: true)
                               : (Stream)new KeepOpen(holder))
                    {
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            crc.Update(new ArraySegment<byte>(buffer, 0, read));
                            sink.Write(buffer, 0, read);
                            size += read;
                            gauge.Advance(read, item.EntryName);
                        }
                    }

                    crcValue = crc.Value;
                }
            }

            holder.Position = 0;
            return new Packed(null, choice.Method, size, crcValue, holder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            holder.Dispose();

            // 1 件の失敗で全体を止めない。まとめて報告する
            return Packed.Failed(Strings.Reason(ex));
        }
        catch
        {
            holder.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 大きなファイル 1 つを、区切りごとに分けて並列に圧縮する (#196)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 区切りごとに別々に Deflate で圧縮し、最後の区切り以外は「続きがある」形 (同期フラッシュ) で
    /// 終える。こうするとバイトの境目でブロックが終わるので、元の順につなげれば 1 本の Deflate の
    /// データになる。展開する側は、区切ったことを知らなくてよい。
    /// </para>
    /// <para>
    /// 継ぎ目では前の区切りのデータを参照できないので、わずかに縮みにくくなる
    /// (<see cref="PieceSize"/>)。読むのは 1 本で順に行い、同時に抱える区切りはコアの数までにする。
    /// CRC も区切りごとに求めて、後でつなぎ合わせる。
    /// </para>
    /// </remarks>
    /// <returns>元の大きさと、元の中身の CRC。</returns>
    private static (long Size, long Crc) PackInPieces(
        Stream input, Stream holder, string name, Gauge gauge, CancellationToken cancellationToken)
    {
        var running = new Queue<(byte[] Buffer, Task<Piece> Task)>();
        var lanes = Environment.ProcessorCount;
        var ended = false;
        long size = 0;
        uint crc = 0;

        try
        {
            while (!ended || running.Count > 0)
            {
                while (!ended && running.Count < lanes)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var buffer = ArrayPool<byte>.Shared.Rent(PieceSize);
                    int read;
                    try
                    {
                        read = input.ReadAtLeast(buffer.AsSpan(0, PieceSize), PieceSize, throwOnEndOfStream: false);
                    }
                    catch
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                        throw;
                    }

                    // 区切りに満たなければ、ここで終わり。ちょうど終わったときは、次に 0 バイトの区切りが来る
                    var last = read < PieceSize;
                    ended = last;
                    running.Enqueue((buffer, Task.Run(() => DeflatePiece(buffer, read, last, name, gauge))));
                }

                var (done, task) = running.Dequeue();
                Piece piece;
                try
                {
                    piece = task.GetAwaiter().GetResult();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(done);
                }

                cancellationToken.ThrowIfCancellationRequested();
                holder.Write(piece.Data, 0, piece.Length);
                crc = CombineCrc(crc, piece.Crc, piece.Count);
                size += piece.Count;
            }
        }
        finally
        {
            // 走っている区切りを待ってから、借りたバッファーを返す。使っている最中には返せない
            foreach (var (buffer, task) in running)
            {
                try
                {
                    task.GetAwaiter().GetResult();
                }
                catch
                {
                    // 先に起きた失敗か中断を伝える。ここでの失敗は捨てる
                }

                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return (size, crc);
    }

    /// <summary>1 つの区切りを圧縮する。</summary>
    /// <param name="last">ファイルの最後の区切りか。最後でなければ、続きがある形で終える。</param>
    private static Piece DeflatePiece(byte[] buffer, int count, bool last, string name, Gauge gauge)
    {
        var crc = new Crc32();
        crc.Update(new ArraySegment<byte>(buffer, 0, count));

        if (count == 0)
        {
            return new Piece(EmptyFinalBlock, EmptyFinalBlock.Length, 0, (uint)crc.Value);
        }

        var output = new MemoryStream(count / 2);
        var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true);
        deflate.Write(buffer, 0, count);

        // 閉じると最後のブロックの印が付く。最後でなければ、フラッシュした所までを使う。
        // DeflateStream のフラッシュは、空の格納ブロックでバイトの境目に揃える同期フラッシュ
        int length;
        if (last)
        {
            deflate.Dispose();
            length = (int)output.Length;
        }
        else
        {
            deflate.Flush();
            length = (int)output.Length;
            deflate.Dispose();
        }

        gauge.Advance(count, name);
        return new Piece(output.GetBuffer(), length, count, (uint)crc.Value);
    }

    /// <summary>圧縮を終えた区切り。</summary>
    /// <param name="Data">圧縮したデータ。先頭から <paramref name="Length"/> バイトを使う。</param>
    /// <param name="Length">圧縮したデータの長さ。</param>
    /// <param name="Count">元の長さ。</param>
    /// <param name="Crc">元の中身の CRC。</param>
    private readonly record struct Piece(byte[] Data, int Length, int Count, uint Crc);

    /// <summary>
    /// 続けて並んだ 2 つのデータの CRC から、つなげたデータの CRC を求める (zlib の crc32_combine と同じ)。
    /// </summary>
    /// <param name="first">前のデータの CRC。</param>
    /// <param name="second">後ろのデータの CRC。</param>
    /// <param name="secondLength">後ろのデータの長さ。</param>
    private static uint CombineCrc(uint first, uint second, long secondLength)
        => MultiplyModP(PowerOfX(secondLength, 3), first) ^ second;

    /// <summary>CRC32 の生成多項式 (ビットを逆順にしたもの)。</summary>
    private const uint CrcPolynomial = 0xEDB88320;

    /// <summary>x の 2^n 乗を生成多項式で割った余り。<see cref="PowerOfX"/> で使う。</summary>
    private static readonly uint[] PowersOfX = BuildPowersOfX();

    private static uint[] BuildPowersOfX()
    {
        var table = new uint[32];
        var p = 1u << 30; // x の 1 乗
        table[0] = p;
        for (var n = 1; n < table.Length; n++)
        {
            table[n] = p = MultiplyModP(p, p);
        }

        return table;
    }

    /// <summary>2 つの多項式の積を、生成多項式で割った余り。</summary>
    private static uint MultiplyModP(uint a, uint b)
    {
        var m = 1u << 31;
        var p = 0u;
        while (true)
        {
            if ((a & m) != 0)
            {
                p ^= b;
                if ((a & (m - 1)) == 0)
                {
                    break;
                }
            }

            m >>= 1;
            b = (b & 1) != 0 ? (b >> 1) ^ CrcPolynomial : b >> 1;
        }

        return p;
    }

    /// <summary>x の (n × 2^k) 乗を、生成多項式で割った余り。k = 3 なら n バイト分になる。</summary>
    private static uint PowerOfX(long n, int k)
    {
        var p = 1u << 31; // x の 0 乗
        while (n != 0)
        {
            if ((n & 1) != 0)
            {
                p = MultiplyModP(PowersOfX[k & 31], p);
            }

            n >>= 1;
            k++;
        }

        return p;
    }

    /// <summary>圧縮を終えた 1 件を書庫に書き込む。</summary>
    private static void Write(
        ZipOutputStream output, PlanItem item, Packed packed, Gauge gauge, CancellationToken cancellationToken)
    {
        var stamp = ZipArchiveWriter.ReadLastWriteTime(item.SourcePath);

        if (item.IsDirectory)
        {
            // 空のフォルダーも構造として残す
            output.PutNextEntry(ZipUpdate.NewEntry(item.EntryName, SharpCompressionMethod.Stored, stamp));
            output.CloseEntry();
            return;
        }

        if (packed.Data is null)
        {
            // 大きくて圧縮しないもの。読みながらそのまま書く
            output.PutNextEntry(ZipUpdate.NewEntry(item.EntryName, SharpCompressionMethod.Stored, stamp));
            using var input = new FileStream(
                item.SourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, BufferSize);
            CopyWatched(input, output, item.EntryName, gauge, cancellationToken);
            output.CloseEntry();
            return;
        }

        var entry = ZipUpdate.NewEntry(item.EntryName, packed.Method, stamp);
        entry.Crc = packed.Crc;
        entry.Size = packed.Size;
        entry.CompressedSize = packed.Data.Length;

        if (packed.Method == SharpCompressionMethod.Deflated)
        {
            output.PutNextPassthroughEntry(entry);
        }
        else
        {
            output.PutNextEntry(entry);
        }

        CancellableCopy.Copy(packed.Data, output, cancellationToken);
        output.CloseEntry();
    }

    private static void CopyWatched(
        Stream source, Stream destination, string name, Gauge gauge, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
                gauge.Advance(read, name);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>圧縮を終えた 1 件。</summary>
    /// <param name="Error">読めなかった理由。読めたなら <see langword="null"/>。</param>
    /// <param name="Method">圧縮方式。</param>
    /// <param name="Size">元の大きさ。</param>
    /// <param name="Crc">元の中身の CRC。</param>
    /// <param name="Data">
    /// 書庫に書くデータ。書き込むときに元のファイルから直接読むものは <see langword="null"/>。
    /// </param>
    private sealed record Packed(
        string? Error, SharpCompressionMethod Method, long Size, long Crc, Stream? Data) : IDisposable
    {
        public static readonly Packed Folder = new(null, SharpCompressionMethod.Stored, 0, 0, null);

        public static readonly Packed Direct = new(null, SharpCompressionMethod.Stored, 0, 0, null);

        public static Packed Failed(string reason) => new(reason, SharpCompressionMethod.Stored, 0, 0, null);

        public void Dispose() => Data?.Dispose();
    }

    /// <summary>
    /// 進み具合をまとめて知らせる。圧縮は並列に進むので、どこからでも呼ばれる。
    /// </summary>
    private sealed class Gauge(long totalBytes, IProgress<AddProgress>? progress)
    {
        /// <summary>読むたびに知らせると細かすぎる。1 MB ごとに間引く。</summary>
        private const long Step = 1024 * 1024;

        private readonly Lock _lock = new();
        private long _done;
        private long _reportedAt;

        public void Advance(int bytes, string name)
        {
            if (progress is null)
            {
                return;
            }

            // 知らせる順と数が前後しないよう、まとめて 1 か所で数える
            lock (_lock)
            {
                _done += bytes;
                if (_done - _reportedAt < Step && _done < totalBytes)
                {
                    return;
                }

                _reportedAt = _done;
                progress.Report(new AddProgress(_done, totalBytes, name));
            }
        }
    }

    /// <summary>閉じても下の流れを閉じない包み。格納のときに圧縮の流れの代わりに使う。</summary>
    private sealed class KeepOpen(Stream inner) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Flush() => inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// 書庫のコメントを、元の書庫のバイト列のまま書き戻すための <see cref="System.Text.Encoding"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// コメントには文字コードの印が無く、読むときに UTF-8 か従来の日本語の文字コードかを
    /// 推し量っている (#13)。書くときに同じ推し量りはできない。読んだときのバイト列を覚えておき、
    /// 同じ文字列を書くときにそれを返す。覚えていない文字列は UTF-8 で書く。
    /// これまでの書き換え (<see cref="ZipUpdate"/>) は、日本語のコメントを化けさせていた。
    /// </para>
    /// <para>
    /// SharpZipLib は、印の無い名前もこれで書く。ここへ来るのは UTF-8 として読める名前だけなので
    /// (<see cref="Carry"/>)、UTF-8 で書けば元と同じバイト列になる。
    /// どの書き方も配列を受け取る形に行き着くので、そこだけを差し替える。
    /// </para>
    /// </remarks>
    private sealed class VerbatimText : System.Text.Encoding
    {
        private readonly Dictionary<string, byte[]> _known = new(StringComparer.Ordinal);

        public void Remember(string text, byte[] bytes) => _known.TryAdd(text, bytes);

        public override int GetByteCount(char[] chars, int index, int count)
            => _known.TryGetValue(new string(chars, index, count), out var bytes)
                ? bytes.Length
                : UTF8.GetByteCount(chars, index, count);

        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            if (!_known.TryGetValue(new string(chars, charIndex, charCount), out var known))
            {
                return UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
            }

            known.CopyTo(bytes, byteIndex);
            return known.Length;
        }

        // 読むことはない。抽象メンバーのため UTF-8 に任せておく
        public override int GetCharCount(byte[] bytes, int index, int count)
            => UTF8.GetCharCount(bytes, index, count);

        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
            => UTF8.GetChars(bytes, byteIndex, byteCount, chars, charIndex);

        public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);

        public override int GetMaxCharCount(int byteCount) => UTF8.GetMaxCharCount(byteCount);
    }

    /// <summary>元の書庫の項目を、そのままでは写せなかった。</summary>
    private sealed class CarryException : Exception;
}
