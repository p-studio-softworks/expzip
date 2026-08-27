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
            using var stream = File.OpenRead(path);
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
    /// ふつうの ZIP では、そのままのファイルを返す。
    /// </remarks>
    public static Stream Open(string path)
    {
        var offset = Detect(path);
        var stream = File.OpenRead(path);

        return offset > 0 ? new OffsetStream(stream, offset) : stream;
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
            using var stream = File.OpenRead(path);

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
            using var stream = File.OpenRead(path);

            var length = stream.Length;

            if (length < EndLength)
            {
                return false;
            }

            var window = (int)Math.Min(length, SearchLength);
            var buffer = new byte[window];
            stream.Position = length - window;
            stream.ReadExactly(buffer);

            for (var i = window - EndLength; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != EndSignature)
                {
                    continue;
                }

                var comment = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 20));

                if (length - window + i + EndLength + comment <= length)
                {
                    return true;
                }
            }

            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or EndOfStreamException)
        {
            return false;
        }
    }

    private static long Detect(Stream stream)
    {
        var length = stream.Length;

        if (length < EndLength)
        {
            return 0;
        }

        var window = (int)Math.Min(length, SearchLength);
        var buffer = new byte[window];
        stream.Position = length - window;
        stream.ReadExactly(buffer);

        // 後ろから署名を探す。コメントの中に同じ並びが入っていることがあるため、
        // 見つけた位置がコメントの長さと辻褄が合うところまで確かめる
        for (var i = window - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != EndSignature)
            {
                continue;
            }

            var comment = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 20));
            var end = length - window + i;

            if (end + EndLength + comment > length)
            {
                continue;
            }

            long directorySize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 12));
            long directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 16));

            // ZIP64 の書庫はここに 0xFFFFFFFF が入り、本当の値は別のレコードにある
            if (directorySize == uint.MaxValue || directoryOffset == uint.MaxValue)
            {
                if (!TryReadZip64(stream, length, out directorySize, out directoryOffset))
                {
                    return 0;
                }
            }

            return Shift(stream, end, directorySize, directoryOffset);
        }

        return 0;
    }

    /// <summary>ZIP64 の終端レコードから、中央ディレクトリの大きさと位置を読む。</summary>
    private static bool TryReadZip64(Stream stream, long length, out long size, out long offset)
    {
        size = 0;
        offset = 0;

        // 終端レコードの手前あたりを後ろから探す。位置を辿る道もあるが、
        // その位置自体がずれている可能性があるため、署名で探すほうが確実
        var window = (int)Math.Min(length, SearchLength + 4096);
        var buffer = new byte[window];
        stream.Position = length - window;
        stream.ReadExactly(buffer);

        for (var i = window - 56; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != Zip64EndSignature)
            {
                continue;
            }

            size = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i + 40));
            offset = BinaryPrimitives.ReadInt64LittleEndian(buffer.AsSpan(i + 48));
            return size >= 0 && offset >= 0;
        }

        return false;
    }

    /// <summary>ずれ幅を求め、そこが本当に ZIP の始まりかを確かめる。</summary>
    private static long Shift(Stream stream, long endPosition, long size, long offset)
    {
        var actual = endPosition - size;
        var shift = actual - offset;

        if (shift <= 0 || actual < 0 || shift >= stream.Length)
        {
            return 0;
        }

        // 求めた位置に中央ディレクトリが、ずらした先頭に局所ヘッダがあるか。
        // どちらも合って初めて、ずれ幅を信じる
        if (!HasSignature(stream, actual, DirectorySignature)
            && !HasSignature(stream, endPosition, EndSignature))
        {
            return 0;
        }

        // 中身が空の書庫では局所ヘッダが無い。その場合はここまでの照合で十分
        return size == 0 || HasSignature(stream, shift, LocalSignature) ? shift : 0;
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
