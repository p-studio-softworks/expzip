using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace Expzip.Archives;

/// <summary>exe / dll の中の 1 項目 (#183)。</summary>
/// <param name="Path">一覧に出す名前。</param>
/// <param name="Offset">ファイルの中の位置。<paramref name="Content"/> があるときは使わない。</param>
/// <param name="Length">大きさ。</param>
/// <param name="Content">
/// 組み立て直した中身。アイコンやバージョン情報のように、そのままでは使えない形で
/// 入っているものは、読んだ時点で使える形に直しておく。
/// </param>
internal sealed record PeItem(string Path, long Offset, long Length, byte[]? Content)
{
    /// <summary>中身を読む流れを開く。<paramref name="source"/> は閉じない。</summary>
    public Stream Open(Stream source)
    {
        if (Content is not null)
        {
            return new MemoryStream(Content, writable: false);
        }

        source.Position = Offset;
        return new BoundedStream(source, Length);
    }
}

/// <summary>
/// exe / dll の中を、区画と埋め込みの部品に分けて見せる (#183)。読み取りのみ。
/// </summary>
/// <remarks>
/// <para>
/// exe と dll は同じ組み立て (PE 形式) で、仕様は公開されていて長く変わっていない。
/// 先頭の見出しの後に区画 (<c>.text</c> はプログラムの命令、<c>.data</c> はデータなど) が並ぶ。
/// </para>
/// <para>
/// 埋め込みの部品 (アイコン、バージョン情報など) は、区画の 1 つに種類 → 名前 → 言語の
/// 3 段の木として入っている。その区画は丸ごとではなく、<c>.rsrc</c> フォルダーの下に
/// 部品ごとに並べる。
/// </para>
/// <para>
/// 書き換えはしない。電子署名が壊れたり、動かなくなったりする。
/// </para>
/// </remarks>
internal static class PeReader
{
    /// <summary>埋め込みの部品を並べるフォルダーの名前。</summary>
    public const string ResourceFolder = ".rsrc";

    /// <summary>区画の数の上限。仕様上の上限が 96。</summary>
    private const int MaxSections = 96;

    /// <summary>部品の数の上限。壊れたファイルで木を辿り続けないため。</summary>
    private const int MaxResources = 100_000;

    /// <summary>使える形に直す部品の大きさの上限。これより大きいものはそのまま出す。</summary>
    private const int MaxConverted = 64 * 1024 * 1024;

    private readonly record struct Section(
        string Name, uint VirtualAddress, uint VirtualSize, uint RawPointer, uint RawSize);

    private sealed record Headers(Section[] Sections, uint ResourceRva, uint ResourceSize);

    /// <summary>部品の種類か名前。番号か文字のどちらか。</summary>
    internal readonly record struct ResourceKey(int Id, string? Name)
    {
        public override string ToString() => Name ?? Id.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>木の葉。部品 1 個の、ある言語の中身。</summary>
    internal readonly record struct Resource(
        ResourceKey Type, ResourceKey Name, int Language, long Offset, int Size);

    /// <summary>exe / dll として読めるか。先頭の見出しだけを見る。</summary>
    public static bool IsPe(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);
            return ReadHeaders(stream) is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>中身を一覧にする。</summary>
    /// <exception cref="InvalidDataException">exe / dll として読めない場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var items = ReadLayout(path, cancellationToken);
        var builder = new ArchiveTreeBuilder(path);

        foreach (var item in items)
        {
            builder.AddFile(
                item.Path,
                item.Length,
                compressedLength: 0,
                compressedLengthKnown: false,
                lastWriteTime: default);
        }

        progress?.Report(new OpenProgress(items.Count, items.Count));

        return builder.Build(path, ArchiveFormat.Pe, totalCompressedLength: new FileInfo(path).Length);
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

        var targets = ReadLayout(archivePath, cancellationToken)
            .Where(i => sourceNames is null || sourceNames.Contains(i.Path))
            .ToList();

        state.TotalBytes = targets.Sum(static i => i.Length);

        using var source = ArchiveFile.OpenRead(archivePath);

        foreach (var item in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            state.Write(item.Path, item.Length, lastWriteTime: null,
                () => item.Open(source), cancellationToken);
        }

        return state.ToResult();
    }

