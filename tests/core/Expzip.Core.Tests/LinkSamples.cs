using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace Expzip.Tests;

/// <summary>
/// リンクを入れた tar を組み立てる (#367)。tar コマンドの無い PC でも、攻撃の形をそのまま作れるようにする。
/// </summary>
internal sealed class TarSample
{
    private readonly MemoryStream _data = new();

    /// <summary>ふつうのファイル。</summary>
    public TarSample File(string name, string content) => Add(name, '0', Encoding.UTF8.GetBytes(content));

    /// <summary>フォルダー。</summary>
    public TarSample Folder(string name) => Add(name.TrimEnd('/') + "/", '5', []);

    /// <summary>シンボリックリンク。</summary>
    public TarSample Symlink(string name, string target) => Add(name, '2', [], target);

    /// <summary>ハードリンク。<paramref name="content"/> を付けると、中身の付いたハードリンクになる。</summary>
    public TarSample HardLink(string name, string target, string content = "")
        => Add(name, '1', Encoding.UTF8.GetBytes(content), target);

    /// <summary>PAX の拡張ヘッダー。<paramref name="global"/> なら後ろのすべての項目に効く。</summary>
    public TarSample Pax(IReadOnlyDictionary<string, string> values, bool global = false)
    {
        var body = new StringBuilder();
        foreach (var (key, value) in values)
        {
            var record = $" {key}={value}\n";
            var length = Encoding.UTF8.GetByteCount(record);
            var total = length + length.ToString().Length;
            if (total.ToString().Length != length.ToString().Length)
            {
                total++;
            }

            body.Append(total).Append(record);
        }

        return Add("PaxHeader", global ? 'g' : 'x', Encoding.UTF8.GetBytes(body.ToString()));
    }

    public string Save(string path)
    {
        // 終わりは 512 バイトの空の塊が 2 つ
        _data.Write(new byte[1024]);
        System.IO.File.WriteAllBytes(path, _data.ToArray());
        return path;
    }

    private TarSample Add(string name, char type, byte[] content, string link = "")
    {
        var header = new byte[512];
        Put(header, 0, 100, name);
        Put(header, 100, 8, "0000644");
        Put(header, 108, 8, "0000000");
        Put(header, 116, 8, "0000000");
        Put(header, 124, 12, Convert.ToString(content.Length, 8).PadLeft(11, '0'));
        Put(header, 136, 12, Convert.ToString(1_700_000_000L, 8).PadLeft(11, '0'));
        header[156] = (byte)type;
        Put(header, 157, 100, link);
        Put(header, 257, 6, "ustar");
        Put(header, 263, 2, "00");

        // 見出しの合計は、合計の欄を空白として数える
        for (var i = 148; i < 156; i++)
        {
            header[i] = (byte)' ';
        }

        var sum = header.Sum(static b => (int)b);
        Put(header, 148, 7, Convert.ToString(sum, 8).PadLeft(6, '0'));

        _data.Write(header);
        _data.Write(content);
        _data.Write(new byte[(512 - content.Length % 512) % 512]);
        return this;
    }

    private static void Put(byte[] header, int offset, int length, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length > length)
        {
            throw new ArgumentException("tar の欄に入りません: " + text);
        }

        bytes.CopyTo(header, offset);
    }
}

/// <summary>
/// 属性を指定した 7z を組み立てる (#367)。中身は圧縮しない (Copy)。7-Zip の無い PC でも、Linux の 7z と同じリンクの属性や、
/// 7-Zip の <c>-snl</c> の属性 (0x400) を作れるようにする。
/// </summary>
internal sealed class SevenZipSample
{
    /// <summary>Unix の属性があることを示す印 (FILE_ATTRIBUTE_UNIX_EXTENSION)。上位 16 ビットが Unix の mode。</summary>
    public const uint UnixExtension = 0x8000;

    /// <summary>Unix のシンボリックリンク (S_IFLNK | 0777) の属性。</summary>
    public const uint UnixSymlink = UnixExtension | (0xA1FFu << 16);

