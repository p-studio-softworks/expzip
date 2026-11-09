using System.IO.Compression;
using System.Text;
using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 書庫の中の名前の文字コードを選び直す (#335)。読み方は、その書庫を読む処理に引数で渡す。
/// </summary>
public sealed class NameReadingTests
{
    static NameReadingTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // 「资料/报告.txt」。中国語 (簡体字) の名前
    private const string ChineseFolder = "资料";
    private const string ChineseFile = "报告.txt";

    [Fact]
    public void 中国語の名前のZIPを選び直した読み方で開くと名前が読める()
    {
        using var place = new Place();
        var archive = place.MakeZip(936, ($"{ChineseFolder}/{ChineseFile}", "A"), ("plain.txt", "B"));

        var usual = ArchiveReader.Open(archive, NameReading.Default);
        Assert.DoesNotContain(usual.Root.Folders, f => f.Name == ChineseFolder);
        Assert.True(usual.IsEditable);
        Assert.NotEmpty(usual.LegacyNameBytes);

        var chosen = ArchiveReader.Open(archive, NameReading.Chosen(936));
        var folder = Assert.Single(chosen.Root.Folders, f => f.Name == ChineseFolder);
        Assert.Equal(ChineseFile, Assert.Single(folder.Files).Name);
        Assert.Contains(chosen.Root.Files, f => f.Name == "plain.txt");

        // 選び直した書庫は書き換えない
        Assert.False(chosen.IsEditable);
        Assert.Equal(936, chosen.Names.CodePage);
    }

    [Fact]
    public void 候補には読めるものだけが並ぶ()
    {
        using var place = new Place();
        var archive = place.MakeZip(936, ($"{ChineseFolder}/{ChineseFile}", "A"));
        var contents = ArchiveReader.Open(archive, NameReading.Default);

        var candidates = NameCandidates.Find(contents.LegacyNameBytes);

        Assert.Contains(candidates, c => c.CodePage == 936 && c.Common);
        // UTF-8 として正しくないバイト列なので、UTF-8 は並ばない
        Assert.DoesNotContain(candidates, c => c.CodePage == 65001);
        // 切り替えの記号を使うものや UTF-16 は並ばない
        Assert.DoesNotContain(candidates, c => c.CodePage is 50220 or 1200 or 52936);
    }

    [Fact]
    public void UTF8の印のある名前だけの書庫は選び直す名前が無い()
    {
        using var place = new Place();
        var archive = place.MakeZip(65001, ($"{ChineseFolder}/{ChineseFile}", "A"));

        var contents = ArchiveReader.Open(archive, NameReading.Default);

        Assert.Empty(contents.LegacyNameBytes);
        Assert.Empty(NameCandidates.Find(contents.LegacyNameBytes));
        // 選び直しても、UTF-8 の印のある名前は変わらない
        var chosen = ArchiveReader.Open(archive, NameReading.Chosen(1252));
        Assert.Contains(chosen.Root.Folders, f => f.Name == ChineseFolder);
    }

    [Fact]
    public void 選び直した読み方で展開すると一覧と同じ名前でできる()
    {
        using var place = new Place();
        var archive = place.MakeZip(936, ($"{ChineseFolder}/{ChineseFile}", "A"));
        var chosen = NameReading.Chosen(936);
        var contents = ArchiveReader.Open(archive, chosen);
        var entry = Assert.Single(contents.Root.Folders.Single().Files);
        var destination = place.Folder("out");

        // 項目を選んでも、すべて展開しても、一覧と同じ名前
        var one = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, chosen,
            new HashSet<string>(StringComparer.Ordinal) { entry.SourceName }, destination, false, null, CancellationToken.None);
        Assert.Equal(1, one.Extracted);
        Assert.True(File.Exists(Path.Combine(destination, ChineseFolder, ChineseFile)));

        var all = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, chosen, null, place.Folder("all"), false, null, CancellationToken.None);
        Assert.Equal(1, all.Extracted);
        Assert.True(File.Exists(Path.Combine(place.Root, "all", ChineseFolder, ChineseFile)));
    }

    [Fact]
    public void 区切りとして読む文字コードで選び直しても展開先の外に出ない()
    {
        // Shift_JIS の「表」は 0x95 0x5C。西ヨーロッパ (Windows) では「•\」になり、\ が区切りになる
        using var place = new Place();
        var archive = place.MakeZip(932, ("表表..表.txt", "A"));
        var chosen = NameReading.Chosen(1252);
        var contents = ArchiveReader.Open(archive, chosen);
        Assert.NotEmpty(contents.Root.Folders);
        var destination = place.Folder("out");

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, chosen, null, destination, false, null, CancellationToken.None);

        Assert.Empty(result.Failed);
        var written = Directory.EnumerateFiles(place.Root, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.All(written, f => Assert.StartsWith(destination + Path.DirectorySeparatorChar, f, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void 中国語の名前のtarも選び直せる()
    {
        using var place = new Place();
        var archive = place.MakeTar(936, $"{ChineseFolder}/{ChineseFile}", "A");

        var usual = ArchiveReader.Open(archive, NameReading.Default);
        Assert.NotEmpty(usual.LegacyNameBytes);
        Assert.False(usual.IsEditable);

        var chosen = NameReading.Chosen(936);
        var contents = ArchiveReader.Open(archive, chosen);
        Assert.Equal(ChineseFile, contents.Root.Folders.Single(f => f.Name == ChineseFolder).Files.Single().Name);

        var destination = place.Folder("out");
        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Tar, chosen, null, destination, false, null, CancellationToken.None);
        Assert.Equal(1, result.Extracted);
        Assert.True(File.Exists(Path.Combine(destination, ChineseFolder, ChineseFile)));
    }

    [Fact]
    public void 同じファイルになる2項目は上書きしない指定なら後ろを飛ばして名前を残す()
    {
        // 選んだものから新しい書庫を作るときは、この名前があれば書庫を作らずに止める (#335)。黙って 1 つにしない
        using var place = new Place();
        var archive = place.MakeZip(65001, ("a.txt", "FIRST"), ("a.txt.", "SECOND"));

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, place.Folder("out"), false, null, CancellationToken.None);

        Assert.Equal(1, result.Extracted);
        Assert.Equal(["a.txt."], result.SkippedNames);
        Assert.Equal("FIRST", File.ReadAllText(Path.Combine(place.Root, "out", "a.txt")));
    }

    [Fact]
    public void コメントも選び直した読み方で読む()
    {
        using var place = new Place();
        var archive = place.MakeZip(936, ("a.txt", "A"));
        var gbk = Encoding.GetEncoding(936);
        using (var zip = new ZipArchive(File.Open(archive, FileMode.Open), ZipArchiveMode.Update, false, gbk))
        {
            zip.Comment = "说明";
        }

        Assert.Equal("说明", ArchiveReader.Open(archive, NameReading.Chosen(936)).Comment);
    }

    private sealed class Place : IDisposable
    {
        public Place()
            => Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"))).FullName;

        public string Root { get; }

        public string Folder(string name) => Directory.CreateDirectory(Path.Combine(Root, name)).FullName;

        /// <summary>名前を指定の文字コードで書いた ZIP。UTF-8 なら UTF-8 の印が立ち、ほかは立たない。</summary>
        public string MakeZip(int codePage, params (string Name, string Text)[] entries)
        {
            var path = Path.Combine(Root, $"names{codePage}.zip");
            using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create, false, Encoding.GetEncoding(codePage));
            foreach (var (name, text) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(text);
            }

            return path;
        }

        /// <summary>名前を指定の文字コードで書いた、ファイル 1 個の ustar 形式の tar。</summary>
        public string MakeTar(int codePage, string name, string text)
        {
            var path = Path.Combine(Root, $"names{codePage}.tar");
            var content = Encoding.ASCII.GetBytes(text);
            var header = new byte[512];
            Encoding.GetEncoding(codePage).GetBytes(name).CopyTo(header, 0);
            Ascii("0000644", header, 100);
            Ascii("0000000", header, 108);
            Ascii("0000000", header, 116);
            Ascii(Convert.ToString(content.Length, 8).PadLeft(11, '0'), header, 124);
            Ascii("14000000000", header, 136);
            header[156] = (byte)'0';
            Ascii("ustar", header, 257);
            Ascii("00", header, 263);
            for (var i = 148; i < 156; i++)
            {
                header[i] = (byte)' ';
            }

            Ascii(Convert.ToString(header.Sum(static b => b), 8).PadLeft(6, '0'), header, 148);
            header[155] = (byte)' ';

            using var file = File.Create(path);
            file.Write(header);
            file.Write(content);
            file.Write(new byte[512 - content.Length]);
            file.Write(new byte[1024]);
            return path;
        }

        private static void Ascii(string value, byte[] header, int at) => Encoding.ASCII.GetBytes(value).CopyTo(header, at);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
