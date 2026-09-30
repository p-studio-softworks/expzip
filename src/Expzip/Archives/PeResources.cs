using System.Buffers.Binary;
using System.Globalization;
using System.IO;
using System.Text;

namespace Expzip.Archives;

/// <summary>
/// exe / dll に埋め込まれた部品を、一覧に並べる形にする (#183)。
/// </summary>
/// <remarks>
/// <para>
/// 部品はそのままでは使えない形で入っているものがある。よく使うものは、取り出して
/// すぐに開ける形に直す。
/// </para>
/// <list type="bullet">
/// <item>アイコン・カーソル: 大きさごとの絵と、それらをまとめる目録に分かれて入っている。
/// 目録ごとに 1 つの <c>.ico</c> / <c>.cur</c> に組み直す。目録から使われている絵は単独では出さない</item>
/// <item>ビットマップ: ファイルの頭 (14 バイト) が省かれている。付け足して <c>.bmp</c> にする</item>
/// <item>バージョン情報・文字列の表: 読める文字 (<c>.txt</c>) にする</item>
/// </list>
/// <para>
/// ほかの部品はそのままの中身で出す。直せなかったものも、そのままの中身で出す。
/// </para>
/// </remarks>
internal static class PeResources
{
    private const int Cursor = 1;
    private const int Bitmap = 2;
    private const int Icon = 3;
    private const int StringTable = 6;
    private const int GroupCursor = 12;
    private const int GroupIcon = 14;
    private const int Version = 16;
    private const int AnimatedCursor = 21;
    private const int AnimatedIcon = 22;
    private const int Manifest = 24;

    /// <summary>種類の番号と、フォルダーの名前。</summary>
    /// <remarks>名前は Windows の開発資料での呼び名。目録はアイコン・カーソルのフォルダーにまとめる。</remarks>
    private static readonly Dictionary<int, string> Folders = new()
    {
        [Cursor] = "CURSOR",
        [Bitmap] = "BITMAP",
        [Icon] = "ICON",
        [4] = "MENU",
        [5] = "DIALOG",
        [StringTable] = "STRING",
        [7] = "FONTDIR",
        [8] = "FONT",
        [9] = "ACCELERATOR",
        [10] = "RCDATA",
        [11] = "MESSAGETABLE",
        [GroupCursor] = "CURSOR",
        [GroupIcon] = "ICON",
        [Version] = "VERSION",
        [17] = "DLGINCLUDE",
        [19] = "PLUGPLAY",
        [20] = "VXD",
        [AnimatedCursor] = "ANICURSOR",
        [AnimatedIcon] = "ANIICON",
        [23] = "HTML",
        [Manifest] = "MANIFEST",
    };

    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>部品を一覧の項目にする。</summary>
    /// <param name="used">既に使った名前。区画の名前と重ならないようにする。</param>
    /// <param name="maxConverted">使える形に直す大きさの上限。</param>
    public static IEnumerable<PeItem> Arrange(
        Stream stream,
        IReadOnlyList<PeReader.Resource> resources,
        HashSet<string> used,
        int maxConverted,
        CancellationToken cancellationToken)
    {
        var items = new List<PeItem>();

        // 同じ名前の部品が複数の言語で入っているときだけ、名前に言語を添える
        var languages = resources
            .GroupBy(static r => (r.Type, r.Name))
            .ToDictionary(static g => g.Key, static g => g.Select(static r => r.Language).Distinct().Count());

        string NameOf(PeReader.Resource r, string extension)
        {
            var suffix = languages[(r.Type, r.Name)] > 1 ? "_" + LanguageName(r.Language) : string.Empty;
            var folder = r.Type.Name is { } named ? PeReader.SafeName(named) : Folders.GetValueOrDefault(r.Type.Id, r.Type.Id.ToString(CultureInfo.InvariantCulture));
            return PeReader.Unique(used,
                $"{PeReader.ResourceFolder}/{folder}/{PeReader.SafeName(r.Name.ToString())}{suffix}{extension}");
        }

        PeItem Raw(PeReader.Resource r) => new(NameOf(r, string.Empty), r.Offset, r.Size, null);

        byte[]? Read(PeReader.Resource r)
        {
            if (r.Size > maxConverted)
            {
                return null;
            }

            var data = new byte[r.Size];
            stream.Position = r.Offset;
            stream.ReadExactly(data);
            return data;
        }

        // 目録から使われている絵。単独では出さない
        var parts = new HashSet<(int Type, int Id)>();

        foreach (var group in resources.Where(static r => r.Type.Name is null && r.Type.Id is GroupIcon or GroupCursor))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isIcon = group.Type.Id == GroupIcon;
            var partType = isIcon ? Icon : Cursor;
            var data = Read(group);
            var built = data is null ? null : Assemble(stream, resources, data, group.Language, partType, parts);

            items.Add(built is null
                ? Raw(group)
                : new PeItem(NameOf(group, isIcon ? ".ico" : ".cur"), 0, built.Length, built));
        }

