using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 同じ名前のファイルとフォルダーを一緒に取り出すと、片方が入らない (#427)。
/// 選んだ項目から新しい書庫を作るときとドラッグは、新しい空のフォルダーで「上書きできなかった項目」があれば止める。
/// ここでは、その前提 (書庫の中のぶつかりは、どの形式でも上書きできなかった項目になり、正しい書庫では出ない) を確かめる。
/// </summary>
/// <remarks>ZIP を並べて展開するかを <see cref="SeekPenalty.Override"/> で決めるので、ほかと並べて走らせない。</remarks>
[Collection(nameof(TraceCollection))]
public sealed class SameNameCollisionTests : IDisposable
{
    private readonly LinkPlace _place = new();
    private readonly Func<string, bool?>? _previous = SeekPenalty.Override;

    public void Dispose()
    {
        SeekPenalty.Override = _previous;
        _place.Dispose();
    }

    private static (ArchiveFormat Format, string? Password) Parse(string formatName)
        => formatName == "ZipPassword" ? (ArchiveFormat.Zip, "pass") : (Enum.Parse<ArchiveFormat>(formatName), null);

    private static string Extension(ArchiveFormat format)
        => format == ArchiveFormat.SevenZip ? "7z" : format.ToString().ToLowerInvariant();

    /// <summary>フォルダー <c>a</c>、その下の <c>a/x.txt</c>、同じ名前のファイル <c>a</c>。</summary>
    private string Nested(ArchiveFormat format, string? password, bool folderFirst)
    {
        var path = _place.PathOf("nested." + Extension(format));
        switch (format)
        {
            case ArchiveFormat.Zip:
            {
                var zip = new ZipSample();
                return (folderFirst
                    ? zip.Folder("a").File("a/x.txt", "X").File("a", "FILE")
                    : zip.File("a", "FILE").Folder("a").File("a/x.txt", "X")).Save(path, password);
            }
            case ArchiveFormat.SevenZip:
            {
                var sample = new SevenZipSample();
                return (folderFirst
                    ? sample.Folder("a").File("a/x.txt", "X").File("a", "FILE")
                    : sample.File("a", "FILE").Folder("a").File("a/x.txt", "X")).Save(path);
            }
            case ArchiveFormat.Tar:
            {
                var tar = new TarSample();
                return (folderFirst
                    ? tar.BareFolder("a").File("a/x.txt", "X").File("a", "FILE")
                    : tar.File("a", "FILE").BareFolder("a").File("a/x.txt", "X")).Save(path);
            }
            default:
            {
                var rar = new RarSample();
                return (folderFirst
                    ? rar.File("a", directory: true).File("a/x.txt", "X").File("a", "FILE")
                    : rar.File("a", "FILE").File("a", directory: true).File("a/x.txt", "X")).Save(path);
            }
        }
    }

    private static ArchiveFolder Root(string archive)
        => ArchiveReader.Open(archive, NameReading.Default, null, CancellationToken.None).Root;

    /// <summary>画面と同じ選び方。一覧でフォルダー <c>a</c> とファイル <c>a</c> の両方、または検索の結果で <c>a</c> と <c>a/x.txt</c>。</summary>
    private static (EntrySelection Selection, List<string> Roots) Pick(ArchiveFolder root, bool search)
    {
        var file = root.Files.Single(static f => f.Name == "a");
        var folder = root.Folders.Single(static f => f.Name == "a");
        return search
            ? (EntrySelection.From([file, folder.Files.Single()], []), ["a", "a/x.txt"])
            : (EntrySelection.From([file], [folder]), ["a", "a"]);
    }

    private ExtractResult Take(string archive, ArchiveFormat format, EntrySelection? selection, string? password,
        LinkOptions links, string? into = null)
        => ArchiveExtractor.Extract(archive, format, NameReading.Default, selection, into ?? _place.Output,
            overwrite: false, progress: null, cancellationToken: CancellationToken.None, password: password, links: links);

    // ------------------------------------------------------------------ ぶつかる書庫

    public static TheoryData<string, bool, bool, bool, bool> Combinations()
    {
        var data = new TheoryData<string, bool, bool, bool, bool>();
        bool[] both = [true, false];
        foreach (var format in new[] { "SevenZip", "Rar", "Tar", "Zip", "ZipPassword" })
        {
            foreach (var folderFirst in both)
            {
                foreach (var search in both)
                {
                    foreach (var drag in both)
                    {
                        foreach (var parallel in both)
                        {
                            data.Add(format, folderFirst, search, drag, parallel);
                        }
                    }
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Combinations))]
    public void 同じ名前のファイルとフォルダーは上書きできなかった項目になる(
        string formatName, bool folderFirst, bool search, bool drag, bool parallel)
    {
        // 並べて展開するかは ZIP でしか変わらない。並べても 1 本でも、同じに扱えること
        SeekPenalty.Override = _ => !parallel;
        var (format, password) = Parse(formatName);
        var archive = Nested(format, password, folderFirst);
        var (selection, roots) = Pick(Root(archive), search);
        var links = drag
            ? new LinkOptions { Roots = roots, JunctionsNotAllowed = true }
            : new LinkOptions { Roots = roots, Collect = true };

        var result = Take(archive, format, selection, password, links);

        Assert.Empty(result.Rejected);
        Assert.Empty(result.Failed);
        // フォルダーが先ならファイル a、ファイルが先なら a/x.txt が入らない
        var expected = folderFirst ? "a" : "a/x.txt";
        Assert.Contains(result.NotReplaced, n => ArchiveTreeBuilder.Trim(n.Name) == expected);

        // 知らせには種類を添える。入らないのはどちらの並びでもファイル
        Assert.Contains(result.NotReplacedItems(), n => ArchiveTreeBuilder.Trim(n.Name) == expected && n.Kind == ItemKind.File);
        Assert.DoesNotContain(result.NotReplacedItems(), static n => n.Kind != ItemKind.File);
    }

    // ------------------------------------------------------------------ 上書きできなかった項目の種類

    [Fact]
    public void フォルダーの項目を作れなかったときはフォルダーと知らせる()
    {
        // 書庫の中のぶつかりでは、同じ名前のファイルがあるフォルダーの項目は作らない (#397) ので、ここには来ない。
        // 使い回すフォルダーに同じ名前のファイルが残っているときなどに来る
        File.WriteAllText(_place.In("e"), "LEFT");
        Directory.CreateDirectory(_place.In("g"));
        var state = new ExtractState(_place.Output, false, null, null, null);
        state.Write("g", 1, null, static () => new MemoryStream("G"u8.ToArray()), CancellationToken.None);
        state.CreateFolder("e");

        Assert.Equal([("g", ItemKind.File), ("e", ItemKind.Folder)], state.ToResult().NotReplacedItems());
    }

    [Fact]
    public void リンクを置けなかったときはリンクと知らせる()
    {
        var archive = new TarSample().File("a", "OLD").Folder("d").File("d/y", "Y").Symlink("a", "d")
            .Save(_place.PathOf("relinked.tar"));
        var root = Root(archive);
        var roots = root.Files.Select(static f => f.Name).Concat(root.Folders.Select(static f => f.Name)).ToList();

        var result = Take(archive, ArchiveFormat.Tar, EntrySelection.From(root.Files, root.Folders), null,
            new LinkOptions { Roots = roots, JunctionsNotAllowed = true });

        Assert.Equal([("a", ItemKind.Link)], result.NotReplacedItems());
    }

    [Fact]
    public void 種類は結果を合わせても同じ順に並ぶ()
    {
        // ファイルを書く所 (ZIP の並べる取り出し) は種類を記録しない。足りない分はファイル
        var files = new ExtractResult(0, 0, [], [], false) { NotReplaced = [("a", "r")] };
        var folders = new ExtractResult(0, 0, [], [], false) { NotReplaced = [("a", "r")], NotReplacedKinds = [ItemKind.Folder] };

        Assert.Equal([("a", ItemKind.File), ("a", ItemKind.Folder)], files.With(folders).NotReplacedItems());
    }

    // ------------------------------------------------------------------ 取り出さなかったリンクを除いた失敗

    [Fact]
    public void 取り出さなかったリンクはその数だけ除く()
    {
        // 同じ名前と理由で、ふつうのファイルとリンクが 1 つずつ失敗した。まとめて除くと、ファイルの失敗まで消える
        var result = new ExtractResult(0, 0, [], [("a", "r"), ("a", "r"), ("b", "s")], false)
        {
            LinkFailures = [("a", "r")],
        };

        Assert.Equal([("a", "r"), ("b", "s")], result.FailedExceptLinks());
    }

    /// <summary>ZIP の中の、格納した項目の中身を 1 バイト変える。CRC が合わなくなる。</summary>
    private static void Break(string zip, string content)
    {
        var bytes = File.ReadAllBytes(zip);
        var mark = System.Text.Encoding.UTF8.GetBytes(content);
        var at = bytes.AsSpan().IndexOf(mark);
        Assert.True(at >= 0, "中身が格納されていない");
        bytes[at] ^= 0xFF;
        File.WriteAllBytes(zip, bytes);
    }

    [Fact]
    public void 壊れた項目は取り出さなかったリンクと分けて失敗に残る()
    {
        var zip = new ZipSample().Folder("p").File("p/bad.txt", "BROKEN-CONTENT-0123456789").File("p/ok.txt", "OK")
            .Symlink("p/l", "../../outside").Save(_place.PathOf("broken.zip"));
        Break(zip, "BROKEN-CONTENT-0123456789");
        var root = Root(zip);

        var result = Take(zip, ArchiveFormat.Zip, EntrySelection.From([], root.Folders), null,
            new LinkOptions { Roots = ["p"], JunctionsNotAllowed = true });

        Assert.Contains(result.LinkFailures, static f => f.Name == "p/l");
        Assert.Equal(["p/bad.txt"], result.FailedExceptLinks().Select(static f => f.Name));
    }

    [Fact]
    public void 取り出さなかったリンクだけならリンクを除いた失敗は無い()
    {
        // ほかの項目は渡し、落としたあとにリンクの知らせを出す (#367) 動きを変えない
        var zip = new ZipSample().Folder("p").File("p/ok.txt", "OK").Symlink("p/l", "../../outside")
            .Save(_place.PathOf("links.zip"));
        var root = Root(zip);

        var result = Take(zip, ArchiveFormat.Zip, EntrySelection.From([], root.Folders), null,
            new LinkOptions { Roots = ["p"], JunctionsNotAllowed = true });

        Assert.NotEmpty(result.LinkFailures);
        Assert.Empty(result.FailedExceptLinks());
        Assert.Empty(result.Rejected);
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.Zip))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    public void 安全でない名前の項目は拒否した項目に入る(string formatName)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var archive = format == ArchiveFormat.Zip
            ? new ZipSample().File("ok.txt", "OK").File("../evil.txt", "E").Save(_place.PathOf("evil.zip"))
            : new TarSample().File("ok.txt", "OK").File("../evil.txt", "E").Save(_place.PathOf("evil.tar"));

        var result = Take(archive, format, null, null, new LinkOptions { Roots = [], JunctionsNotAllowed = true });

        Assert.NotEmpty(result.Rejected);
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.Zip), true)]
    [InlineData(nameof(ArchiveFormat.Tar), false)]
    [InlineData(nameof(ArchiveFormat.SevenZip), false)]
    public void 名前が安全でないリンクで止まるかは形式で違う(string formatName, bool rejected)
    {
        // ZIP は名前を確かめる所で拒否し (ドラッグは止まる)、7z と tar は取り出さなかったリンクになる (止まらず、落としたあとに知らせる)。
        // どちらも安全な側。そろえるかは決めていない (#427 の設計の確認の指摘 4)
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var path = _place.PathOf("evil-link." + Extension(format));
        var archive = format switch
        {
            ArchiveFormat.Zip => new ZipSample().File("ok.txt", "OK").Symlink("../l", "ok.txt").Save(path),
            ArchiveFormat.Tar => new TarSample().File("ok.txt", "OK").Symlink("../l", "ok.txt").Save(path),
            _ => new SevenZipSample().File("ok.txt", "OK").Symlink("../l", "ok.txt").Save(path),
        };

        var result = Take(archive, format, null, null, new LinkOptions { Roots = [], JunctionsNotAllowed = true });

        Assert.Equal(rejected, result.Rejected.Count > 0);
        if (!rejected)
        {
            Assert.NotEmpty(result.LinkFailures);
            Assert.Empty(result.FailedExceptLinks());
        }
    }

    [Theory]
    [InlineData(new[] { "a", "a/x.txt" }, true)]
    [InlineData(new[] { "a/x.txt", "a" }, true)]
    [InlineData(new[] { "p/a", "p/a/b/c.txt" }, true)]
    [InlineData(new[] { "Readme", "readme/x" }, true)]
    [InlineData(new[] { "a.", "a/x.txt" }, true)]
    [InlineData(new[] { "a ", "a/x.txt" }, true)]
    [InlineData(new[] { "a", "a /x.txt" }, true)]
    [InlineData(new[] { "a", "a./x.txt" }, true)]
    [InlineData(new[] { "p/a", "p./a/x.txt" }, true)]
    [InlineData(new[] { "a/x", "b/y" }, false)]
    [InlineData(new[] { "a/x", "a/y", "ab" }, false)]
    public void ファイルの書き出す先が別の項目のフォルダーと同じなら並べない(string[] names, bool collides)
    {
        SeekPenalty.Override = static _ => false;
        var zip = new ZipSample();
        foreach (var name in names)
        {
            zip.File(name, name);
        }

        var archive = zip.Save(_place.PathOf("lanes.zip"));

        // 書き出す先は展開と同じ決め方 (末尾のピリオドや空白は落ちる)
        var targets = names.Select(n => ArchiveExtractor.TargetFor(_place.Output, n, null)!).ToList();

        Assert.Equal(collides, ArchiveExtractor.FileInPlaceOfFolder(_place.Output, targets));
        if (collides)
        {
            Assert.Equal(1, ArchiveExtractor.CountLanes(archive, _place.Output, targets));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void tarでファイルの後ろに同じ名前のリンクがあるとドラッグでは上書きできなかった項目になる(bool drag)
    {
        // 追記で、ファイル a がフォルダーを指すリンク a に変わった書庫
        var archive = new TarSample().File("a", "OLD").Folder("d").File("d/y", "Y").Symlink("a", "d")
            .Save(_place.PathOf("relinked.tar"));
        var root = Root(archive);
        var roots = root.Files.Select(static f => f.Name).Concat(root.Folders.Select(static f => f.Name)).ToList();
        var selection = EntrySelection.From(root.Files, root.Folders);

        var result = Take(archive, ArchiveFormat.Tar, selection, null, drag
            ? new LinkOptions { Roots = roots, JunctionsNotAllowed = true }
            : new LinkOptions { Roots = roots, Collect = true });

        if (drag)
        {
            // 前に入れたファイル a を、リンクの代わりに黙って渡さない
            Assert.Contains(result.NotReplaced, static n => n.Name == "a");
        }
        else
        {
            // 新しい書庫では、前のファイルを消してリンクを受け取る。止めない
            Assert.Empty(result.NotReplaced);
            Assert.Contains(result.Links, static l => l.Name == "a");
        }
    }

    // ------------------------------------------------------------------ 正しい書庫では止めない

    public static TheoryData<string> Correct => new()
    {
        "7z 同じ名前のファイル 2 個",
        "RAR 同じ名前のファイル 2 個",
        "tar 同じ名前のファイル 2 個",
        "ZIP 大文字小文字だけ違うファイル",
        "ZIP 長い名前と短い名前",
        "ZIP 末尾のピリオド",
        "7z フォルダーの項目が 2 回",
        "RAR フォルダーの項目が 2 回",
        "7z フォルダーの項目が中身より後ろ",
        "ZIP フォルダーの項目の大文字小文字違い",
        "tar の ./ と ./f/",
        "tar のフォルダーを指すリンク",
        "tar のファイルを指すリンク",
    };

    private (string Archive, ArchiveFormat Format) Sample(string name)
    {
        string P(string file) => _place.PathOf(file);
        return name switch
        {
            "7z 同じ名前のファイル 2 個" => (new SevenZipSample().File("d", "1").File("d", "2").Save(P("d.7z")), ArchiveFormat.SevenZip),
            "RAR 同じ名前のファイル 2 個" => (new RarSample().File("d", "1").File("d", "2").Save(P("d.rar")), ArchiveFormat.Rar),
            "tar 同じ名前のファイル 2 個" => (new TarSample().File("d", "1").File("d", "2").Save(P("d.tar")), ArchiveFormat.Tar),
            "ZIP 大文字小文字だけ違うファイル" => (new ZipSample().File("D", "1").File("d", "2").Save(P("d.zip")), ArchiveFormat.Zip),
            "ZIP 長い名前と短い名前" => (new ZipSample().File("longfilename.txt", "1").File("LONGFI~1.TXT", "2").Save(P("l.zip")), ArchiveFormat.Zip),
            "ZIP 末尾のピリオド" => (new ZipSample().File("e.", "1").File("e", "2").Save(P("e.zip")), ArchiveFormat.Zip),
            "7z フォルダーの項目が 2 回" => (new SevenZipSample().Folder("f").Folder("f").File("f/y", "Y").Save(P("f.7z")), ArchiveFormat.SevenZip),
            "RAR フォルダーの項目が 2 回" => (new RarSample().File("f", directory: true).File("f", directory: true).File("f/y", "Y").Save(P("f.rar")), ArchiveFormat.Rar),
            "7z フォルダーの項目が中身より後ろ" => (new SevenZipSample().File("f/y", "Y").Folder("f").Save(P("g.7z")), ArchiveFormat.SevenZip),
            "ZIP フォルダーの項目の大文字小文字違い" => (new ZipSample().Folder("F").File("f/y", "Y").Folder("f").Save(P("h.zip")), ArchiveFormat.Zip),
            "tar の ./ と ./f/" => (new TarSample().Folder("./").Folder("./f").File("./f/y", "Y").Save(P("t.tar")), ArchiveFormat.Tar),
            "tar のフォルダーを指すリンク" => (new TarSample().Folder("p").Folder("p/d").File("p/d/x", "X").Symlink("p/l", "d").Save(P("l.tar")), ArchiveFormat.Tar),
            "tar のファイルを指すリンク" => (new TarSample().Folder("p").File("p/x", "X").Symlink("p/l", "x").Save(P("m.tar")), ArchiveFormat.Tar),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    [Theory]
    [MemberData(nameof(Correct))]
    public void 正しい書庫では上書きできなかった項目は出ない(string name)
    {
        foreach (var parallel in new[] { true, false })
        {
            foreach (var drag in new[] { true, false })
            {
                SeekPenalty.Override = _ => !parallel;
                var (archive, format) = Sample(name);
                var root = Root(archive);
                var roots = root.Files.Select(static f => f.Name).Concat(root.Folders.Select(static f => f.Name)).ToList();
                var into = _place.PathOf($"out-{parallel}-{drag}");
                Directory.CreateDirectory(into);

                var result = Take(archive, format, EntrySelection.From(root.Files, root.Folders), null, drag
                    ? new LinkOptions { Roots = roots, JunctionsNotAllowed = true }
                    : new LinkOptions { Roots = roots, Collect = true }, into);

                Assert.Empty(result.NotReplaced);
                Assert.Empty(result.Rejected);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Correct))]
    public void 正しい書庫を同じフォルダーに2回取り出しても上書きできなかった項目は出ない(string name)
    {
        // ドラッグの書庫ごとのフォルダーを使い回すときと同じ
        var (archive, format) = Sample(name);
        var root = Root(archive);
        var roots = root.Files.Select(static f => f.Name).Concat(root.Folders.Select(static f => f.Name)).ToList();
        var selection = EntrySelection.From(root.Files, root.Folders);
        var links = new LinkOptions { Roots = roots, JunctionsNotAllowed = true };

        Take(archive, format, selection, null, links);
        if (EntrySelection.LeftoverInTheWay(root, selection, [], _place.Output))
        {
            // 逆の種類が残っていれば、画面は新しいフォルダーに取り出す
            return;
        }

        var second = Take(archive, format, selection, null, links);

        Assert.Empty(second.NotReplaced);
    }

    [Fact]
    public void ぶつかる書庫の残りがあると使い回さない()
    {
        // 前のドラッグで、フォルダーが先の書庫を一覧で両方選んだ残り (フォルダー a と a/x.txt)
        var archive = Nested(ArchiveFormat.SevenZip, null, folderFirst: true);
        var root = Root(archive);
        Directory.CreateDirectory(_place.In("a"));
        File.WriteAllText(_place.In("a/x.txt"), "X");

        var file = root.Files.Single(static f => f.Name == "a");
        Assert.True(EntrySelection.LeftoverInTheWay(root, EntrySelection.From([file], []), [], _place.Output));
    }

    // ------------------------------------------------------------------ ドラッグで渡すパス

    [Fact]
    public void ファイルの行にはふつうのフォルダーを渡さない()
    {
        Directory.CreateDirectory(_place.In("a"));
        File.WriteAllText(_place.In("f"), "F");

        Assert.False(EntrySelection.DragPathReady(_place.In("a"), isFolder: false));
        Assert.True(EntrySelection.DragPathReady(_place.In("f"), isFolder: false));
        Assert.False(EntrySelection.DragPathReady(_place.In("none"), isFolder: false));
    }

    [Fact]
    public void ファイルの行にはフォルダーを指すリンクを渡す()
    {
        // リンクの行は、フォルダーを指すシンボリックリンクとして取り出されることがある。リパースポイントの付いたフォルダーは通す
        Directory.CreateDirectory(_place.In("d"));
        Assert.SkipUnless(Junction.TryCreate(_place.In("l"), _place.In("d")), "ジャンクションを作れない");

        Assert.True(EntrySelection.DragPathReady(_place.In("l"), isFolder: false));
    }

    [Fact]
    public void フォルダーの行にはファイルを渡さず無ければ空のフォルダーを作る()
    {
        File.WriteAllText(_place.In("f"), "F");

        Assert.False(EntrySelection.DragPathReady(_place.In("f"), isFolder: true));
        Assert.True(EntrySelection.DragPathReady(_place.In("e"), isFolder: true));
        Assert.True(Directory.Exists(_place.In("e")));
    }

    [Fact]
    public void 取り出せなかったリンクの行にもふつうのフォルダーを渡さない()
    {
        // a/x.txt を取り出すとふつうのフォルダー a ができる。指す先が書庫の外のリンク a は取り出さず (失敗)、上書きできなかった項目にもならないので、
        // ドラッグは止まらない。それでもリンクの行にフォルダー a を渡さない (#427 の設計の確認)
        var archive = new TarSample().File("a/x.txt", "X").Symlink("a", "../outside").Save(_place.PathOf("hole.tar"));
        var root = Root(archive);

        var result = Take(archive, ArchiveFormat.Tar, EntrySelection.From(root.Files, root.Folders), null,
            new LinkOptions { Roots = ["a", "a"], JunctionsNotAllowed = true });

        Assert.Empty(result.NotReplaced);
        Assert.Contains(result.Failed, static f => f.Name == "a");
        Assert.True(Directory.Exists(_place.In("a")));
        Assert.False(EntrySelection.DragPathReady(_place.In("a"), isFolder: false));
    }
}
