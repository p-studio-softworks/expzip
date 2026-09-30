using System.Buffers.Binary;
using System.IO;

namespace Expzip.Archives;

/// <summary>exe の後ろに付け足された MSI の 1 つ。</summary>
/// <param name="Name">一覧に出す名前。</param>
/// <param name="Offset">exe の中の位置。</param>
/// <param name="Length">大きさ。</param>
internal readonly record struct EmbeddedMsi(string Name, long Offset, long Length);

/// <summary>
/// MSI が入った exe を読む (#182)。後ろに付け足された MSI を並べる。読み取りのみ。
/// </summary>
/// <remarks>
/// <para>
/// Advanced Installer や InstallShield などで作ったインストーラーには、MSI をそのまま
/// exe の後ろに付け足したものがある (実物で確認)。MSI は OLE 複合ファイルという形式で、
/// 見出しから大きさを計算できるので、そこだけを切り出して MSI として並べる。
/// 並べた MSI は、書庫の中の書庫として開ける。
/// </para>
/// <para>
/// 同じ形式のファイルには MSI の差分 (.mst) などもある。見出しの「種類の番号」(CLSID) が
/// MSI 本体のものだけを拾う。
/// </para>
/// <para>
/// 名前は exe のどこにも書かれていないので、exe の名前から付ける。
/// exe の部品 (リソース) として名前付きで入っているもの (VNC Viewer など) は、
/// exe / dll の見方 (#183) で名前のまま並ぶので、ここでは扱わない。
/// </para>
/// </remarks>
internal static class MsiExeReader
{
    /// <summary>OLE 複合ファイルのしるし。</summary>
    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];

    /// <summary>MSI 本体の種類の番号 ({000C1084-0000-0000-C000-000000000046})。差分は 1082、更新は 1086。</summary>
    private static readonly byte[] MsiClsid =
        [0x84, 0x10, 0x0C, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x46];

    private const uint FreeSector = 0xFFFFFFFF;
    private const uint EndOfChain = 0xFFFFFFFE;

    /// <summary>MSI が入った exe か。</summary>
    public static bool IsMsiExe(string path)
    {
        try
        {
            return Find(path, firstOnly: true).Count > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary>後ろに付け足された MSI を探す。</summary>
    /// <param name="firstOnly">見分けだけのときは、1 つ見つけたら止める。</param>
    public static IReadOnlyList<EmbeddedMsi> Find(
        string path, bool firstOnly = false, CancellationToken cancellationToken = default)
    {
        using var stream = ArchiveFile.OpenRead(path);
        var found = new List<(long Offset, long Length)>();

        if (PeReader.OverlayStart(stream) is not { } start)
        {
            return [];
        }

        // 付け足されたデータを頭からなめて、しるしを探す。しるしが塊の切れ目にまたがっても
        // 見落とさないよう、塊を少し重ねて読む
        var buffer = new byte[4 * 1024 * 1024];
        var position = start;

        while (position < stream.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            stream.Position = position;
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read < Signature.Length)
            {
                break;
            }

            var span = buffer.AsSpan(0, read);
            var next = position + read - (Signature.Length - 1);
            var searchFrom = 0;

            while (true)
            {
                var hit = span[searchFrom..].IndexOf(Signature);
                if (hit < 0)
                {
                    break;
                }

                var at = position + searchFrom + hit;
                if (MsiLength(stream, at) is { } length)
                {
                    found.Add((at, length));
                    if (firstOnly)
                    {
                        return Name(path, found);
                    }

                    // MSI の中身は飛ばす。中にも同じしるしが出てくることがある
                    next = at + length;
                    break;
                }

                searchFrom += hit + 1;
            }

            if (next <= position)
            {
                break;
            }

            position = next;
        }

        return Name(path, found);
    }

    /// <summary>名前を付ける。1 つなら「exe の名前.msi」、複数なら番号を添える。</summary>
    private static IReadOnlyList<EmbeddedMsi> Name(string path, List<(long Offset, long Length)> found)
    {
        var stem = PeReader.SafeName(Path.GetFileNameWithoutExtension(path));
        return found.Count == 1
            ? [new EmbeddedMsi($"{stem}.msi", found[0].Offset, found[0].Length)]
            : found.Select((f, i) => new EmbeddedMsi($"{stem} ({i + 1}).msi", f.Offset, f.Length)).ToList();
    }

    /// <summary>
    /// 位置から始まるものが MSI 本体なら、その大きさを返す。違えば <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// OLE 複合ファイルは、見出しの後に同じ大きさの区画 (セクター) が並ぶ。どの区画が使われているかは
    /// 割り当て表 (FAT) に書いてあり、使われている最後の区画までが大きさになる。
    /// 割り当て表の置き場所は、見出しの 109 個と、その続きの鎖 (DIFAT) に書いてある。
    /// </remarks>
    private static long? MsiLength(Stream stream, long at)
    {
        var header = new byte[512];
        if (at + header.Length > stream.Length)
        {
            return null;
        }

        stream.Position = at;
        stream.ReadExactly(header);

        var shift = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x1E));
        if (shift is not (9 or 12))
        {
            return null;
        }

        var sectorSize = 1 << shift;
        var headerSize = shift == 9 ? 512 : 4096;
        var fatCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x2C));
        var directory = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x30));
        var difat = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x44));

        // 割り当て表が 100 万区画を超えるものは扱わない (4GB を超える。MSI の上限を超えている)
        if (fatCount == 0 || fatCount > 1_000_000)
        {
            return null;
        }

        long SectorAt(uint index) => at + headerSize + (long)index * sectorSize;

        var sector = new byte[sectorSize];
        bool ReadSector(uint index)
        {
            var offset = SectorAt(index);
            if (index >= EndOfChain - 1 || offset + sectorSize > stream.Length)
            {
                return false;
            }

            stream.Position = offset;
            stream.ReadExactly(sector);
            return true;
        }

        // 最初の区画 (ディレクトリ) の、根の項目の種類の番号を見る
        if (!ReadSector(directory) || !sector.AsSpan(0x50, 16).SequenceEqual(MsiClsid))
        {
            return null;
        }

        var fatSectors = new List<uint>();
        for (var i = 0; i < 109 && fatSectors.Count < fatCount; i++)
        {
            var value = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0x4C + i * 4));
            if (value != FreeSector)
            {
                fatSectors.Add(value);
            }
        }

        var perSector = sectorSize / 4;
        var guard = 0;
        while (fatSectors.Count < fatCount && difat is not (FreeSector or EndOfChain) && guard++ < 100_000)
        {
            if (!ReadSector(difat))
            {
                return null;
            }

            for (var i = 0; i < perSector - 1 && fatSectors.Count < fatCount; i++)
            {
                var value = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(i * 4));
                if (value != FreeSector)
                {
                    fatSectors.Add(value);
                }
            }

            difat = BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan((perSector - 1) * 4));
        }

        long highest = -1;
        for (var k = 0; k < fatSectors.Count; k++)
        {
            if (!ReadSector(fatSectors[k]))
            {
                return null;
            }

            for (var j = 0; j < perSector; j++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(sector.AsSpan(j * 4)) != FreeSector)
                {
                    highest = (long)k * perSector + j;
                }
            }
        }

        var length = headerSize + (highest + 1) * sectorSize;
        return highest >= 0 && at + length <= stream.Length ? length : null;
    }

    /// <summary>中身を一覧にする。</summary>
    /// <exception cref="InvalidDataException">MSI が見つからない場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = Find(path, cancellationToken: cancellationToken);
        if (items.Count == 0)
        {
            throw new InvalidDataException();
        }

        var builder = new ArchiveTreeBuilder(path);
        foreach (var item in items)
        {
            builder.AddFile(item.Name, item.Length, compressedLength: item.Length,
                compressedLengthKnown: true, lastWriteTime: default);
        }

        progress?.Report(new OpenProgress(items.Count, items.Count));
        return builder.Build(path, ArchiveFormat.MsiExe);
    }

    /// <summary>指定したエントリを展開する。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。</summary>
    public static ExtractResult Extract(
        string archivePath,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        bool overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null)
    {
        var state = new ExtractState(
            Path.GetFullPath(destinationDirectory), overwrite, zoneIdentifier, basePath, progress);

        var targets = Find(archivePath, cancellationToken: cancellationToken)
            .Where(i => sourceNames is null || sourceNames.Contains(i.Name))
            .ToList();
        state.TotalBytes = targets.Sum(static i => i.Length);

        using var source = ArchiveFile.OpenRead(archivePath);
        foreach (var item in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            state.Write(item.Name, item.Length, lastWriteTime: null, () => Slice(source, item), cancellationToken);
        }

        return state.ToResult();
    }

    /// <summary>1 つの MSI を読む流れ。<paramref name="source"/> は閉じない。</summary>
    public static Stream Slice(Stream source, EmbeddedMsi item)
    {
        source.Position = item.Offset;
        return new BoundedStream(source, item.Length);
    }
}
