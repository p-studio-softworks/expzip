using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 7z の見出しを、SharpCompress に渡す前に確かめる (#294)。
/// </summary>
/// <remarks>
/// <para>
/// SharpCompress は見出しに書かれた数のぶんだけ、中身を確かめる前に部品やリストを作る。
/// 割り込む手段も無いので、数十バイトの書庫で何 GB でも使ってしまう。個数ではなく、作られる部品の
/// メモリを見積もって断る。部品ごとの大きさが違うので、個数の合計では実際のメモリを表せない。
/// </para>
/// <para>
/// <b>読めないものは断る</b>。任せると、この解析だけを失敗させて素通りできるため。
/// そのため読み方は SharpCompress に合わせてある (ID は可変長の数、知らない属性は読み飛ばす)。
/// 任せるのは、圧縮データを AES が直接読む見出しと、7z でないものだけ。どちらも SharpCompress が
/// 先へ進まないか、この形式の読み方をしない。
/// </para>
/// </remarks>
internal static class SevenZipHeader
{
    private const int SignatureHeaderLength = 32;

    /// <summary>見出しの伸長に使わせる辞書の上限。7-Zip の見出しの辞書は数 MB まで。</summary>
    private const uint MaxDictionary = 256u * 1024 * 1024;

    /// <summary>1 つのフォルダーのコーダーと入出力の数の上限 (7-Zip と同じ)。</summary>
    private const ulong MaxCoders = 32;

    // SharpCompress が作る部品の 1 個あたりの大きさ。実測した値 (.NET 10、64 ビット)
    private const long BytesPerFolder = 460;
    private const long BytesPerCoder = 70;
    private const long BytesPerCoderProperty = 2;
    private const long BytesPerBindPair = 50;
    private const long BytesPerFile = 380;
    private const long BytesPerStream = 16;
    private const long BytesPerDigest = 8;

    private const ulong End = 0x00;
    private const ulong Header = 0x01;
    private const ulong ArchiveProperties = 0x02;
    private const ulong AdditionalStreams = 0x03;
    private const ulong MainStreams = 0x04;
    private const ulong FilesInfo = 0x05;
    private const ulong PackInfo = 0x06;
    private const ulong UnpackInfo = 0x07;
    private const ulong SubStreamsInfo = 0x08;
    private const ulong Size = 0x09;
    private const ulong Crc = 0x0A;
    private const ulong Folder = 0x0B;
    private const ulong CodersUnpackSize = 0x0C;
    private const ulong NumUnpackStream = 0x0D;
    private const ulong EmptyStream = 0x0E;
    private const ulong EmptyFile = 0x0F;
    private const ulong Anti = 0x10;
    private const ulong Names = 0x11;
    private const ulong CreationTime = 0x12;
    private const ulong AccessTime = 0x13;
    private const ulong ModifiedTime = 0x14;
    private const ulong WinAttributes = 0x15;
    private const ulong EncodedHeader = 0x17;
    private const ulong StartPosition = 0x18;

    private static ReadOnlySpan<byte> AesMethod => [0x06, 0xF1, 0x07, 0x01];

    private static ReadOnlySpan<byte> Lzma2Method => [0x21];

    private static ReadOnlySpan<byte> DeflateMethod => [0x04, 0x01, 0x08];

    /// <summary>
    /// CRC が無く、正しく読めるか確かめる手段も無いファイルの印 (#321)。
    /// </summary>
    /// <remarks>
    /// SharpCompress 0.50.7 は、複数のファイルをまとめて Deflate で圧縮したフォルダーを正しく読めないことがある。
    /// CRC があれば失敗として見つかるが、無いと、大きさが合ったまま違う中身を書き出す。
    /// </remarks>
    public const long Unverifiable = -2;

    /// <summary>見出しを確かめる。使うメモリの見積もりを返す。</summary>
    /// <param name="archive">7z の本体が先頭に来る、読み取りと移動ができる流れ。</param>
    /// <param name="memoryBudget">見出しの部品に使ってよいメモリ。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <returns>見出しの部品に使うメモリの見積もり。一覧の予算から差し引く。</returns>
    /// <exception cref="ArchiveTooLargeException">部品か、伸長に要るメモリが大きすぎる場合。</exception>
    /// <exception cref="DamagedDataException">見出しを読めない場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static long Check(Stream archive, long memoryBudget, CancellationToken cancellationToken)
    {
        var budget = new Budget(memoryBudget);
        Inspect(archive, budget, collect: false, cancellationToken);
        return budget.Used;
    }

    /// <summary>
    /// 中身を読む前に、圧縮データが途中で切れていないかを確かめる。
    /// </summary>
    /// <remarks>
    /// 本体の LZMA2 は SharpCompress が内側で範囲を切って読むので、入力を見張っても終わりに気付けない。
    /// 塊の見出しだけをたどるので、一覧を読むたびに行わず、中身を読むときだけ行う。別の部品の出力を読む形と
    /// 暗号化された形はたどれない。これらはウィンドウを閉じれば終了できることで受け止める。
    /// </remarks>
    /// <exception cref="ArchiveTooLargeException">部品か、伸長に要るメモリが大きすぎる場合。</exception>
    /// <exception cref="DamagedDataException">見出しを読めないか、データが切れている場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    /// <returns>
    /// ファイルの番号ごとの、フォルダー側の CRC。無ければ -1 (#321)。見出しが暗号化されていて読めなければ <see langword="null"/>。
    /// </returns>
    public static long[]? CheckContent(Stream archive, long memoryBudget, CancellationToken cancellationToken)
    {
        if (Inspect(archive, new Budget(memoryBudget), collect: true, cancellationToken) is not { } streams)
        {
            return null;
        }

        try
        {
            var position = checked(SignatureHeaderLength + (long)streams.PackPosition);
            var index = 0;

            foreach (var folder in streams.FolderList)
            {
                if (folder.PackStreams == 1 && folder.ReadsLzma2 && index < streams.PackSizes.Count)
                {
                    WalkLzma2(archive, position, checked((long)streams.PackSizes[index]), cancellationToken);
                }

                for (var i = 0UL; i < folder.PackStreams && index < streams.PackSizes.Count; i++)
                {
                    position = checked(position + (long)streams.PackSizes[index++]);
                }
            }
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            throw new DamagedDataException(ex);
        }

        return streams.FileCrcs;
    }

    /// <summary>LZMA2 の塊の見出しを順にたどる。範囲の中で終わりの印に届かなければ切れている。</summary>
    private static void WalkLzma2(Stream archive, long position, long length, CancellationToken cancellationToken)
    {
        Span<byte> head = stackalloc byte[3];
        var left = length;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (left < 1)
            {
                throw new InvalidDataException();
            }

            archive.Position = position;
            var control = archive.ReadByte();
            if (control < 0)
            {
                throw new EndOfStreamException();
            }

            if (control == 0)
            {
                return;
            }

            long used;
            if (control is 1 or 2)
            {
                archive.ReadExactly(head[..2]);
                used = 3 + ((head[0] << 8) | head[1]) + 1;
            }
            else if (control >= 0x80)
            {
                archive.ReadExactly(head[..2]);
                archive.ReadExactly(head[..2]);
                used = (control >= 0xC0 ? 6 : 5) + ((head[0] << 8) | head[1]) + 1;
            }
            else
            {
                throw new InvalidDataException();
            }

            if (used > left)
            {
                throw new InvalidDataException();
            }

            position += used;
            left -= used;
        }
    }

    private static Streams? Inspect(Stream archive, Budget budget, bool collect, CancellationToken cancellationToken)
    {
        try
        {
            var start = new byte[SignatureHeaderLength];
            archive.Position = 0;
            archive.ReadExactly(start);

            // 7z でなければ、別の形式として SharpCompress が判断する (拡張子を変えただけの ZIP など)
            if (!start.AsSpan(0, 6).SequenceEqual<byte>([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C]))
            {
                // xz だけは、SharpCompress の判断の中で伸長されて止まらなくなることがある。外から見張れないので断る
                archive.Position = 0;
                return SharpArchiveAccess.IsXz(archive) ? throw new InvalidDataException() : null;
            }

            var offset = BitConverter.ToInt64(start, 12);
            var length = BitConverter.ToInt64(start, 20);

            if (length == 0)
            {
                return null;
            }

            var room = archive.Length - SignatureHeaderLength;
            if (offset < 0 || length < 0 || length > int.MaxValue || offset > room || length > room - offset)
            {
                throw new InvalidDataException();
            }

            archive.Position = SignatureHeaderLength + offset;
            var reader = new Reader(
                new CancellableReadStream(new BoundedStream(archive, length), cancellationToken),
                budget);

            return CheckTop(archive, reader, budget, collect, cancellationToken);
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            throw new DamagedDataException(ex);
        }
    }

    /// <summary>読み取りの失敗そのもの (ファイルを読めない) や中断ではなく、中身がおかしいとき。</summary>
    private static bool IsDamage(Exception ex)
        => ex is EndOfStreamException
           || ex is not (OperationCanceledException or OutOfMemoryException
               or UnauthorizedAccessException or IOException);

    private static Streams? CheckTop(
        Stream archive, Reader reader, Budget budget, bool collect, CancellationToken cancellationToken)
    {
        var id = reader.ReadId();

        if (id == Header)
        {
            return CheckHeader(reader, collect);
        }

        if (id != EncodedHeader)
        {
            throw new InvalidDataException();
        }

        var streams = new Streams();
        ReadStreamsInfo(reader, streams);

        // フォルダーが無ければ、SharpCompress は空の書庫として扱う。2 個以上は SharpCompress も断る
        if (streams.Folders == 0)
        {
            return null;
        }

        if (streams.Folders != 1 || streams.PackSizeCount != 1 || !streams.SawPackInfo)
        {
            throw new InvalidDataException();
        }

        // SharpCompress は主な出力から圧縮データまでの部品だけを作る。どの順にたどるかを確かめる
        if (streams.FirstPath is not { } path)
        {
            throw new InvalidDataException();
        }

        var aes = path.Count(static coder => coder.Method.AsSpan().SequenceEqual(AesMethod));

        // 圧縮データを AES が直接読むなら、パスワードが無いと SharpCompress は先へ進まない。
        // ほかの位置の AES は、そこまでの部品を作らせられるので認めない
        if (aes > 0)
        {
            if (aes == 1 && path[^1].Method.AsSpan().SequenceEqual(AesMethod))
            {
                return null;
            }

            throw new InvalidDataException();
        }

        var position = checked(SignatureHeaderLength + (long)streams.PackPosition);
        var packedLength = checked((long)streams.FirstPackSize);
        var unpackedLength = checked((long)streams.UnpackSize);

        if (position > archive.Length || packedLength > archive.Length - position || unpackedLength > int.MaxValue)
        {
            throw new InvalidDataException();
        }

        // 伸長した見出しはメモリに読まれる
        budget.Charge((ulong)unpackedLength, 1);

        archive.Position = position;

        // SharpCompress と同じ読み方で最後まで読む。ここで終わりを超えなければ、同じバイト列を読む
        // SharpCompress も超えない。超えると SharpCompress は止まらなくなるので、ここで断る
        var packed = new EndGuardStream(new BoundedStream(archive, packedLength), allowedEndReads: 0);
        using var decoded = Decode(packed, unpackedLength, streams);
        var limited = new BoundedStream(decoded, unpackedLength);
        var inner = new Reader(new CancellableReadStream(limited, cancellationToken), budget);

        // 入れ子の見出しは SharpCompress も読まない
        if (inner.ReadId() != Header)
        {
            throw new InvalidDataException();
        }

        var result = CheckHeader(inner, collect);

        inner.ReadToEnd();
        if (limited.Position != unpackedLength)
        {
            throw new InvalidDataException();
        }

        decoded.ReadByte();

        return result;
    }

    private static Stream Decode(Stream packed, long unpackedLength, Streams streams)
    {
        switch (streams.Method)
        {
            case [0x00]:
                return new BoundedStream(packed, unpackedLength);

            case [0x03, 0x01, 0x01] when streams.Properties.Length == 5:
                CheckDictionary(BitConverter.ToUInt32(streams.Properties, 1));
                return SharpCompress.Compressors.LZMA.LzmaStream.Create(
                    streams.Properties, packed, packed.Length, unpackedLength, null, isLzma2: false, leaveOpen: true);

            case [0x21] when streams.Properties is [<= 40]:
                var bits = streams.Properties[0];
                CheckDictionary(bits == 40 ? uint.MaxValue : (uint)(2 | (bits & 1)) << (bits / 2 + 11));
                return SharpCompress.Compressors.LZMA.LzmaStream.Create(
                    streams.Properties, packed, packed.Length, unpackedLength, null, isLzma2: true, leaveOpen: true);

            // 見出しに別の圧縮を使うツールは見つかっていない。使われると中身を確かめられない
            default:
                throw new InvalidDataException();
        }
    }

    private static void CheckDictionary(uint size)
    {
        if (size > MaxDictionary)
        {
            throw new ArchiveTooLargeException(ArchiveLimit.Memory);
        }
    }

    private static Streams CheckHeader(Reader reader, bool collect)
    {
        var id = reader.ReadId();
        var streams = new Streams { Collect = collect };

        if (id == ArchiveProperties)
        {
            while (reader.ReadId() != End)
            {
                reader.SkipData();
            }

            id = reader.ReadId();
        }

        // 別の場所に見出しの一部を持つ形。SharpCompress はここで伸長を始める。使うツールは見つかっていない
        if (id == AdditionalStreams)
        {
            throw new InvalidDataException();
        }

        if (id == MainStreams)
        {
            ReadStreamsInfo(reader, streams);
            id = reader.ReadId();
        }

        var files = 0UL;
        if (id == FilesInfo)
        {
            files = reader.ReadCount(BytesPerFile);
            ReadFileProperties(reader, streams, files);
        }
        else if (id != End)
        {
            throw new InvalidDataException();
        }

        // 中身のある流れは、それぞれ 1 個のファイルに当たる。流れのほうが多ければ見出しが矛盾している
        if (streams.SubStreams > files)
        {
            throw new InvalidDataException();
        }

        if (collect)
        {
            streams.FileCrcs = FileCrcs(streams, files);
        }

        return streams;
    }

    /// <summary>
    /// ファイルの番号ごとに、フォルダー側の CRC を並べる (#321)。
    /// </summary>
    /// <remarks>
    /// ストリームが 1 個のフォルダーでは、この CRC がそのファイルの CRC になる。SharpCompress はこれをファイルに渡さないので、
    /// このままでは壊れていても照合されない。ストリームが 2 個以上のフォルダーの CRC は、ファイルごとには使えない。
    /// 中身のあるファイルの数とストリームの数が合わなければ、見出しが矛盾している。
    /// </remarks>
    private static long[] FileCrcs(Streams streams, ulong files)
    {
        var result = new long[checked((int)files)];
        var empty = streams.EmptyStream;

        // SubStreamsInfo が無ければ、フォルダーごとに 1 個
        var counts = streams.StreamCounts;
        if (counts is null)
        {
            counts = new ulong[checked((int)streams.Folders)];
            Array.Fill(counts, 1UL);
        }

        var folder = 0;
        var left = 0UL;

        for (var i = 0; i < result.Length; i++)
        {
            result[i] = -1;

            if (empty is not null && empty[i])
            {
                continue;
            }

            // ストリームが 0 個のフォルダーは飛ばす
            while (left == 0)
            {
                if (folder >= counts.Length)
                {
                    throw new InvalidDataException();
                }

                left = counts[folder++];
            }

            var current = folder - 1;
            if (counts[current] == 1 && current < streams.FolderHasCrc.Length && streams.FolderHasCrc[current])
            {
                result[i] = streams.FolderCrc[current];
            }
            else if (counts[current] > 1 && current < streams.FolderList.Count && streams.FolderList[current].Deflate)
            {
                result[i] = Unverifiable;
            }

            left--;
        }

        // 余ったストリームがあれば、ファイルの数が足りない
        for (; folder < counts.Length; folder++)
        {
            left = checked(left + counts[folder]);
        }

        if (left != 0)
        {
            throw new InvalidDataException();
        }

        return result;
    }

    /// <summary>
    /// ファイルの属性を読み飛ばす。同じ属性が 2 回あれば断る。
    /// </summary>
    /// <remarks>
    /// SharpCompress は属性を読むたびにファイルの数だけ処理する。繰り返されると、小さな見出しで長い時間を使われ、
    /// その間は中断もできない。Dummy は 7-Zip が位置をそろえるために何度も書くが、長さのぶんしか時間を使わない。
    /// </remarks>
    private static void ReadFileProperties(Reader reader, Streams streams, ulong files)
    {
        var seen = new HashSet<ulong>();

        while (true)
        {
            var id = reader.ReadId();
            if (id == End)
            {
                return;
            }

            if (id is EmptyStream or EmptyFile or Anti or Names or CreationTime or AccessTime
                    or ModifiedTime or WinAttributes or StartPosition
                && !seen.Add(id))
            {
                throw new InvalidDataException();
            }

            if (id == EmptyStream && streams.Collect)
            {
                streams.EmptyStream = reader.ReadEmptyStream(files);
                continue;
            }

            reader.SkipData();
        }
    }

    private static void ReadStreamsInfo(Reader reader, Streams streams)
    {
        while (true)
        {
            var id = reader.ReadId();

            // 同じ節が 2 回あると、SharpCompress は後のものを使う。確かめたものと違うものが使われる
            switch (id)
            {
                case End:
                    return;

                case PackInfo when !streams.SawPackInfo:
                    ReadPackInfo(reader, streams);
                    break;

                case UnpackInfo when !streams.SawUnpackInfo:
                    ReadUnpackInfo(reader, streams);
                    break;

                case SubStreamsInfo when streams.SawUnpackInfo && !streams.SawSubStreams:
                    ReadSubStreams(reader, streams);
                    break;

                default:
                    throw new InvalidDataException();
            }
        }
    }

    private static void ReadPackInfo(Reader reader, Streams streams)
    {
        streams.SawPackInfo = true;
        streams.PackPosition = reader.ReadNumber();
        var count = reader.ReadCount(BytesPerStream);
        streams.PackSizeCount = count;

        reader.WaitFor(Size);
        for (var i = 0UL; i < count; i++)
        {
            var size = reader.ReadNumber();
            if (i == 0)
            {
                streams.FirstPackSize = size;
            }

            if (streams.Collect)
            {
                streams.PackSizes.Add(size);
            }
        }

        var sawCrc = false;
        while (true)
        {
            var id = reader.ReadId();
            if (id == End)
            {
                return;
            }

            if (id != Crc)
            {
                reader.SkipData();
            }
            else if (sawCrc)
            {
                throw new InvalidDataException();
            }
            else
            {
                sawCrc = true;
                reader.SkipDigests(count);
            }
        }
    }

    private static void ReadUnpackInfo(Reader reader, Streams streams)
    {
        streams.SawUnpackInfo = true;

        reader.WaitFor(Folder);
        var folders = reader.ReadCount(BytesPerFolder);
        streams.Folders = folders;

        // 別の場所から読む形。上と同じで、使うツールは見つかっていない
        if (reader.ReadByte() != 0)
        {
            throw new InvalidDataException();
        }

        var outputs = new List<ulong>();
        for (var i = 0UL; i < folders; i++)
        {
            outputs.Add(ReadFolder(reader, streams, isFirst: i == 0));
        }

        reader.WaitFor(CodersUnpackSize);
        foreach (var count in outputs)
        {
            for (var i = 0UL; i < count; i++)
            {
                var size = reader.ReadNumber();
                if (i == 0 && streams.UnpackSizeUnset)
                {
                    streams.UnpackSize = size;
                    streams.UnpackSizeUnset = false;
                }
            }
        }

        var sawCrc = false;
        while (true)
        {
            var id = reader.ReadId();
            if (id == End)
            {
                return;
            }

            if (id != Crc)
            {
                reader.SkipData();
            }
            else if (sawCrc)
            {
                throw new InvalidDataException();
            }
            else
            {
                sawCrc = true;
                (streams.FolderHasCrc, streams.FolderCrc) = reader.ReadFolderDigests(folders, streams.Collect);
            }
        }
    }

    /// <summary>フォルダーの定義を 1 個読む。出力の流れの数を返す。</summary>
    private static ulong ReadFolder(Reader reader, Streams streams, bool isFirst)
    {
        var coderCount = reader.ReadNumber();
        if (coderCount is 0 or > MaxCoders)
        {
            throw new InvalidDataException();
        }

        var coders = new List<Coder>((int)coderCount);
        var inputs = 0UL;
        var outputs = 0UL;

        for (var i = 0UL; i < coderCount; i++)
        {
            var flags = reader.ReadByte();
            if ((flags & 0x80) != 0 || (flags & 0x0F) > 8)
            {
                throw new InvalidDataException();
            }

            var id = reader.ReadBytes(flags & 0x0F);

            var coderInputs = 1UL;
            var coderOutputs = 1UL;
            if ((flags & 0x10) != 0)
            {
                coderInputs = reader.ReadNumber();
                coderOutputs = reader.ReadNumber();

                // 入出力の無い部品は、読み出しの経路に入らないまま定義だけを置ける
                if (coderInputs is 0 or > MaxCoders || coderOutputs is 0 or > MaxCoders)
                {
                    throw new InvalidDataException();
                }
            }

            inputs += coderInputs;
            outputs += coderOutputs;

            byte[] properties = [];
            if ((flags & 0x20) != 0)
            {
                var length = reader.ReadNumber();
                if (length > 64)
                {
                    throw new InvalidDataException();
                }

                properties = reader.ReadBytes((int)length);
            }

            reader.Charge(1, BytesPerCoder + (BytesPerCoderProperty * properties.Length));
            coders.Add(new Coder(id, coderInputs, coderOutputs, properties));
        }

        if (outputs == 0 || inputs < outputs - 1)
        {
            throw new InvalidDataException();
        }

        var bindPairs = outputs - 1;
        reader.Charge(bindPairs, BytesPerBindPair);

        var pairs = new List<(ulong In, ulong Out)>((int)bindPairs);
        for (var i = 0UL; i < bindPairs; i++)
        {
            pairs.Add((reader.ReadNumber(), reader.ReadNumber()));
        }

        var packedStreams = inputs - bindPairs;
        reader.Charge(packedStreams, BytesPerStream);
        if (packedStreams != 1)
        {
            for (var i = 0UL; i < packedStreams; i++)
            {
                reader.ReadNumber();
            }
        }

        // 経路が要るのは見出しの先頭のフォルダーと、中身を読む前の確認だけ
        var path = isFirst || streams.Collect ? FindPath(coders, pairs) : null;

        if (isFirst)
        {
            streams.FirstPath = path;
            streams.Method = coders.Count == 1 ? coders[0].Method : [];
            streams.Properties = coders[0].Properties;
        }

        if (streams.Collect)
        {
            streams.FolderList.Add(new FolderInfo(
                packedStreams,
                path is not null && path[^1].Method.AsSpan().SequenceEqual(Lzma2Method),
                coders.Exists(static coder => coder.Method.AsSpan().SequenceEqual(DeflateMethod))));
        }

        return outputs;
    }

    /// <summary>
    /// 主な出力から圧縮データまでの部品を順に返す。1 入力 1 出力の部品が 1 列に並んだ形でなければ null。
    /// </summary>
    /// <remarks>
    /// 7-Zip の暗号化された見出しはこの形。結び付けの番号は、部品の入力と出力の番号そのもの。
    /// </remarks>
    private static List<Coder>? FindPath(List<Coder> coders, List<(ulong In, ulong Out)> pairs)
    {
        if (coders.Any(static coder => coder.Inputs != 1 || coder.Outputs != 1)
            || pairs.Count != coders.Count - 1
            || pairs.Any(pair => pair.In >= (ulong)coders.Count || pair.Out >= (ulong)coders.Count)
            || pairs.Select(static pair => pair.In).Distinct().Count() != pairs.Count
            || pairs.Select(static pair => pair.Out).Distinct().Count() != pairs.Count)
        {
            return null;
        }

        var main = 0;
        while (pairs.Any(pair => pair.Out == (ulong)main))
        {
            main++;
        }

        var path = new List<Coder>(coders.Count);
        var current = main;

        while (path.Count < coders.Count)
        {
            path.Add(coders[current]);

            var next = pairs.FindIndex(pair => pair.In == (ulong)current);
            if (next < 0)
            {
                break;
            }

            current = (int)pairs[next].Out;
        }

        // 輪になっている形や、たどれない部品が残る形は認めない
        return path.Count == coders.Count && !pairs.Exists(pair => pair.In == (ulong)current) ? path : null;
    }

    private static void ReadSubStreams(Reader reader, Streams streams)
    {
        streams.SawSubStreams = true;

        // フォルダーはここまでに読み終えているので、この数のぶんの見出しの中身がある
        var counts = new ulong[checked((int)streams.Folders)];
        Array.Fill(counts, 1UL);
        streams.StreamCounts = counts;
        var sawCounts = false;

        ulong id;
        while (true)
        {
            id = reader.ReadId();
            if (id == NumUnpackStream && !sawCounts)
            {
                sawCounts = true;
                for (var i = 0; i < counts.Length; i++)
                {
                    // 中身を読まずに、この数のリストが作られる
                    counts[i] = reader.ReadCount(BytesPerStream);
                }

                continue;
            }

            if (id == NumUnpackStream || id == Size || id == Crc || id == End)
            {
                if (id == NumUnpackStream)
                {
                    throw new InvalidDataException();
                }

                break;
            }

            reader.SkipData();
        }

        if (id == Size)
        {
            foreach (var count in counts)
            {
                for (var i = 1UL; i < count; i++)
                {
                    reader.ReadNumber();
                }
            }

            id = reader.ReadId();
        }

        var total = 0UL;
        var digests = 0UL;
        for (var i = 0; i < counts.Length; i++)
        {
            total = checked(total + counts[i]);
            var hasFolderCrc = i < streams.FolderHasCrc.Length && streams.FolderHasCrc[i];
            if (counts[i] != 1 || !hasFolderCrc)
            {
                digests = checked(digests + counts[i]);
            }
        }

        streams.SubStreams = total;

        var sawCrc = false;
        while (id != End)
        {
            if (id != Crc)
            {
                reader.SkipData();
            }
            else if (sawCrc)
            {
                throw new InvalidDataException();
            }
            else
            {
                sawCrc = true;
                reader.SkipDigests(digests);
            }

            id = reader.ReadId();
        }
    }

    /// <summary>フォルダーの部品 1 個。</summary>
    private sealed record Coder(byte[] Method, ulong Inputs, ulong Outputs, byte[] Properties);

    /// <summary>中身を読む前の確認に使う、フォルダーのこと。</summary>
    private readonly record struct FolderInfo(ulong PackStreams, bool ReadsLzma2, bool Deflate = false);

    /// <summary>見出しから読み取ったこと。</summary>
    private sealed class Streams
    {
        public bool Collect { get; init; }

        public bool SawPackInfo { get; set; }

        public bool SawUnpackInfo { get; set; }

        public bool SawSubStreams { get; set; }

        public ulong PackPosition { get; set; }

        public ulong PackSizeCount { get; set; }

        public ulong FirstPackSize { get; set; }

        public ulong Folders { get; set; }

        public bool UnpackSizeUnset { get; set; } = true;

        public ulong UnpackSize { get; set; }

        public ulong SubStreams { get; set; }

        public bool[] FolderHasCrc { get; set; } = [];

        public uint[] FolderCrc { get; set; } = [];

        public ulong[]? StreamCounts { get; set; }

        public bool[]? EmptyStream { get; set; }

        public long[]? FileCrcs { get; set; }

        public List<Coder>? FirstPath { get; set; }

        public byte[] Method { get; set; } = [];

        public byte[] Properties { get; set; } = [];

        public List<ulong> PackSizes { get; } = [];

        public List<FolderInfo> FolderList { get; } = [];
    }

    /// <summary>部品に使うメモリの見積もり。超えたら読むのをやめる。</summary>
    private sealed class Budget(long limit)
    {
        public long Used { get; private set; }

        public void Charge(ulong count, long bytesEach)
        {
            var left = limit - Used;

            if (count > (ulong)(left / bytesEach))
            {
                throw new ArchiveTooLargeException(ArchiveLimit.Entries);
            }

            Used += (long)count * bytesEach;
        }
    }

    /// <summary>見出しの読み手。</summary>
    private sealed class Reader(Stream source, Budget budget)
    {
        private readonly BufferedStream _source = new(source, 64 * 1024);

        public int ReadByte()
        {
            var value = _source.ReadByte();
            return value < 0 ? throw new EndOfStreamException() : value;
        }

        public byte[] ReadBytes(int count)
        {
            var bytes = new byte[count];
            _source.ReadExactly(bytes);
            return bytes;
        }

        /// <summary>7z の可変長の数を読む。先頭の 1 バイトの上位ビットが、続くバイトの数を表す。</summary>
        public ulong ReadNumber()
        {
            var first = ReadByte();
            var mask = 0x80;
            var value = 0UL;

            for (var i = 0; i < 8; i++)
            {
                if ((first & mask) == 0)
                {
                    return value + ((ulong)(first & (mask - 1)) << (8 * i));
                }

                value |= (ulong)ReadByte() << (8 * i);
                mask >>= 1;
            }

            return value;
        }

        /// <summary>ID を読む。SharpCompress と同じく可変長の数として読む。</summary>
        public ulong ReadId() => ReadNumber();

        /// <summary>SharpCompress がそのぶんの部品を作る数を読み、見積もりに足す。</summary>
        public ulong ReadCount(long bytesEach)
        {
            var count = ReadNumber();
            Charge(count, bytesEach);
            return count;
        }

        public void Charge(ulong count, long bytesEach) => budget.Charge(count, bytesEach);

        public void WaitFor(ulong wanted)
        {
            while (true)
            {
                var id = ReadId();
                if (id == wanted)
                {
                    return;
                }

                if (id == End)
                {
                    throw new InvalidDataException();
                }

                SkipData();
            }
        }

        public void SkipData() => Skip(ReadNumber());

        /// <summary>最後まで読む。</summary>
        public void ReadToEnd()
        {
            Span<byte> buffer = stackalloc byte[4096];

            while (_source.Read(buffer) > 0)
            {
            }
        }

        /// <summary>CRC の並びを読み飛ばす。数は見出しの中身で裏付けられないので、配列を作らない。</summary>
        public void SkipDigests(ulong count)
        {
            Charge(count, BytesPerDigest);

            var present = count;

            if (ReadByte() == 0)
            {
                present = 0;
                var current = 0;
                for (var i = 0UL; i < count; i++)
                {
                    if (i % 8 == 0)
                    {
                        current = ReadByte();
                    }

                    if ((current & (0x80 >> (int)(i % 8))) != 0)
                    {
                        present++;
                    }
                }
            }

            Skip(checked(present * 4));
        }

        /// <summary>
        /// 中身の無いファイルの印を読む。上位のビットから順に、ファイルの数だけ並ぶ。長さが合わなければ見出しが矛盾している。
        /// </summary>
        public bool[] ReadEmptyStream(ulong files)
        {
            var length = ReadNumber();
            if (length != (files + 7) / 8)
            {
                throw new InvalidDataException();
            }

            var empty = new bool[checked((int)files)];
            var current = 0;
            for (var i = 0; i < empty.Length; i++)
            {
                if (i % 8 == 0)
                {
                    current = ReadByte();
                }

                empty[i] = (current & (0x80 >> (i % 8))) != 0;
            }

            return empty;
        }

        /// <summary>フォルダーの CRC を読む。どのフォルダーに CRC があるかと、<paramref name="keep"/> なら値を返す。</summary>
        public (bool[] Defined, uint[] Values) ReadFolderDigests(ulong count, bool keep)
        {
            Charge(count, BytesPerDigest);

            var defined = new bool[checked((int)count)];

            if (ReadByte() != 0)
            {
                Array.Fill(defined, true);
            }
            else
            {
                var current = 0;
                for (var i = 0; i < defined.Length; i++)
                {
                    if (i % 8 == 0)
                    {
                        current = ReadByte();
                    }

                    defined[i] = (current & (0x80 >> (i % 8))) != 0;
                }
            }

            if (!keep)
            {
                Skip((ulong)defined.Count(static d => d) * 4);
                return (defined, []);
            }

            var values = new uint[defined.Length];
            for (var i = 0; i < defined.Length; i++)
            {
                if (defined[i])
                {
                    values[i] = (uint)(ReadByte() | ReadByte() << 8 | ReadByte() << 16 | ReadByte() << 24);
                }
            }

            return (defined, values);
        }

        private void Skip(ulong count)
        {
            Span<byte> buffer = stackalloc byte[4096];

            while (count > 0)
            {
                var read = _source.Read(buffer[..(int)Math.Min(count, (ulong)buffer.Length)]);
                if (read == 0)
                {
                    throw new EndOfStreamException();
                }

                count -= (ulong)read;
            }
        }
    }
}
