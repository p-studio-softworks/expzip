using System.IO.Compression;
using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 既にあるファイルは、ディスクに書き切ってから置き換える。作ったばかりのものは待たない (#327)。
/// </summary>
[Collection(nameof(TraceCollection))]
public sealed class ReplaceTests
{
    [Fact]
    public void Replace_書き切ってから置き換える()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.zip");
        var work = log.PathOf("work.tmp");
        File.WriteAllText(target, "OLD");
        File.WriteAllText(work, "NEW");

        AtomicFile.Replace(work, target);

        Assert.Equal("flush move", log.Names);
        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void Replace_置き換える先が無ければ待たない()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.zip");
        var work = log.PathOf("work.tmp");
        File.WriteAllText(work, "NEW");

        AtomicFile.Replace(work, target);

        Assert.Equal("move", log.Names);
    }

    [Fact]
    public void ReplaceFresh_待たない()
    {
        using var log = new TraceLog();
        var target = log.PathOf("a.zip");
        var work = log.PathOf("work.tmp");
        File.WriteAllText(target, "EMPTY");
        File.WriteAllText(work, "NEW");

        AtomicFile.ReplaceFresh(work, target);

        Assert.Equal("move", log.Names);
    }

    [Fact]
    public void CreateWith_作ったばかりの書庫は待たない()
    {
        using var log = new TraceLog();
        var source = log.PathOf("src.txt");
        File.WriteAllText(source, "hello");
        var archive = log.PathOf("new.zip");

        var result = ZipArchiveWriter.CreateWith(archive, [source], "", false, null, CancellationToken.None);

        Assert.Equal(1, result.Added);
        Assert.DoesNotContain("flush", log.Names);
        Assert.Contains("move", log.Names);
        Assert.Equal(["src.txt"], TestArchives.Names(archive));
    }

    [Fact]
    public void CreateWith_既にある名前なら変えずに断る()
    {
        using var log = new TraceLog();
        var source = log.PathOf("src.txt");
        File.WriteAllText(source, "hello");
        var archive = log.PathOf("taken.zip");
        File.WriteAllText(archive, "USER DATA");

        Assert.ThrowsAny<IOException>(() =>
            ZipArchiveWriter.CreateWith(archive, [source], "", false, null, CancellationToken.None));

        Assert.Equal("USER DATA", File.ReadAllText(archive));
        Assert.Empty(log.Events);
    }
}

/// <summary>
/// 書庫の編集は、どれも書き切ってから元の書庫を置き換える (#327)。追加は並列の道と SharpZipLib の道の両方で。
/// 新しい書庫を SharpZipLib の道で作る場合 (WorkFileStorage の archiveIsNew) は、そこへ通す書庫を作れないので確かめていない。
/// </summary>
[Collection(nameof(TraceCollection))]
public sealed class EditTests
{
    public static TheoryData<bool> Paths => [false, true];

