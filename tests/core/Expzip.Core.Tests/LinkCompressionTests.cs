using System.Diagnostics;
using System.Text;
using Expzip.Archives;
using ICSharpCode.SharpZipLib.Zip;

namespace Expzip.Tests;

/// <summary>
/// フォルダーを圧縮するときに、中にあるシンボリックリンクとジャンクションの中には入らず、リンクとして書庫に入れる (#366)。
/// </summary>
/// <remarks>
/// ジャンクションは管理者でなくても作れるので、いつも確かめる。シンボリックリンクは、開発者モードか管理者でないと
/// 作れないので、作れない PC では飛ばす。
/// </remarks>
public sealed class LinkCompressionTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose()
    {
        // リンクの中には入らずに消す。rmdir /s はジャンクションとシンボリックリンクをたどらず、リンクそのものを消す
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c rmdir /s /q \"{_root}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        })!;
        process.WaitForExit();
    }

    [Fact]
    public void 自分の親を指すジャンクションがあっても落ちずにリンクとして入る()
    {
        var source = Folder("source");
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        Junction(Path.Combine(source, "sub", "loop"), source);

        var result = Compress([source], out var zip);

        Assert.Empty(result.Failed);
        using var archive = new ZipFile(zip);
        var loop = Entry(archive, "source/sub/loop");
        Assert.True(ZipLinks.IsLink(loop));
        Assert.True(ZipLinks.PointsToFolder(loop));
        // 選んだフォルダーの中を指すので、置き場からの相対パスに直す。ユーザー名が書庫に入らない
        Assert.Equal("..", Content(archive, loop));
        Assert.DoesNotContain(archive.Cast<ZipEntry>(), e => e.Name.StartsWith("source/sub/loop/", StringComparison.Ordinal));
        Assert.Single(result.Links);
        Assert.False(result.Links[0].Outside);
    }

    [Fact]
    public void 外を指すジャンクションは中身を読まずに指す先のパスのまま入れて知らせる()
    {
        var outside = Folder("outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET");
        var source = Folder("source");
        Junction(Path.Combine(source, "docs"), outside);

        var result = Compress([source], out var zip);

        using var archive = new ZipFile(zip);
        var docs = Entry(archive, "source/docs");
        Assert.True(ZipLinks.IsLink(docs));
        Assert.Equal(outside.Replace('\\', '/'), Content(archive, docs));
        Assert.DoesNotContain(archive.Cast<ZipEntry>(), e => e.Name.Contains("secret", StringComparison.Ordinal));
        Assert.True(Assert.Single(result.Links).Outside);
    }

    [Fact]
    public void Windowsが置いた隠しとシステムのジャンクションは入れない()
    {
        var source = Folder("source");
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        var music = Path.Combine(source, "My Music");
        Junction(music, Folder("music"));
        File.SetAttributes(music, File.GetAttributes(music) | FileAttributes.Hidden | FileAttributes.System);

        var result = Compress([source], out var zip);

        using var archive = new ZipFile(zip);
        Assert.Null(archive.GetEntry("source/My Music"));
        Assert.NotNull(archive.GetEntry("source/a.txt"));
        Assert.Empty(result.Links);
        Assert.Empty(result.Failed);
    }

    [Fact]
    public void 直接選んだジャンクションは指す先の中身を入れる()
    {
        var real = Folder("real");
        File.WriteAllText(Path.Combine(real, "a.txt"), "A");
        Junction(Path.Combine(real, "back"), real);
        var chosen = Path.Combine(_root, "chosen");
        Junction(chosen, real);

        var result = Compress([chosen], out var zip);

        using var archive = new ZipFile(zip);
        Assert.Equal("A", Content(archive, Entry(archive, "chosen/a.txt")));
        // 中にあるリンクはリンクとして入るので、回り続けない
        Assert.True(ZipLinks.IsLink(Entry(archive, "chosen/back")));
        Assert.Single(result.Links);
    }

    [Fact]
    public void ジャンクションを通って選んだフォルダーの中に作る書庫そのものは入れない()
    {
        var real = Folder("real");
        File.WriteAllText(Path.Combine(real, "a.txt"), "A");
        var chosen = Path.Combine(_root, "chosen");
        Junction(chosen, real);

        // 一時的な名前の書庫を、実際のフォルダーの側の名前で作る
        var temporary = Path.Combine(real, "new.zip.tmp");
        var result = ZipArchiveWriter.CreateWith(
            temporary, [chosen], string.Empty, replaceExisting: true, progress: null, CancellationToken.None, [temporary]);

        Assert.Empty(result.Failed);
        using var archive = new ZipFile(temporary);
        Assert.Null(archive.GetEntry("chosen/new.zip.tmp"));
        Assert.NotNull(archive.GetEntry("chosen/a.txt"));
    }

    [Fact]
    public void ファイルを指すシンボリックリンクは相対パスのまま入り中身を読まない()
    {
        var source = Folder("source");
        File.WriteAllText(Path.Combine(source, "real.txt"), "REAL");
        Assert.SkipUnless(TrySymlink(Path.Combine(source, "alias.txt"), "real.txt"), "この PC ではシンボリックリンクを作れない");

        var outside = Path.Combine(Folder("outside"), "secret.txt");
        File.WriteAllText(outside, "SECRET");
        Assert.SkipUnless(TrySymlink(Path.Combine(source, "leak.txt"), outside), "この PC ではシンボリックリンクを作れない");

        var result = Compress([source], out var zip);

        using var archive = new ZipFile(zip);
        var alias = Entry(archive, "source/alias.txt");
        Assert.True(ZipLinks.IsLink(alias));
        Assert.False(ZipLinks.PointsToFolder(alias));
        Assert.Equal("real.txt", Content(archive, alias));

        var leak = Entry(archive, "source/leak.txt");
        Assert.True(ZipLinks.IsLink(leak));
        Assert.Equal(outside.Replace('\\', '/'), Content(archive, leak));
        Assert.Equal(2, result.Links.Count);
        Assert.Single(result.Links, static l => l.Outside);
    }

    [Fact]
    public void 書庫のフォルダーと同じ名前のリンクは足さない()
    {
        // x/docs/a.txt がある書庫へ x/docs というリンクを足すと、展開したときにリンクを通って外に書かれる書庫になる
        File.WriteAllText(Path.Combine(Folder("one", "x", "docs"), "a.txt"), "A");
        Compress([Path.Combine(_root, "one", "x")], out var zip);

        Junction(Path.Combine(Folder("two", "x"), "docs"), Folder("elsewhere"));
        var result = ZipArchiveWriter.Add(
            zip, [Path.Combine(_root, "two", "x")], string.Empty, replaceExisting: true, progress: null, CancellationToken.None);

        Assert.Single(result.Failed);
        using var archive = new ZipFile(zip);
        Assert.False(ZipLinks.IsLink(Entry(archive, "x/docs/a.txt")));
        Assert.DoesNotContain(archive.Cast<ZipEntry>(), ZipLinks.IsLink);
    }

    [Fact]
    public void 書庫の中のリンクの中へは足さない()
    {
        var source = Folder("source");
        Junction(Path.Combine(source, "docs"), Folder("elsewhere"));
        Compress([source], out var zip);

        var file = Path.Combine(_root, "b.txt");
        File.WriteAllText(file, "B");
        var result = ZipArchiveWriter.Add(zip, [file], "source/docs", replaceExisting: true, progress: null, CancellationToken.None);

        Assert.Single(result.Failed);
        using var archive = new ZipFile(zip);
        Assert.Null(archive.GetEntry("source/docs/b.txt"));
    }

    [Fact]
    public void 書庫の中のリンクの中にフォルダーを作らない()
    {
        var source = Folder("source");
        Junction(Path.Combine(source, "docs"), Folder("elsewhere"));
        Compress([source], out var zip);

        Assert.Throws<LinkOverlapException>(() => ZipArchiveWriter.CreateFolder(zip, "source/docs/new"));
    }

    [Fact]
    public void リンクと同じ名前の空のフォルダーや大文字小文字だけが違う名前は足さない()
    {
        var source = Folder("source");
        Junction(Path.Combine(source, "docs"), Folder("elsewhere"));
        Compress([source], out var zip);

        // 空のフォルダー source/docs/ と、ファイル source/DOCS
        Folder("again", "source", "docs");
        var other = Folder("other", "source");
        File.WriteAllText(Path.Combine(other, "DOCS"), "X");
        var empty = ZipArchiveWriter.Add(
            zip, [Path.Combine(_root, "again", "source")], string.Empty, replaceExisting: true, progress: null, CancellationToken.None);
        var cased = ZipArchiveWriter.Add(zip, [other], string.Empty, replaceExisting: true, progress: null, CancellationToken.None);

        Assert.Single(empty.Failed);
        Assert.Single(cased.Failed);
        using var archive = new ZipFile(zip);
        var names = string.Join(", ", archive.Cast<ZipEntry>().Select(static e => e.Name));
        Assert.DoesNotContain(archive.Cast<ZipEntry>(), e => e.Name == "source/docs/");
        Assert.True(archive.Cast<ZipEntry>().All(e => e.Name != "source/DOCS"), names);
    }

    [Fact]
    public void もともと重なりのある書庫でも関係の無いところは編集できる()
    {
        // ほかのツールで作った、リンク docs とフォルダー docs/ が並ぶ書庫
        var zip = Path.Combine(_root, "overlap.zip");
        using (var output = new ZipOutputStream(File.Create(zip)))
        {
            var link = new ZipEntry("docs") { CompressionMethod = CompressionMethod.Stored };
            ZipLinks.Mark(link, pointsToFolder: true);
            output.PutNextEntry(link);
            output.Write("x"u8);
            output.CloseEntry();
            output.PutNextEntry(new ZipEntry("docs/"));
            output.CloseEntry();
            output.PutNextEntry(new ZipEntry("other.txt"));
            output.Write("O"u8);
            output.CloseEntry();
        }

        Assert.True(ZipArchiveWriter.CreateFolder(zip, "new"));
        ZipArchiveWriter.Rename(zip, "other.txt", "renamed.txt", isFolder: false, progress: null, CancellationToken.None);

        using var archive = new ZipFile(zip);
        Assert.NotNull(archive.GetEntry("renamed.txt"));
    }

    [Fact]
    public void たどっている間に中止すると中止したと返す()
    {
        var source = Folder("source");
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var zip = Path.Combine(_root, "cancelled.zip");
        var result = ZipArchiveWriter.CreateWith(zip, [source], string.Empty, replaceExisting: true, progress: null, cancellation.Token);

        Assert.True(result.Cancelled);
    }

    [Fact]
    public void ドライブの根を選んだ場所としても中のものは中と分かる()
    {
        Assert.True(DiskLinks.IsSameOrInside(@"E:\a\b.txt", @"E:\"));
        Assert.True(DiskLinks.IsSameOrInside(@"E:\", @"E:\"));
        Assert.True(DiskLinks.IsSameOrInside(@"C:\s\a.txt", @"C:\s"));
        Assert.False(DiskLinks.IsSameOrInside(@"C:\sx\a.txt", @"C:\s"));
    }

    [Fact]
    public void 名前を変えてもリンクのまま残る()
    {
        var source = Folder("source");
        Junction(Path.Combine(source, "docs"), Folder("elsewhere"));
        Compress([source], out var zip);

        ZipArchiveWriter.Rename(zip, "source/docs", "source/papers", isFolder: false, progress: null, CancellationToken.None);

        using var archive = new ZipFile(zip);
        var papers = Entry(archive, "source/papers");
        Assert.True(ZipLinks.IsLink(papers));
        Assert.True(ZipLinks.PointsToFolder(papers));
    }

    [Fact]
    public void パスワード付きの書庫でもリンクの印が付き名前を変えても残る()
    {
        var source = Folder("source");
        Junction(Path.Combine(source, "docs"), Folder("elsewhere"));
        var zip = Path.Combine(_root, "locked.zip");
        ZipArchiveWriter.CreateEmpty(zip);

        var result = ZipEncryptedWriter.Add(zip, [source], string.Empty, replaceExisting: true, "pw", progress: null, CancellationToken.None);
        Assert.Single(result.Links);

        ZipEncryptedWriter.Move(zip, [new PathChange("source/docs", "source/papers", false)], "pw", progress: null, CancellationToken.None);

        using var archive = new ZipFile(zip) { Password = "pw" };
        var papers = Entry(archive, "source/papers");
        Assert.True(ZipLinks.IsLink(papers));
        Assert.True(papers.IsCrypted || papers.AESKeySize > 0);
        Assert.Equal(Path.Combine(_root, "elsewhere").Replace('\\', '/'), Content(archive, papers));
    }

    [Fact]
    public void 一つずつ圧縮する道でもリンクの印が同じになる()
    {
        // UTF-8 の印が無く、UTF-8 として読めない名前 (古い日本語の書庫) がある書庫は、並列の道を使わずに 1 つずつ圧縮する
        var zip = Path.Combine(_root, "slow.zip");
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        using (var output = new ZipOutputStream(File.Create(zip), StringCodec.FromCodePage(932)))
        {
            output.PutNextEntry(new ZipEntry("日本語.txt") { IsUnicodeText = false });
            output.Write("OLD"u8);
            output.CloseEntry();
        }

        var source = Folder("source");
        Junction(Path.Combine(source, "docs"), source);

        var result = ZipArchiveWriter.Add(zip, [source], string.Empty, replaceExisting: true, progress: null, CancellationToken.None);

        Assert.Single(result.Links);
        using var archive = new ZipFile(zip);
        var docs = Entry(archive, "source/docs");
        Assert.True(ZipLinks.IsLink(docs));
        Assert.True(ZipLinks.PointsToFolder(docs));
        // source/docs から source を指すので、置き場のフォルダーそのもの
        Assert.Equal(".", Content(archive, docs));
    }

    [Fact]
    public void 深すぎるフォルダーはたどる途中で止める()
    {
        var deep = Folder("deep");
        var current = deep;
        for (var i = 0; i < ArchiveLimits.MaxFolderDepth + 5; i++)
        {
            current = Directory.CreateDirectory(Path.Combine(current, "d")).FullName;
        }

        var zip = Path.Combine(_root, "deep.zip");
        Assert.Throws<ArchiveTooLargeException>(() => ZipArchiveWriter.CreateWith(
            zip, [deep], string.Empty, replaceExisting: true, progress: null, CancellationToken.None));
    }

    [Fact]
    public void 読む直前に外のファイルへ差し替えられていたら読まない()
    {
        // 計画を作ったあとで、フォルダーがジャンクションに差し替えられた場合と同じ形
        var source = Folder("source");
        var outside = Folder("outside");
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "SECRET");
        Junction(Path.Combine(source, "swapped"), outside);

        var scope = new ReadScope(DiskLinks.FinalPathOf(source)!, ByGuid: false);

        Assert.Throws<FileChangedWhileReadingException>(() => scope.Open(Path.Combine(source, "swapped", "secret.txt")));

        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        using var stream = scope.Open(Path.Combine(source, "a.txt"));
        Assert.Equal(1, stream.Length);
    }

    [Fact]
    public void 一覧でリンクの数を数える()
    {
        var source = Folder("source");
        File.WriteAllText(Path.Combine(source, "a.txt"), "A");
        Junction(Path.Combine(source, "one"), source);
        Junction(Path.Combine(source, "two"), source);
        Compress([source], out var zip);

        var contents = ZipArchiveReader.Open(zip, NameReading.Default);

        Assert.Equal(2, contents.LinkCount);
    }

    [Fact]
    public void リンク先の形がおかしいものは入れられないとする()
    {
        // ボリュームの GUID やデバイスの名前は、別の PC では意味が無いので入れない
        Assert.Null(ZipLinks.ForArchive(@"C:\s\l", @"\??\Volume{00000000-0000-0000-0000-000000000000}\", false, true, [@"C:\s"]));
        Assert.Null(ZipLinks.ForArchive(@"C:\s\l", @"\\.\PhysicalDrive0", false, false, [@"C:\s"]));
        Assert.Null(ZipLinks.ForArchive(@"C:\s\l", "a\u0001b", true, false, [@"C:\s"]));
        Assert.Null(ZipLinks.ForArchive(@"C:\s\l", @"\Windows", true, false, [@"C:\s"]));

        Assert.Equal("//server/share/x", ZipLinks.ForArchive(@"C:\s\l", @"\??\UNC\server\share\x", false, true, [@"C:\s"])!.Target);
        Assert.Equal("sub/x", ZipLinks.ForArchive(@"C:\s\l", @"\??\C:\s\sub\x", false, false, [@"C:\s"])!.Target);
        Assert.True(ZipLinks.ForArchive(@"C:\s\l", @"..\..\x", true, false, [@"C:\s"])!.Outside);
    }

    private AddResult Compress(IReadOnlyList<string> sources, out string zip)
    {
        zip = Path.Combine(_root, Guid.NewGuid().ToString("N") + ".zip");
        return ZipArchiveWriter.CreateWith(zip, sources, string.Empty, replaceExisting: true, progress: null, CancellationToken.None);
    }

    private string Folder(params string[] names)
        => Directory.CreateDirectory(Path.Combine([_root, .. names])).FullName;

    private static ZipEntry Entry(ZipFile archive, string name)
        => archive.GetEntry(name) ?? throw new Xunit.Sdk.XunitException($"{name} が書庫に無い");

    private static string Content(ZipFile archive, ZipEntry entry)
    {
        using var stream = archive.GetInputStream(entry);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>ジャンクションを作る。管理者でなくても作れる。</summary>
    private static void Junction(string link, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.True(process.ExitCode == 0 && Directory.Exists(link), $"ジャンクションを作れない: {link}");
    }

    private static bool TrySymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
