using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Tests;

/// <summary>
/// フォルダーを指すリンクとジャンクションは、今回の展開で作ったフォルダーだけを指す (#392)。
/// 攻撃の見本は、設計の確認 (#392 のノート) で挙がったもの。
/// </summary>
/// <remarks>
/// ジャンクションは特別な権利が無くても作れるので、RAR5 のジャンクションで確かめる。シンボリックリンクは開発者モードがオフの PC では
/// 作れないので、そのときは「権限がありません」で失敗したことだけを確かめる (作らない理由が今回の確かめではないこと)。
/// </remarks>
public sealed class LinkFolderTargetTests : IDisposable
{
    private readonly LinkPlace _place = new();

    public void Dispose() => _place.Dispose();

    private static string NoPrivilege => Strings.Reason(new LinkPrivilegeException());

    private ExtractResult Extract(
        string archive, ArchiveFormat format, IReadOnlySet<string>? names = null, string? password = null)
        => ArchiveExtractor.Extract(archive, format, NameReading.Default, names is null ? null : new EntrySelection(names), _place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None, password: password);

    private static (int, string, bool) JunctionTo(string target) => (3, target, true);

    /// <summary>ジャンクションができて、中に <paramref name="file"/> が見える。</summary>
    private void AssertJunction(ExtractResult result, string name, string file, string content)
    {
        Assert.DoesNotContain(result.Failed, f => f.Name == name);
        var path = _place.In(name);
        Assert.True(new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint), "ジャンクションとして展開する");
        Assert.Equal(content, File.ReadAllText(Path.Combine(path, file)));
    }

    /// <summary>今回の確かめでは止めていない。作れたか、権限が無くて作れなかったか。</summary>
    private void AssertNotRefused(ExtractResult result, string name)
    {
        var failure = result.Failed.Where(f => f.Name == name).ToList();
        if (failure.Count > 0)
        {
            Assert.Equal(NoPrivilege, Assert.Single(failure).Reason);
            return;
        }

        Assert.True(Path.Exists(_place.In(name)), "展開する");
    }

    private void AssertRefused(ExtractResult result, string name, string reason)
    {
        Assert.Contains(result.Failed, f => f.Name == name && f.Reason == reason);
        Assert.False(Path.Exists(_place.In(name)), "展開しなかったリンクは何も置かない");
    }

    // ------------------------------------------------------------------ 前からあるフォルダー

    [Fact]
    public void 展開先に前からあるフォルダーを指すジャンクションは作らない()
    {
        Directory.CreateDirectory(_place.In("docs"));
        File.WriteAllText(_place.In("docs/user.txt"), "USER");
        var archive = new RarSample()
            .File("docs", directory: true)
            .File("alias", redirect: JunctionTo("docs"))
            .Save(_place.PathOf("existing.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        AssertRefused(result, "alias", Strings.LinkFolderNotExtracted);
        Assert.Equal("USER", File.ReadAllText(_place.In("docs/user.txt")));
    }

    [Fact]
    public void 前からあるフォルダーに書き足してもそのフォルダーを指すリンクは作らない()
    {
        Directory.CreateDirectory(_place.In("docs"));
        File.WriteAllText(_place.In("docs/user.txt"), "USER");
        var archive = new RarSample()
            .File("docs/x.txt", "X")
            .File("alias", redirect: JunctionTo("docs"))
            .Save(_place.PathOf("added.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        Assert.Equal("X", File.ReadAllText(_place.In("docs/x.txt")));
        AssertRefused(result, "alias", Strings.LinkFolderNotExtracted);
    }

    [Fact]
    public void tarのフォルダーを指すシンボリックリンクも前からあるフォルダーは指さない()
    {
        Directory.CreateDirectory(_place.In("docs"));
        var tar = new TarSample()
            .Folder("docs")
            .Symlink("alias", "docs")
            .Save(_place.PathOf("existing.tar"));

        var result = Extract(tar, ArchiveFormat.Tar);

        AssertRefused(result, "alias", Strings.LinkFolderNotExtracted);
    }

    [Fact]
    public void 大文字小文字や短い名前が違っても前からあるフォルダーを取り違えない()
    {
        Directory.CreateDirectory(_place.In("docs"));
        Directory.CreateDirectory(_place.In("Long Folder Name"));
        var shortName = ExtractionTests.ShortNameOf(_place.In("Long Folder Name")) is { } s ? Path.GetFileName(s) : null;

        var sample = new RarSample()
            .File("DOCS", directory: true)
            .File("upper", redirect: JunctionTo("DOCS"));
        if (shortName is not null && shortName != "Long Folder Name")
        {
            sample.File(shortName, directory: true).File("short", redirect: JunctionTo(shortName));
        }

        var result = Extract(sample.Save(_place.PathOf("case.rar")), ArchiveFormat.Rar);

        AssertRefused(result, "upper", Strings.LinkFolderNotExtracted);
        if (shortName is not null && shortName != "Long Folder Name")
        {
            AssertRefused(result, "short", Strings.LinkFolderNotExtracted);
        }
    }

    [Fact]
    public void 展開先に前からあるジャンクションそのものを指すリンクは作らない()
    {
        var elsewhere = Directory.CreateDirectory(_place.PathOf("elsewhere")).FullName;
        File.WriteAllText(Path.Combine(elsewhere, "user.txt"), "USER");
        Assert.SkipUnless(Junction.TryCreate(_place.In("j"), elsewhere), "ジャンクションを作れない");
        var archive = new RarSample()
            .File("j", directory: true)
            .File("alias", redirect: JunctionTo("j"))
            .Save(_place.PathOf("junction.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        AssertRefused(result, "alias", Strings.LinkFolderNotExtracted);
    }

    [Fact]
    public void 指す先までの途中に前からあるジャンクションがあれば作らない()
    {
        // a は前からあるジャンクション。a/new は今回作るが、a の向きが変わると別の場所を指すことになる
        var elsewhere = Directory.CreateDirectory(_place.PathOf("elsewhere")).FullName;
        Assert.SkipUnless(Junction.TryCreate(_place.In("a"), elsewhere), "ジャンクションを作れない");
        var archive = new RarSample()
            .File("a/new/f.txt", "F")
            .File("alias", redirect: JunctionTo("a/new"))
            .Save(_place.PathOf("through.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        AssertRefused(result, "alias", Strings.LinkTargetThroughLink);
    }

    [Fact]
    public void 同じ書庫をもう一度展開するとリンクは上書きできなかったものとして知らせる()
    {
        // 2 回目から見ると、1 回目に展開した data は前からあるフォルダー。指す先の理由ではなく、同じ名前があることを知らせる
        var archive = new RarSample()
            .File("data/a.txt", "A")
            .File("alias", redirect: JunctionTo("data"))
            .Save(_place.PathOf("twice.rar"));

        Extract(archive, ArchiveFormat.Rar);
        var second = Extract(archive, ArchiveFormat.Rar);

        Assert.DoesNotContain(second.Failed, f => f.Name == "alias");
        Assert.Contains(second.NotReplaced, f => f.Name == "alias" && f.Reason == Strings.LinkNameTaken);
        Assert.Equal("A", File.ReadAllText(_place.In("alias/a.txt")));
    }

    // ------------------------------------------------------------------ 選んでいないもの、作っていないもの

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 一部を選んで展開したとき選んでいないフォルダーを指すリンクは作らない(bool existsBefore)
    {
        if (existsBefore)
        {
            Directory.CreateDirectory(_place.In("other"));
        }

        var archive = new RarSample()
            .File("data/a.txt", "A")
            .File("other/b.txt", "B")
            .File("alias", redirect: JunctionTo("other"))
            .Save(_place.PathOf("partial.rar"));

        var result = Extract(archive, ArchiveFormat.Rar, names: new HashSet<string>(["data/a.txt", "alias"]));

        AssertRefused(result, "alias", Strings.LinkFolderNotExtracted);
    }

    // ------------------------------------------------------------------ 今回作ったフォルダー (正しいものを拒まない)

    [Fact]
    public void 今回作ったフォルダーを指すジャンクションは作る()
    {
        var archive = new RarSample()
            .File("data/a.txt", "A")
            .File("alias", redirect: JunctionTo("data"))
            .Save(_place.PathOf("created.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        AssertJunction(result, "alias", "a.txt", "A");
    }

    [Fact]
    public void 上の段のフォルダーも今回作ったものなら指せる()
    {
        var archive = new RarSample()
            .File("a/b/c/f.txt", "F")
            .File("la", redirect: JunctionTo("a"))
            .File("lb", redirect: JunctionTo("a/b"))
            .Save(_place.PathOf("upper.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        AssertJunction(result, "la", "b/c/f.txt", "F");
        AssertJunction(result, "lb", "c/f.txt", "F");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 中身がリンクだけのフォルダーを指すリンクは書庫の順によらず作る(bool aliasFirst)
    {
        // d はリンク d/l1 を置くときに初めて作られる
        var sample = new RarSample().File("x/f.txt", "F");
        if (aliasFirst)
        {
            sample.File("alias", redirect: JunctionTo("d")).File("d/l1", redirect: JunctionTo("../x"));
        }
        else
        {
            sample.File("d/l1", redirect: JunctionTo("../x")).File("alias", redirect: JunctionTo("d"));
        }

        var result = Extract(sample.Save(_place.PathOf("order.rar")), ArchiveFormat.Rar);

        AssertJunction(result, "d/l1", "f.txt", "F");
        AssertJunction(result, "alias", "l1/f.txt", "F");
    }

    [Fact]
    public void 中身がハードリンクだけのフォルダーも今回作ったものとして指せる()
    {
        var archive = new RarSample()
            .File("a.txt", "A")
            .File("alias", redirect: JunctionTo("h"))
            .File("h/hard.txt", redirect: (4, "a.txt", false))
            .Save(_place.PathOf("hard.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        AssertJunction(result, "alias", "hard.txt", "A");
    }

    [Fact]
    public void 逆の順に並んだリンクの鎖も作る()
    {
        var archive = new RarSample()
            .File("l1", redirect: JunctionTo("l2"))
            .File("l2", redirect: JunctionTo("l3"))
            .File("l3", redirect: JunctionTo("data"))
            .File("data/a.txt", "A")
            .Save(_place.PathOf("chain.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        AssertJunction(result, "l3", "a.txt", "A");
        AssertJunction(result, "l2", "a.txt", "A");
        AssertJunction(result, "l1", "a.txt", "A");
    }

    [Fact]
    public void ファイルを指すリンクの指す先が今回作ったフォルダーなら止めない()
    {
        // 書庫ではファイルを指す Windows のシンボリックリンク。展開先では今回作ったフォルダーになっている (#392 で決めた)
        var archive = new RarSample()
            .File("data/a.txt", "A")
            .File("alias", redirect: (2, "data", false))
            .Save(_place.PathOf("filelink.rar"));

        var result = Extract(archive, ArchiveFormat.Rar);

        AssertNotRefused(result, "alias");
    }

    // ------------------------------------------------------------------ フォルダーを作るほかの入口

    [Theory]
    [InlineData(null)]
    [InlineData("pass")]
    public void ZIPでも今回作ったフォルダーを指すリンクは作る(string? password)
    {
        var zip = new ZipSample()
            .File("data/a.txt", "A")
            .Symlink("alias", "data")
            .Save(_place.PathOf("created.zip"), password);

        var result = Extract(zip, ArchiveFormat.Zip, password: password);

        AssertNotRefused(result, "alias");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("pass")]
    public void ZIPでも前からあるフォルダーを指すリンクは作らない(string? password)
    {
        Directory.CreateDirectory(_place.In("data"));
        var zip = new ZipSample()
            .File("data/a.txt", "A")
            .Symlink("alias", "data")
            .Save(_place.PathOf("existing.zip"), password);

        var result = Extract(zip, ArchiveFormat.Zip, password: password);

        AssertRefused(result, "alias", Strings.LinkFolderNotExtracted);
    }

    [Fact]
    public void 七zでも今回作ったフォルダーを指すリンクは作り前からあるものは指さない()
    {
        Directory.CreateDirectory(_place.In("old"));
        var archive = new SevenZipSample()
            .File("data/a.txt", "A")
            .File("old/b.txt", "B")
            .Symlink("alias", "data")
            .Symlink("stolen", "old")
            .Save(_place.PathOf("links.7z"));

        var result = Extract(archive, ArchiveFormat.SevenZip);

        AssertNotRefused(result, "alias");
        AssertRefused(result, "stolen", Strings.LinkFolderNotExtracted);
    }

    [Fact]
    public void 並べて展開するZIPでも今回作ったフォルダーを指すリンクは作る()
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

            var zip = sample.Symlink("alias", "data").Save(_place.PathOf("lanes.zip"));
            Assert.SkipUnless(ArchiveExtractor.CountLanes(zip, _place.Output, targets) > 1, "この PC では並べて展開しない");

            var result = Extract(zip, ArchiveFormat.Zip);

            Assert.Equal(40, Enumerable.Range(0, 40).Count(i => File.Exists(_place.In($"data/f{i}.txt"))));
            AssertNotRefused(result, "alias");
        }
        finally
        {
            SeekPenalty.Override = previous;
        }
    }
}