    [Theory]
    [MemberData(nameof(Paths))]
    public void 追加(bool sharpZipLib)
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib);
        var source = log.PathOf("added.txt");
        File.WriteAllText(source, "added");
        if (sharpZipLib)
        {
            // 並列の道が引き受けないことを先に確かめる。引き受けないときは何も書かずに戻る
            Assert.Null(ZipParallelWriter.TryAdd(
                archive, ZipArchiveWriter.BuildPlan([source], ""), false, null, CancellationToken.None, archiveIsNew: false));
        }

        log.Clear();

        var result = ZipArchiveWriter.Add(archive, [source], "", false, null, CancellationToken.None);

        Assert.Equal(1, result.Added);
        AssertFlushedBeforeReplacing(log, archive);
        Assert.Contains("added.txt", TestArchives.Names(archive));
    }

    // SharpZipLib の道しか無い
    [Fact]
    public void 削除()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        log.Clear();

        var result = ZipArchiveWriter.Delete(archive, new HashSet<string> { "b.txt" }, [], CancellationToken.None);

        Assert.Equal(1, result.Deleted);
        AssertFlushedBeforeReplacing(log, archive);
    }

    // SharpZipLib の道しか無い
    [Fact]
    public void 名前の変更()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        log.Clear();

        var result = ZipArchiveWriter.Rename(archive, "b.txt", "c.txt", false, null, CancellationToken.None);

        Assert.Equal(1, result.Renamed);
        AssertFlushedBeforeReplacing(log, archive);
    }

    // SharpZipLib の道しか無い
    [Fact]
    public void 移動()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        log.Clear();

        var result = ZipArchiveWriter.Move(archive, [new PathChange("b.txt", "docs/b.txt", false)], null, CancellationToken.None);

        Assert.Equal(1, result.Renamed);
        AssertFlushedBeforeReplacing(log, archive);
    }

    // SharpZipLib の道しか無い
    [Fact]
    public void フォルダーの作成()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        log.Clear();

        Assert.True(ZipArchiveWriter.CreateFolder(archive, "new"));
        AssertFlushedBeforeReplacing(log, archive);
    }

    [Fact]
    public void 暗号化した追加()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        var source = log.PathOf("secret.txt");
        File.WriteAllText(source, "secret");
        log.Clear();

        var result = ZipEncryptedWriter.Add(archive, [source], "", false, "password", null, CancellationToken.None);

        Assert.Equal(1, result.Added);
        AssertFlushedBeforeReplacing(log, archive);
    }

    [Fact]
    public void 元に戻す()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        var store = Directory.CreateDirectory(log.PathOf("undo")).FullName;
        var history = new UndoHistory();
        var pending = history.Begin(archive, UndoKind.Delete, store);
        Assert.NotNull(pending);
        ZipArchiveWriter.Delete(archive, new HashSet<string> { "b.txt" }, [], CancellationToken.None);
        history.Complete(pending);
        log.Clear();

        Assert.Equal(UndoOutcome.Restored, history.Undo(archive, out _));

        AssertFlushedBeforeReplacing(log, archive);
        Assert.Contains("b.txt", TestArchives.Names(archive));
    }

    /// <summary>書庫の名前への最後の置き換えより前に、書き切った知らせがある。</summary>
    private static void AssertFlushedBeforeReplacing(TraceLog log, string archive)
    {
        var events = log.Events;
        var last = events.Select(static (e, i) => (e, i))
            .Last(x => x.e.What == "move" && string.Equals(x.e.Path, archive, StringComparison.OrdinalIgnoreCase)).i;
        Assert.True(events.Take(last).Any(static e => e.What == "flush"), log.Names);
    }
}

/// <summary>テストの書庫を作る。</summary>
internal static class TestArchives
{
    /// <summary>
    /// a.txt と b.txt の書庫を作る。<paramref name="sharpZipLib"/> なら、並列の道が扱わない圧縮方式の項目を足し、
    /// 編集が SharpZipLib の道を通るようにする。
    /// </summary>
    public static string Make(TraceLog log, bool sharpZipLib)
    {
        var path = log.PathOf(sharpZipLib ? "slow.zip" : "fast.zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var name in new[] { "a.txt", "b.txt" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }

            if (sharpZipLib)
            {
                using var writer = new StreamWriter(zip.CreateEntry("odd.bin", CompressionLevel.NoCompression).Open());
                writer.Write("odd");
            }
        }

        if (sharpZipLib)
        {
            // 格納の項目の圧縮方式を、BZip2 (12) と書き換える。中身はそのまま写されるだけなので、書庫は読める
            var bytes = File.ReadAllBytes(path);
            for (var i = 0; i + 46 < bytes.Length; i++)
            {
                if (bytes[i] != 0x50 || bytes[i + 1] != 0x4B)
                {
                    continue;
                }

                var (method, nameAt, lengthAt) = (bytes[i + 2], bytes[i + 3]) switch
                {
                    (3, 4) => (8, 30, 26),
                    (1, 2) => (10, 46, 28),
                    _ => (-1, 0, 0),
                };
                if (method < 0)
                {
                    continue;
                }

                var name = System.Text.Encoding.UTF8.GetString(bytes, i + nameAt, BitConverter.ToUInt16(bytes, i + lengthAt));
                if (name == "odd.bin")
                {
                    bytes[i + method] = 12;
                    bytes[i + method + 1] = 0;
                }
            }

            File.WriteAllBytes(path, bytes);
        }

        return path;
    }

    public static List<string> Names(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        return [.. zip.Entries.Select(static e => e.FullName)];
    }
}
