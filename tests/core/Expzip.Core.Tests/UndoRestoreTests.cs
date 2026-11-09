using System.Runtime.InteropServices;
using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 元に戻すときは、書庫の隣の作業用の名前から名前を変えて置き換える (#352)。
/// 取っておいた書庫から直接名前を変えると、別のドライブではコピーになり、途中で止まると書庫が壊れる。
/// </summary>
[Collection(nameof(TraceCollection))]
public sealed class UndoRestoreTests
{
    [Fact]
    public void 同じドライブではハードリンクから置き換える()
    {
        // ハードリンクの引数の順を誤ると黙ってコピーになり、遅くなるだけで気づけない
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        var (history, store) = DeleteOne(log, archive);

        var links = new List<uint>();
        log.OnEvent = (what, path) =>
        {
            if (what == "move" && SameName(path, archive))
            {
                links.AddRange(WorkFiles(archive).Select(LinkCount));
            }
        };

        Assert.Equal(UndoOutcome.Restored, history.Undo(archive, out _));

        Assert.Equal([2u], links);
        Assert.Contains("b.txt", TestArchives.Names(archive));
        Assert.Empty(Directory.EnumerateFiles(store));
        Assert.Empty(WorkFiles(archive));
    }

    [Fact]
    public void ジャンクションで別のドライブにある書庫も隣から置き換える()
    {
        // パスの先頭は一時フォルダーと同じでも、別のドライブにある
        using var log = new TraceLog();
        using var other = OtherDrive.Create();
        var folder = log.PathOf("other");
        Assert.SkipUnless(Junction.TryCreate(folder, other.Root), "ジャンクションを作れない");
        Assert.Equal(Path.GetPathRoot(log.Root), Path.GetPathRoot(folder));

        var archive = Path.Combine(folder, "fast.zip");
        File.Move(TestArchives.Make(log, sharpZipLib: false), archive);
        File.WriteAllText(archive + MarkOfTheWeb.StreamSuffix, MarkOfTheWeb.Internet);
        var (history, store) = DeleteOne(log, archive);

        var besideAtMove = 0;
        log.OnEvent = (what, path) =>
        {
            if (what == "move" && SameName(path, archive))
            {
                besideAtMove = WorkFiles(archive).Count;
            }
        };

        Assert.Equal(UndoOutcome.Restored, history.Undo(archive, out _));

        Assert.Equal(1, besideAtMove);
        Assert.Contains("b.txt", TestArchives.Names(archive));
        Assert.Equal(MarkOfTheWeb.Internet, MarkOfTheWeb.TryRead(archive));
        Assert.Empty(Directory.EnumerateFiles(store));
        Assert.Empty(WorkFiles(archive));
    }

    [Fact]
    public void 別のドライブへはコピーせずに断る()
    {
        // 作業用ファイルを置き換える先と別のドライブに置く呼び出し元ができても、黙ってコピーにしない
        using var log = new TraceLog();
        using var other = OtherDrive.Create();
        var folder = log.PathOf("other");
        Assert.SkipUnless(Junction.TryCreate(folder, other.Root), "ジャンクションを作れない");
        var target = Path.Combine(folder, "a.zip");
        var work = log.PathOf("work.tmp");
        File.WriteAllText(target, "OLD");
        File.WriteAllText(work, "NEW");

        var error = Assert.ThrowsAny<IOException>(() => AtomicFile.Replace(work, target));

        // 理由の文に、アプリを使う人の知らない作業用の名前を入れない
        Assert.DoesNotContain(work, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.Equal("NEW", File.ReadAllText(work));
    }

    [Fact]
    public void 置き換えられなければ書庫も取っておいた書庫も残る()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        var (history, store) = DeleteOne(log, archive);
        var edited = File.ReadAllBytes(archive);

        using (new FileStream(archive, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.NotNull(Record.Exception(() => history.Undo(archive, out _)));
        }

        Assert.Equal(edited, File.ReadAllBytes(archive));
        Assert.Single(Directory.EnumerateFiles(store));
        Assert.Empty(WorkFiles(archive));

        // 閉じたあとなら戻せる
        Assert.Equal(UndoOutcome.Restored, history.Undo(archive, out _));
        Assert.Contains("b.txt", TestArchives.Names(archive));
    }

    [Fact]
    public void 置き場を片付けても書庫の読み取り専用は外れない()
    {
        // 取っておいた書庫は、書き換えるまでは書庫と同じ実体のハードリンク。
        // 属性は実体ごとに付くので、片付けで読み取り専用を外すと、書庫のものも外れる (#356)
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        File.SetAttributes(archive, File.GetAttributes(archive) | FileAttributes.ReadOnly);
        var workspace = Directory.CreateDirectory(log.PathOf("workspace")).FullName;
        var history = new UndoHistory();

        var pending = history.Begin(archive, UndoKind.Delete, Path.Combine(workspace, "undo"));
        Assert.NotNull(pending);
        Assert.ThrowsAny<Exception>(() =>
            ZipArchiveWriter.Delete(archive, new HashSet<string> { "b.txt" }, [], CancellationToken.None));
        history.Complete(pending);

        Assert.True(TempWorkspace.TryDeleteTree(workspace));

        Assert.True(File.GetAttributes(archive).HasFlag(FileAttributes.ReadOnly));
        Assert.Contains("b.txt", TestArchives.Names(archive));
        File.SetAttributes(archive, FileAttributes.Normal);
    }

    [Fact]
    public void 読み取り専用のファイルが入った置き場も片付ける()
    {
        // 書庫の中身に読み取り専用のファイルがあると、取り出したものも読み取り専用になる
        using var log = new TraceLog();
        var workspace = Directory.CreateDirectory(log.PathOf("workspace")).FullName;
        var file = Path.Combine(Directory.CreateDirectory(Path.Combine(workspace, "a")).FullName, "x.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        Assert.True(TempWorkspace.TryDeleteTree(workspace));
        Assert.False(Directory.Exists(workspace));
    }

    [Fact]
    public void 片付けは置き場の中のジャンクションの先に入らない()
    {
        // ほかのプログラムが置き場にジャンクションを作っても、その先の外のファイルは消さず、属性も変えない
        using var log = new TraceLog();
        var outside = Directory.CreateDirectory(log.PathOf("outside")).FullName;
        var kept = Path.Combine(outside, "keep.txt");
        File.WriteAllText(kept, "keep");
        File.SetAttributes(kept, FileAttributes.ReadOnly);
        var workspace = Directory.CreateDirectory(log.PathOf("workspace")).FullName;
        var link = Path.Combine(workspace, "link");
        Assert.SkipUnless(Junction.TryCreate(link, outside), "ジャンクションを作れない");

        Assert.True(TempWorkspace.TryDeleteTree(workspace));

        Assert.False(Directory.Exists(workspace));
        Assert.True(File.Exists(kept));
        Assert.True(File.GetAttributes(kept).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(kept, FileAttributes.Normal);
    }

    [Fact]
    public void 消せなかった取っておいた書庫は属性を変えずに残す()
    {
        // ほかから削除を許さずに開かれていると消せない。読み取り専用を外さずに残し、次の起動に回す
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        var workspace = Directory.CreateDirectory(log.PathOf("workspace")).FullName;
        var store = Path.Combine(workspace, "store.zip");
        Assert.True(UndoHistory.HardLink.TryCreate(store, archive));
        File.SetAttributes(archive, File.GetAttributes(archive) | FileAttributes.ReadOnly);

        using (new FileStream(store, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.False(TempWorkspace.TryDeleteTree(workspace));
            Assert.True(File.GetAttributes(archive).HasFlag(FileAttributes.ReadOnly));
            Assert.True(File.Exists(store));
        }

        Assert.True(TempWorkspace.TryDeleteTree(workspace));
        Assert.True(File.GetAttributes(archive).HasFlag(FileAttributes.ReadOnly));
        Assert.Contains("b.txt", TestArchives.Names(archive));
        File.SetAttributes(archive, FileAttributes.Normal);
    }

    // 読み取り専用を気にせずに消す指定が使えない古い Windows の道。この PC では差し替えないと通らない
    [Fact]
    public void 古いWindowsの道でも書庫と同じ実体のものは属性を変えずに残す()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        var store = log.PathOf("store.zip");
        Assert.True(UndoHistory.HardLink.TryCreate(store, archive));
        File.SetAttributes(archive, File.GetAttributes(archive) | FileAttributes.ReadOnly);

        TempWorkspace.ExtendedDeleteDisabled = true;
        try
        {
            Assert.False(TempWorkspace.TryDeleteFile(store));
        }
        finally
        {
            TempWorkspace.ExtendedDeleteDisabled = false;
        }

        Assert.True(File.Exists(store));
        Assert.True(File.GetAttributes(archive).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(archive, FileAttributes.Normal);
    }

    [Theory]
    [InlineData(FileAttributes.ReadOnly)]
    [InlineData(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System)]
    [InlineData(FileAttributes.Normal)]
    public void 古いWindowsの道でもほかに名前の無いファイルは消す(FileAttributes attributes)
    {
        using var log = new TraceLog();
        var file = log.PathOf("x.txt");
        File.WriteAllText(file, "x");
        File.SetAttributes(file, attributes);

        TempWorkspace.ExtendedDeleteDisabled = true;
        try
        {
            Assert.True(TempWorkspace.TryDeleteFile(file));
        }
        finally
        {
            TempWorkspace.ExtendedDeleteDisabled = false;
        }

        Assert.False(File.Exists(file));
    }

    /// <summary>取っておいてから b.txt を消す。</summary>
    private static (UndoHistory History, string Store) DeleteOne(TraceLog log, string archive)
    {
        var store = Directory.CreateDirectory(log.PathOf("undo")).FullName;
        var history = new UndoHistory();
        var pending = history.Begin(archive, UndoKind.Delete, store);
        Assert.NotNull(pending);
        ZipArchiveWriter.Delete(archive, new HashSet<string> { "b.txt" }, [], CancellationToken.None);
        history.Complete(pending);
        Assert.DoesNotContain("b.txt", TestArchives.Names(archive));
        log.Clear();
        return (history, store);
    }

    private static List<string> WorkFiles(string archive)
        => [.. Directory.EnumerateFiles(Path.GetDirectoryName(archive)!, "~*.tmp")];

    private static bool SameName(string a, string b)
        => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static uint LinkCount(string path)
    {
        using var handle = File.OpenHandle(path);
        Assert.True(GetFileInformationByHandle(handle, out var info));
        return info.Links;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, out FileInfoByHandle info);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInfoByHandle
    {
        public uint Attributes;
        public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint VolumeSerialNumber;
        public uint SizeHigh, SizeLow;
        public uint Links;
        public uint IndexHigh, IndexLow;
    }

    /// <summary>一時フォルダーと別の、固定の NTFS ドライブに作ったフォルダー。無ければテストを飛ばす。</summary>
    private sealed class OtherDrive : IDisposable
    {
        private OtherDrive(string root) => Root = root;

        public string Root { get; }

        public static OtherDrive Create()
        {
            var temp = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()));
            var drive = DriveInfo.GetDrives().FirstOrDefault(d =>
                d is { DriveType: DriveType.Fixed, IsReady: true }
                && d.DriveFormat == "NTFS"
                && d.AvailableFreeSpace > 100L * 1024 * 1024
                && !string.Equals(d.RootDirectory.FullName, temp, StringComparison.OrdinalIgnoreCase));
            Assert.SkipWhen(drive is null, "一時フォルダーと別の NTFS のドライブが無い");

            var root = Path.Combine(drive!.RootDirectory.FullName, "ExpzipCoreTests-" + Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(root);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Assert.Skip($"{drive.Name} にフォルダーを作れない");
            }

            return new OtherDrive(root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 消せないものは残るだけ
            }
        }
    }
}
