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
    public void インターネットから来た書庫では中身より先に印が付いている()
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
    public void 置き換えの道でも中身より先に印が付いている()
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

        // 置き換える前に、印と中身を書き切る (#347)
        Assert.StartsWith("direct fallback marked markflush flush move", log.Names);
        // 印を付けたのは一時ファイル。前からあるファイルには、置き換えるまで触らない
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

    [Fact]
    public void 長い名前と短い名前の2項目を並列で展開しても書きかけが残らない()
    {
        using var log = new TraceLog();
        var probe = log.PathOf("longfilename.txt");
        File.WriteAllText(probe, "probe");
        var shortName = ShortNameOf(probe);
        File.Delete(probe);
        Assert.SkipWhen(shortName is null || string.Equals(shortName, probe, StringComparison.OrdinalIgnoreCase),
            "この場所では 8.3 形式の短い名前が作られない");

        var archive = log.PathOf("names.zip");
        var big = new string('L', 4 * 1024 * 1024);
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var (name, text) in new[] { ("longfilename.txt", big), (Path.GetFileName(shortName!), "SHORT") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name, CompressionLevel.NoCompression).Open());
                writer.Write(text);
            }
        }

        var destination = Directory.CreateDirectory(log.PathOf("out")).FullName;
        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, null, destination, false, null, CancellationToken.None);

        // 2 つは同じファイルなので、1 つだけが取り出され、もう 1 つは飛ばすか失敗になる。並列の順番で、どちらが残るかは変わる
        Assert.Equal(1, result.Extracted);
        Assert.Equal(1, result.Skipped + result.Failed.Count);
        var file = Assert.Single(Directory.EnumerateFiles(destination));
        var written = File.ReadAllText(file);
        Assert.True(written == big || written == "SHORT", $"{Path.GetFileName(file)} が書きかけ ({written.Length} 文字)");

        Assert.Empty(Directory.EnumerateFiles(destination, "~*.tmp"));
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

    private static string? ShortNameOf(string path)
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