        // 文字列の表は 16 個ずつの塊に分かれて入っている。言語ごとに 1 つの文書にまとめる
        var strings = new SortedDictionary<int, List<(int Id, string Text)>>();

        foreach (var r in resources)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (r.Type.Name is not null)
            {
                items.Add(Raw(r));
                continue;
            }

            switch (r.Type.Id)
            {
                case GroupIcon or GroupCursor:
                    break;

                case Icon or Cursor when parts.Contains((r.Type.Id, r.Name.Id)) && r.Name.Name is null:
                    break;

                case StringTable when r.Name.Name is null && Read(r) is { } data:
                    if (!strings.TryGetValue(r.Language, out var list))
                    {
                        strings[r.Language] = list = [];
                    }

                    list.AddRange(ReadStringBlock(data, r.Name.Id));
                    break;

                case Bitmap when Read(r) is { } data && ToBitmapFile(data) is { } file:
                    items.Add(new PeItem(NameOf(r, ".bmp"), 0, file.Length, file));
                    break;

                case Version when Read(r) is { } data && VersionText(data) is { } text:
                    items.Add(new PeItem(NameOf(r, ".txt"), 0, text.Length, text));
                    break;

                case Manifest:
                    items.Add(new PeItem(NameOf(r, ".manifest"), r.Offset, r.Size, null));
                    break;

                case AnimatedCursor or AnimatedIcon:
                    items.Add(new PeItem(NameOf(r, ".ani"), r.Offset, r.Size, null));
                    break;

                default:
                    items.Add(Raw(r));
                    break;
            }
        }

        foreach (var (language, list) in strings)
        {
            var text = new StringBuilder();
            foreach (var (id, value) in list.OrderBy(static s => s.Id))
            {
                text.Append(id.ToString(CultureInfo.InvariantCulture)).Append(": ")
                    .Append(value.Replace("\r", "\\r").Replace("\n", "\\n")).Append("\r\n");
            }

            var bytes = Utf8.GetBytes(text.ToString());
            var path = PeReader.Unique(used,
                $"{PeReader.ResourceFolder}/{Folders[StringTable]}/{LanguageName(language)}.txt");
            items.Add(new PeItem(path, 0, bytes.Length, bytes));
        }

        return items;
    }

    /// <summary>言語の番号を、<c>ja-JP</c> のような名前にする。</summary>
    private static string LanguageName(int language)
    {
        if (language == 0)
        {
            return "neutral";
        }

        try
        {
            var name = CultureInfo.GetCultureInfo(language).Name;
            return name.Length > 0 ? name : language.ToString(CultureInfo.InvariantCulture);
        }
        catch (CultureNotFoundException)
        {
            return language.ToString(CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// 目録と、目録が指す絵から、<c>.ico</c> / <c>.cur</c> を組み立てる。組み立てられなければ <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 目録の 1 件は 14 バイト、ファイルの 1 件は 16 バイト。ファイルの側は、番号の代わりに
    /// 絵の位置を持つ。カーソルの絵は頭の 4 バイトに指す位置 (ホットスポット) を持っていて、
    /// ファイルではそれを目録の側へ移す。
    /// </remarks>
    private static byte[]? Assemble(
        Stream stream, IReadOnlyList<PeReader.Resource> resources, byte[] group, int language,
        int partType, HashSet<(int, int)> parts)
    {
        if (group.Length < 6)
        {
            return null;
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(group.AsSpan(4));
        if (count == 0 || group.Length < 6 + count * 14)
        {
            return null;
        }

        var images = new List<(byte[] Entry, byte[] Image)>();

        for (var i = 0; i < count; i++)
        {
            var entry = group.AsSpan(6 + i * 14, 14);
            var id = BinaryPrimitives.ReadUInt16LittleEndian(entry[12..]);

            // 同じ言語のものを選ぶ。無ければどれか
            var candidates = resources.Where(r => r.Type.Name is null && r.Type.Id == partType
                                                  && r.Name.Name is null && r.Name.Id == id).ToList();
            if (candidates.Count == 0)
            {
                return null;
            }

            var part = candidates.FirstOrDefault(r => r.Language == language, candidates[0]);
            var image = new byte[part.Size];
            stream.Position = part.Offset;
            stream.ReadExactly(image);

            var head = new byte[12];

            if (partType == Icon)
            {
                entry[..8].CopyTo(head);
            }
            else
            {
                if (image.Length < 4)
                {
                    return null;
                }

                var width = BinaryPrimitives.ReadUInt16LittleEndian(entry);
                var height = BinaryPrimitives.ReadUInt16LittleEndian(entry[2..]) / 2;
                head[0] = (byte)(width >= 256 ? 0 : width);
                head[1] = (byte)(height >= 256 ? 0 : height);
                image.AsSpan(0, 4).CopyTo(head.AsSpan(4));
                image = image[4..];
            }

            BinaryPrimitives.WriteInt32LittleEndian(head.AsSpan(8), image.Length);
            images.Add((head, image));
            parts.Add((partType, id));
        }

        using var file = new MemoryStream();
        Span<byte> header = stackalloc byte[6];
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)(partType == Icon ? 1 : 2));
        BinaryPrimitives.WriteUInt16LittleEndian(header[4..], (ushort)images.Count);
        file.Write(header);

        var offset = 6 + images.Count * 16;
        Span<byte> position = stackalloc byte[4];

        foreach (var (head, image) in images)
        {
            file.Write(head);
            BinaryPrimitives.WriteInt32LittleEndian(position, offset);
            file.Write(position);
            offset += image.Length;
        }

        foreach (var (_, image) in images)
        {
            file.Write(image);
        }

        return file.ToArray();
    }

    /// <summary>ビットマップの頭 (14 バイト) を付け足して、<c>.bmp</c> にする。</summary>
    /// <remarks>頭には、絵の点が始まる位置を書く。見出しと色の表の大きさから求める。</remarks>
    private static byte[]? ToBitmapFile(byte[] dib)
    {
        if (dib.Length < 12)
        {
            return null;
        }

        var headerSize = BinaryPrimitives.ReadInt32LittleEndian(dib);
        if (headerSize < 12 || headerSize > dib.Length)
        {
            return null;
        }

        long palette;
        if (headerSize == 12)
        {
            // 古い形の見出し。色の表は 1 色 3 バイト
            var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(10));
            palette = bits <= 8 ? (1L << bits) * 3 : 0;
        }
        else
        {
            if (headerSize < 40)
            {
                return null;
            }

            var bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14));
            var compression = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(16));
            var used = BinaryPrimitives.ReadUInt32LittleEndian(dib.AsSpan(32));
            palette = (used != 0 ? used : bits <= 8 ? 1L << bits : 0) * 4;

            // 色の取り出し方を表す値が、見出しの後に続くことがある
            if (headerSize == 40)
            {
                palette += compression switch { 3 => 12, 6 => 16, _ => 0 };
            }
        }

        var start = Math.Min(14 + headerSize + palette, 14L + dib.Length);

        var file = new byte[14 + dib.Length];
        file[0] = (byte)'B';
        file[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(2), file.Length);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(10), (int)start);
        dib.CopyTo(file, 14);
        return file;
    }

    /// <summary>文字列の表の 1 塊を読む。1 塊に 16 個、番号は塊の番号から決まる。</summary>
    private static IEnumerable<(int Id, string Text)> ReadStringBlock(byte[] data, int block)
    {
        var found = new List<(int, string)>();
        var position = 0;

        for (var i = 0; i < 16 && position + 2 <= data.Length; i++)
        {
            var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position)) * 2;
            position += 2;

            if (position + length > data.Length)
            {
                break;
            }

            if (length > 0)
            {
                found.Add(((block - 1) * 16 + i, Encoding.Unicode.GetString(data, position, length)));
            }

            position += length;
        }

        return found;
    }

    /// <summary>バージョン情報を読める文字にする。読めなければ <see langword="null"/>。</summary>
    /// <remarks>
    /// 中は「長さ・値の長さ・種類・名前・値・子」の入れ子で、区切りごとに 4 バイト境界へ揃えてある。
    /// 数で持っている版 (ファイルと製品のバージョン) と、文字で持っている項目 (発行元、製品名など) を出す。
    /// </remarks>
    private static byte[]? VersionText(byte[] data)
    {
        if (ParseVersionNode(data, 0, 0) is not { } root || root.Node.Key != "VS_VERSION_INFO")
        {
            return null;
        }

        var text = new StringBuilder();
        var fixedInfo = root.Node.Value;

        if (fixedInfo.Length >= 52 && BinaryPrimitives.ReadUInt32LittleEndian(fixedInfo) == 0xFEEF04BD)
        {
            text.Append("FileVersion: ").Append(FourPart(fixedInfo, 8)).Append("\r\n");
            text.Append("ProductVersion: ").Append(FourPart(fixedInfo, 16)).Append("\r\n");
        }

        foreach (var info in root.Node.Children.Where(static c => c.Key == "StringFileInfo"))
        {
            foreach (var table in info.Children)
            {
                text.Append("\r\n[").Append(table.Key).Append("]\r\n");

                foreach (var item in table.Children)
                {
                    var value = Encoding.Unicode.GetString(item.Value);
                    var end = value.IndexOf('\0');
                    text.Append(item.Key).Append(": ").Append(end >= 0 ? value[..end] : value).Append("\r\n");
                }
            }
        }

        return Utf8.GetBytes(text.ToString());
    }

    private static string FourPart(byte[] data, int at)
    {
        var high = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
        var low = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at + 4));
        return $"{high >> 16}.{high & 0xFFFF}.{low >> 16}.{low & 0xFFFF}";
    }

    private sealed record VersionNode(string Key, byte[] Value, List<VersionNode> Children);

    private static (VersionNode Node, int Length)? ParseVersionNode(byte[] data, int start, int depth)
    {
        if (depth > 8 || start + 6 > data.Length)
        {
            return null;
        }

        var length = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start));
        var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + 2));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(start + 4));

        if (length < 6 || start + length > data.Length)
        {
            return null;
        }

        var end = start + length;
        var position = start + 6;
        var key = new StringBuilder();

        while (position + 2 <= end)
        {
            var c = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(position));
            position += 2;
            if (c == '\0')
            {
                break;
            }

            key.Append(c);
        }

        position = Align(position);

        // 文字の値は、長さが文字数で書かれている
        var valueBytes = Math.Clamp(type == 1 ? valueLength * 2 : valueLength, 0, Math.Max(0, end - position));
        var value = data.AsSpan(position, valueBytes).ToArray();
        position = Align(position + valueBytes);

        var children = new List<VersionNode>();
        while (position + 6 <= end)
        {
            if (ParseVersionNode(data, position, depth + 1) is not { } child)
            {
                break;
            }

            children.Add(child.Node);
            position = Align(position + child.Length);
        }

        return (new VersionNode(key.ToString(), value, children), length);
    }

    private static int Align(int position) => (position + 3) & ~3;
}
