using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Tests;

/// <summary>
/// tar・ZIP・7z のリンクを、リンクのまま展開する (#367)。攻撃の見本は設計の確認 (#367 のノート) で挙がったもの。
/// </summary>
/// <remarks>
/// 開発者モードがオフの PC ではシンボリックリンクを作れない。そのときは「権限がありません」で失敗し、
/// ほかの種類のリンクやファイルに変えずに、何も置かないことを確かめる。
/// </remarks>
public sealed class LinkExtractionTests : IDisposable
{
    private readonly LinkPlace _place = new();

    public void Dispose() => _place.Dispose();

    private static string NoPrivilege => Strings.Reason(new LinkPrivilegeException());

    private ExtractResult Extract(
        string archive, ArchiveFormat format, IReadOnlySet<string>? names = null, ExtractOverwrite? overwrite = null,
        string? basePath = null, LinkOptions? links = null, string? password = null)
        => ArchiveExtractor.Extract(archive, format, NameReading.Default, names, _place.Output,
            overwrite ?? ExtractOverwrite.Only([]), null, CancellationToken.None, basePath: basePath, password: password,
            links: links);

    /// <summary>シンボリックリンクができたか、権限が無くて何も置かなかったか。</summary>
    /// <param name="name">展開先での名前。</param>
    /// <param name="inArchive">書庫の中の名前 (結果の失敗に出る名前)。省くと <paramref name="name"/>。</param>
    private void AssertSymlink(
        ExtractResult result, string name, string expectedTarget, string? content = null, string? inArchive = null)
    {
        var path = _place.In(name);
        var failure = result.Failed.Where(f => f.Name == (inArchive ?? name)).ToList();
        if (failure.Count > 0)
        {
            Assert.Equal(NoPrivilege, Assert.Single(failure).Reason);
            Assert.False(Path.Exists(path), "権限が無いときは、ファイルとしても置かない");
            return;
        }

        var info = new FileInfo(path);
        Assert.True(info.Exists || Directory.Exists(path), "展開する");
        Assert.True(info.Attributes.HasFlag(FileAttributes.ReparsePoint), "リンクとして展開する");
        Assert.Equal(expectedTarget, info.LinkTarget);
        if (content is not null)
        {
            Assert.Equal(content, File.ReadAllText(path));
        }
    }

    private void AssertFailed(ExtractResult result, string name, string reason)
    {
        Assert.Contains(result.Failed, f => f.Name == name && f.Reason == reason);
        Assert.False(Path.Exists(_place.In(name)), "展開しなかったリンクは、中身のファイルとしても書き出さない");
    }

    // ------------------------------------------------------------------ ZIP

    [Theory]
    [InlineData(3, null)]
    [InlineData(0, null)]
    [InlineData(10, null)]
    [InlineData(3, "pass")]
    [InlineData(0, "pass")]
    public void ZIPのリンクは作ったOSによらず外部属性で見分けパスワードの有無でも同じ(int host, string? password)
    {
        var zip = new ZipSample()
            .File("a.txt", "A")
            .Symlink("sub/link.txt", "../a.txt", host)
            .Save(_place.PathOf("links.zip"), password);

        var result = Extract(zip, ArchiveFormat.Zip, password: password);

        Assert.Equal("A", File.ReadAllText(_place.In("a.txt")));
        AssertSymlink(result, "sub/link.txt", @"..\a.txt", "A");
    }