    /// <summary>並べる項目を読む。一覧・取り出し・検査で共通に使う。</summary>
    /// <exception cref="InvalidDataException">exe / dll として読めない場合。</exception>
    public static IReadOnlyList<PeItem> ReadLayout(string path, CancellationToken cancellationToken = default)
    {
        using var stream = ArchiveFile.OpenRead(path);

        var headers = ReadHeaders(stream) ?? throw new InvalidDataException();
        var items = new List<PeItem>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 部品の木が入っている区画。部品に分けて並べるので、区画としては出さない
        var resources = ReadResources(stream, headers, cancellationToken);
        var resourceSection = resources.Count > 0 ? SectionOf(headers, headers.ResourceRva) : null;

        foreach (var section in headers.Sections)
        {
            if (section == resourceSection || section.RawSize == 0 || section.RawPointer >= stream.Length)
            {
                continue;
            }

            var length = Math.Min(section.RawSize, stream.Length - section.RawPointer);
            items.Add(new PeItem(Unique(used, SafeName(section.Name)), section.RawPointer, length, null));
        }

        items.AddRange(PeResources.Arrange(stream, resources, used, MaxConverted, cancellationToken));
        return items;
    }

    /// <summary>ファイル名に使えない文字を置き換える。部品の名前はどんな文字でも入れられる。</summary>
    internal static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        var safe = new string(chars).TrimEnd(' ', '.');

