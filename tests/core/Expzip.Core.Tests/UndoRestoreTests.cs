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
