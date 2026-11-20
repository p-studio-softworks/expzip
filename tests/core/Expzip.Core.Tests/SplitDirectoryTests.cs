using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Expzip.Archives;
using Expzip.Inspection;

namespace Expzip.Tests;

/// <summary>
/// 部品によって読む目録 (中央ディレクトリ) が違う ZIP を、知らせて扱わない (#396)。
/// 攻撃の見本は設計の確認 (#396 のノート) で挙がったもの。
/// </summary>
/// <remarks>
/// 一覧と展開は .NET の部品、検査とハッシュ値とパスワード付きの展開は SharpZipLib、方式の扱えない項目は SharpCompress で読む。
/// 読む目録がそろわない書庫では、ハッシュ値を見て確かめた中身と、展開されるものが違ってしまう。
/// </remarks>
public sealed class SplitDirectoryTests : IDisposable
{
    private readonly LinkPlace _place = new();

    public void Dispose() => _place.Dispose();

    private string Save(string name, byte[] bytes)
    {
        var path = _place.PathOf(name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] First = Encoding.ASCII.GetBytes("FIRST");
    private static readonly byte[] Second = Encoding.ASCII.GetBytes("SECOND");

    /// <summary>チケットの見本。終わりの記録は 1 つ目の目録を指し、その後ろに 2 つ目の目録がある。</summary>
    private static byte[] TwoDirectories()
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);

        // SharpZipLib は書かれた位置を符号なしで持ち、ずれを足して読む。書く位置が負にならないように空けておく
        zip.Bytes(new byte[128]);
        var second = zip.Local("a.txt", Second);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s1 = zip.Position - o1;

        // .NET は目録の署名が続くかぎり読み進めるので、間に署名でないバイトを挟む
        zip.Bytes(0, 0, 0, 0);