        return safe.Length == 0 || safe == "." || safe == ".." ? "_" + safe : safe;
    }

    /// <summary>同じ名前が既にあれば番号を付けて分ける。</summary>
    internal static string Unique(HashSet<string> used, string path)
    {
        if (used.Add(path))
        {
            return path;
        }

        var extension = Path.GetExtension(path);
        var stem = path[..^extension.Length];

        for (var i = 2; ; i++)
        {
            var candidate = $"{stem}_{i}{extension}";
            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>先頭の見出しと区画の表を読む。exe / dll でなければ <see langword="null"/>。</summary>
    private static Headers? ReadHeaders(Stream stream)
    {
        var length = stream.Length;
        if (length < 0x40)
        {
            return null;
        }

        Span<byte> dos = stackalloc byte[0x40];
        stream.Position = 0;
        stream.ReadExactly(dos);

        if (dos[0] != 'M' || dos[1] != 'Z')
        {
            return null;
        }

        // 「PE」の見出しの位置
        var header = BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
        if (header < 4 || header > length - 24)
        {
            return null;
        }

        Span<byte> coff = stackalloc byte[24];
        stream.Position = header;
        stream.ReadExactly(coff);

        if (!coff[..4].SequenceEqual("PE\0\0"u8))
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(coff[6..]);
        var optionalSize = BinaryPrimitives.ReadUInt16LittleEndian(coff[20..]);

        if (count > MaxSections || optionalSize < 2 || header + 24L + optionalSize + count * 40L > length)
        {
            return null;
        }

        var optional = new byte[optionalSize];
        stream.ReadExactly(optional);

        // 32 ビットと 64 ビットで、置き場所の表 (データディレクトリ) の位置が違う
        var (countAt, directoriesAt) = BinaryPrimitives.ReadUInt16LittleEndian(optional) switch
        {
            0x10B => (92, 96),
            0x20B => (108, 112),
            _ => (-1, -1),
        };

        if (countAt < 0)
        {
            return null;
        }

        // 部品の木は表の 3 番目 (番号 2)
        uint resourceRva = 0, resourceSize = 0;
        const int resourceIndex = 2;
        if (optional.Length >= countAt + 4
            && BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(countAt)) > resourceIndex
            && optional.Length >= directoriesAt + (resourceIndex + 1) * 8)
        {
            var at = directoriesAt + resourceIndex * 8;
            resourceRva = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(at));
            resourceSize = BinaryPrimitives.ReadUInt32LittleEndian(optional.AsSpan(at + 4));
        }

        var table = new byte[count * 40];
        stream.ReadExactly(table);

        var sections = new Section[count];
        for (var i = 0; i < count; i++)
        {
            var entry = table.AsSpan(i * 40, 40);
            sections[i] = new Section(
                Encoding.UTF8.GetString(entry[..8]).TrimEnd('\0'),
                VirtualSize: BinaryPrimitives.ReadUInt32LittleEndian(entry[8..]),
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]),
                RawSize: BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]),
                RawPointer: BinaryPrimitives.ReadUInt32LittleEndian(entry[20..]));
        }

        return new Headers(sections, resourceRva, resourceSize);
    }

    /// <summary>番地 (読み込まれたときの位置) を含む区画。</summary>
    private static Section? SectionOf(Headers headers, uint rva)
    {
        foreach (var section in headers.Sections)
        {
            var size = Math.Max(section.VirtualSize, section.RawSize);
            if (rva >= section.VirtualAddress && rva - section.VirtualAddress < size)
            {
                return section;
            }
        }

        return null;
    }

    /// <summary>番地をファイルの中の位置に直す。ファイルに中身が無ければ -1。</summary>
    private static long ToOffset(Headers headers, uint rva)
    {
        if (SectionOf(headers, rva) is not { } section)
        {
            return -1;
        }

        var inside = rva - section.VirtualAddress;
        return inside < section.RawSize ? section.RawPointer + (long)inside : -1;
    }

    /// <summary>部品の木を辿って、葉を集める。木が無い・壊れているときは空。</summary>
    /// <remarks>
    /// 木の中の位置は、木の先頭からの相対で書かれている。葉が指す中身だけは番地で書かれている。
    /// 壊れた木で同じ枝を回り続けないよう、辿った枝を覚えておく。
    /// </remarks>
    private static List<Resource> ReadResources(Stream stream, Headers headers, CancellationToken cancellationToken)
    {
        var found = new List<Resource>();
        if (headers.ResourceRva == 0)
        {
            return found;
        }

        var root = ToOffset(headers, headers.ResourceRva);
        if (root < 0)
        {
            return found;
        }

        var visited = new HashSet<long>();
        Span<byte> data = stackalloc byte[8];

        try
        {
            foreach (var (type, typeTarget) in ReadDirectory(stream, root, root, visited))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (typeTarget.IsData)
                {
                    continue;
                }

                foreach (var (name, nameTarget) in ReadDirectory(stream, root, typeTarget.Offset, visited))
                {
                    if (nameTarget.IsData)
                    {
                        continue;
                    }

                    foreach (var (language, leaf) in ReadDirectory(stream, root, nameTarget.Offset, visited))
                    {
                        if (!leaf.IsData || found.Count >= MaxResources)
                        {
                            continue;
                        }

                        stream.Position = leaf.Offset;
                        stream.ReadExactly(data);

                        var rva = BinaryPrimitives.ReadUInt32LittleEndian(data);
                        var size = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                        var offset = ToOffset(headers, rva);

                        // 中身がファイルの外を指すものは出さない
                        if (offset < 0 || size > int.MaxValue || offset + size > stream.Length)
                        {
                            continue;
                        }

                        found.Add(new Resource(type, name, language.Id, offset, (int)size));
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // 木の途中が壊れている。読めたところまでを出す
        }

        return found;
    }

    private readonly record struct Target(bool IsData, long Offset);

    /// <summary>木の 1 段を読む。</summary>
    private static List<(ResourceKey Key, Target Target)> ReadDirectory(
        Stream stream, long root, long offset, HashSet<long> visited)
    {
        var entries = new List<(ResourceKey, Target)>();
        if (!visited.Add(offset) || offset + 16 > stream.Length)
        {
            return entries;
        }

        Span<byte> head = stackalloc byte[16];
        stream.Position = offset;
        stream.ReadExactly(head);

        var count = BinaryPrimitives.ReadUInt16LittleEndian(head[12..])
                    + BinaryPrimitives.ReadUInt16LittleEndian(head[14..]);

        if (offset + 16 + count * 8L > stream.Length)
        {
            return entries;
        }

        var table = new byte[count * 8];
        stream.ReadExactly(table);

        for (var i = 0; i < count; i++)
        {
            var name = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 8));
            var target = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(i * 8 + 4));

            // 上の 1 ビットが立っていれば、名前は文字 / 行き先は次の段
            var key = (name & 0x8000_0000) != 0
                ? new ResourceKey(0, ReadName(stream, root + (name & 0x7FFF_FFFF)))
                : new ResourceKey((int)(name & 0xFFFF), null);

            entries.Add((key, new Target(
                IsData: (target & 0x8000_0000) == 0,
                Offset: root + (target & 0x7FFF_FFFF))));
        }

        return entries;
    }

    /// <summary>部品の名前を読む。2 バイトの長さに続く UTF-16。</summary>
    private static string ReadName(Stream stream, long offset)
    {
        Span<byte> length = stackalloc byte[2];
        stream.Position = offset;
        stream.ReadExactly(length);

        var chars = new byte[BinaryPrimitives.ReadUInt16LittleEndian(length) * 2];
        stream.ReadExactly(chars);
        return Encoding.Unicode.GetString(chars);
    }
}
