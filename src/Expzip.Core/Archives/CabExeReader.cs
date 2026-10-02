using System.Buffers.Binary;
using System.IO;

namespace Expzip.Archives;

/// <summary>
/// CAB が入った exe を見分ける (#182)。中身の読み取りは <see cref="CabReader"/> に任せる。
/// </summary>
/// <remarks>
/// <para>
/// 取り出すプログラムと CAB を 1 つにした exe は、作るツールによって CAB の置き場所が違う。
/// 実物で確かめた置き場所は次の 2 通り。
/// </para>
/// <list type="bullet">
/// <item>部品 (リソース) の <c>RCDATA</c> の <c>CABINET</c>。Windows に付いている IExpress で作った exe</item>
/// <item>読み込まれない部分の頭。区画の後ろに付け足したもの (古い InstallShield の
/// PackageForTheWeb など) と、区画の中の読み込まれない余り (Microsoft の再配布パッケージなど)</item>
/// </list>
/// <para>
/// 後者は、読み込まれない部分の頭から 64 KB 以内に、見出しが正しい CAB があるものだけにする。
/// 実物では、どれも 6 KB 以内にあった。奥まで探すと、別の中身にたまたま入っている CAB を拾う。
/// </para>
/// <para>
/// 一覧には CAB の中のファイルをそのまま並べる。自己解凍書庫 (#32) と同じ見せ方。
/// </para>
/// </remarks>
internal static class CabExeReader
{
    /// <summary>IExpress が CAB を入れる部品の種類 (RCDATA) と名前。</summary>
    private const int RawData = 10;

    private const string IExpressName = "CABINET";

    /// <summary>読み込まれない部分の頭から、CAB を探す範囲。</summary>
    private const int SearchLength = 64 * 1024;

    /// <summary>CAB の見出しの大きさ。</summary>
    private const int HeaderLength = 36;

    /// <summary>CAB が入った exe か。</summary>
    public static bool IsCabExe(string path) => Try(path, static stream => Find(stream) is not null);

    /// <summary>IExpress で作った exe か。</summary>
    /// <remarks>
    /// 見分けの順番のために分けてある (<see cref="ArchiveFormats"/>)。IExpress の CAB は無圧縮で
    /// 作ることができ、中に ZIP を入れると、exe の末尾近くに ZIP の終端レコードが見える (実物で確認)。
    /// 自己解凍書庫の ZIP と取り違えないよう、ZIP より先に見る。
    /// </remarks>
    public static bool IsIExpress(string path) => Try(path, static stream => FindIExpress(stream) is not null);

    private static bool Try(string path, Func<Stream, bool> test)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            return test(stream);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>中の CAB の位置。見つからなければ <see langword="null"/>。</summary>
    public static long? Find(string path)
    {
        using var stream = ArchiveFile.OpenRead(path);
        return Find(stream);
    }

    private static long? Find(Stream stream) => FindIExpress(stream) ?? FindUnmapped(stream);

    /// <summary>IExpress の CAB (部品の RCDATA の CABINET) の位置。</summary>
    private static long? FindIExpress(Stream stream)
        => PeReader.FindResource(stream, RawData, IExpressName) is { } resource
           && IsCabAt(stream, resource.Offset, resource.Offset + resource.Size)
            ? resource.Offset
            : null;

    /// <summary>読み込まれない部分の頭にある CAB の位置。</summary>
    private static long? FindUnmapped(Stream stream)
    {
        if (PeReader.MappedEnd(stream) is not { } start || start >= stream.Length)
        {
            return null;
        }

        var buffer = new byte[(int)Math.Min(SearchLength + HeaderLength, stream.Length - start)];
        stream.Position = start;
        stream.ReadExactly(buffer);

        var from = 0;
        while (from < Math.Min(SearchLength, buffer.Length))
        {
            var hit = buffer.AsSpan(from).IndexOf("MSCF\0\0\0\0"u8);
            if (hit < 0 || from + hit >= SearchLength)
            {
                return null;
            }

            var at = start + from + hit;
            if (IsCabAt(stream, at, stream.Length))
            {
                return at;
            }

            from += hit + 1;
        }

        return null;
    }

    /// <summary>
    /// 位置から、見出しが正しく <paramref name="end"/> までに収まる CAB が始まるか。
    /// </summary>
    /// <remarks>
    /// 見出しの版 (1.3)、ファイルとフォルダーの数、大きさを見る。
    /// 分割された CAB の 2 つ目以降 (前の CAB がある印) は、単独では読めないので外す。
    /// </remarks>
    private static bool IsCabAt(Stream stream, long at, long end)
    {
        if (at + HeaderLength > end)
        {
            return false;
        }

        Span<byte> header = stackalloc byte[HeaderLength];
        stream.Position = at;
        stream.ReadExactly(header);

        const int hasPrevious = 0x0001;
        var size = BinaryPrimitives.ReadUInt32LittleEndian(header[8..]);
        var filesAt = BinaryPrimitives.ReadUInt32LittleEndian(header[16..]);
        var folders = BinaryPrimitives.ReadUInt16LittleEndian(header[26..]);
        var files = BinaryPrimitives.ReadUInt16LittleEndian(header[28..]);
        var flags = BinaryPrimitives.ReadUInt16LittleEndian(header[30..]);

        return header[..8].SequenceEqual("MSCF\0\0\0\0"u8)
               && header[24] == 3 && header[25] == 1
               && folders > 0 && files > 0
               && (flags & hasPrevious) == 0
               && filesAt >= HeaderLength && filesAt < size
               && at + size <= end;
    }

    /// <summary>中の CAB を頭から読む流れを開く。</summary>
    /// <exception cref="InvalidDataException">CAB が見つからない場合。</exception>
    public static Func<Stream> Opener(string path)
    {
        var offset = Find(path) ?? throw new InvalidDataException();
        return () => new OffsetStream(ArchiveFile.OpenRead(path), offset);
    }
}
