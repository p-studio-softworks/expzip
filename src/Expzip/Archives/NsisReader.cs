using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Expzip.Archives;

/// <summary>
/// NSIS 製インストーラーの中身を一覧にする (#68)。
/// </summary>
/// <remarks>
/// <para>
/// NSIS は書庫ではなく<b>インストーラーの実行ファイル</b>。中に取り出す手順が
/// 命令の並びとして入っていて、そのうち「ファイルを取り出す」命令を拾うと
/// 中身の一覧が得られる。7-Zip がしているのと同じことをする。
/// </para>
/// <para>
/// 組み立ては次の通り。実物で1バイトずつ突き合わせて確かめてある。
/// </para>
/// <list type="number">
/// <item>末尾寄りにある<b>先頭ヘッダ</b>を探す。<c>DEADBEEF</c> + <c>NullsoftInst</c></item>
/// <item>その直後の塊を展開する。展開後の大きさは先頭ヘッダに書いてある</item>
/// <item>展開した先頭に<b>塊の位置表</b>がある。命令と文字列表の場所が分かる</item>
/// <item>命令を順に見て、番号 20 (ファイルを取り出す) を拾う</item>
/// <item>命令の1つ目の引数が名前の位置、2つ目が中身の位置</item>
/// </list>
/// <para>
/// 同じファイルが複数の分岐から取り出されることがあるため、<b>中身の位置で畳む</b>。
/// 実物 (273MB、39件) で 7-Zip の件数とちょうど一致した。
/// </para>
/// <para>
/// いまは一覧までで、中身の取り出しには対応していない。まとめ圧縮 (LZMA) の書庫も
/// 一覧を出せない。段を分けて進めている。
/// </para>
/// </remarks>
internal static class NsisReader
{
    /// <summary>先頭ヘッダのしるし。</summary>
    private static ReadOnlySpan<byte> Signature =>
        [0xEF, 0xBE, 0xAD, 0xDE, (byte)'N', (byte)'u', (byte)'l', (byte)'l',
         (byte)'s', (byte)'o', (byte)'f', (byte)'t', (byte)'I', (byte)'n',
         (byte)'s', (byte)'t'];

    /// <summary>先頭ヘッダの長さ。旗・しるし・展開後の大きさ・全体の大きさ。</summary>
    private const int FirstHeaderLength = 28;

    /// <summary>しるしを探す範囲。実物の自己解凍部は数百KBで収まる。</summary>
    private const int SearchLimit = 4 * 1024 * 1024;

    /// <summary>1つの命令の長さ。番号 + 引数6つ。</summary>
    private const int EntryLength = 28;

    /// <summary>ファイルを取り出す命令の番号。</summary>
    private const uint ExtractFile = 20;

    /// <summary>塊の位置表の要素数。</summary>
    private const int BlockCount = 8;

    /// <summary>位置表のうち、命令と文字列表と言語表の位置。</summary>
    private const int EntriesBlock = 2;
    private const int StringsBlock = 3;
    private const int LanguagesBlock = 4;

    /// <summary>ヘッダの展開後がこれを超えるものは扱わない。細工された値への備え。</summary>
    private const int HeaderLimit = 64 * 1024 * 1024;

    /// <summary>NSIS 製の実行ファイルなら、先頭ヘッダの位置を返す。違えば -1。</summary>
    public static long FindHeader(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);

            var window = (int)Math.Min(stream.Length, SearchLimit);

            if (window < FirstHeaderLength)
            {
                return -1;
            }

            var buffer = new byte[window];
            stream.ReadExactly(buffer);

            var at = buffer.AsSpan().IndexOf(Signature);