        // SharpZipLib は読む位置をずれの分 (s1 + 4) だけ直し、局所ヘッダーの位置にも同じだけ足して読む
        zip.Directory("a.txt", Second, second - (s1 + 4));
        zip.End(1, s1, o1);
        return zip.ToArray();
    }

    // ------------------------------------------------------------------ 攻撃の見本

    [Fact]
    public void 目録が2つあるとSharpZipLibは別の中身を読む()
    {
        // 前提: 確かめ方が無いと、一覧と検査で別の中身になる
        var path = Save("two.zip", TwoDirectories());

        using (var zip = ZipFile.OpenRead(path))
        {
            using var reader = new StreamReader(zip.GetEntry("a.txt")!.Open());
            Assert.Equal("FIRST", reader.ReadToEnd());
        }

        using (var sharp = new ICSharpCode.SharpZipLib.Zip.ZipFile(path))
        {
            using var reader = new StreamReader(sharp.GetInputStream(sharp.GetEntry("a.txt")));
            Assert.Equal("SECOND", reader.ReadToEnd());
        }

        Assert.Equal(ZipDirectories.Split, ZipPrefix.Examine(path));
    }

    [Fact]
    public void 目録と終わりの記録の間に署名の記録があると食い違う()
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s1 = zip.Position - o1;
        zip.Bytes(0x50, 0x4b, 0x05, 0x05, 0, 0);
        zip.End(1, s1, o1);

        Assert.Equal(ZipDirectories.Split, ZipPrefix.Examine(Save("signature.zip", zip.ToArray())));
    }

    /// <summary>
    /// ZIP64 の記録は 2 つ目の目録を、終わりの記録の 32 ビットの位置の欄は 1 つ目の目録を指す。
    /// 目印の値をどの欄に付けるかで、部品ごとに ZIP64 として読むかが変わる。
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void ZIP64の記録と32ビットの欄が別の目録を指すと食い違う(bool markDisk, bool markCount, bool markSize)
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var second = zip.Local("a.txt", Second);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s = zip.Position - o1;
        var o2 = zip.Position;
        zip.Directory("a.txt", Second, second);
        var record = zip.Zip64End(1, s, o2);
        zip.Zip64Locator(record);
        zip.End(1, s, o1, markDisk: markDisk, markCount: markCount, markSize: markSize);

        Assert.Equal(ZipDirectories.Split, ZipPrefix.Examine(Save("zip64.zip", zip.ToArray())));
    }

    [Fact]
    public void コメントの中に偽の終わりの記録があると食い違う()
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s1 = zip.Position - o1;

        // 偽の終わりの記録はコメントの長さが末尾と合わないので、FindEnd は本物を選ぶ。.NET と SharpZipLib は後ろの署名から使う
        var fake = new RawZip();
        fake.End(1, s1, o1);
        zip.End(1, s1, o1, comment: [.. fake.ToArray(), (byte)'x', (byte)'x']);

        Assert.Equal(ZipDirectories.Split, ZipPrefix.Examine(Save("fake-end.zip", zip.ToArray())));
    }

    [Fact]
    public void ZIP64の記録が終わりの記録の直前で終わらないと食い違う()
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s = zip.Position - o1;
        var record = zip.Zip64End(1, s, o1);
        zip.Bytes(0, 0, 0, 0);
        zip.Zip64Locator(record);
        zip.End(1, s, o1, markSize: true, markOffset: true);

        Assert.Equal(ZipDirectories.Split, ZipPrefix.Examine(Save("zip64-gap.zip", zip.ToArray())));
    }

    // ------------------------------------------------------------------ 誤って止めない

    [Fact]
    public void 標準の部品とSharpZipLibとExpzipが作るZIPはそろう()
    {
        var dotnet = _place.PathOf("dotnet.zip");
        using (var zip = ZipFile.Open(dotnet, ZipArchiveMode.Create))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("a.txt").Open()))
            {
                writer.Write("A");
            }

            zip.CreateEntry("d/");
        }

        var sharp = new ZipSample().File("a.txt", "A").Folder("d").Save(_place.PathOf("sharp.zip"));

        var source = _place.PathOf("a.txt");
        File.WriteAllText(source, "A");
        var expzip = _place.PathOf("expzip.zip");
        ZipArchiveWriter.CreateWith(expzip, [source], "", false, null, CancellationToken.None);

        var empty = _place.PathOf("empty.zip");
        ZipArchiveWriter.CreateEmpty(empty);

        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(dotnet));
        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(sharp));
        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(expzip));
        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(empty));
    }

    [Fact]
    public void コメントや後ろの余りのある書庫はそろう()
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s1 = zip.Position - o1;
        zip.End(1, s1, o1, comment: Encoding.ASCII.GetBytes("comment"));
        var commented = zip.ToArray();

        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(Save("comment.zip", commented)));
        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(Save("trailing.zip", [.. commented, 1, 2, 3, 4])));
    }

    [Fact]
    public void 格納のZIPを最後の項目に持つZIPはそろう()
    {
        var inner = new RawZip();
        var a = inner.Local("a.txt", First);
        var o = inner.Position;
        inner.Directory("a.txt", First, a);
        inner.End(1, inner.Position - o, o);

        var outer = new RawZip();
        var b = outer.Local("inner.zip", inner.ToArray());
        var o1 = outer.Position;
        outer.Directory("inner.zip", inner.ToArray(), b);
        outer.End(1, outer.Position - o1, o1);

        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(Save("nested.zip", outer.ToArray())));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ZIP64の書庫はそろう(bool marked)
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s = zip.Position - o1;
        var record = zip.Zip64End(1, s, o1);
        zip.Zip64Locator(record);
        zip.End(1, s, o1, markDisk: marked, markCount: marked, markSize: marked, markOffset: marked);
        var path = Save("zip64.zip", zip.ToArray());

        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(path));
        Assert.Equal("FIRST", ReadListed(path, "a.txt"));
    }

    /// <summary>先頭にプログラムを付けただけで、位置は ZIP の始まりから書かれた書庫 (Expzip の自己解凍書庫と同じ作り)。</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 先頭にプログラムが付いた書庫はそろう(bool zip64)
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s = zip.Position - o1;
        if (zip64)
        {
            var record = zip.Zip64End(1, s, o1);
            zip.Zip64Locator(record);
        }

        zip.End(1, s, o1, markDisk: zip64, markCount: zip64, markSize: zip64, markOffset: zip64);
        var path = Save("sfx.exe", [.. Prefix(), .. zip.ToArray()]);

        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(path));
        Assert.Equal(Prefix().Length, ZipPrefix.Detect(path));
        Assert.Equal("FIRST", ReadListed(path, "a.txt"));
    }

    /// <summary>
    /// SharpZipLib が扱えない方式の項目は SharpCompress で読み直す。先頭のプログラムを隠さずに渡すと、目録が 1 つでも
    /// 別の位置を読み、検査とハッシュ値が展開と別の中身を見る (#396 の指摘 4)。
    /// </summary>
    [Fact]
    public void 先頭にプログラムが付いた書庫のLZMAの項目をSharpCompressで読める()
    {
        using var buffer = new MemoryStream();
        using (var writer = new SharpCompress.Writers.Zip.ZipWriter(
                   buffer, new SharpCompress.Writers.Zip.ZipWriterOptions(SharpCompress.Common.CompressionType.LZMA)))
        {
            writer.Write("a.txt", new MemoryStream(First), new DateTime(2026, 1, 1));
        }

        var path = Save("lzma.exe", [.. Prefix(), .. buffer.ToArray()]);
        var contents = Open(path);
        var read = new Dictionary<string, string>();
        ArchiveContentReader.Visit(path, contents, null, item =>
        {
            using var reader = new StreamReader(item.Open());
            read[item.Name] = reader.ReadToEnd();
        }, CancellationToken.None);

        Assert.Equal("FIRST", read["a.txt"]);
    }

    [Fact]
    public void 先頭にプログラムが付き位置をファイルの先頭から書いた書庫はそろう()
    {
        var prefix = Prefix();
        var zip = new RawZip(prefix);
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        zip.End(1, zip.Position - o1, o1);
        var path = Save("sfx-absolute.exe", zip.ToArray());

        Assert.Equal(ZipDirectories.Agree, ZipPrefix.Examine(path));
        Assert.Equal(0, ZipPrefix.Detect(path));
        Assert.Equal("FIRST", ReadListed(path, "a.txt"));
    }

    // ------------------------------------------------------------------ 食い違う書庫の扱い

    private static ArchiveContents Open(string path) => ArchiveReader.Open(path, NameReading.Default, null, CancellationToken.None);

    [Fact]
    public void 食い違う書庫は開けて一覧は読めるが書き換えられない()
    {
        var contents = Open(Save("two.zip", TwoDirectories()));

        Assert.True(contents.HasSplitDirectory);
        Assert.False(contents.IsEditable);
        Assert.False(contents.RequiresPassword);
        Assert.Equal(First.Length, Assert.Single(contents.Root.Files).Length);
    }

    [Fact]
    public void 食い違う書庫の検査は危険を出し中身を読まない()
    {
        var contents = Open(Save("two.zip", TwoDirectories()));
        var report = ArchiveInspector.Inspect(contents, null);

        var split = Assert.Single(report.Findings, f => f.Issue == InspectionIssue.SplitDirectory);
        Assert.Equal(InspectionSeverity.Danger, split.Severity);
        Assert.Equal(InspectionCheck.Structure, split.Check);
        Assert.DoesNotContain(report.Findings, f => f.Issue is InspectionIssue.Unreadable or InspectionIssue.HeaderMismatch);
    }

    [Fact]
    public void 食い違う書庫のハッシュ値は計算しない()
    {
        var contents = Open(Save("two.zip", TwoDirectories()));

        Assert.Throws<SplitDirectoryException>(() =>
            ArchiveContentReader.Visit(contents.FilePath, contents, null, _ => { }, CancellationToken.None));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("secret")]
    public void 食い違う書庫は展開しない(string? password)
    {
        var path = Save("two.zip", TwoDirectories());

        Assert.Throws<SplitDirectoryException>(() => ArchiveExtractor.Extract(
            path, ArchiveFormat.Zip, NameReading.Default, null, _place.Output, false, null, CancellationToken.None,
            password: password));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_place.Output));
    }

    [Fact]
    public void 食い違う書庫は削除とコメントの書き換えをしない()
    {
        var bytes = TwoDirectories();
        var path = Save("two.zip", bytes);

        Assert.Throws<SplitDirectoryException>(() =>
            ZipArchiveWriter.Delete(path, new HashSet<string>(["a.txt"]), [], CancellationToken.None));
        Assert.Throws<SplitDirectoryException>(() => ZipComment.Write(path, "comment"));

        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    /// <summary>
    /// 先頭が局所ヘッダーの署名で始まり、先頭にずれのある ZIP64 の書庫 (#396 の実装のあとの確認の指摘 1)。
    /// 書き換えの部品は先頭を隠さずに読み、ZIP64 では一覧と別の目録を読むので、書き換えない。
    /// </summary>
    [Fact]
    public void 先頭にずれのある書庫は先頭が局所ヘッダーでも書き換えない()
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s = zip.Position - o1;
        var record = zip.Zip64End(1, s, o1);
        zip.Zip64Locator(record);
        zip.End(1, s, o1, markSize: true, markOffset: true);

        var prefix = Prefix();
        prefix[0] = 0x50;
        prefix[1] = 0x4b;
        prefix[2] = 0x03;
        prefix[3] = 0x04;
        var bytes = (byte[])[.. prefix, .. zip.ToArray()];
        var path = Save("prefixed.zip", bytes);

        var contents = Open(path);
        Assert.False(contents.HasSplitDirectory);
        Assert.True(contents.IsSelfExtracting);
        Assert.False(contents.IsEditable);
        Assert.Throws<PrefixedZipEditException>(() => ZipUpdate.Open(path));
        Assert.Equal(bytes, File.ReadAllBytes(path));
    }

    /// <summary>
    /// 自己解凍のプログラムは ZIP64 の記録を読まない (#396 の実装のあとの確認の指摘 2)。位置の記録のある書庫からは作らない。
    /// </summary>
    [Fact]
    public void ZIP64の位置の記録がある書庫は自己解凍書庫にしない()
    {
        var zip = new RawZip();
        var first = zip.Local("a.txt", First);
        var o1 = zip.Position;
        zip.Directory("a.txt", First, first);
        var s = zip.Position - o1;
        var record = zip.Zip64End(1, s, o1);
        zip.Zip64Locator(record);
        zip.End(1, s, o1);

        var contents = Open(Save("zip64.zip", zip.ToArray()));

        Assert.Equal(SfxRejection.Zip64, SfxWriter.Reject(contents));
    }

    /// <summary>断るかを決めたあとで置き換わった書庫からも作らない (#396 の仕様書の係の指摘 1)。</summary>
    [Fact]
    public void 自己解凍書庫を作るときは写すファイルで確かめ直す()
    {
        var path = Save("two.zip", TwoDirectories());
        var exe = _place.PathOf("two.exe");

        var error = Assert.Throws<SfxRejectedException>(() => SfxWriter.Create(path, exe));

        Assert.Equal(SfxRejection.SplitDirectory, error.Reason);
        Assert.False(File.Exists(exe));
    }

    [Fact]
    public void 食い違う書庫の理由は日本語で出る()
    {
        Assert.DoesNotContain("central", Localization.Strings.Reason(new SplitDirectoryException()), StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ 手伝い

    private static byte[] Prefix()
    {
        // 局所ヘッダーの署名を含まない、プログラムの代わりの塊
        var prefix = new byte[4096];
        prefix[0] = (byte)'M';
        prefix[1] = (byte)'Z';
        return prefix;
    }

    private static string ReadListed(string path, string name)
    {
        using var stream = ZipPrefix.Open(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}

/// <summary>ZIP をバイト単位で組み立てる (#396)。項目は格納 (無圧縮) だけ。</summary>
internal sealed class RawZip
{
    private readonly MemoryStream _data = new();

    public RawZip(byte[]? prefix = null)
    {
        if (prefix is not null)
        {
            _data.Write(prefix);
        }
    }

    /// <summary>今までに書いた長さ。次に書くものの、ファイルの先頭からの位置。</summary>
    public long Position => _data.Length;

    public byte[] ToArray() => _data.ToArray();

    public void Bytes(params byte[] bytes) => _data.Write(bytes);

    /// <summary>局所ヘッダーと中身を書き、局所ヘッダーの位置を返す。</summary>
    public long Local(string name, byte[] content)
    {
        var at = Position;
        var nameBytes = Encoding.ASCII.GetBytes(name);
        U32(0x04034b50);
        U16(20);
        U16(0);
        U16(0);
        U16(0);
        U16(0x21);
        U32(Crc(content));
        U32((uint)content.Length);
        U32((uint)content.Length);
        U16((ushort)nameBytes.Length);
        U16(0);
        _data.Write(nameBytes);
        _data.Write(content);
        return at;
    }

    /// <summary>目録の 1 件を書く。</summary>
    public void Directory(string name, byte[] content, long localOffset)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        U32(0x02014b50);
        U16(20);
        U16(20);
        U16(0);
        U16(0);
        U16(0);
        U16(0x21);
        U32(Crc(content));
        U32((uint)content.Length);
        U32((uint)content.Length);
        U16((ushort)nameBytes.Length);
        U16(0);
        U16(0);
        U16(0);
        U16(0);
        U32(0x20);
        U32((uint)localOffset);
        _data.Write(nameBytes);
    }

    /// <summary>ZIP64 の終わりの記録を書き、その位置を返す。</summary>
    public long Zip64End(long entries, long size, long offset)
    {
        var at = Position;
        U32(0x06064b50);
        U64(44);
        U16(45);
        U16(45);
        U32(0);
        U32(0);
        U64((ulong)entries);
        U64((ulong)entries);
        U64((ulong)size);
        U64((ulong)offset);
        return at;
    }

    /// <summary>ZIP64 の終わりの記録の位置の記録を書く。</summary>
    public void Zip64Locator(long record)
    {
        U32(0x07064b50);
        U32(0);
        U64((ulong)record);
        U32(1);
    }

    /// <summary>終わりの記録を書く。mark の付いた欄には、ZIP64 の記録を見よという目印の値を書く。</summary>
    public void End(int entries, long size, long offset, byte[]? comment = null,
        bool markDisk = false, bool markCount = false, bool markSize = false, bool markOffset = false)
    {
        comment ??= [];
        U32(0x06054b50);
        U16(markDisk ? ushort.MaxValue : (ushort)0);
        U16(markDisk ? ushort.MaxValue : (ushort)0);
        U16(markCount ? ushort.MaxValue : (ushort)entries);
        U16(markCount ? ushort.MaxValue : (ushort)entries);
        U32(markSize ? uint.MaxValue : (uint)size);
        U32(markOffset ? uint.MaxValue : (uint)offset);
        U16((ushort)comment.Length);
        _data.Write(comment);
    }

    private void U16(ushort value)
    {
        Span<byte> span = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(span, value);
        _data.Write(span);
    }

    private void U32(uint value)
    {
        Span<byte> span = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(span, value);
        _data.Write(span);
    }

    private void U64(ulong value)
    {
        Span<byte> span = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(span, value);
        _data.Write(span);
    }

    private static uint Crc(byte[] data)
    {
        var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
        crc.Update(data);
        return (uint)crc.Value;
    }
}
