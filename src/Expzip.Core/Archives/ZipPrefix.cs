using System.Buffers.Binary;
using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 先頭に書庫でない塊が付いた ZIP (自己解凍書庫) を読むための位置合わせ (#32)。
/// </summary>
/// <remarks>
/// <para>
/// 自己解凍書庫は「取り出すプログラム + ふつうの ZIP」を繋いだもの。ZIP は末尾から
/// 順に辿る形式なので、繋いであっても読める作りになっている。
/// </para>
/// <para>
/// ただし中央ディレクトリが指す位置が、<b>ファイルの先頭からの位置</b>のこともあれば
/// <b>ZIP の始まりからの位置</b>のこともある。後者だと、そのまま読ませたライブラリは
/// 位置を取り違えて「件数が合わない」と言って開けない。
/// </para>
/// <para>
/// ずれ幅は終端レコードから計算できる。中央ディレクトリは終端レコードの直前で
/// 終わっているはずなので、<c>(終端レコードの位置 - 中央ディレクトリの大きさ)</c> が
/// 本当の開始位置になる。記録されている位置との差がそのままずれ幅。
/// </para>
/// <para>
/// 見つけたずれ幅のぶんだけ頭を隠した流れを渡せば、あとはふつうの ZIP として扱える。
/// 読む側に手を入れなくて済む。
/// </para>
/// </remarks>
internal static class ZipPrefix
{
    /// <summary>終端レコードの署名。</summary>
    private const uint EndSignature = 0x06054b50;

    /// <summary>ZIP64 の終端レコードの署名。</summary>
    private const uint Zip64EndSignature = 0x06064b50;

    /// <summary>局所ヘッダの署名。ずれ幅の答え合わせに使う。</summary>
    private const uint LocalSignature = 0x04034b50;

    /// <summary>中央ディレクトリの署名。中身が空の書庫での答え合わせに使う。</summary>
    private const uint DirectorySignature = 0x02014b50;

    /// <summary>ZIP64 の終わりの記録の位置の記録の署名。</summary>
    private const uint Zip64LocatorSignature = 0x07064b50;

    /// <summary>ZIP64 の終わりの記録の位置の記録の長さ。</summary>
    private const int LocatorLength = 20;

    /// <summary>ZIP64 の終わりの記録の固定部の長さ。</summary>
    private const int Zip64EndLength = 56;

    /// <summary>終端レコードの固定部の長さ。</summary>
    private const int EndLength = 22;

    /// <summary>書庫コメントの上限 (65,535) と固定部を足した探索範囲。</summary>
    private const int SearchLength = EndLength + 0xFFFF;

    /// <summary>
    /// ZIP の中身が始まる位置を返す。ふつうの ZIP では 0。
    /// </summary>
    /// <remarks>
    /// 判断がつかない場合は 0 を返す。いままでと同じ読み方になるだけで、悪くはならない。
    /// </remarks>
    public static long Detect(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            return Detect(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or EndOfStreamException or ArgumentException
                                   or NotSupportedException)
        {
            return 0;
        }
    }

    /// <summary>ZIP の中身だけを見せる流れを開く。</summary>
    /// <remarks>
    /// <para>ふつうの ZIP では、そのままのファイルを返す。</para>
    /// <para>
    /// 部品によって読む目録が違う書庫は開かない (#396)。一覧に出ていない中身を、検査、ハッシュ値、展開、書き換えで
    /// 読まないように、中身を読む道はすべてここを通す。一覧を読むときだけ <see cref="OpenForListing"/> を使う。
    /// </para>
    /// </remarks>
    /// <exception cref="SplitDirectoryException">部品によって読む目録が違う場合。</exception>
    public static Stream Open(string path)
    {
        var stream = OpenForListing(path, out var split);
        if (split)
        {
            stream.Dispose();
            throw new SplitDirectoryException();
        }

        return stream;
    }

    /// <summary>
    /// 書き換えの部品 (SharpZipLib) にそのまま渡してよい ZIP かを確かめる (#396)。
    /// </summary>
    /// <remarks>
    /// 書き換えの部品は <see cref="Open"/> を通らずにファイルを開き、先頭を隠さない。部品によって読む目録が違う書庫と、
    /// 先頭にずれのある書庫は書き換えない。ずれのある ZIP64 の書庫では、SharpZipLib が一覧と別の目録を読む。
    /// </remarks>
    /// <exception cref="SplitDirectoryException">部品によって読む目録が違う場合。</exception>
    /// <exception cref="PrefixedZipEditException">先頭にずれがある場合。</exception>
    public static void EnsureEditable(string path)
    {
        using var stream = ArchiveFile.OpenRead(path);
        var (shift, state) = TryExamine(stream);

        if (state == ZipDirectories.Split)
        {
            throw new SplitDirectoryException();
        }

        if (shift != 0)
        {
            throw new PrefixedZipEditException();
        }
    }

    /// <summary>部品によって読む目録が違う書庫なら、<see cref="SplitDirectoryException"/> を投げる (#396)。</summary>
    /// <remarks>
    /// 書くために開いた流れの上で確かめる。確かめてから開き直すと、そのあいだに置き換わったファイルに書いてしまう。
    /// 流れの位置は動く。
    /// </remarks>
    public static void EnsureAgreed(Stream stream)
    {
        if (Agreement(stream) == ZipDirectories.Split)
        {
            throw new SplitDirectoryException();
        }
    }

    /// <summary>部品がそろって同じ目録を読むか (#396)。流れの位置は動く。</summary>
    public static ZipDirectories Agreement(Stream stream) => TryExamine(stream).State;

    /// <summary>
    /// 終わりの記録の直前に ZIP64 の位置の記録があるか (#396)。読めなければ <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// 自己解凍のプログラムは ZIP64 の記録を読まない。位置の記録のある書庫では、ほかの部品と別の目録を読みうる。
    /// </remarks>
    public static bool HasZip64Locator(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            return HasZip64Locator(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>終わりの記録の直前に ZIP64 の位置の記録があるか (#396)。流れの位置は動く。</summary>
    public static bool HasZip64Locator(Stream stream)
    {
        try
        {
            var length = stream.Length;
            if (length < EndLength)
            {
                return false;
            }

            var window = (int)Math.Min(length, SearchLength);
            var buffer = new byte[window];
            stream.Position = length - window;
            stream.ReadExactly(buffer);

            var i = FindEnd(stream, buffer, length);
            return i >= 0 && HasSignature(stream, length - window + i - LocatorLength, Zip64LocatorSignature);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or EndOfStreamException
                                   or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// 先頭に書庫でない塊が付いているか。付いていれば自己解凍書庫とみなす (#32)。
    /// </summary>
    /// <remarks>
    /// ふつうの ZIP は局所ヘッダか終端レコードで始まる。そうでなければ、
    /// 何かが前に付いている。4バイト見るだけで済む。
    /// </remarks>
    public static bool HasPrefix(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);

            if (stream.Length < 4)
            {
                return false;
            }

            Span<byte> head = stackalloc byte[4];
            stream.ReadExactly(head);

            var signature = BinaryPrimitives.ReadUInt32LittleEndian(head);
            return signature != LocalSignature && signature != EndSignature;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>
    /// 名前では分からないファイルの中に ZIP が入っているか (#32)。
    /// </summary>
    /// <remarks>
    /// 末尾の終端レコードを探すだけ。自己解凍書庫かどうかの判断に使う。
    /// </remarks>
    public static bool LooksLikeZip(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);

            var length = stream.Length;

            if (length < EndLength)
            {
                return false;
            }

            var window = (int)Math.Min(length, SearchLength);
            var buffer = new byte[window];
            stream.Position = length - window;
            stream.ReadExactly(buffer);

            return FindEnd(stream, buffer, length) >= 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or EndOfStreamException)
        {
            return false;
        }
    }

    /// <summary>
    /// 部品 (.NET、SharpZipLib、SharpCompress) がそろって同じ目録を読むかを確かめる (#396)。
    /// </summary>
    /// <remarks>読めないときは <see cref="ZipDirectories.Unknown"/>。</remarks>
    public static ZipDirectories Examine(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            return TryExamine(stream).State;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException)
        {
            return ZipDirectories.Unknown;
        }
    }

    /// <summary>
    /// 一覧を読むために開く。目録が食い違っていても開き、そのことを <paramref name="split"/> で返す (#396)。
    /// </summary>
    /// <remarks>一覧と展開は .NET の部品で読み、どちらも同じ目録を読む。一覧だけは見せる。</remarks>
    public static Stream OpenForListing(string path, out bool split)
    {
        var stream = ArchiveFile.OpenRead(path);
        try
        {
            var (shift, state) = TryExamine(stream);
            split = state == ZipDirectories.Split;
            stream.Position = 0;
            return shift > 0 ? new OffsetStream(stream, shift) : stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>確かめる。末尾が読めないときは、いままでと同じく「ずれ無し、確かめられない」とする。</summary>
    private static (long Shift, ZipDirectories State) TryExamine(Stream stream)
    {
        try
        {
            return Examine(stream);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException or NotSupportedException
                                   or OverflowException)
        {
            return (0, ZipDirectories.Unknown);
        }
    }

    private static long Detect(Stream stream) => TryExamine(stream).Shift;

    /// <summary>
    /// 先頭のずれを求め、部品がそろって同じ目録を読むかを確かめる (#396)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 書庫の作りが仕様に合っているかではなく、部品がどこを読むかで決める。部品ごとの読み方の違いは次のとおり。
    /// </para>
    /// <list type="bullet">
    /// <item>終わりの記録: <see cref="FindEnd"/> はコメントの長さと辻褄の合うものを選ぶが、部品は後ろから最初の署名を使う</item>
    /// <item>目録の位置: SharpZipLib は、ZIP64 として読まない書庫で目録が終わりの記録の直前で終わっていないと、
    /// 直前で終わる位置に読み直す。.NET は書かれた位置を読む</item>
    /// <item>ZIP64: SharpZipLib は位置の記録があれば ZIP64 の値を使い、.NET は 32 ビットの欄に目印の値があるときだけ使う</item>
    /// </list>
    /// <para>
    /// そこで、終わりの記録の選び方がそろい、目録がちょうど直前 (ZIP64 なら ZIP64 の記録の直前) で終わり、
    /// 32 ビットの欄が目印の値か ZIP64 の値と同じで、求めた先頭のずれをそのまま隠せるときだけ、そろうとする。
    /// </para>
    /// </remarks>
    private static (long Shift, ZipDirectories State) Examine(Stream stream)
    {
        var length = stream.Length;

        if (length < EndLength)
        {
            return (0, ZipDirectories.Unknown);
        }

        var window = (int)Math.Min(length, SearchLength);
        var buffer = new byte[window];
        stream.Position = length - window;
        stream.ReadExactly(buffer);

        var i = FindEnd(stream, buffer, length);
        if (i < 0)
        {
            return (0, ZipDirectories.Unknown);
        }

        var layout = ReadLayout(stream, buffer.AsSpan(i), length - window + i);
        var shift = Shift(stream, layout);

        // 部品は、コメントの長さを見ずに後ろから最初の署名を使う
        if (LastEnd(buffer) != i)
        {
            return (shift, ZipDirectories.Split);
        }

        if (layout.State != ZipDirectories.Agree)
        {
            return (shift, layout.State);
        }

        // 求めた先頭のずれを隠せないと、SharpZipLib は自分で読む位置を直し、.NET は書かれた位置を読む
        return layout.Start >= 0 && layout.Start == shift
            ? (shift, ZipDirectories.Agree)
            : (shift, ZipDirectories.Split);
    }

    /// <summary>後ろから最初に見つかる終わりの記録の署名の位置。部品はこれを使う。無ければ -1。</summary>
    private static int LastEnd(ReadOnlySpan<byte> buffer)
    {
        for (var i = buffer.Length - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[i..]) == EndSignature)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>終わりの記録から辿った目録の場所。</summary>
    /// <param name="DirectoryEnd">目録が終わっているはずの位置 (ファイルの先頭から)。ZIP64 では ZIP64 の記録の位置、そうでなければ終わりの記録の位置。</param>
    /// <param name="Size">目録の大きさ。</param>
    /// <param name="Offset">書かれた目録の位置 (ZIP の始まりから)。</param>
    /// <param name="State">終わりの記録の欄どうしが同じ目録を指すか。</param>
    private readonly record struct Layout(long DirectoryEnd, long Size, long Offset, ZipDirectories State)
    {
        /// <summary>目録がちょうど <see cref="DirectoryEnd"/> で終わるとしたときの、先頭のずれ。</summary>
        public long Start => DirectoryEnd - Size - Offset;
    }

    /// <summary>終わりの記録 (<paramref name="end"/> の位置、中身は <paramref name="record"/>) から、目録の場所を読む。</summary>
    private static Layout ReadLayout(Stream stream, ReadOnlySpan<byte> record, long end)
    {
        var disk = BinaryPrimitives.ReadUInt16LittleEndian(record[4..]);
        var directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(record[6..]);
        var diskEntries = BinaryPrimitives.ReadUInt16LittleEndian(record[8..]);
        var entries = BinaryPrimitives.ReadUInt16LittleEndian(record[10..]);
        var size = BinaryPrimitives.ReadUInt32LittleEndian(record[12..]);
        var offset = BinaryPrimitives.ReadUInt32LittleEndian(record[16..]);

        switch (ReadZip64(stream, end, out var zip64))
        {
            case Zip64Found.Yes:
                // 32 ビットの欄は目印の値か、ZIP64 の記録と同じ値。どの部品がどちらの値を採っても同じ目録を読む。
                // 目録は ZIP64 の記録のちょうど直前で終わる
                var agree = Same(disk, zip64.Disk) && Same(directoryDisk, zip64.DirectoryDisk)
                            && Same(diskEntries, zip64.DiskEntries) && Same(entries, zip64.Entries)
                            && Same(size, zip64.Size) && Same(offset, zip64.Offset)
                            && zip64.Offset + zip64.Size == zip64.Recorded;
                return new Layout(zip64.Position, zip64.Size, zip64.Offset,
                    agree ? ZipDirectories.Agree : ZipDirectories.Split);

            case Zip64Found.Broken:
                // 位置の記録はあるのに、ZIP64 の記録が辿れない。SharpZipLib と .NET で読み方が分かれる
                return new Layout(end, size, offset, ZipDirectories.Split);

            default:
                // 目印の値があるのに ZIP64 の記録が無い。どの部品も開けない
                var marked = diskEntries == ushort.MaxValue || entries == ushort.MaxValue
                             || size == uint.MaxValue || offset == uint.MaxValue;
                return new Layout(end, size, offset, marked ? ZipDirectories.Unknown : ZipDirectories.Agree);
        }
    }

    /// <summary>16 ビットの欄が、目印の値か ZIP64 の記録と同じ値か。</summary>
    private static bool Same(ushort value, long full) => value == ushort.MaxValue || value == full;

    /// <summary>32 ビットの欄が、目印の値か ZIP64 の記録と同じ値か。</summary>
    private static bool Same(uint value, long full) => value == uint.MaxValue || value == full;

    /// <summary>
    /// 末尾を読んだ <paramref name="buffer"/> の中から、終端レコードの位置を探す。無ければ -1。
    /// </summary>
    /// <param name="buffer">ファイルの末尾。終端レコードとコメントが入る長さ (<see cref="SearchLength"/>) か、ファイル全体。</param>
    /// <param name="stream">ファイル。中央ディレクトリの署名を確かめるのに使う。</param>
    /// <param name="length">ファイルの長さ。</param>
    /// <remarks>
    /// <para>
    /// 後ろから署名を探す。コメントの中に同じ並びが入っていることがあるため、
    /// 見つけた位置がコメントの長さと辻褄が合うところまで確かめる。
    /// コメントの中の偽物も「末尾を越えない」は満たすので、後ろから最初に見つけたものを採ると取り違える (#263)。
    /// 次の順に採る。
    /// </para>
    /// <list type="number">
    /// <item>コメントの終わりがファイルの末尾とぴったり合い、指す先に中央ディレクトリがあるもの</item>
    /// <item>コメントの終わりがファイルの末尾とぴったり合うもの (ZIP64 など、指す先を確かめられないもの)</item>
    /// <item>末尾を越えないもの (終端レコードの後ろに余りのある書庫)</item>
    /// </list>
    /// <para>
    /// 偽物がコメントのちょうど末尾にあると、偽物も末尾とぴったり合う。中央ディレクトリまで確かめて見分ける。
    /// </para>
    /// </remarks>
    internal static int FindEnd(Stream stream, ReadOnlySpan<byte> buffer, long length)
    {
        var start = length - buffer.Length;
        var exact = -1;
        var fallback = -1;

        for (var i = buffer.Length - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[i..]) != EndSignature)
            {
                continue;
            }

            var comment = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(i + 20)..]);
            var tail = start + i + EndLength + comment;

            if (tail == length)
            {
                // 中央ディレクトリは終端レコードの直前で終わっている。先頭に何か付いていても、この位置は変わらない
                long size = BinaryPrimitives.ReadUInt32LittleEndian(buffer[(i + 12)..]);
                if (size > 0 && size != uint.MaxValue
                    && HasSignature(stream, start + i - size, DirectorySignature))
                {
                    return i;
                }

                if (exact < 0)
                {
                    exact = i;
                }
            }
            else if (tail < length && fallback < 0)
            {
                fallback = i;
            }
        }

        return exact >= 0 ? exact : fallback;
    }

    private enum Zip64Found
    {
        /// <summary>位置の記録が無い。ZIP64 の書庫ではない。</summary>
        No,

        /// <summary>位置の記録が指す ZIP64 の記録を読めた。</summary>
        Yes,

        /// <summary>位置の記録はあるが、ZIP64 の記録が位置の記録の直前に無い。</summary>
        Broken,
    }

    /// <summary>ZIP64 の終わりの記録の中身。</summary>
    /// <param name="Position">ZIP64 の記録の位置 (ファイルの先頭から)。</param>
    /// <param name="Recorded">位置の記録に書かれた ZIP64 の記録の位置 (ZIP の始まりから)。</param>
    private readonly record struct Zip64Record(
        long Position, long Recorded, long Disk, long DirectoryDisk, long DiskEntries, long Entries, long Size, long Offset);

    /// <summary>
    /// 終わりの記録の直前にある位置の記録と、その直前にある ZIP64 の終わりの記録を読む。
    /// </summary>
    /// <remarks>
    /// 部品は終わりの記録のちょうど 20 バイト前に位置の記録を探す。ZIP64 の記録は長さが変わるので、
    /// 長さの欄の値で終わりがちょうど位置の記録に合うものを、後ろから探す。以前は署名だけで探しており、
    /// ZIP64 の記録が 2 つあると部品と別のものを読んでいた (#396)。
    /// </remarks>
    private static Zip64Found ReadZip64(Stream stream, long end, out Zip64Record record)
    {
        record = default;
        var locator = end - LocatorLength;

        if (!HasSignature(stream, locator, Zip64LocatorSignature))
        {
            return Zip64Found.No;
        }

        Span<byte> head = stackalloc byte[LocatorLength];
        stream.Position = locator;
        stream.ReadExactly(head);
        var recorded = BinaryPrimitives.ReadInt64LittleEndian(head[8..]);

        var window = (int)Math.Min(locator, SearchLength);
        var buffer = new byte[window];
        stream.Position = locator - window;
        stream.ReadExactly(buffer);

        for (var i = window - Zip64EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != Zip64EndSignature
                || BinaryPrimitives.ReadUInt64LittleEndian(buffer.AsSpan(i + 4)) != (ulong)(window - i - 12))
            {
                continue;
            }

            var r = buffer.AsSpan(i);
            record = new Zip64Record(
                locator - window + i,
                recorded,
                BinaryPrimitives.ReadUInt32LittleEndian(r[16..]),
                BinaryPrimitives.ReadUInt32LittleEndian(r[20..]),
                BinaryPrimitives.ReadInt64LittleEndian(r[24..]),
                BinaryPrimitives.ReadInt64LittleEndian(r[32..]),
                BinaryPrimitives.ReadInt64LittleEndian(r[40..]),
                BinaryPrimitives.ReadInt64LittleEndian(r[48..]));

            return record.Size >= 0 && record.Offset >= 0 && record.Recorded >= 0
                ? Zip64Found.Yes
                : Zip64Found.Broken;
        }

        return Zip64Found.Broken;
    }

    /// <summary>ずれ幅を求め、そこが本当に ZIP の始まりかを確かめる。</summary>
    /// <remarks>
    /// 目録は <see cref="Layout.DirectoryEnd"/> のちょうど直前で終わっているはず。ZIP64 では ZIP64 の記録の直前で、
    /// 以前は終わりの記録の直前として計算しており、ずれを ZIP64 の記録の分だけ多く見積もっていた (#396)。
    /// </remarks>
    private static long Shift(Stream stream, Layout layout)
    {
        var actual = layout.DirectoryEnd - layout.Size;
        var shift = actual - layout.Offset;

        if (shift <= 0 || actual < 0 || shift >= stream.Length)
        {
            return 0;
        }

        // 中身が空の書庫では目録も局所ヘッダーも無い。終わりの記録の照合で十分
        if (layout.Size == 0)
        {
            return shift;
        }

        // 求めた位置に目録が、ずらした先頭に局所ヘッダーがあるか。どちらも合って初めて、ずれ幅を信じる。
        // 以前は目録の照合を「または」で書いていて、いつも通っていた (#396)
        return HasSignature(stream, actual, DirectorySignature) && HasSignature(stream, shift, LocalSignature)
            ? shift
            : 0;
    }

    private static bool HasSignature(Stream stream, long position, uint signature)
    {
        if (position < 0 || position + 4 > stream.Length)
        {
            return false;
        }

        Span<byte> head = stackalloc byte[4];
        stream.Position = position;
        stream.ReadExactly(head);

        return BinaryPrimitives.ReadUInt32LittleEndian(head) == signature;
    }
}