            // しるしは先頭ヘッダの4バイト目から始まる
            return at >= 4 ? at - 4 : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or EndOfStreamException)
        {
            return -1;
        }
    }

    /// <summary>NSIS 製かどうか。</summary>
    public static bool IsNsis(string path) => FindHeader(path) >= 0;

    /// <summary>中身を一覧にする。</summary>
    /// <exception cref="InvalidDataException">NSIS として読めない場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var layout = ReadLayout(path, cancellationToken);
        var builder = new ArchiveTreeBuilder(path);
        var done = 0;

        foreach (var file in layout.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            builder.AddFile(
                ToArchivePath(file.Name),
                length: 0,
                compressedLength: file.StoredLength,
                compressedLengthKnown: true,
                lastWriteTime: default,
                isEncrypted: false,
                lengthKnown: false);

            if (++done % 100 == 0)
            {
                progress?.Report(new OpenProgress(done, layout.Files.Count));
            }
        }

        progress?.Report(new OpenProgress(done, done));

        return builder.Build(path, ArchiveFormat.Nsis, totalCompressedLength: null,
            isSelfExtracting: true);
    }

    /// <summary>組み立てを読む。一覧と取り出しで共通に使う。</summary>
    /// <exception cref="InvalidDataException">NSIS として読めない場合。</exception>
    public static NsisLayout ReadLayout(string path, CancellationToken cancellationToken = default)
    {
        var head = FindHeader(path);

        if (head < 0)
        {
            throw new InvalidDataException(Localization.Strings.NsisNotSupported);
        }

        var (header, dataStart) = ReadHeader(path, head);

        var table = new (uint Offset, uint Count)[BlockCount];

        for (var i = 0; i < BlockCount; i++)
        {
            var at = 4 + (i * 8);
            table[i] = (
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at)),
                BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(at + 4)));
        }

        var (entriesAt, entryCount) = table[EntriesBlock];
        var stringsAt = table[StringsBlock].Offset;
        var languagesAt = table[LanguagesBlock].Offset;

        if (entriesAt > header.Length || stringsAt > languagesAt || languagesAt > header.Length)
        {
            throw new InvalidDataException(Localization.Strings.NsisNotSupported);
        }

        var pool = header[(int)stringsAt..(int)languagesAt];

        // 文字列表が UTF-16 かどうかは、2バイト目が 0 かどうかで見分ける。
        // 名前は ASCII で始まることがほとんどのため、これで足りる
        var unicode = pool.Length > 3 && pool[1] == 0 && pool[3] == 0;
        var strings = new NsisStrings(pool, unicode);

        // 同じファイルが複数の分岐から取り出されることがある。中身の位置で畳む
        var seen = new HashSet<uint>();
        var files = new List<NsisFile>();

        using var source = File.OpenRead(path);

        for (var i = 0u; i < entryCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var at = entriesAt + (i * EntryLength);

            if (at + EntryLength > header.Length)
            {
                break;
            }

            var code = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan((int)at));

            if (code != ExtractFile)
            {
                continue;
            }

            var nameAt = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan((int)at + 8));
            var dataAt = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan((int)at + 12));

            if (!seen.Add(dataAt))
            {
                continue;
            }

            var name = strings.Read(nameAt);

            if (name.Length == 0)
            {
                continue;
            }

            files.Add(new NsisFile(name, dataAt, StoredLength(source, dataStart + dataAt)));
        }

        return new NsisLayout(dataStart, files);
    }

    /// <summary>塊に入っている大きさ (圧縮後) を読む。読めない場合は 0。</summary>
    /// <remarks>
    /// 展開後の大きさは書いていないため、そちらは実際に展開するまで分からない。
    /// 一覧のためだけに書庫を丸ごと展開するのは高くつく。
    /// </remarks>
    private static long StoredLength(Stream source, long at)
    {
        if (at < 0 || at + 4 > source.Length)
        {
            return 0;
        }

        try
        {
            source.Position = at;

            Span<byte> lead = stackalloc byte[4];
            source.ReadExactly(lead);

            return BinaryPrimitives.ReadUInt32LittleEndian(lead) & 0x7FFFFFFF;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            return 0;
        }
    }

    /// <summary>先頭ヘッダの直後にある塊を展開し、中身の領域の始まりも返す。</summary>
    private static (byte[] Header, long DataStart) ReadHeader(string path, long head)
    {
        using var stream = File.OpenRead(path);

        stream.Position = head;

        Span<byte> first = stackalloc byte[FirstHeaderLength];
        stream.ReadExactly(first);

        var headerSize = BinaryPrimitives.ReadUInt32LittleEndian(first[20..]);

        if (headerSize is 0 or > HeaderLimit)
        {
            throw new InvalidDataException(Localization.Strings.NsisNotSupported);
        }

        Span<byte> lead = stackalloc byte[4];
        stream.ReadExactly(lead);

        var value = BinaryPrimitives.ReadUInt32LittleEndian(lead);
        var compressed = (value & 0x80000000) != 0;
        var blockSize = value & 0x7FFFFFFF;

        if (!compressed)
        {
            // 無圧縮。読んだ4バイトは大きさそのもの
            var plain = new byte[headerSize];
            stream.ReadExactly(plain);
            return (plain, stream.Position);
        }

        if (blockSize > HeaderLimit)
        {
            throw new InvalidDataException(Localization.Strings.NsisNotSupported);
        }

        var payload = new byte[blockSize];
        stream.ReadExactly(payload);

        var header = new byte[headerSize];

        try
        {
            using var source = new MemoryStream(payload);

            // NSIS の deflate は生 (zlib の包みが無い)
            using var inflate = new DeflateStream(source, CompressionMode.Decompress);
            inflate.ReadExactly(header);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            // まとめ圧縮 (LZMA) の書庫はここで落ちる。まだ対応していない
            throw new InvalidDataException(Localization.Strings.NsisNotSupported, ex);
        }

        // 中身の領域はヘッダの塊のすぐ後ろから始まる
        return (header, head + FirstHeaderLength + 4 + blockSize);
    }

    /// <summary>NSIS の名前を書庫内のパスに直す。</summary>
    /// <remarks>
    /// 取り出し先は <c>$INSTDIR\...</c> のように変数で始まる。区切りを揃え、
    /// 先頭の <c>$</c> はそのまま残す。どこへ入るはずのものかが分かるほうがよい。
    /// </remarks>
    public static string ToArchivePath(string name)
        => name.Replace('\\', '/').TrimStart('/');
}