    /// <summary>Windows のリパースポイント (7-Zip の <c>-snl</c>)。</summary>
    public const uint ReparsePoint = 0x400;

    private readonly List<(string Name, byte[] Content, uint Attributes, bool Folder)> _items = [];

    public SevenZipSample File(string name, string content, uint attributes = 0x20)
        => Add(name, Encoding.UTF8.GetBytes(content), attributes, folder: false);

    public SevenZipSample Folder(string name) => Add(name, [], 0x10, folder: true);

    /// <summary>Unix のシンボリックリンク。中身が指す先。</summary>
    public SevenZipSample Symlink(string name, string target) => File(name, target, UnixSymlink);

    public SevenZipSample Add(string name, byte[] content, uint attributes, bool folder)
    {
        _items.Add((name, content, attributes, folder));
        return this;
    }

    /// <summary>書く。<paramref name="solid"/> なら中身のある項目を 1 つの塊にまとめる。</summary>
    public string Save(string path, bool solid = false)
    {
        var streams = _items.Where(static i => !i.Folder && i.Content.Length > 0).ToList();
        var packed = streams.SelectMany(static s => s.Content).ToArray();

        var header = new MemoryStream();
        header.WriteByte(0x01); // kHeader

        if (streams.Count > 0)
        {
            header.WriteByte(0x04); // kMainStreamsInfo

            // PackInfo
            var packSizes = solid ? [packed.Length] : streams.Select(static s => (long)s.Content.Length).ToArray();
            header.WriteByte(0x06);
            Number(header, 0);
            Number(header, packSizes.Length);
            header.WriteByte(0x09);
            foreach (var size in packSizes)
            {
                Number(header, size);
            }

            header.WriteByte(0x00);

            // UnPackInfo: 塊ごとに Copy を 1 つ
            header.WriteByte(0x07);
            header.WriteByte(0x0B);
            Number(header, packSizes.Length);
            header.WriteByte(0x00); // External
            foreach (var _ in packSizes)
            {
                Number(header, 1);  // NumCoders
                header.WriteByte(0x01); // 番号の長さ 1、単純
                header.WriteByte(0x00); // Copy
            }

            header.WriteByte(0x0C);
            foreach (var size in packSizes)
            {
                Number(header, size);
            }

            header.WriteByte(0x00);

            // SubStreamsInfo: まとめたときは塊の中の数と大きさ。CRC はすべての流れに付ける
            header.WriteByte(0x08);
            if (solid)
            {
                header.WriteByte(0x0D);
                Number(header, streams.Count);
                header.WriteByte(0x09);
                foreach (var stream in streams.Take(streams.Count - 1))
                {
                    Number(header, stream.Content.Length);
                }
            }

            header.WriteByte(0x0A);
            header.WriteByte(0x01); // すべてある
            foreach (var stream in streams)
            {
                header.Write(BitConverter.GetBytes(Crc32(stream.Content)));
            }

            header.WriteByte(0x00);
            header.WriteByte(0x00);
        }

        // FilesInfo
        header.WriteByte(0x05);
        Number(header, _items.Count);

        var empty = _items.Select(static i => i.Folder || i.Content.Length == 0).ToArray();
        if (empty.Any(static e => e))
        {
            var bits = Bits(empty);
            header.WriteByte(0x0E);
            Number(header, bits.Length);
            header.Write(bits);

            var emptyFiles = _items.Where(static i => i.Folder || i.Content.Length == 0).Select(static i => !i.Folder).ToArray();
            if (emptyFiles.Any(static e => e))
            {
                var fileBits = Bits(emptyFiles);
                header.WriteByte(0x0F);
                Number(header, fileBits.Length);
                header.Write(fileBits);
            }
        }

        var names = new MemoryStream();
        names.WriteByte(0x00); // External
        foreach (var item in _items)
        {
            names.Write(Encoding.Unicode.GetBytes(item.Name + "\0"));
        }

        header.WriteByte(0x11);
        Number(header, names.Length);
        header.Write(names.ToArray());

        header.WriteByte(0x15);
        Number(header, 2 + 4 * _items.Count);
        header.WriteByte(0x01); // すべてある
        header.WriteByte(0x00); // External
        foreach (var item in _items)
        {
            header.Write(BitConverter.GetBytes(item.Attributes));
        }

        header.WriteByte(0x00); // FilesInfo の終わり
        header.WriteByte(0x00); // Header の終わり

        var headerBytes = header.ToArray();
        var start = new MemoryStream();
        start.Write(BitConverter.GetBytes((long)packed.Length));
        start.Write(BitConverter.GetBytes((long)headerBytes.Length));
        start.Write(BitConverter.GetBytes(Crc32(headerBytes)));
        var startBytes = start.ToArray();

        using var file = System.IO.File.Create(path);
        file.Write([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]);
        file.Write(BitConverter.GetBytes(Crc32(startBytes)));
        file.Write(startBytes);
        file.Write(packed);
        file.Write(headerBytes);
        return path;
    }

