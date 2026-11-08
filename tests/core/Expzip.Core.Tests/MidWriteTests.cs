using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 書いている途中に、ほかのプログラムが同じ名前のファイルを置いたとき (#347)。
/// 展開した方を優先して置き換える。置かれたファイルのアクセス権は引き継がず、書き込み禁止でも置き換える。
/// 読み取り専用・隠し・システムの属性が付いていれば置き換えない。上書きしない指定なら、置かれたファイルを残す。
/// </summary>
[Collection(nameof(TraceCollection))]
public sealed class MidWriteTests
{
    private static readonly byte[] Content = Encoding.ASCII.GetBytes("EXTRACTED");

    [Fact]
    public void 置かれたファイルを展開した方で置き換える()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");

        Assert.True(WriteWithPlaced(target, overwrite: true, () => File.WriteAllText(target, "PLACED")));

        Assert.Equal(Content, File.ReadAllBytes(target));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 上書きしない指定なら置かれたファイルを残す()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");

        Assert.False(WriteWithPlaced(target, overwrite: false, () => File.WriteAllText(target, "PLACED")));

        Assert.Equal("PLACED", File.ReadAllText(target));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 置かれた書き込み禁止のファイルも展開した方で置き換える()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        var me = WindowsIdentity.GetCurrent().User!;

        Assert.True(WriteWithPlaced(target, overwrite: true, () =>
        {
            File.WriteAllText(target, "PLACED");
            var rules = new FileInfo(target).GetAccessControl();
            rules.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.WriteData, AccessControlType.Deny));
            new FileInfo(target).SetAccessControl(rules);
        }));

        Assert.Equal(Content, File.ReadAllBytes(target));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 置かれたファイルのアクセス権は引き継がない()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");
        var plain = log.PathOf("plain.txt");
        File.WriteAllText(plain, "plain");
        var folderRules = Sddl(plain);
        string? placedRules = null;

        Assert.True(WriteWithPlaced(target, overwrite: true, () =>
        {
            File.WriteAllText(target, "PLACED");
            var rules = new FileInfo(target).GetAccessControl();
            rules.SetAccessRuleProtection(isProtected: true, preserveInheritance: true);
            rules.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            new FileInfo(target).SetAccessControl(rules);
            placedRules = Sddl(target);
        }));

        Assert.NotEqual(folderRules, placedRules);
        Assert.Equal(folderRules, Sddl(target));
    }

    [Theory]
    [InlineData(FileAttributes.ReadOnly)]
    [InlineData(FileAttributes.Hidden)]
    [InlineData(FileAttributes.System)]
    public void 置かれた保護属性のファイルは置き換えない(FileAttributes attribute)
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");

        Assert.Throws<UnauthorizedAccessException>(() => WriteWithPlaced(target, overwrite: true, () =>
        {
            File.WriteAllText(target, "PLACED");
            File.SetAttributes(target, attribute);
        }));

        Assert.Equal("PLACED", File.ReadAllText(target));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 置かれたフォルダーは置き換えない()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.txt");

        Assert.Throws<UnauthorizedAccessException>(() => WriteWithPlaced(target, overwrite: true, () =>
        {
            Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target, "inside.txt"), "INSIDE");
        }));

        Assert.Equal("INSIDE", File.ReadAllText(Path.Combine(target, "inside.txt")));
        AssertNoLeftovers(log);
    }

    [Fact]
    public void 置かれたジャンクションの先には書かない()
    {
        using var log = new TraceLog();
        var outside = Directory.CreateDirectory(log.PathOf("outside")).FullName;
        var destination = Directory.CreateDirectory(log.PathOf("dest")).FullName;
        var target = Path.Combine(destination, "a.txt");
        var created = false;

        var error = Record.Exception(() => WriteWithPlaced(target, overwrite: true, () => created = Junction.TryCreate(target, outside)));

        Assert.SkipUnless(created, "ジャンクションを作れなかった");
        Assert.IsType<UnauthorizedAccessException>(error);
        Assert.Empty(Directory.EnumerateFileSystemEntries(outside));
    }

    /// <summary>何も無い名前に書き始め、書き終える前に <paramref name="place"/> で同じ名前のものを置いてから確定する。</summary>
    private static bool WriteWithPlaced(string target, bool overwrite, Action place)
    {
        using var file = AtomicFile.Create(target, overwrite);
        file.Stream.Write(Content);
        place();
        return file.Commit();
    }

    private static string Sddl(string path)
        => new FileInfo(path).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);

    private static void AssertNoLeftovers(TraceLog log)
        => Assert.Empty(Directory.EnumerateFiles(log.Root, "~*.tmp", SearchOption.AllDirectories));
}
