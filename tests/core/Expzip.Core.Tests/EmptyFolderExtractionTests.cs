using System.Text;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Tests;

/// <summary>
/// 展開で、書庫に入っている中身の無いフォルダーも作る (#397)。攻撃の見本は、設計の確認 (#397 のノート) で挙がったもの。
/// </summary>
public sealed class EmptyFolderExtractionTests : IDisposable
{
    private readonly LinkPlace _place = new();

    public void Dispose() => _place.Dispose();

    private static string NoPrivilege => Strings.Reason(new LinkPrivilegeException());

    private ExtractResult Extract(
        string archive, ArchiveFormat format, EntrySelection? names = null, string? password = null,
        string? basePath = null, CancellationToken cancellationToken = default, LinkOptions? links = null, string? output = null)
        => ArchiveExtractor.Extract(archive, format, NameReading.Default, names, output ?? _place.Output,
            ExtractOverwrite.Only([]), null, cancellationToken, basePath: basePath, password: password, links: links);

    private static void AssertClean(ExtractResult result)
    {
        Assert.Empty(result.Rejected);
        Assert.Empty(result.Failed);
        Assert.Empty(result.NotReplaced);
        Assert.Empty(result.SkippedNames);
    }

    private bool IsPlainFolder(string relative)
    {
        var path = _place.In(relative);
        return Directory.Exists(path) && !new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);
    }

    /// <summary>形式ごとに、ファイル 1 個と中身の無いフォルダー (奥の段も) を入れた書庫。</summary>
    private string Sample(ArchiveFormat format, string name, string? password = null)
    {
        var path = _place.PathOf(name);
        return format switch
        {
            ArchiveFormat.Zip => new ZipSample().File("f.txt", "F").Folder("empty").Folder("a/b/c").Save(path, password),
            ArchiveFormat.SevenZip => new SevenZipSample().File("f.txt", "F").Folder("empty").Folder("a/b/c").Save(path),
            ArchiveFormat.Tar => new TarSample().File("f.txt", "F").Folder("empty").Folder("a/b/c").Save(path),
            _ => new RarSample().File("f.txt", "F").File("empty", directory: true).File("a/b/c", directory: true).Save(path),
        };
    }

    // ------------------------------------------------------------------ 作る

    [Theory]
    [InlineData(nameof(ArchiveFormat.Zip), null)]
    [InlineData(nameof(ArchiveFormat.Zip), "pass")]
    [InlineData(nameof(ArchiveFormat.SevenZip), null)]
    [InlineData(nameof(ArchiveFormat.Tar), null)]
    [InlineData(nameof(ArchiveFormat.Rar), null)]
    public void リンクの無い書庫でも中身の無いフォルダーを作る(string formatName, string? password)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var archive = Sample(format, "plain." + format, password);

        var result = Extract(archive, format, password: password);

        AssertClean(result);
        Assert.Equal(1, result.Extracted);
        Assert.Equal("F", File.ReadAllText(_place.In("f.txt")));
        Assert.True(IsPlainFolder("empty"));
        Assert.True(IsPlainFolder("a/b/c"));
        Assert.Equal(2, result.CreatedFolders);
    }

    [Fact]
    public void 並べて展開するZIPでも中身の無いフォルダーを作る()
    {
        var previous = SeekPenalty.Override;
        SeekPenalty.Override = static _ => false;
        try
        {
            var sample = new ZipSample();
            var targets = new List<string>();
            for (var i = 0; i < 40; i++)
            {
                sample.File($"data/f{i}.txt", "F" + i);
                targets.Add(_place.In($"data/f{i}.txt"));
            }

            var zip = sample.Folder("empty").Folder("data/inner").Save(_place.PathOf("lanes.zip"));
            Assert.SkipUnless(ArchiveExtractor.CountLanes(zip, _place.Output, targets) > 1, "この PC では並べて展開しない");

            var result = Extract(zip, ArchiveFormat.Zip);

            AssertClean(result);
            Assert.Equal(40, result.Extracted);
            Assert.True(IsPlainFolder("empty"));
            Assert.True(IsPlainFolder("data/inner"));
        }
        finally
        {
            SeekPenalty.Override = previous;
        }
    }

    [Fact]
    public void 正しい名前を拒まない()
    {
        // 深い段だけが中身の無いもの、濁点を分けて書いた名前、末尾が \ の ZIP のフォルダーの項目
        var decomposed = "か\u3099".Normalize(NormalizationForm.FormD);
        var zip = new ZipSample()
            .Folder("x/y/z/deep")
            .Folder(decomposed)
            .Add("back\\", [], 0, 0x10)
            .Save(_place.PathOf("names.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        AssertClean(result);
        Assert.True(IsPlainFolder("x/y/z/deep"));
        Assert.True(Directory.Exists(_place.In(decomposed)));
        Assert.True(IsPlainFolder("back"));
    }

    [Fact]
    public void 同じ名前のフォルダーの項目が2つあっても作るのは1回()
    {
        var zip = new ZipSample().Folder("dup").Folder("dup").Folder("DUP").Save(_place.PathOf("dup.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        AssertClean(result);
        Assert.True(IsPlainFolder("dup"));
        Assert.Equal(1, result.CreatedFolders);
    }

    // ------------------------------------------------------------------ 一部を選んだとき

    [Fact]
    public void 選んだフォルダーの中の中身の無いフォルダーだけを作る()
    {
        // 「docs」を選び、その親 (書庫のルート) までを取り除く。docs の中の empty は作り、other は作らない
        var zip = new ZipSample()
            .File("docs/a.txt", "A")
            .Folder("docs")
            .Folder("docs/empty")
            .Folder("other")
            .Save(_place.PathOf("partial.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, names: new EntrySelection(["docs/a.txt"], ["docs/", "docs/empty/"]));

        AssertClean(result);
        Assert.True(IsPlainFolder("docs/empty"));
        Assert.False(Path.Exists(_place.In("other")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.Zip))]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    public void 先頭を取り除いた位置に作る(string formatName)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var path = _place.PathOf("base." + format switch
        {
            ArchiveFormat.SevenZip => "7z",
            _ => format.ToString().ToLowerInvariant(),
        });
        var archive = format switch
        {
            ArchiveFormat.Zip => new ZipSample().Folder("top/sub/empty").Save(path),
            ArchiveFormat.SevenZip => new SevenZipSample().Folder("top/sub/empty").Save(path),
            ArchiveFormat.Tar => new TarSample().Folder("top/sub/empty").Save(path),
            _ => new RarSample().File("top/sub/empty", directory: true).Save(path),
        };
        // 画面と同じく、一覧のフォルダーが持つ元の名前で選ぶ
        var contents = ArchiveReader.Open(archive, NameReading.Default, null, CancellationToken.None);
        var empty = contents.Root.Folders.Single().Folders.Single().Folders.Single();
        Assert.NotNull(empty.SourceName);

        var result = Extract(archive, format, names: new EntrySelection([], [empty.SourceName!]), basePath: "top");

        AssertClean(result);
        Assert.True(IsPlainFolder("sub/empty"));
        Assert.False(Path.Exists(_place.In("top")));
    }

    // ------------------------------------------------------------------ 展開先の外、展開先そのもの

    [Fact]
    public void 展開先の外を指すフォルダーの項目は作らず拒否した項目に入れる()
    {
        var zip = new ZipSample()
            .Folder("../escaped")
            .Folder("a/../../escaped2")
            .Save(_place.PathOf("outside.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        Assert.Equal(2, result.Rejected.Count);
        Assert.False(Directory.Exists(_place.PathOf("escaped")));
        Assert.False(Directory.Exists(_place.PathOf("escaped2")));
        Assert.Equal(0, result.CreatedFolders);
    }

    [Fact]
    public void 七zの外を指すフォルダーの項目も作らない()
    {
        var archive = new SevenZipSample().Folder("../escaped").Save(_place.PathOf("outside.7z"));

        var result = Extract(archive, ArchiveFormat.SevenZip);

        Assert.Single(result.Rejected);
        Assert.False(Directory.Exists(_place.PathOf("escaped")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void tarの先頭の点のフォルダーは知らせずに飛ばす(bool partial)
    {
        // tar -C dir -cf x.tar . で作ると、先頭に ./ の項目が入る
        var tar = new TarSample()
            .Folder(".")
            .Folder("./empty")
            .File("./f.txt", "F")
            .Save(_place.PathOf("dot.tar"));

        var result = Extract(tar, ArchiveFormat.Tar,
            names: partial ? new EntrySelection(["./f.txt"], ["./", "./empty/"]) : null);

        AssertClean(result);
        Assert.True(IsPlainFolder("empty"));
        Assert.Equal("F", File.ReadAllText(_place.In("f.txt")));
    }

    [Fact]
    public void 外を指すリンクの下のフォルダーの項目は展開先の中のふつうのフォルダーになる()
    {
        // link が外を指すリンクで、link/sub がフォルダーの項目。フォルダーはリンクより先に作るので、リンクを通らない
        var tar = new TarSample()
            .Symlink("link", "../../outside")
            .Folder("link/sub")
            .Save(_place.PathOf("under.tar"));

        var result = Extract(tar, ArchiveFormat.Tar);

        Assert.True(IsPlainFolder("link"));
        Assert.True(IsPlainFolder("link/sub"));
        Assert.False(Directory.Exists(_place.PathOf("outside")));
        Assert.True(result.Failed.Any(f => f.Name == "link") || result.NotReplaced.Any(f => f.Name == "link"), "リンクは置かない");
    }

    // ------------------------------------------------------------------ 展開先に同じ名前のもの

    [Fact]
    public void 展開先に同じ名前のファイルがあれば残して上書きできなかった項目に入れる()
    {
        File.WriteAllText(_place.In("empty"), "USER");
        var time = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Local);
        File.SetLastWriteTime(_place.In("empty"), time);
        File.WriteAllText(_place.In("x"), "USER2");
        var archive = new SevenZipSample().Folder("empty").Folder("x/y").Save(_place.PathOf("file.7z"));

        var conflicts = ArchiveExtractor.FindConflicts([], _place.Output, null, ["empty", "x/y"]);
        var result = Extract(archive, ArchiveFormat.SevenZip);

        Assert.Contains(conflicts, c => c.Name == "empty" && c.Block == ExtractBlock.FileInPlaceOfFolder);
        Assert.Contains(conflicts, c => c.Name == "x/y" && c.Block == ExtractBlock.FileInTheWay);
        Assert.Contains(result.NotReplaced, n => n.Name == "empty" && n.Reason == Strings.ExtractBlocked(ExtractBlock.FileInPlaceOfFolder));
        Assert.Contains(result.NotReplaced, n => n.Name == "x/y" && n.Reason == Strings.ExtractBlocked(ExtractBlock.FileInTheWay));
        Assert.Equal("USER", File.ReadAllText(_place.In("empty")));
        Assert.Equal(time, File.GetLastWriteTime(_place.In("empty")));
        Assert.Equal("USER2", File.ReadAllText(_place.In("x")));
        Assert.Empty(result.Failed);
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    [InlineData(nameof(ArchiveFormat.Zip))]
    public void 同じ書庫をもう一度展開してもフォルダーは確認にも結果にも挙がらない(string formatName)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var archive = Sample(format, "twice." + format);
        var folders = format == ArchiveFormat.Zip ? new[] { "empty/", "a/b/c/" } : ["empty", "a/b/c"];

        Extract(archive, format);
        var conflicts = ArchiveExtractor.FindConflicts([], _place.Output, null, folders);
        var second = Extract(archive, format);

        Assert.Empty(conflicts);
        Assert.Empty(second.NotReplaced);
        Assert.Empty(second.Failed);
        Assert.Equal(0, second.CreatedFolders);
    }

    [Fact]
    public void 展開先に前からあるフォルダーやジャンクションは中身をそのまま残す()
    {
        Directory.CreateDirectory(_place.In("docs"));
        File.WriteAllText(_place.In("docs/user.txt"), "USER");
        var elsewhere = Directory.CreateDirectory(_place.PathOf("elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "user.txt"), "USER2");
        Assert.SkipUnless(Junction.TryCreate(_place.In("j"), elsewhere), "ジャンクションを作れない");
        var zip = new ZipSample().Folder("docs").Folder("j").Save(_place.PathOf("existing.zip"));

        var result = Extract(zip, ArchiveFormat.Zip);

        AssertClean(result);
        Assert.Equal("USER", File.ReadAllText(_place.In("docs/user.txt")));
        Assert.Equal("USER2", File.ReadAllText(Path.Combine(elsewhere, "user.txt")));
        Assert.True(new DirectoryInfo(_place.In("j")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal(0, result.CreatedFolders);
    }

    [Fact]
    public void 前からあるジャンクションの下のフォルダーはその先に作りそこを指すリンクは作らない()
    {
        var elsewhere = Directory.CreateDirectory(_place.PathOf("elsewhere")).FullName;
        Assert.SkipUnless(Junction.TryCreate(_place.In("j"), elsewhere), "ジャンクションを作れない");
        var archive = new RarSample()
            .File("j/new", directory: true)
            .File("alias", redirect: (3, "j/new", true))
            .Save(_place.PathOf("through.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        Assert.True(Directory.Exists(Path.Combine(elsewhere, "new")));
        Assert.Contains(result.Failed, f => f.Name == "alias" && f.Reason == Strings.LinkTargetThroughLink);
        Assert.False(Path.Exists(_place.In("alias")));
    }

    // ------------------------------------------------------------------ 書庫の中に同じ名前のもの

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 同じ名前のファイルがあればフォルダーは作らずファイルを展開する(bool folderFirst)
    {
        var sample = new ZipSample();
        if (folderFirst)
        {
            sample.Folder("A").File("a", "FILE");
        }
        else
        {
            sample.File("a", "FILE").Folder("A");
        }

        var result = Extract(sample.Save(_place.PathOf("same.zip")), ArchiveFormat.Zip);

        AssertClean(result);
        Assert.Equal("FILE", File.ReadAllText(_place.In("a")));
        Assert.Equal(0, result.CreatedFolders);
    }

    [Fact]
    public void 同じ名前のファイルを選んでいなくてもフォルダーは作らない()
    {
        // 書庫の全体で決める。選び方によって作られたり作られなかったりしない
        var zip = new ZipSample().File("a", "FILE").Folder("a").Save(_place.PathOf("same.zip"));

        var result = Extract(zip, ArchiveFormat.Zip, names: new EntrySelection([], ["a/"]));

        AssertClean(result);
        Assert.False(Path.Exists(_place.In("a")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.Zip), true)]
    [InlineData(nameof(ArchiveFormat.Zip), false)]
    [InlineData(nameof(ArchiveFormat.SevenZip), true)]
    [InlineData(nameof(ArchiveFormat.SevenZip), false)]
    [InlineData(nameof(ArchiveFormat.Tar), true)]
    [InlineData(nameof(ArchiveFormat.Tar), false)]
    public void 同じ名前のリンクとフォルダーの項目があればどちらの順でもリンクを展開する(string formatName, bool linkFirst)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var path = _place.PathOf("linkfolder." + format);
        string archive;
        switch (format)
        {
            case ArchiveFormat.Zip:
                var zip = new ZipSample().File("x.txt", "X");
                archive = (linkFirst ? zip.Symlink("a", "x.txt").Folder("a") : zip.Folder("a").Symlink("a", "x.txt")).Save(path);
                break;
            case ArchiveFormat.SevenZip:
                var seven = new SevenZipSample().File("x.txt", "X");
                archive = (linkFirst ? seven.Symlink("a", "x.txt").Folder("a") : seven.Folder("a").Symlink("a", "x.txt")).Save(path);
                break;
            default:
                var tar = new TarSample().File("x.txt", "X");
                archive = (linkFirst ? tar.Symlink("a", "x.txt").Folder("a") : tar.Folder("a").Symlink("a", "x.txt")).Save(path);
                break;
        }

        var result = Extract(archive, format);

        // 開発者モードがオフの PC では、権限が無くて作れないことだけを確かめる (今回の決まりで止めていないこと)
        var failure = result.Failed.Where(f => f.Name == "a").ToList();
        if (failure.Count > 0)
        {
            Assert.Equal(NoPrivilege, Assert.Single(failure).Reason);
            Assert.False(Directory.Exists(_place.In("a")), "空のフォルダーにしない");
            return;
        }

        Assert.True(File.GetAttributes(_place.In("a")).HasFlag(FileAttributes.ReparsePoint), "リンクとして展開する");
        Assert.Equal("X", File.ReadAllText(_place.In("a")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RARでも同じ名前のジャンクションとフォルダーの項目があればジャンクションを展開する(bool linkFirst)
    {
        var sample = new RarSample().File("x/f.txt", "F");
        if (linkFirst)
        {
            sample.File("a", redirect: (3, "x", true)).File("a", directory: true);
        }
        else
        {
            sample.File("a", directory: true).File("a", redirect: (3, "x", true));
        }

        var result = Extract(sample.Save(_place.PathOf("junction.rar")), ArchiveFormat.Rar);

        Assert.DoesNotContain(result.Failed, f => f.Name == "a");
        Assert.True(new DirectoryInfo(_place.In("a")).Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("F", File.ReadAllText(_place.In("a/f.txt")));
    }

    // ------------------------------------------------------------------ フォルダーの印の付いたリンク

    [Fact]
    public void RARのフォルダーの印の付いたリンクは空のフォルダーにせず知らせる()
    {
        var archive = new RarSample()
            .File("x/f.txt", "F")
            .File("dirlink", directory: true, redirect: (3, "x", true))
            .Save(_place.PathOf("dirlink.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        Assert.Contains(result.Failed, f => f.Name == "dirlink" && f.Reason == Strings.LinkKindNotExtracted);
        Assert.False(Path.Exists(_place.In("dirlink")));
    }

    [Fact]
    public void 七zのフォルダーの印の付いたリンクは空のフォルダーにせず知らせる()
    {
        var archive = new SevenZipSample()
            .Add("snl", [], 0x10 | SevenZipSample.ReparsePoint, folder: true)
            .Add("unix", [], 0x10 | SevenZipSample.UnixSymlink, folder: true)
            .Save(_place.PathOf("dirlink.7z"));

        var result = Extract(archive, ArchiveFormat.SevenZip);

        Assert.Contains(result.Failed, f => f.Name == "snl" && f.Reason == Strings.LinkKindNotExtracted);
        Assert.Contains(result.Failed, f => f.Name == "unix" && f.Reason == Strings.LinkKindNotExtracted);
        Assert.False(Path.Exists(_place.In("snl")));
        Assert.False(Path.Exists(_place.In("unix")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pass")]
    public void ZIPのリンクの属性の付いたフォルダーは空のフォルダーにせず知らせる(string? password)
    {
        var zip = new ZipSample()
            .Add("dirlink/", [], 3, unchecked((int)0xA1FF0010))
            .Save(_place.PathOf("dirlink.zip"), password);

        var result = Extract(zip, ArchiveFormat.Zip, password: password);

        Assert.Contains(result.Failed, f => f.Name == "dirlink/" && f.Reason == Strings.LinkKindNotExtracted);
        Assert.False(Path.Exists(_place.In("dirlink")));
    }

    // ------------------------------------------------------------------ リンクが中身の無いフォルダーを指すとき (#392)

    [Fact]
    public void 中身の無いフォルダーを指すリンクも作る()
    {
        var archive = new RarSample()
            .File("empty", directory: true)
            .File("alias", redirect: (3, "empty", true))
            .Save(_place.PathOf("empty.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        Assert.DoesNotContain(result.Failed, f => f.Name == "alias");
        Assert.True(new DirectoryInfo(_place.In("alias")).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void 前からあるフォルダーと大文字小文字や短い名前だけが違う中身の無いフォルダーを指すリンクは作らない()
    {
        Directory.CreateDirectory(_place.In("docs"));
        Directory.CreateDirectory(_place.In("Long Folder Name"));
        var shortName = ExtractionTests.ShortNameOf(_place.In("Long Folder Name")) is { } s ? Path.GetFileName(s) : null;

        var sample = new RarSample()
            .File("DOCS", directory: true)
            .File("upper", redirect: (3, "DOCS", true));
        var hasShort = shortName is not null && shortName != "Long Folder Name";
        if (hasShort)
        {
            sample.File(shortName!, directory: true).File("short", redirect: (3, shortName!, true));
        }

        var result = Extract(sample.Save(_place.PathOf("case.rar")), ArchiveFormat.Rar);

        Assert.Contains(result.Failed, f => f.Name == "upper" && f.Reason == Strings.LinkFolderNotExtracted);
        if (hasShort)
        {
            Assert.Contains(result.Failed, f => f.Name == "short" && f.Reason == Strings.LinkFolderNotExtracted);
        }
    }

    // ------------------------------------------------------------------ 新しい書庫を作るとき、中止

    [Fact]
    public void 選んだ項目から新しい書庫を作るときも取り出しは失敗しない()
    {
        // 新しい書庫は、拒否・失敗・飛ばした項目が 1 個でもあると作らない。中身の無いフォルダーと、同じ名前のフォルダーの項目が 2 個ある書庫
        var zip = new ZipSample()
            .File("top/a.txt", "A")
            .Folder("top")
            .Folder("top/empty")
            .Folder("top/empty")
            .Save(_place.PathOf("new.zip"));
        var scratch = Directory.CreateDirectory(_place.PathOf("scratch")).FullName;

        var result = ArchiveExtractor.Extract(zip, ArchiveFormat.Zip, NameReading.Default,
            new EntrySelection(["top/a.txt"], ["top/", "top/empty/"]), scratch, overwrite: false, null, CancellationToken.None,
            links: new LinkOptions { Roots = ["top"], Collect = true });

        AssertClean(result);
        Assert.True(Directory.Exists(Path.Combine(scratch, "top", "empty")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.Zip))]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    public void 中止したら残りのフォルダーは作らない(string formatName)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var archive = Sample(format, "cancel." + format);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        // 7z は、展開を始める前の見出しの確かめで中止の例外になる (前からの動き)。どちらでも、フォルダーは作らない
        try
        {
            Assert.True(Extract(archive, format, cancellationToken: cancellation.Token).Cancelled);
        }
        catch (OperationCanceledException)
        {
        }

        Assert.False(Path.Exists(_place.In("empty")));
        Assert.False(Path.Exists(_place.In("a")));
    }
}