    /// <summary>7z の可変長の数。</summary>
    private static void Number(Stream stream, long value)
    {
        var v = (ulong)value;
        var first = 0;
        var mask = 0x80;
        int i;
        for (i = 0; i < 8; i++)
        {
            if (v < (1UL << (7 * (i + 1))))
            {
                first |= (int)(v >> (8 * i));
                break;
            }

            first |= mask;
            mask >>= 1;
        }

        stream.WriteByte((byte)first);
        for (var j = 0; j < i; j++)
        {
            stream.WriteByte((byte)(v >> (8 * j)));
        }
    }

    private static byte[] Bits(bool[] values)
    {
        var bytes = new byte[(values.Length + 7) / 8];
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    private static uint Crc32(byte[] data)
    {
        var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
        crc.Update(data);
        return (uint)crc.Value;
    }
}

/// <summary>リンクを入れた ZIP を組み立てる (#367)。作った OS と外部属性を自由に決められる。</summary>
internal sealed class ZipSample
{
    private readonly List<(string Name, byte[] Content, int Host, int External)> _items = [];

    public ZipSample File(string name, string content) => Add(name, Encoding.UTF8.GetBytes(content), 0, 0x20);

    public ZipSample Folder(string name) => Add(name.TrimEnd('/') + "/", [], 0, 0x10);

    /// <summary>Unix のシンボリックリンク (Info-ZIP の形)。<paramref name="host"/> で作った OS を変えられる。</summary>
    public ZipSample Symlink(string name, string target, int host = 3)
        => Add(name, Encoding.UTF8.GetBytes(target), host, unchecked((int)0xA1FF0000));

    public ZipSample Add(string name, byte[] content, int host, int external)
    {
        _items.Add((name, content, host, external));
        return this;
    }

    public string Save(string path, string? password = null)
    {
        using (var output = new ZipOutputStream(System.IO.File.Create(path)))
        {
            if (password is not null)
            {
                output.Password = password;
            }

            foreach (var (name, content, host, external) in _items)
            {
                var entry = new ZipEntry(name)
                {
                    HostSystem = host,
                    ExternalFileAttributes = external,
                    CompressionMethod = CompressionMethod.Stored,
                    Size = content.Length,
                    DateTime = new DateTime(2026, 1, 1),
                };

                if (password is not null)
                {
                    entry.AESKeySize = 256;
                }

                var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
                crc.Update(content);
                entry.Crc = crc.Value;

                output.PutNextEntry(entry);
                output.Write(content);
                output.CloseEntry();
            }
        }

        return path;
    }
}

/// <summary>テストごとの作業用のフォルダー。展開先は <see cref="Output"/>。</summary>
internal sealed class LinkPlace : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "expzip-links-" + Guid.NewGuid().ToString("N"));

    public LinkPlace()
    {
        Directory.CreateDirectory(Output);
    }

    public string Output => Path.Combine(_root, "out");

    public string PathOf(string name) => Path.Combine(_root, name);

    public string In(string relative) => Path.Combine(Output, relative.Replace('/', Path.DirectorySeparatorChar));

    public void Dispose()
    {
        try
        {
            Archives.TempWorkspace.TryDeleteTree(_root);
        }
        catch (IOException)
        {
        }
    }
}
