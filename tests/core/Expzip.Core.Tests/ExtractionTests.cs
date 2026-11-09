using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 取り出しで、本来の名前に直接作る道 (#328)。確かめてから作るまでの間に同じ名前のものが置かれたら、
/// 置き換えの道に移り、書き込み禁止やアクセス権の確認が働く。
/// </summary>
[Collection(nameof(TraceCollection))]
public sealed class ExtractionTests
{
    private static readonly byte[] Content = Encoding.ASCII.GetBytes("NEW CONTENT");

    [Fact]
    public void 何も無ければ直接作る()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");

        Assert.True(Write(target, overwrite: false));

        Assert.Equal("direct", log.Names);
        Assert.Equal(Content, File.ReadAllBytes(target));
    }

    [Fact]
    public void 置かれたファイル_上書きしない指定なら残す()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        PlaceAtDirect(log, target, () => File.WriteAllText(target, "PLACED"));

        Assert.False(Write(target, overwrite: false));

        Assert.Equal("PLACED", File.ReadAllText(target));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 置かれたファイル_上書きする指定なら書き切ってから置き換える()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        PlaceAtDirect(log, target, () => File.WriteAllText(target, "PLACED"));

        Assert.True(Write(target, overwrite: true));

        Assert.Equal(Content, File.ReadAllBytes(target));
        var names = log.Names;
        Assert.Contains("flush", names);
        Assert.True(names.IndexOf("flush", StringComparison.Ordinal) < names.LastIndexOf("move", StringComparison.Ordinal), names);
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 置かれてすぐ消えたら新しく作る()
    {
        // 直接作るのに失敗したあと、置き換えの道に入る前に、置かれたものが無くなる
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        log.OnEvent = (what, path) =>
        {
            if (what == "direct")
            {
                File.WriteAllText(target, "PLACED");
            }
            else if (what == "fallback")
            {
                File.Delete(target);
            }
        };

        Assert.True(Write(target, overwrite: false));

        Assert.Equal(Content, File.ReadAllBytes(target));
        Assert.StartsWith("direct fallback", log.Names);
        AssertNoLeftovers(log);
    }

    [Theory]
    [InlineData(FileAttributes.ReadOnly)]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public void 置かれた保護属性のファイルは置き換えない(FileAttributes attribute)
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        PlaceAtDirect(log, target, () =>
        {
            File.WriteAllText(target, "PLACED");
            File.SetAttributes(target, attribute);
        });

        Assert.Throws<UnauthorizedAccessException>(() => Write(target, overwrite: true));

        Assert.Equal("PLACED", File.ReadAllText(target));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 置かれた書き込み禁止のファイルは置き換えない()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        PlaceAtDirect(log, target, () =>
        {
            File.WriteAllText(target, "PLACED");
            var rules = new FileInfo(target).GetAccessControl();
            rules.AddAccessRule(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteData, AccessControlType.Deny));
            new FileInfo(target).SetAccessControl(rules);
        });

        try
        {
            Assert.Throws<UnauthorizedAccessException>(() => Write(target, overwrite: true));
            Assert.Equal("PLACED", File.ReadAllText(target));
            AssertNoLeftovers(log);
        }
        finally
        {
            var rules = new FileInfo(target).GetAccessControl();
            rules.RemoveAccessRuleAll(new FileSystemAccessRule(
                WindowsIdentity.GetCurrent().User!, FileSystemRights.WriteData, AccessControlType.Deny));
            new FileInfo(target).SetAccessControl(rules);
        }
    }

    [Fact]
    public void 置かれたファイルの個別のアクセス権を引き継ぐ()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        string? wanted = null;
        PlaceAtDirect(log, target, () =>
        {
            File.WriteAllText(target, "PLACED");
            var rules = new FileInfo(target).GetAccessControl();
            rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            new FileInfo(target).SetAccessControl(rules);
            wanted = new FileInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
        });

        Assert.True(Write(target, overwrite: true));

        Assert.Equal(Content, File.ReadAllBytes(target));
        Assert.Equal(wanted, new FileInfo(target).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }

    [Fact]
    public void 置かれたフォルダーは置き換えない()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        PlaceAtDirect(log, target, () =>
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "inside.txt"), "INSIDE");
        });

        Assert.Throws<UnauthorizedAccessException>(() => Write(target, overwrite: true));

        Assert.Equal("INSIDE", File.ReadAllText(Path.Combine(target, "inside.txt")));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 置かれたハードリンクの先は変えない()
    {
        using var log = new TraceLog();
        var outside = Directory.CreateDirectory(log.PathOf("outside")).FullName;
        var victim = Path.Combine(outside, "victim.txt");
        File.WriteAllText(victim, "VICTIM");
        var destination = Directory.CreateDirectory(log.PathOf("dest")).FullName;
        var target = Path.Combine(destination, "a.txt");
        PlaceAtDirect(log, target, () => Assert.True(CreateHardLink(target, victim, IntPtr.Zero)));

        Assert.True(Write(target, overwrite: true));

        Assert.Equal(Content, File.ReadAllBytes(target));
        Assert.Equal("VICTIM", File.ReadAllText(victim));
    }

    [Fact]
    public void 置かれたシンボリックリンクの先には書かない()
    {
        using var log = new TraceLog();
        var victim = log.PathOf("victim.txt");
        File.WriteAllText(victim, "VICTIM");
        var destination = Directory.CreateDirectory(log.PathOf("dest")).FullName;
        var target = Path.Combine(destination, "a.txt");
        var created = false;
        PlaceAtDirect(log, target, () => created = TryCreateSymbolicLink(target, victim));

        try
        {
            Write(target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 断ってもよい。リンクの先が変わらないことだけを見る
        }

        Assert.SkipUnless(created, "この PC ではシンボリックリンクを作れない (開発者モードか管理者の権限が要る)");
        Assert.Equal("VICTIM", File.ReadAllText(victim));
    }

    [Fact]
    public void 置かれた壊れたシンボリックリンクの先には作らない()
    {
        using var log = new TraceLog();
        var missing = log.PathOf("missing.txt");
        var destination = Directory.CreateDirectory(log.PathOf("dest")).FullName;
        var target = Path.Combine(destination, "a.txt");
        var created = false;
        PlaceAtDirect(log, target, () => created = TryCreateSymbolicLink(target, missing));

        try
        {
            Write(target, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        Assert.SkipUnless(created, "この PC ではシンボリックリンクを作れない (開発者モードか管理者の権限が要る)");
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void 置かれたジャンクションの中には書かない()
    {
        using var log = new TraceLog();
        var outside = Directory.CreateDirectory(log.PathOf("outside")).FullName;
        var destination = Directory.CreateDirectory(log.PathOf("dest")).FullName;
        var target = Path.Combine(destination, "a.txt");
        var created = false;
        PlaceAtDirect(log, target, () => created = Junction.TryCreate(target, outside));

        var error = Record.Exception(() => Write(target, overwrite: true));

        // 作れなかったときは、普通に書けて終わる。その結果は見ない
        Assert.SkipUnless(created, "ジャンクションを作れなかった");
        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    [Fact]
    public void 置かれたファイルが使用中なら残す()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        FileStream? holder = null;
        PlaceAtDirect(log, target, () =>
        {
            File.WriteAllText(target, "PLACED");
            holder = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        });

        try
        {
            Assert.ThrowsAny<IOException>(() => Write(target, overwrite: true));
        }
        finally
        {
            holder?.Dispose();
        }

        Assert.Equal("PLACED", File.ReadAllText(target));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void インターネットから来た書庫では中身より先にMotWが付いている()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        string? markAtThatTime = null;
        long lengthAtThatTime = -1;
        log.OnEvent = (what, path) =>
        {
            if (what == "marked" && path == target)
            {
                markAtThatTime = ReadMark(path);
                lengthAtThatTime = new FileInfo(path).Length;
            }
        };

        using (var file = AtomicFile.CreateForExtraction(target, overwrite: false, MarkOfTheWeb.Internet))
        {
            Assert.False(file.MarkLost);
            file.Stream.Write(Content);
            file.Commit();
        }

        Assert.Equal("direct marked", log.Names);
        Assert.Equal(MarkOfTheWeb.Internet, markAtThatTime);
        Assert.Equal(0, lengthAtThatTime);
        Assert.Equal(MarkOfTheWeb.Internet, ReadMark(target));
    }

    [Fact]
    public void 置き換えの道でも中身より先にMotWが付いている()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        File.WriteAllText(target, "OLD");
        string? markAtThatTime = null;
        string? markedPath = null;
        long lengthAtThatTime = -1;
        log.OnEvent = (what, path) =>
        {
            if (what == "marked")
            {
                markedPath = path;
                markAtThatTime = ReadMark(path);
                lengthAtThatTime = new FileInfo(path).Length;
            }
        };

        using (var file = AtomicFile.CreateForExtraction(target, overwrite: true, MarkOfTheWeb.Internet))
        {
            Assert.False(file.MarkLost);
            file.Stream.Write(Content);
            Assert.True(file.Commit());
        }

        // 置き換える前に、MotW と中身を書き切る (#347)
        Assert.StartsWith("direct fallback marked markflush flush move", log.Names);
        // MotW を付けたのは一時ファイル。前からあるファイルには、置き換えるまで触らない
        Assert.NotEqual(target, markedPath);
        Assert.Equal(MarkOfTheWeb.Internet, markAtThatTime);
        Assert.Equal(0, lengthAtThatTime);
        Assert.Equal(MarkOfTheWeb.Internet, ReadMark(target));
    }

    [Fact]
    public void 短い名前で同じファイルを指す名前は既にあるものとして扱う()
    {
        using var log = new TraceLog();
        var longName = log.PathOf("longfilename.txt");
        File.WriteAllText(longName, "LONG");
        var shortName = ShortNameOf(longName);
        Assert.SkipWhen(shortName is null || string.Equals(shortName, longName, StringComparison.OrdinalIgnoreCase),
            "この場所では 8.3 形式の短い名前が作られない");

        Assert.False(Write(shortName!, overwrite: false));

        Assert.Equal("LONG", File.ReadAllText(longName));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 長い名前を書いている間に短い名前を書いても長い名前のものが残る()
    {
        // 並列の展開で 2 つが重なったときを、毎回同じ順番で起こす。
        // 長い名前を直接作って書いている間 (「閉じたら消す」を予約している間) に、短い名前を書いて確定させる
        using var log = new TraceLog();
        var longName = log.PathOf("longfilename.txt");
        File.WriteAllText(longName, "probe");
        var shortName = ShortNameOf(longName);
        File.Delete(longName);
        Assert.SkipWhen(shortName is null || string.Equals(shortName, longName, StringComparison.OrdinalIgnoreCase),
            "この場所では 8.3 形式の短い名前が作られない");

        bool? shortKept;
        using (var first = AtomicFile.CreateForExtraction(longName, overwrite: false, zone: null))
        {
            first.Stream.Write(Content);
            try
            {
                shortKept = Write(shortName!, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                shortKept = null;
            }

            Assert.True(first.Commit());
        }

        // 短い名前の方は、確定しないか、失敗する
        Assert.NotEqual(true, shortKept);
        Assert.Equal(Content, File.ReadAllBytes(longName));
        Assert.Single(Directory.EnumerateFiles(log.Root));
        AssertNoLeftovers(log);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 長い名前と短い名前の2項目は1つずつ展開する(bool overwrite)
    {
        // 並べると、1 つ目を書いている最中に 2 つ目が来て、まれに「アクセスが拒否されました」になっていた (#349)。
        // 1 つずつなら、上書きしない指定では 2 つ目を飛ばし、上書きする指定では 2 つ目が上書きする
        using var log = new TraceLog();
        var shortName = ShortNameFor(log);
        var big = new string('L', 4 * 1024 * 1024);
        var archive = MakeNamesZip(log, shortName, big, breakLong: false);

        for (var i = 0; i < 20; i++)
        {
            var destination = Directory.CreateDirectory(log.PathOf($"out{i}")).FullName;
            var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination, overwrite, null, CancellationToken.None);

            Assert.Empty(result.Failed);
            Assert.Equal(overwrite ? 2 : 1, result.Extracted);
            Assert.Equal(overwrite ? 0 : 1, result.Skipped);
            var file = Assert.Single(Directory.EnumerateFiles(destination));
            Assert.Equal(overwrite ? "SHORT" : big, File.ReadAllText(file));
            Assert.Empty(Directory.EnumerateFiles(destination, "~*.tmp"));
        }
    }

    [Fact]
    public void 長い名前の項目が壊れていても短い名前の項目は取り出す()
    {
        // 1 つ目を書いている間に 2 つ目を飛ばすと、1 つ目が消えたときにどちらも残らない。1 つずつなら残る
        using var log = new TraceLog();
        var shortName = ShortNameFor(log);
        var archive = MakeNamesZip(log, shortName, new string('L', 4096), breakLong: true);
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination, false, null, CancellationToken.None);

        Assert.Single(result.Failed);
        Assert.Equal(1, result.Extracted);
        var file = Assert.Single(Directory.EnumerateFiles(destination));
        Assert.Equal("SHORT", File.ReadAllText(file));
        Assert.Empty(Directory.EnumerateFiles(destination, "~*.tmp"));
    }

    [Theory]
    [InlineData("LONGFI~1.TXT", true)]
    [InlineData(@"DOCUME~2\a.txt", true)]
    [InlineData("a~b.txt", false)]
    [InlineData("~$report.docx", false)]
    [InlineData("plain.txt", false)]
    [InlineData("end~", false)]
    [InlineData("~1start.txt", true)]
    // 全角の「～１」は短い名前として既に正しい形なので、Windows は短い名前を付けない
    [InlineData("～１.txt", false)]
    public void 短い名前かもしれない名前を見分ける(string name, bool expected)
        => Assert.Equal(expected, ArchiveExtractor.MayBeShortName(name));

    [Fact]
    public void 前からあるジャンクションを通る名前と先の名前は1つずつ展開する()
    {
        // link\a.txt と real\a.txt は同じファイル。並べると片方が「アクセスが拒否されました」になっていた (#349)
        using var log = new TraceLog();
        var archive = log.PathOf("alias.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("real/a.txt", new string('L', 4 * 1024 * 1024)), ("link/a.txt", "LINK") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());
                writer.Write(text);
            }
        }

        for (var i = 0; i < 20; i++)
        {
            var destination = Directory.CreateDirectory(log.PathOf($"out{i}")).FullName;
            var real = Directory.CreateDirectory(Path.Combine(destination, "real")).FullName;
            Assert.SkipUnless(Junction.TryCreate(Path.Combine(destination, "link"), real), "ジャンクションを作れない");

            Assert.True(ArchiveExtractor.PassesThroughLink(destination, [Path.Combine(destination, "real", "a.txt"), Path.Combine(destination, "link", "a.txt")]), "リンクを通ると見なす");
            var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination, false, null, CancellationToken.None);

            Assert.True(result.Failed.Count == 0, string.Join(", ", result.Failed.Select(f => f.Name + ": " + f.Reason)));
            Assert.Equal(1, result.Extracted);
            Assert.Equal(1, result.Skipped);
            Assert.Single(Directory.EnumerateFiles(real));
            Assert.Empty(Directory.EnumerateFiles(real, "~*.tmp"));
        }
    }

    [Fact]
    public void 短い名前の項目は置き換わるファイルの長い名前で確認に挙がる()
    {
        // 前は名前の文字列で突き合わせていたので、短い名前の項目は確認に挙がらず、本人は気付けなかった (#353)。
        // 書き出しと同じ決め方 (GetFullPath が既にある短い名前を長い名前に直す) で、置き換わるファイルの名前で挙がる
        using var log = new TraceLog();
        var shortName = ShortNameFor(log);
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;
        var users = Path.Combine(destination, "longfilename.txt");
        File.WriteAllText(users, "USER DATA");
        Assert.True(File.Exists(Path.Combine(destination, shortName)), "短い名前で同じファイルを指す");

        var conflict = Assert.Single(ArchiveExtractor.FindConflicts([shortName], destination, null));

        Assert.Equal(shortName, conflict.Name);
        Assert.Equal(users, conflict.Target, ignoreCase: true);
    }

    [Fact]
    public void はいと答えても置き換えるのは確認に挙げたファイルだけ()
    {
        // 確認に挙げなかった既にあるファイル (確認のあとに置かれたものなど) は置き換えずに知らせる (#353)
        using var log = new TraceLog();
        var archive = MakeZip(log, "two.zip", ("listed.txt", "NEW"), ("later.txt", "NEW"));
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;
        File.WriteAllText(Path.Combine(destination, "listed.txt"), "OLD");

        var conflicts = ArchiveExtractor.FindConflicts(["listed.txt", "later.txt"], destination, null);
        Assert.Equal(["listed.txt"], conflicts.Select(static c => c.Name));
        File.WriteAllText(Path.Combine(destination, "later.txt"), "PLACED AFTER");

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination,
            ExtractOverwrite.Only(conflicts.Select(static c => c.Target)), null, CancellationToken.None);

        Assert.Equal("NEW", File.ReadAllText(Path.Combine(destination, "listed.txt")));
        Assert.Equal("PLACED AFTER", File.ReadAllText(Path.Combine(destination, "later.txt")));
        Assert.Equal(1, result.Extracted);
        Assert.Equal(["later.txt"], result.SkippedNames);
    }

    [Fact]
    public void 名前の書き方が違っても同じファイルに書く項目は確認に挙がる()
    {
        // 確認と書き出しで書き出し先の決め方が違うと、確認に挙がらずに上書きする (#353)。
        // tar の先頭の ./ はふつうの書庫でも付く
        using var log = new TraceLog();
        string[] names = ["./a.txt", "d/./b.txt", "d/../c.txt", "e.txt.", "f//g.txt", "h.txt "];
        var archive = MakeZip(log, "names.zip", [.. names.Select(static n => (n, "NEW"))]);
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;
        Directory.CreateDirectory(Path.Combine(destination, "d"));
        Directory.CreateDirectory(Path.Combine(destination, "f"));
        string[] existing = ["a.txt", @"d\b.txt", "c.txt", "e.txt", @"f\g.txt", "h.txt"];
        foreach (var file in existing)
        {
            File.WriteAllText(Path.Combine(destination, file), "OLD");
        }

        var conflicts = ArchiveExtractor.FindConflicts(names, destination, null);
        Assert.Equal(names, conflicts.Select(static c => c.Name));

        // 「いいえ」: どれも置き換えない
        var kept = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination,
            ExtractOverwrite.Only([]), null, CancellationToken.None);
        Assert.Equal(names, kept.SkippedNames);
        Assert.All(existing, f => Assert.Equal("OLD", File.ReadAllText(Path.Combine(destination, f))));

        // 「はい」: 挙げたものを置き換える
        var replaced = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination,
            ExtractOverwrite.Only(conflicts.Select(static c => c.Target)), null, CancellationToken.None);
        Assert.Empty(replaced.SkippedNames);
        Assert.All(existing, f => Assert.Equal("NEW", File.ReadAllText(Path.Combine(destination, f))));
    }

    [Fact]
    public void 読めないフォルダーが展開先にあっても確認に挙がる()
    {
        // 展開先を丸ごと数えると、読めないフォルダーで止まり、同じ名前は無いとみなしていた (#353)。
        // 「ドキュメント」の中には、一覧を読むことが禁じられた隠しのジャンクションがある
        using var log = new TraceLog();
        var archive = MakeZip(log, "a.zip", ("a.txt", "NEW"));
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;
        File.WriteAllText(Path.Combine(destination, "a.txt"), "OLD");
        var locked = Directory.CreateDirectory(Path.Combine(destination, "locked"));
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var rule = new System.Security.AccessControl.FileSystemAccessRule(
            me, System.Security.AccessControl.FileSystemRights.ListDirectory, System.Security.AccessControl.AccessControlType.Deny);
        var security = locked.GetAccessControl();
        security.AddAccessRule(rule);
        locked.SetAccessControl(security);
        try
        {
            Assert.ThrowsAny<UnauthorizedAccessException>(() => Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories).ToList());

            var conflicts = ArchiveExtractor.FindConflicts(["a.txt", "locked/x.txt"], destination, null);

            Assert.Equal(["a.txt"], conflicts.Select(static c => c.Name));
        }
        finally
        {
            security.RemoveAccessRule(rule);
            locked.SetAccessControl(security);
        }

        Assert.Equal("OLD", File.ReadAllText(Path.Combine(destination, "a.txt")));
    }

    [Fact]
    public void 同じ名前の項目が2つあれば後ろの項目が置き換える()
    {
        // tar -r で足した tar では後ろが新しい版。この展開で作ったファイルは置き換えてよい (#353)
        using var log = new TraceLog();
        var archive = MakeZip(log, "twice.zip", ("a.txt", "OLD VERSION"), ("a.txt", "NEW VERSION"));
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal(2, result.Extracted);
        Assert.Empty(result.SkippedNames);
        Assert.Equal("NEW VERSION", File.ReadAllText(Path.Combine(destination, "a.txt")));
    }

    [Fact]
    public void 長い名前と短い名前の2項目は後ろの項目が置き換える()
    {
        // 1 つ目はこの展開で作ったファイルなので、短い名前で指していても置き換えてよい (#349、#353)
        using var log = new TraceLog();
        var shortName = ShortNameFor(log);
        var archive = MakeNamesZip(log, shortName, "LONG", breakLong: false);
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal(2, result.Extracted);
        Assert.Empty(result.SkippedNames);
        Assert.Equal("SHORT", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(destination))));
    }

    [Fact]
    public void 濁点などの形だけが違う名前の作ったファイルと既にあるファイルを取り違えない()
    {
        // NTFS は é を 1 文字で書いた名前と、e に記号を重ねた名前を別のファイルとして持つ。
        // 1 つ目で作ったファイルと、2 つ目が指す既にあるファイルを同じとみなすと、「いいえ」と答えたファイルを置き換える (#353)
        using var log = new TraceLog();
        var composed = "café.txt";
        var decomposed = "café.txt";
        // 並べると終わる順で結果が変わるので、~ と数字の名前の項目を入れて 1 つずつ展開させる (#349)
        var archive = MakeZip(log, "forms.zip", (composed, "NEW 1"), (decomposed, "NEW 2"), ("serial~1.txt", "S"));
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;
        var users = Path.Combine(destination, decomposed);
        File.WriteAllText(users, "USER DATA");
        Assert.False(File.Exists(Path.Combine(destination, composed)), "この場所では別のファイルになる");

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal("USER DATA", File.ReadAllText(users));
        Assert.Equal("NEW 1", File.ReadAllText(Path.Combine(destination, composed)));
        Assert.Equal([decomposed], result.SkippedNames);
    }

    [Fact]
    public void 大文字小文字だけが違う2項目は後ろの項目が置き換える()
    {
        // Windows では同じファイル。この展開で作ったファイルなので、後ろが置き換える
        using var log = new TraceLog();
        var archive = MakeZip(log, "case.zip", ("A.txt", "FIRST"), ("a.txt", "SECOND"));
        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, destination,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Empty(result.SkippedNames);
        Assert.Equal("SECOND", File.ReadAllText(Assert.Single(Directory.EnumerateFiles(destination))));
    }

    private static string MakeZip(TraceLog log, string name, params (string Name, string Text)[] entries)
    {
        var archive = log.PathOf(name);
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Create);
        foreach (var (entryName, text) in entries)
        {
            using var writer = new StreamWriter(zip.CreateEntry(entryName).Open());
            writer.Write(text);
        }

        return archive;
    }

    /// <summary>この場所で 8.3 形式の短い名前が作られなければ、テストを飛ばす。</summary>
    private static string ShortNameFor(TraceLog log)
    {
        var probe = log.PathOf("longfilename.txt");
        File.WriteAllText(probe, "probe");
        var shortName = ShortNameOf(probe);
        File.Delete(probe);
        Assert.SkipWhen(shortName is null || string.Equals(shortName, probe, StringComparison.OrdinalIgnoreCase),
            "この場所では 8.3 形式の短い名前が作られない");
        return Path.GetFileName(shortName!);
    }

    /// <summary>長い名前の項目、短い名前の項目の順に入れる。<paramref name="breakLong"/> なら長い名前の項目の CRC を壊す。</summary>
    private static string MakeNamesZip(TraceLog log, string shortName, string longText, bool breakLong)
    {
        var archive = log.PathOf(breakLong ? "broken.zip" : "names.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("longfilename.txt", longText), (shortName, "SHORT") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());
                writer.Write(text);
            }
        }

        if (breakLong)
        {
            // ローカルヘッダー (14 バイト目から) と中央ディレクトリ (16 バイト目から) の CRC を、どちらも同じ違う値にする
            var bytes = File.ReadAllBytes(archive);
            var name = Encoding.ASCII.GetBytes("longfilename.txt");
            for (var i = 0; i + 46 < bytes.Length; i++)
            {
                var (crcAt, nameAt) = BitConverter.ToUInt32(bytes, i) switch
                {
                    0x04034b50 => (14, 30),
                    0x02014b50 => (16, 46),
                    _ => (-1, 0),
                };
                if (crcAt >= 0 && bytes.AsSpan(i + nameAt, name.Length).SequenceEqual(name))
                {
                    bytes[i + crcAt] ^= 0xFF;
                }
            }

            File.WriteAllBytes(archive, bytes);
        }

        return archive;
    }

    /// <summary>取り出しと同じ手順で書く。置き換えなかったら false。</summary>
    private static bool Write(string target, bool overwrite)
    {
        using var file = AtomicFile.CreateForExtraction(target, overwrite, zone: null);
        file.Stream.Write(Content);
        return file.Commit();
    }

    /// <summary>本来の名前に直接作る直前に、同じ名前のものを置く。</summary>
    private static void PlaceAtDirect(TraceLog log, string target, Action place)
    {
        var done = false;
        log.OnEvent = (what, path) =>
        {
            if (!done && what == "direct" && string.Equals(path, target, StringComparison.OrdinalIgnoreCase))
            {
                done = true;
                place();
            }
        };
    }

    private static void AssertNoLeftovers(TraceLog log)
        => Assert.Empty(Directory.EnumerateFiles(log.Root, "~*.tmp", SearchOption.AllDirectories));

    private static string? ReadMark(string path)
    {
        // 書いている途中のファイルは、書き込みと削除の権利で開かれている。共有を広く取って読む
        try
        {
            using var stream = new FileStream(
                path + ":Zone.Identifier", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"(読めない: {ex.GetType().Name} {ex.Message})";
        }
    }

    private static bool TryCreateSymbolicLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static string? ShortNameOf(string path)
    {
        var buffer = new StringBuilder(1024);
        var length = GetShortPathNameW(path, buffer, buffer.Capacity);
        return length == 0 ? null : buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, int bufferLength);
}