    [Theory]
    [InlineData("../../outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(@"C:\Windows")]
    [InlineData("//server/share/x")]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\??\C:\Windows")]
    [InlineData("a.txt:stream")]
    [InlineData("missing.txt")]
    public void ZIPの書庫の外を指すリンクは展開しない(string target)
    {
        var zip = new ZipSample()
            .File("a.txt", "A")
            .Symlink("link", target)
            .Save(_place.PathOf("outside.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        Assert.Equal(1, result.Extracted);
        AssertFailed(result, "link", Strings.LinkPointsOutside);
    }

    [Fact]
    public void リンクの下に置いた項目を外に書かせない()
    {
        var zip = new ZipSample()
            .Symlink("evil", "../../outside")
            .File("evil/payload.txt", "EVIL")
            .Save(_place.PathOf("through.zip"));

        Extract(zip, ArchiveFormat.Zip);

        Assert.False(Directory.Exists(_place.PathOf("outside")));
        Assert.False(new DirectoryInfo(_place.In("evil")).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Theory]
    [InlineData("a//lnk")]
    [InlineData("a/./lnk")]
    [InlineData("./a/lnk")]
    public void 名前の空の区切りや点でリンクの指す先がずれない(string name)
    {
        // 書いたとおりに読めば a/lnk から 2 つ上 (展開先の外)。空の区切りを 1 段と数えると、書庫の中の x を指してしまう
        var zip = new ZipSample()
            .File("x", "X")
            .Symlink(name, "../../x")
            .Save(_place.PathOf("names.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        Assert.Contains(result.Failed, f => f.Reason == Strings.LinkPointsOutside);
        Assert.False(Path.Exists(_place.In("a/lnk")));
    }

    [Fact]
    public void 同じ名前のリンクとファイルは書庫で後ろのものだけを展開する_リンクが先()
    {
        var zip = new ZipSample()
            .File("a.txt", "A")
            .Symlink("setup.txt", "a.txt")
            .File("setup.txt", "NEW")
            .Save(_place.PathOf("dup1.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        Assert.Empty(result.Failed);
        var setup = new FileInfo(_place.In("setup.txt"));
        Assert.False(setup.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("NEW", File.ReadAllText(setup.FullName));
    }

    [Fact]
    public void 同じ名前のリンクとファイルは書庫で後ろのものだけを展開する_ファイルが先()
    {
        var zip = new ZipSample()
            .File("a.txt", "A")
            .File("setup.txt", "OLD")
            .Symlink("setup.txt", "a.txt")
            .Save(_place.PathOf("dup2.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        // 前のファイルは書き出さない。リンクを作れないときは何も残らない
        AssertSymlink(result, "setup.txt", "a.txt", "A");
    }

    [Theory]
    [InlineData("toolong")]
    [InlineData("control")]
    [InlineData("utf8")]
    [InlineData("empty")]
    [InlineData("slash")]
    public void 読めない指す先のリンクは展開しない(string kind)
    {
        byte[] target = kind switch
        {
            "toolong" => System.Text.Encoding.ASCII.GetBytes(new string('a', 4097)),
            "control" => "a\n.txt"u8.ToArray(),
            "utf8" => [0x61, 0xFF, 0xFE],
            "empty" => [],
            _ => "a.txt/"u8.ToArray(),
        };

        var zip = new ZipSample()
            .File("a.txt", "A")
            .Add("link", target, 3, unchecked((int)0xA1FF0000))
            .Save(_place.PathOf("bad.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        Assert.Contains(result.Failed, f => f.Name == "link");
        Assert.False(Path.Exists(_place.In("link")));
    }

    [Fact]
    public void 指す先の中身が壊れていれば展開しない()
    {
        var path = new ZipSample()
            .File("a.txt", "A")
            .Symlink("link", "a.txt")
            .Save(_place.PathOf("crc.zip"));

        // 指す先の文字を 1 つ書き換える (CRC が合わなくなる)
        var bytes = File.ReadAllBytes(path);
        var at = IndexOf(bytes, "linka.txt"u8.ToArray()) + 4;
        bytes[at] = (byte)'b';
        File.WriteAllBytes(path, bytes);

        var result = Extract(path, ArchiveFormat.Zip);

        Assert.Contains(result.Failed, f => f.Name == "link");
        Assert.False(Path.Exists(_place.In("link")));
    }

    [Fact]
    public void ファイルとフォルダーのどちらを指すか決められないリンクは展開しない()
    {
        var zip = new ZipSample()
            .File("docs", "FILE")
            .File("Docs/a.txt", "A")
            .Symlink("link", "docs")
            .Save(_place.PathOf("unknown.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        AssertFailed(result, "link", Strings.LinkKindUnknown);
    }

    [Fact]
    public void ZIPのフォルダーを指すリンクは書庫から決める()
    {
        var zip = new ZipSample()
            .File("data/a.txt", "A")
            .Symlink("alias", "data")
            .Save(_place.PathOf("folder.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        var alias = _place.In("alias");
        var failure = result.Failed.SingleOrDefault(f => f.Name == "alias");
        if (failure.Name is not null)
        {
            Assert.Equal(NoPrivilege, failure.Reason);
            Assert.False(Path.Exists(alias));
            return;
        }

        Assert.True(Directory.Exists(alias));
        Assert.Equal("A", File.ReadAllText(Path.Combine(alias, "a.txt")));
    }

    [Fact]
    public void 並べて展開してもリンクはファイルを書き終えてから置く()
    {
        var previous = SeekPenalty.Override;
        SeekPenalty.Override = static _ => false;
        try
        {
            var sample = new ZipSample().Symlink("link.txt", "f0.txt");
            for (var i = 0; i < 40; i++)
            {
                sample.File($"f{i}.txt", "F" + i);
            }

            var result = Extract(sample.Save(_place.PathOf("lanes.zip")), ArchiveFormat.Zip);

            Assert.Equal(40, Enumerable.Range(0, 40).Count(i => File.Exists(_place.In($"f{i}.txt"))));
            AssertSymlink(result, "link.txt", "f0.txt", "F0");
        }
        finally
        {
            SeekPenalty.Override = previous;
        }
    }

    [Fact]
    public void フォルダーを選んで展開しても中を指すリンクは展開する()
    {
        var zip = new ZipSample()
            .File("top/a.txt", "A")
            .Symlink("top/link.txt", "a.txt")
            .Save(_place.PathOf("base.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, names: new HashSet<string>(["top/a.txt", "top/link.txt"]), basePath: "top");

        AssertSymlink(result, "link.txt", "a.txt", "A", inArchive: "top/link.txt");
    }

    [Fact]
    public void 上書きしないで飛ばしたリンクも飛ばした項目に数える()
    {
        File.WriteAllText(_place.In("link.txt"), "USER");
        var zip = new ZipSample()
            .File("a.txt", "A")
            .Symlink("link.txt", "a.txt")
            .Save(_place.PathOf("skip.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, overwrite: false);

        Assert.Contains("link.txt", result.SkippedNames);
        Assert.Equal("USER", File.ReadAllText(_place.In("link.txt")));
    }

    // ------------------------------------------------------------------ tar

    [Fact]
    public void 点の付いたtarでフォルダーを選んでもリンクを展開する()
    {
        var tar = new TarSample()
            .Folder("./top")
            .File("./top/a.txt", "A")
            .HardLink("./top/hard.txt", "./top/a.txt")
            .Symlink("./top/sym.txt", "a.txt")
            .Save(_place.PathOf("dot.tar"));

        var result = Extract(tar, ArchiveFormat.Tar,
            names: new HashSet<string>(["./top/a.txt", "./top/hard.txt", "./top/sym.txt"]), basePath: "top");

        Assert.Equal("A", File.ReadAllText(_place.In("hard.txt")));
        AssertSymlink(result, "sym.txt", "a.txt", "A", inArchive: "./top/sym.txt");
    }

    [Fact]
    public void tarのハードリンクは今回展開したファイルだけを指す()
    {
        File.WriteAllText(_place.In("old.txt"), "USER");
        var tar = new TarSample()
            .File("a.txt", "A")
            .HardLink("hard.txt", "a.txt")
            .HardLink("stolen.txt", "old.txt")
            .HardLink("outside.txt", "../outside.txt")
            .HardLink("before.txt", "later.txt")
            .File("later.txt", "L")
            .Save(_place.PathOf("hard.tar"));

        var result = Extract(tar, ArchiveFormat.Tar);

        Assert.Equal("A", File.ReadAllText(_place.In("hard.txt")));
        Assert.Equal("L", File.ReadAllText(_place.In("before.txt")));
        AssertFailed(result, "stolen.txt", Strings.LinkTargetNotExtracted);
        AssertFailed(result, "outside.txt", Strings.LinkTargetNotExtracted);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void tarのハードリンクは上書きの指定が真偽でも展開する(bool overwrite)
    {
        // ドラッグや 1 個を開く道は真偽で渡す。リンクの項目が出てくる前に書いたファイルも覚えている
        var tar = new TarSample()
            .File("a.txt", "A")
            .HardLink("hard.txt", "a.txt")
            .Save(_place.PathOf("bool.tar"));

        var result = Extract(tar, ArchiveFormat.Tar, overwrite: (ExtractOverwrite)overwrite);

        Assert.Empty(result.Failed);
        Assert.Equal("A", File.ReadAllText(_place.In("hard.txt")));
    }

    [Fact]
    public void PAXのグローバルヘッダーのlinkpathでふつうのファイルをリンクにしない()
    {
        var tar = new TarSample()
            .Pax(new Dictionary<string, string> { ["linkpath"] = "../../outside" }, global: true)
            .File("a.txt", "A")
            .Save(_place.PathOf("global.tar"));

        var result = Extract(tar, ArchiveFormat.Tar);

        Assert.Empty(result.Failed);
        var file = new FileInfo(_place.In("a.txt"));
        Assert.False(file.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("A", File.ReadAllText(file.FullName));
    }

    [Fact]
    public void tarの長すぎる指す先のリンクは展開しない()
    {
        var tar = new TarSample()
            .File("a.txt", "A")
            .Pax(new Dictionary<string, string> { ["linkpath"] = new string('b', 4097) })
            .Symlink("link", "a.txt")
            .Save(_place.PathOf("long.tar"));

        var result = Extract(tar, ArchiveFormat.Tar);

        Assert.Contains(result.Failed, f => f.Name == "link");
        Assert.False(Path.Exists(_place.In("link")));
    }

    [Fact]
    public void tarの同じ名前はいちばん後ろのものだけ()
    {
        var tar = new TarSample()
            .File("a.txt", "A")
            .Symlink("setup.txt", "a.txt")
            .File("setup.txt", "NEW")
            .Save(_place.PathOf("dup.tar"));

        var result = Extract(tar, ArchiveFormat.Tar);

        Assert.Empty(result.Failed);
        Assert.Equal("NEW", File.ReadAllText(_place.In("setup.txt")));
        Assert.False(new FileInfo(_place.In("setup.txt")).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void tarのリンクの種類をSharpCompressから読める()
    {
        // 種類は SharpCompress の公開の API に無いので、中の値を読む。版を上げて読めなくなったら、ここで落ちる
        var tar = new TarSample()
            .File("a.txt", "A")
            .Symlink("s", "a.txt")
            .HardLink("h", "a.txt")
            .Save(_place.PathOf("kinds.tar"));

        using var stream = File.OpenRead(tar);
        using var reader = SharpArchiveAccess.OpenTarReader(stream, NameReading.Default);
        var kinds = new List<RarLinkKind>();
        while (reader.MoveToNextEntry())
        {
            kinds.Add(ArchiveLinks.TarKind(reader.Entry));
        }

        Assert.Equal([RarLinkKind.None, RarLinkKind.UnixSymbolicLink, RarLinkKind.HardLink], kinds);
    }

    // ------------------------------------------------------------------ 7z

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 七zのUnixのリンクはリンクとして展開する(bool solid)
    {
        var archive = new SevenZipSample()
            .File("a.txt", "A")
            .Symlink("sub/link.txt", "../a.txt")
            .File("b.txt", "B")
            .Save(_place.PathOf("links.7z"), solid);

        var result = Extract(archive, ArchiveFormat.SevenZip);

        Assert.Equal("B", File.ReadAllText(_place.In("b.txt")));
        AssertSymlink(result, "sub/link.txt", @"..\a.txt", "A");
    }

    [Theory]
    [InlineData(SevenZipSample.ReparsePoint)]
    [InlineData(SevenZipSample.ReparsePoint | SevenZipSample.UnixSymlink)]
    public void 七zのWindowsのリンクの形は展開しない(uint attributes)
    {
        var archive = new SevenZipSample()
            .File("a.txt", "A")
            .Add("link", [0x0C, 0x00, 0x00, 0xA0, 0x10, 0x00], attributes, folder: false)
            .Save(_place.PathOf("snl.7z"));

        var result = Extract(archive, ArchiveFormat.SevenZip);

        Assert.Equal("A", File.ReadAllText(_place.In("a.txt")));
        AssertFailed(result, "link", Strings.LinkKindNotExtracted);
    }

    [Fact]
    public void 七zのソリッドで同じ名前のリンクとファイルは後ろのものだけ()
    {
        var archive = new SevenZipSample()
            .File("a.txt", "A")
            .Symlink("setup.txt", "a.txt")
            .File("setup.txt", "NEW")
            .Save(_place.PathOf("dup.7z"), solid: true);

        var result = Extract(archive, ArchiveFormat.SevenZip);

        Assert.Empty(result.Failed);
        Assert.Equal("NEW", File.ReadAllText(_place.In("setup.txt")));
    }

    [Fact]
    public void 七zのリンクだけを選んでも指す先が書庫の中なら展開する()
    {
        var archive = new SevenZipSample()
            .File("a.txt", "A")
            .Symlink("link.txt", "a.txt")
            .Save(_place.PathOf("one.7z"));

        var result = Extract(archive, ArchiveFormat.SevenZip, names: new HashSet<string>(["link.txt"]));

        // 指す先は書庫の中にあるので展開する (展開先ではまだ無いが、指す先は文字の上で決める)
        AssertSymlink(result, "link.txt", "a.txt");
    }

    // ------------------------------------------------------------------ ドラッグと、選んだ項目から作る

    [Fact]
    public void ドラッグではドラッグする項目の外を指すリンクを取り出さない()
    {
        var zip = new ZipSample()
            .File("top/sub/a.txt", "A")
            .File("top/other.txt", "O")
            .Symlink("top/sub/up.txt", "../other.txt")
            .Symlink("top/sub/in.txt", "a.txt")
            .Save(_place.PathOf("drag.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, links: new LinkOptions { Roots = ["top/sub"] });

        AssertFailed(result, "top/sub/up.txt", Strings.LinkPointsOutsideItems);
        AssertSymlink(result, "top/sub/in.txt", "a.txt", "A");
    }

    [Fact]
    public void 同じフォルダーのリンクと指す先を一緒に選ぶとリンクも取り出す()
    {
        var zip = new ZipSample()
            .File("資料/報告書.pdf", "PDF")
            .Symlink("資料/報告書へのリンク", "報告書.pdf")
            .Save(_place.PathOf("same.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, links: new LinkOptions { Roots = ["資料/報告書.pdf", "資料/報告書へのリンク"] });

        AssertSymlink(result, "資料/報告書へのリンク", "報告書.pdf", "PDF");
    }

    [Fact]
    public void 別々のフォルダーの項目を選んだときは同じ項目の中を指すリンクだけ()
    {
        // 検索の結果で x と y の項目を一緒に選んだ。落とした先では隣どうしになるので、../x/a.txt は別のものを指してしまう
        var zip = new ZipSample()
            .File("x/a.txt", "A")
            .Symlink("y/l.txt", "../x/a.txt")
            .Save(_place.PathOf("apart.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, links: new LinkOptions { Roots = ["x/a.txt", "y/l.txt"] });

        AssertFailed(result, "y/l.txt", Strings.LinkPointsOutsideItems);
    }

    [Fact]
    public void ドラッグではジャンクションを取り出さない()
    {
        var archive = new RarSample()
            .File("data", directory: true)
            .File("data/a.txt", "A")
            .File("alias", redirect: (3, "data", true))
            .Save(_place.PathOf("junction.rar"));

        var result = Extract(archive, ArchiveFormat.Rar, links: new LinkOptions { Roots = ["data", "alias"], JunctionsNotAllowed = true });

        AssertFailed(result, "alias", Strings.LinkJunctionNotDragged);
        Assert.Equal("A", File.ReadAllText(_place.In("data/a.txt")));
    }

    [Fact]
    public void 選んだ項目から作るときはリンクをディスクに作らずに渡す()
    {
        var zip = new ZipSample()
            .File("top/a.txt", "A")
            .Symlink("top/link.txt", "a.txt")
            .Symlink("top/up.txt", "../x.txt")
            .File("x.txt", "X")
            .Save(_place.PathOf("collect.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, links: new LinkOptions { Roots = ["top"], Collect = true });

        var link = Assert.Single(result.Links);
        Assert.Equal(new CollectedLink("top/link.txt", "a.txt", ToFolder: false), link);
        Assert.False(Path.Exists(_place.In("top/link.txt")), "ディスクには作らない (開発者モードが要らない)");
        AssertFailed(result, "top/up.txt", Strings.LinkPointsOutsideItems);
    }

    [Fact]
    public void 選んだ項目から作るときはハードリンクを入れない()
    {
        var tar = new TarSample()
            .File("a.txt", "A")
            .HardLink("hard.txt", "a.txt")
            .Save(_place.PathOf("collect.tar"));

        var result = Extract(tar, ArchiveFormat.Tar, links: new LinkOptions { Roots = ["a.txt", "hard.txt"], Collect = true });

        AssertFailed(result, "hard.txt", Strings.LinkHardLinkNotInZip);
        Assert.Empty(result.Links);
    }

    [Fact]
    public void 選んだ項目から作るときはリンクの中を通る名前と組にしない()
    {
        var zip = new ZipSample()
            .File("data/b.txt", "B")
            .Symlink("docs", "data")
            .File("docs/a.txt", "A")
            .Save(_place.PathOf("through.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, links: new LinkOptions { Roots = ["docs", "data"], Collect = true });

        Assert.Contains(result.Failed, f => f.Name == "docs");
        Assert.Empty(result.Links);
    }

    // ------------------------------------------------------------------ リンクを開く

    private static LinkResolution? Open(string archive, string name)
    {
        var contents = ArchiveReader.Open(archive, NameReading.Default, null, CancellationToken.None);
        var entry = Find(contents.Root, name) ?? throw new InvalidOperationException(name);
        return ArchiveLinkResolver.Resolve(contents, entry, null, CancellationToken.None);

        static ArchiveEntry? Find(ArchiveFolder folder, string name)
            => folder.Files.FirstOrDefault(f => f.FullPath == name) ?? folder.Folders.Select(f => Find(f, name)).FirstOrDefault(static e => e is not null);
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("7z")]
    [InlineData("tar")]
    public void リンクを開くと指す先の項目になる(string kind)
    {
        // 開く前の確認 (実行できるファイルか) は、たどり着いた run.exe の名前で出る
        var path = _place.PathOf("open." + kind);
        _ = kind switch
        {
            "zip" => new ZipSample().File("bin/run.exe", "MZ").Symlink("readme.txt", "bin/run.exe").Save(path),
            "7z" => new SevenZipSample().File("bin/run.exe", "MZ").Symlink("readme.txt", "bin/run.exe").Save(path),
            _ => new TarSample().File("bin/run.exe", "MZ").Symlink("readme.txt", "bin/run.exe").Save(path),
        };

        var resolution = Open(path, "readme.txt");

        Assert.NotNull(resolution);
        Assert.Equal("bin/run.exe", resolution.File?.FullPath);
    }

    [Fact]
    public void リンクを開くときに輪になったリンクと外を指すリンクは開かない()
    {
        var zip = new ZipSample()
            .Symlink("a", "b")
            .Symlink("b", "a")
            .Symlink("out", "../x")
            .File("data/x.txt", "X")
            .Symlink("dir", "data")
            .File("plain.txt", "P")
            .Save(_place.PathOf("loop.zip"));

        Assert.Equal(Strings.LinkLoops, Open(zip, "a")?.Reason);
        Assert.Equal(Strings.LinkOpenOutside, Open(zip, "out")?.Reason);
        Assert.Equal("data", Open(zip, "dir")?.Folder?.FullPath);
        Assert.Null(Open(zip, "plain.txt"));
    }

    [Fact]
    public void 同じ名前のファイルが後ろにあればリンクとして開かない()
    {
        var zip = new ZipSample()
            .File("a.txt", "A")
            .Symlink("setup.txt", "a.txt")
            .File("setup.txt", "NEW")
            .Save(_place.PathOf("dupopen.zip"));

        var contents = ArchiveReader.Open(zip, NameReading.Default, null, CancellationToken.None);

        Assert.False(contents.Links.ContainsKey("setup.txt"));
    }

    // ------------------------------------------------------------------ 新しい書庫にリンクを入れる

    [Fact]
    public void ディスクを通さずにリンクを新しい書庫に入れる()
    {
        var file = _place.In("a.txt");
        File.WriteAllText(file, "A");
        var target = _place.PathOf("new.zip");

        var result = ZipArchiveWriter.CreateWith(
            target, [file], string.Empty, replaceExisting: true, null, CancellationToken.None,
            links: [new CarriedLink("link.txt", "a.txt", false, _place.In("link.txt"))]);

        Assert.Empty(result.Failed);
        using var zip = new ICSharpCode.SharpZipLib.Zip.ZipFile(target);
        var entry = zip.GetEntry("link.txt");
        Assert.True(ZipLinks.IsLink(entry));
        using var reader = new StreamReader(zip.GetInputStream(entry));
        Assert.Equal("a.txt", reader.ReadToEnd());
    }

    // ------------------------------------------------------------------ 実装のあとの確認で挙がった見本

    [Fact]
    public void 上書きしなかった本人のファイルを指すリンクは作らない()
    {
        // 本人の notes.txt の上書きに「いいえ」と答えた。readme.txt が本人のファイルを指すリンクにならないこと
        File.WriteAllText(_place.In("notes.txt"), "USER");
        var zip = new ZipSample()
            .File("notes.txt", "ARCHIVE")
            .Symlink("readme.txt", "notes.txt")
            .Save(_place.PathOf("user.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, overwrite: false);

        Assert.Equal("USER", File.ReadAllText(_place.In("notes.txt")));
        AssertFailed(result, "readme.txt", Strings.LinkTargetNotExtracted);
    }

    [Theory]
    [InlineData("zip")]
    [InlineData("zippass")]
    [InlineData("7z")]
    public void リンクの属性が付いたフォルダーの項目で前のファイルを消さない(string kind)
    {
        var path = _place.PathOf("folderattr." + (kind == "7z" ? "7z" : "zip"));
        _ = kind switch
        {
            "7z" => new SevenZipSample().File("x", "X").Add("x", [], 0x10 | SevenZipSample.UnixSymlink, folder: true).Save(path),
            _ => new ZipSample().File("x", "X").Add("x/", [], 3, unchecked((int)0xA1FF0010)).Save(path, kind == "zippass" ? "pass" : null),
        };

        var result = Extract(path, kind == "7z" ? ArchiveFormat.SevenZip : ArchiveFormat.Zip, password: kind == "zippass" ? "pass" : null);

        // フォルダーの項目はリンクとして扱わないので、前のファイルはそのまま展開する
        Assert.Empty(result.Failed);
        Assert.Equal("X", File.ReadAllText(_place.In("x")));
    }

    [Fact]
    public void 選んだ項目から作るときtarの同じ名前の前のファイルを残さない()
    {
        var tar = new TarSample()
            .File("top/b.txt", "B")
            .File("top/a.txt", "OLD")
            .Symlink("top/a.txt", "b.txt")
            .Save(_place.PathOf("dupcollect.tar"));

        var result = Extract(tar, ArchiveFormat.Tar, links: new LinkOptions { Roots = ["top"], Collect = true });

        Assert.Equal(new CollectedLink("top/a.txt", "b.txt", ToFolder: false), Assert.Single(result.Links));
        Assert.False(File.Exists(_place.In("top/a.txt")), "後ろのリンクが残るので、前のファイルは新しい書庫に入れない");
    }

    // ------------------------------------------------------------------ 書き換え

    [Fact]
    public void 書き換えの側もリンクを外部属性だけで見分ける()
    {
        var entry = new ICSharpCode.SharpZipLib.Zip.ZipEntry("link") { HostSystem = 0, ExternalFileAttributes = unchecked((int)0xA1FF0000) };

        Assert.True(ZipLinks.IsLink(entry));
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return i;
            }
        }

        throw new InvalidOperationException("見つかりません");
    }
}
