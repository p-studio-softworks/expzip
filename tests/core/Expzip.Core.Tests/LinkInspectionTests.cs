using System.Text;
using Expzip.Archives;
using Expzip.Inspection;

namespace Expzip.Tests;

/// <summary>
/// 書庫の検査で、展開先の外へ書かせようとするリンクを知らせる (#391)。攻撃の見本は設計の確認 (#391 のノート) で挙がったもの。
/// </summary>
/// <remarks>
/// Expzip の展開はリンクを最後に、書庫の中を指すものだけ作るので、どれも Expzip では外に書かれない。
/// 検査で何も出ないのを見て、リンクをたどる別のツールで展開した人が外に書かれないように、知らせることを確かめる。
/// </remarks>
public sealed class LinkInspectionTests : IDisposable
{
    private readonly LinkPlace _place = new();

    public void Dispose() => _place.Dispose();

    private static IReadOnlyList<InspectionFinding> Inspect(string archive, string? password = null)
    {
        var contents = ArchiveReader.Open(archive, NameReading.Default, null, CancellationToken.None);
        var report = ArchiveInspector.Inspect(contents, password);
        Assert.False(report.Cancelled);
        return report.Findings;
    }

    private static IReadOnlyList<InspectionFinding> Of(IReadOnlyList<InspectionFinding> findings, InspectionIssue issue)
        => findings.Where(f => f.Issue == issue).ToList();

    private static void AssertHas(IReadOnlyList<InspectionFinding> findings, InspectionIssue issue, string target)
        => Assert.Contains(findings, f => f.Issue == issue && f.Target == target);

    private static void AssertNoDanger(IReadOnlyList<InspectionFinding> findings)
        => Assert.DoesNotContain(findings, f => f.Severity == InspectionSeverity.Danger);

    private static readonly InspectionIssue[] LinkIssues =
    [
        InspectionIssue.LinkOutside, InspectionIssue.LinkUnverified, InspectionIssue.WriteThroughLink,
        InspectionIssue.BeforeLink, InspectionIssue.ReplacesLink,
    ];

    private static void AssertNoLinkIssue(IReadOnlyList<InspectionFinding> findings)
        => Assert.DoesNotContain(findings, f => LinkIssues.Contains(f.Issue));

    // ------------------------------------------------------------------ 重さ

    [Fact]
    public void リンクの事柄の重さ()
    {
        Assert.Equal(InspectionSeverity.Warning, InspectionIssues.SeverityOf(InspectionIssue.LinkOutside));
        Assert.Equal(InspectionSeverity.Warning, InspectionIssues.SeverityOf(InspectionIssue.LinkUnverified));
        Assert.Equal(InspectionSeverity.Danger, InspectionIssues.SeverityOf(InspectionIssue.WriteThroughLink));
        Assert.Equal(InspectionSeverity.Warning, InspectionIssues.SeverityOf(InspectionIssue.BeforeLink));
        Assert.Equal(InspectionSeverity.Danger, InspectionIssues.SeverityOf(InspectionIssue.ReplacesLink));
        Assert.All(LinkIssues, i => Assert.Equal(InspectionCheck.Safety, InspectionIssues.CheckOf(i)));
    }

    // ------------------------------------------------------------------ tar

