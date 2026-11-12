using System.Text;
using ICSharpCode.SharpZipLib.Checksum;

namespace Expzip.Tests;

/// <summary>
/// テストのための RAR5 を組み立てる (#336)。中身は圧縮しない (格納)。
/// </summary>
/// <remarks>
/// RAR を作るツールはライセンスの都合で使わない。攻撃の見本 (辞書の大きさ、見出しの数、リンク、名前) は、
/// RAR5 の決まり (rarlab.com の「RAR 5.0 archive format」) どおりに見出しをここで組み立てる。
/// </remarks>
internal sealed class RarSample
{
    private readonly MemoryStream _body = new();

    /// <summary>書庫の見出しの印 (ソリッドなど)。</summary>
    public ulong ArchiveFlags { get; set; }

    /// <summary>終わりの見出しで「続きの分割ファイルがある」とするか。</summary>
    public bool MoreVolumes { get; set; }

    /// <summary>ファイルを足す。</summary>
    /// <param name="name">名前 (区切りは /)。</param>
    /// <param name="content">中身。</param>
    /// <param name="dictionaryPower">辞書の大きさ (128 KB の 2 の n 乗倍) の n。</param>
    /// <param name="redirect">リンクにするなら、種類 (1 Unix、2 Windows、3 ジャンクション、4 ハードリンク、5 ファイルのコピー) と指す先。</param>
    /// <param name="directory">フォルダーか。</param>
    /// <param name="sizeUnknown">展開後の大きさを不明とするか (標準入力から作った書庫の形)。</param>
    public RarSample File(
        string name, string content = "", int dictionaryPower = 0, (int Type, string Target, bool ToFolder)? redirect = null,
        bool directory = false, int headerType = 2, bool sizeUnknown = false)
    {
        var data = Encoding.UTF8.GetBytes(content);
        var body = new MemoryStream();
        var fileFlags = 0x4UL | (directory ? 0x1UL : 0) | (sizeUnknown ? 0x8UL : 0);
        Vint(body, fileFlags);
        Vint(body, (ulong)data.Length);
        Vint(body, directory ? 0x10UL : 0x20UL); // 属性
        var crc = new Crc32();
        crc.Update(data);
        body.Write(BitConverter.GetBytes((uint)crc.Value));
        Vint(body, (ulong)dictionaryPower << 10); // 格納 (方式 0)
        Vint(body, 0); // Windows
        var nameBytes = Encoding.UTF8.GetBytes(name);
        Vint(body, (ulong)nameBytes.Length);
        body.Write(nameBytes);

        var extra = new MemoryStream();
        if (redirect is { } r)
        {
            var record = new MemoryStream();
            Vint(record, 5);
            Vint(record, (ulong)r.Type);
            Vint(record, r.ToFolder ? 1UL : 0);
            var target = Encoding.UTF8.GetBytes(r.Target);
            Vint(record, (ulong)target.Length);
            record.Write(target);
            Vint(extra, (ulong)record.Length);
            extra.Write(record.ToArray());
        }

        var hasExtra = extra.Length > 0;
        WriteHeader(headerType, (hasExtra ? 0x1UL : 0) | 0x2UL, body.ToArray(), extra.ToArray(), (ulong)data.Length);
        _body.Write(data);
        return this;
    }

    /// <summary>種類と中身をそのまま書いた見出しを足す (壊れた見出しの見本)。</summary>
    public RarSample Raw(byte[] bytes)
    {
        _body.Write(bytes);
        return this;
    }

    /// <summary>書庫を書き出す。</summary>
    public string Save(string path)
    {
        using var output = System.IO.File.Create(path);
        output.Write([0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00]);

        var main = new MemoryStream();
        Vint(main, ArchiveFlags);
        output.Write(Header(1, 0, main.ToArray(), [], 0));

        output.Write(_body.ToArray());

        var end = new MemoryStream();
        Vint(end, MoreVolumes ? 1UL : 0);
        output.Write(Header(5, 0, end.ToArray(), [], 0));
        return path;
    }

    private void WriteHeader(int type, ulong flags, byte[] body, byte[] extra, ulong dataSize)
        => _body.Write(Header(type, flags, body, extra, dataSize));

    /// <summary>CRC32、大きさ (vint)、種類、印、[拡張の大きさ]、[データの大きさ]、中身、拡張。</summary>
    private static byte[] Header(int type, ulong flags, byte[] body, byte[] extra, ulong dataSize)
    {
        var inner = new MemoryStream();
        Vint(inner, (ulong)type);
        Vint(inner, flags);
        if ((flags & 0x1) != 0)
        {
            Vint(inner, (ulong)extra.Length);
        }

        if ((flags & 0x2) != 0)
        {
            Vint(inner, dataSize);
        }

        inner.Write(body);
        inner.Write(extra);

        var withSize = new MemoryStream();
        Vint(withSize, (ulong)inner.Length);
        withSize.Write(inner.ToArray());

        var crc = new Crc32();
        crc.Update(withSize.ToArray());

        var result = new MemoryStream();
        result.Write(BitConverter.GetBytes((uint)crc.Value));
        result.Write(withSize.ToArray());
        return result.ToArray();
    }

    private static void Vint(Stream stream, ulong value)
    {
        do
        {
            var b = (byte)(value & 0x7F);
            value >>= 7;
            stream.WriteByte(value != 0 ? (byte)(b | 0x80) : b);
        }
        while (value != 0);
    }
}