    [Fact]
    public void tarの外を指すリンクとその下の項目()
    {
        var tar = new TarSample()
            .Symlink("l", "../../outside")
            .File("l/x", "EVIL")
            .Save(_place.PathOf("through.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.LinkOutside, "l");
        AssertHas(findings, InspectionIssue.WriteThroughLink, "l/x");
    }

    [Theory]
    [InlineData("h", "h")]
    [InlineData("H", "h")]
    [InlineData("./h", "h")]
    [InlineData("h", "h.")]
    public void tarのハードリンクの後ろに同じ名前のファイル(string link, string file)
    {
        var tar = new TarSample()
            .HardLink(link, "../../x")
            .File(file, "EVIL")
            .Save(_place.PathOf("replace.tar"));

        var findings = Inspect(tar);

        Assert.Single(Of(findings, InspectionIssue.ReplacesLink));
    }

    [Fact]
    public void tarの外を指すリンクの後ろに同じ名前のフォルダー()
    {
        var tar = new TarSample()
            .Symlink("d", "/etc")
            .Folder("d")
            .Save(_place.PathOf("folder.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.LinkOutside, "d");
        Assert.Single(Of(findings, InspectionIssue.ReplacesLink));
    }

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData(@"\\server\share\x")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData("../x")]
    public void tarのハードリンクで先頭が区切りの指す先は外を指す(string target)
    {
        var tar = new TarSample()
            .File("a.txt", "A")
            .HardLink("h", target)
            .File("h", "EVIL")
            .Save(_place.PathOf("hard.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.LinkOutside, "h");
        Assert.Single(Of(findings, InspectionIssue.ReplacesLink));
    }

    [Fact]
    public void リンクの名前の直後に上がる項目もリンクを通る()
    {
        // 先頭から順にたどるツールは、l (リンク) をたどってから 1 つ上がる
        var tar = new TarSample()
            .Folder("sub/deep")
            .Symlink("l", "sub/deep")
            .File("l/../x", "EVIL")
            .Save(_place.PathOf("dotdot.tar"));

        var findings = Inspect(tar);

        Assert.Single(Of(findings, InspectionIssue.WriteThroughLink));
    }

    [Fact]
    public void 短い名前の形でリンクを通る項目()
    {
        // 短い名前が作られるドライブでは、LONGNAME~1 で longname-link をたどれる
        var tar = new TarSample()
            .Symlink("longname-link", "../../outside")
            .File("LONGNA~1/x", "EVIL")
            .Save(_place.PathOf("short.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.WriteThroughLink, "LONGNA~1/x");
    }

    [Fact]
    public void 中を指すが鎖で外へ出るリンク()
    {
        var tar = new TarSample()
            .Symlink("b", "../..")
            .Symlink("a", "b")
            .File("a/x", "EVIL")
            .Save(_place.PathOf("chain.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.LinkOutside, "a");
        AssertHas(findings, InspectionIssue.LinkOutside, "b");
        AssertHas(findings, InspectionIssue.WriteThroughLink, "a/x");
    }

    [Fact]
    public void 指す先の途中のリンクで外へ出る()
    {
        // 文字の上では a を指すが、a/b/up は .. を指すリンクなので、順にたどると外へ出る
        var tar = new TarSample()
            .Folder("a/b")
            .Symlink("a/b/up", "..")
            .Symlink("p", "a/b/up/../..")
            .Save(_place.PathOf("middle.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.LinkOutside, "p");
        Assert.DoesNotContain(findings, f => f.Issue == InspectionIssue.LinkOutside && f.Target == "a/b/up");
    }

    [Fact]
    public void 輪になったリンクは確かめられない()
    {
        var tar = new TarSample()
            .Symlink("a", "b")
            .Symlink("b", "a")
            .Save(_place.PathOf("loop.tar"));

        var findings = Inspect(tar);

        Assert.Equal(2, Of(findings, InspectionIssue.LinkUnverified).Count);
    }

    [Fact]
    public void tarの長すぎる指す先は確かめられない()
    {
        var target = string.Concat(Enumerable.Repeat("a/", 2100)) + "../../../outside";
        var tar = new TarSample()
            .Pax(new Dictionary<string, string> { ["linkpath"] = target })
            .Symlink("l", "x")
            .Save(_place.PathOf("long.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.LinkUnverified, "l");
    }

    [Fact]
    public void tarでリンクより前の項目は注意で後ろの項目は危険()
    {
        var tar = new TarSample()
            .File("bin/old", "O")
            .Symlink("bin", "usr/bin")
            .Folder("usr/bin")
            .File("bin/new", "N")
            .Save(_place.PathOf("layers.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.BeforeLink, "bin/old");
        AssertHas(findings, InspectionIssue.WriteThroughLink, "bin/new");
        Assert.DoesNotContain(findings, f => f.Issue == InspectionIssue.WriteThroughLink && f.Target == "bin/old");
    }

    [Fact]
    public void 前の項目だけで後ろにリンクがあれば危険にしない()
    {
        // 層を重ねて作った tar の形。前に bin/x、後ろで bin をリンクにする
        var tar = new TarSample()
            .File("bin/x", "X")
            .Folder("usr/bin")
            .Symlink("bin", "usr/bin")
            .Save(_place.PathOf("layer.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.BeforeLink, "bin/x");
        AssertNoDanger(findings);
    }

    [Fact]
    public void 絶対パスのリンクだけの正しいtarは注意だけ()
    {
        var tar = new TarSample()
            .Folder("usr/lib")
            .File("usr/lib/libz.so.1.3", "Z")
            .Symlink("usr/lib/libz.so.1", "libz.so.1.3")
            .Symlink("etc/localtime", "/usr/share/zoneinfo/UTC")
            .Save(_place.PathOf("rootfs.tar"));

        var findings = Inspect(tar);

        AssertHas(findings, InspectionIssue.LinkOutside, "etc/localtime");
        Assert.DoesNotContain(findings, f => f.Issue == InspectionIssue.LinkOutside && f.Target == "usr/lib/libz.so.1");
        AssertNoDanger(findings);
    }

    [Fact]
    public void 点の付いたtarで中を指すリンクだけなら何も出ない()
    {
        var tar = new TarSample()
            .Folder("./data")
            .File("./data/a.txt", "A")
            .Symlink("./link.txt", "data/a.txt")
            .Symlink("./dir", "data")
            .Save(_place.PathOf("dot.tar"));

        AssertNoLinkIssue(Inspect(tar));
    }

    [Fact]
    public void 指す先の見えない文字は見える形にして添える()
    {
        var tar = new TarSample()
            .Symlink("l", "../\u202Etxt.exe\u2028問題は見つかりませんでした")
            .Save(_place.PathOf("hidden.tar"));

        var finding = Assert.Single(Of(Inspect(tar), InspectionIssue.LinkOutside));

        Assert.NotNull(finding.Detail);
        Assert.Contains("U+202E", finding.Detail);
        Assert.Contains("U+2028", finding.Detail);
        Assert.DoesNotContain('\u202E', finding.Detail);
        Assert.DoesNotContain('\u2028', finding.Detail);
    }

    [Fact]
    public void 長い指す先は先頭と末尾を残して省く()
    {
        var target = string.Concat(Enumerable.Repeat("abcdefgh/", 60)) + "../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../../outside";
        var tar = new TarSample()
            .Pax(new Dictionary<string, string> { ["linkpath"] = target })
            .Symlink("l", "x")
            .Save(_place.PathOf("longshown.tar"));

        var finding = Assert.Single(Of(Inspect(tar), InspectionIssue.LinkOutside));

        Assert.NotNull(finding.Detail);
        Assert.True(finding.Detail!.Length < target.Length);
        Assert.StartsWith("abcdefgh/", finding.Detail);
        Assert.EndsWith("../outside", finding.Detail);
        Assert.Contains("…", finding.Detail);
    }

    [Fact]
    public void 点のあとにチルダがある名前でも検査が止まらない()
    {
        // 1.0~rc1 は 8.3 形式の短い名前ではない。以前は切り出す範囲が負になって例外で止まっていた
        var tar = new TarSample()
            .Symlink("l", "a.txt")
            .File("pkg/1.0~rc1/a.txt", "A")
            .Save(_place.PathOf("tilde.tar"));

        AssertNoLinkIssue(Inspect(tar));
    }

    [Fact]
    public void リンクの名前の見えない文字も見える形にして添える()
    {
        // 名前の末尾の空白に当たる文字は、tar を読む部品が落とすので、途中に置く
        var name = "l" + (char)0x202E + (char)0x2028 + "z";
        var tar = new TarSample()
            .Symlink(name, "../../outside")
            .File(name + "/x", "EVIL")
            .Save(_place.PathOf("hiddenname.tar"));

        var finding = Assert.Single(Of(Inspect(tar), InspectionIssue.WriteThroughLink));

        Assert.Contains("U+202E", finding.Detail);
        Assert.Contains("U+2028", finding.Detail);
        Assert.DoesNotContain((char)0x2028, finding.Detail!);
    }

    [Fact]
    public void リンクと短い名前の形の項目が多くても長くかからない()
    {
        var tar = new TarSample();
        for (var i = 0; i < 20_000; i++)
        {
            tar.Symlink($"AAAAAAlink{i}", "x").File($"AAAAAA~{i % 9 + 1}/f{i}", "");
        }

        AssertQuick(tar.Save(_place.PathOf("many.tar")));
    }

    [Fact]
    public void 深い指す先のリンクが多くても長くかからない()
    {
        var target = string.Concat(Enumerable.Repeat("a/", 2047)) + "x";
        var tar = new TarSample();
        for (var i = 0; i < 2_000; i++)
        {
            tar.Pax(new Dictionary<string, string> { ["linkpath"] = target }).Symlink($"l{i}", "x");
        }

        AssertQuick(tar.Save(_place.PathOf("deep.tar")));
    }

    /// <summary>リンクの検査だけを走らせ、数秒で終わることを確かめる。中身の検査 (マルウェア検査) は時間を測る対象にしない。</summary>
    private static void AssertQuick(string archive)
    {
        var contents = ArchiveReader.Open(archive, NameReading.Default, null, CancellationToken.None);
        var context = new InspectionContext(contents, null, null, null, CancellationToken.None);
        var clock = System.Diagnostics.Stopwatch.StartNew();

        LinkInspector.Inspect(context);

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"{clock.Elapsed.TotalSeconds:N1} 秒かかった");
    }

    // ------------------------------------------------------------------ ZIP

    [Theory]
    [InlineData("l", "l/x")]
    [InlineData("l", @"l\x")]
    [InlineData("l.", "l/x")]
    [InlineData("L", "l/x")]
    [InlineData("caf\u00E9", "cafe\u0301/x")]
    public void ZIPの外を指すリンクとその下の項目(string link, string item)
    {
        var zip = new ZipSample()
            .Symlink(link, "../../outside")
            .File(item, "EVIL")
            .Save(_place.PathOf("through.zip"));

        var findings = Inspect(zip);

        Assert.Single(Of(findings, InspectionIssue.WriteThroughLink));
    }

    [Fact]
    public void ZIPはリンクより前の項目も危険()
    {
        // ZIP は目録の順と中身の並びの順が違うことがあり、どちらが前かを決められない
        var zip = new ZipSample()
            .File("l/x", "EVIL")
            .Symlink("l", "../../outside")
            .Save(_place.PathOf("before.zip"));

        var findings = Inspect(zip);

        AssertHas(findings, InspectionIssue.WriteThroughLink, "l/x");
        Assert.Empty(Of(findings, InspectionIssue.BeforeLink));
    }

    [Fact]
    public void ZIPのリンクの後ろに同じ名前のファイル()
    {
        var zip = new ZipSample()
            .Symlink("setup.txt", "../../x")
            .File("setup.txt", "EVIL")
            .Save(_place.PathOf("replace.zip"));

        var findings = Inspect(zip);

        Assert.Single(Of(findings, InspectionIssue.ReplacesLink));
        AssertHas(findings, InspectionIssue.LinkOutside, "setup.txt");
    }

    [Fact]
    public void ZIPの長すぎる指す先は確かめられない()
    {
        var zip = new ZipSample()
            .Symlink("l", string.Concat(Enumerable.Repeat("a/", 2100)) + "x")
            .Save(_place.PathOf("long.zip"));

        AssertHas(Inspect(zip), InspectionIssue.LinkUnverified, "l");
    }

    [Fact]
    public void パスワード付きのZIPのリンク()
    {
        var zip = new ZipSample()
            .File("a.txt", "A")
            .Symlink("l", "../../outside")
            .Save(_place.PathOf("locked.zip"), "pass");

        AssertHas(Inspect(zip, "pass"), InspectionIssue.LinkOutside, "l");

        // 合言葉が分からなければ、調べられなかったことは別の行が伝える
        var without = Inspect(zip);
        AssertHas(without, InspectionIssue.EncryptedNotChecked, "l");
        Assert.Empty(Of(without, InspectionIssue.LinkUnverified));
        Assert.Empty(Of(without, InspectionIssue.LinkOutside));
    }

    [Fact]
    public void macOSのフレームワークのZIPは危険にしない()
    {
        var zip = new ZipSample()
            .Folder("Kit.framework/Versions/A/Headers")
            .File("Kit.framework/Versions/A/Headers/Kit.h", "H")
            .File("Kit.framework/Versions/A/Kit", "BIN")
            .Symlink("Kit.framework/Versions/Current", "A")
            .Symlink("Kit.framework/Headers", "Versions/Current/Headers")
            .Symlink("Kit.framework/Kit", "Versions/Current/Kit")
            .Save(_place.PathOf("framework.zip"));

        AssertNoLinkIssue(Inspect(zip));
    }

    // ------------------------------------------------------------------ 7z

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 七zの外を指すリンクとその下の項目(bool solid)
    {
        var archive = new SevenZipSample()
            .File("a.txt", "A")
            .Symlink("l", "../../outside")
            .File("l/x", "EVIL")
            .Save(_place.PathOf("through.7z"), solid);

        var findings = Inspect(archive);

        AssertHas(findings, InspectionIssue.LinkOutside, "l");
        AssertHas(findings, InspectionIssue.WriteThroughLink, "l/x");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 七zでリンクの後ろにフォルダーがあっても指す先を取り違えない(bool solid)
    {
        // SharpCompress はフォルダーを先にまとめて返すので、読み出しの順で数えると一覧の番号とずれる
        var archive = new SevenZipSample()
            .Symlink("l", "../../outside")
            .Symlink("e", "a.txt")
            .File("a.txt", "A")
            .Folder("d")
            .File("b.txt", "B")
            .Save(_place.PathOf("folderlast.7z"), solid);

        var findings = Inspect(archive);

        AssertHas(findings, InspectionIssue.LinkOutside, "l");
        Assert.Empty(Of(findings, InspectionIssue.LinkUnverified));
    }

    [Fact]
    public void 七zのWindowsの形のリンクは確かめられない()
    {
        var archive = new SevenZipSample()
            .Add("l", Encoding.UTF8.GetBytes("reparse"), SevenZipSample.ReparsePoint, folder: false)
            .Save(_place.PathOf("reparse.7z"));

        AssertHas(Inspect(archive), InspectionIssue.LinkUnverified, "l");
    }

    // ------------------------------------------------------------------ RAR5

    [Theory]
    [InlineData(1, "/etc/passwd")]
    [InlineData(2, @"C:\Windows\win.ini")]
    [InlineData(3, @"C:\Windows")]
    [InlineData(4, "../x")]
    [InlineData(5, "/etc/passwd")]
    public void RARの外を指すリンク(int type, string target)
    {
        var archive = new RarSample()
            .File("a.txt", "A")
            .File("link", redirect: (type, target, type == 3))
            .Save(_place.PathOf("links.rar"));

        AssertHas(Inspect(archive), InspectionIssue.LinkOutside, "link");
    }

    [Fact]
    public void RARのフォルダーの印付きのリンクとその下の項目()
    {
        var archive = new RarSample()
            .File("d", redirect: (3, @"C:\Windows", true), directory: true)
            .File("d/x", "EVIL")
            .Save(_place.PathOf("folderlink.rar"));

        var findings = Inspect(archive);

        AssertHas(findings, InspectionIssue.LinkOutside, "d");
        AssertHas(findings, InspectionIssue.WriteThroughLink, "d/x");
    }

    [Fact]
    public void RARの中を指すリンクだけなら何も出ない()
    {
        var archive = new RarSample()
            .File("a.txt", "A")
            .File("sub/link.txt", redirect: (1, "../a.txt", false))
            .File("hard.txt", redirect: (4, "a.txt", false))
            .Save(_place.PathOf("inside.rar"));

        AssertNoLinkIssue(Inspect(archive));
    }
}
