using System.Text;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Tests;

/// <summary>
/// 書庫を置き換えるときに、MotW を付けられなければ、置き換えずに止める (#350)。
/// MotW を保存できる場所で MotW が消えると、そのあと取り出したファイルを開いても警告が出ない。
/// MotW を保存できない場所 (exFAT など) では、今までどおり止めない。
/// </summary>
[Collection(nameof(TraceCollection))]
public sealed class MarkKeptTests
{
    private const string Intranet = "[ZoneTransfer]\r\nZoneId=1\r\n";

    // 小文字の zoneid は読めない MotW。インターネットから来たものとして扱う (#325)
    private const string Unreadable = "[ZoneTransfer]\r\nzoneid=3\r\n";

    public static TheoryData<string> Marks => [MarkOfTheWeb.Internet, Intranet, Unreadable];

    [Theory]
    [MemberData(nameof(Marks))]
    public void 元の書庫のMotWを付けられなければ置き換えない(string mark)
    {
        using var log = new TraceLog();
        var (target, work) = Prepare(log, mark);
        MakeUnmarkable(work);

        Assert.Throws<MarkNotKeptException>(() => AtomicFile.Replace(work, target));

        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.Equal(mark, ReadMark(target));
        // 作業用ファイルは呼び出し側が消す
        Assert.True(File.Exists(work));
    }

    [Fact]
    public void MotWを保存できるドライブなら渡されたMotWを付けられなくても止める()
    {
        // 選んだ項目から新しい書庫を作るとき。元の書庫の MotW を渡す
        using var log = new TraceLog();
        var target = log.PathOf("new.zip");
        var work = log.PathOf("work.tmp");
        File.WriteAllText(work, "NEW");
        MakeUnmarkable(work);

        Assert.Throws<MarkNotKeptException>(() => AtomicFile.Replace(work, target, zone: MarkOfTheWeb.Internet));

        Assert.False(File.Exists(target));
    }

    [Fact]
    public void 上書きしない指定なら既にあることを理由にする()
    {
        using var log = new TraceLog();
        var (target, work) = Prepare(log, MarkOfTheWeb.Internet);
        using var marks = new FakeMarks();
        marks.Set(target, MarkOfTheWeb.Internet);

        var error = Assert.ThrowsAny<IOException>(() => AtomicFile.Replace(work, target, overwrite: false));

        Assert.IsNotType<MarkNotKeptException>(error);
        Assert.Equal("OLD", File.ReadAllText(target));
    }

    [Fact]
    public void MotWをほかのプログラムが開いたままなら置き換えず_元の書庫とMotWを残す()
    {
        // 読ませないようにしても、MotW が無いことにはならない (区分 3 の MotW として扱う)。
        // 置き換えは、開いたままの MotW があるので Windows が断る
        using var log = new TraceLog();
        var (target, work) = Prepare(log, Intranet);

        using (new FileStream(target + MarkOfTheWeb.StreamSuffix, FileMode.Open, FileAccess.Read, FileShare.Delete))
        {
            Assert.Equal(MarkOfTheWeb.Internet, MarkOfTheWeb.TryRead(target));
            Assert.ThrowsAny<Exception>(() => AtomicFile.Replace(work, target));
        }

        Assert.Equal("OLD", File.ReadAllText(target));
        Assert.Equal(Intranet, ReadMark(target));
    }

    [Fact]
    public void 付けたと返っても残っていなければ止める()
    {
        using var log = new TraceLog();
        var (target, work) = Prepare(log, mark: null);
        using var marks = new FakeMarks { Lies = true };
        marks.Set(target, MarkOfTheWeb.Internet);

        Assert.Throws<MarkNotKeptException>(() => AtomicFile.Replace(work, target));

        Assert.Equal("OLD", File.ReadAllText(target));
    }

    [Fact]
    public void 作業用ファイルに同じMotWが既に付いていれば止めない()
    {
        // 元に戻すときの、取っておいた書庫など
        using var log = new TraceLog();
        var (target, work) = Prepare(log, mark: null);
        using var marks = new FakeMarks();
        marks.Set(target, MarkOfTheWeb.Internet);
        marks.Set(work, MarkOfTheWeb.Internet);

        AtomicFile.Replace(work, target);

        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void 置き換える先にMotWが無ければ止めない()
    {
        using var log = new TraceLog();
        var (target, work) = Prepare(log, mark: null);
        using var marks = new FakeMarks();

        AtomicFile.Replace(work, target);

        Assert.Equal("NEW", File.ReadAllText(target));
    }

    [Fact]
    public void 差し替えたMotWの読み書きでは_渡されたMotWを付けられなくても止めない()
    {
        // 差し替えた側 (Mac 版など) は、ドライブに尋ねず、保存できない場所として扱う。
        // Windows で exFAT のドライブに保存するとき (CanStore が false) の道は、テストに無い (exFAT のドライブが要る)
        using var log = new TraceLog();
        var target = log.PathOf("new.zip");
        var work = log.PathOf("work.tmp");
        File.WriteAllText(work, "NEW");
        using var marks = new FakeMarks();

        AtomicFile.Replace(work, target, zone: MarkOfTheWeb.Internet);

        Assert.Equal("NEW", File.ReadAllText(target));
    }

    public static TheoryData<string> Edits => ["add", "add-slow", "add-encrypted", "delete"];

    [Theory]
    [MemberData(nameof(Edits))]
    public void 編集はMotWを付けられなければ書庫を変えない(string edit)
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: edit == "add-slow");
        var source = log.PathOf("added.txt");
        File.WriteAllText(source, "added");
        var before = File.ReadAllBytes(archive);
        using var marks = new FakeMarks();
        marks.Set(archive, MarkOfTheWeb.Internet);

        var error = Record.Exception(() =>
        {
            _ = edit switch
            {
                "delete" => (object)ZipArchiveWriter.Delete(archive, new HashSet<string> { "b.txt" }, [], CancellationToken.None),
                "add-encrypted" => ZipEncryptedWriter.Add(archive, [source], "", false, "password", null, CancellationToken.None),
                _ => ZipArchiveWriter.Add(archive, [source], "", false, null, CancellationToken.None),
            };
        });

        // 包まずに届く。画面は、この例外から理由の文を選ぶ
        Assert.IsType<MarkNotKeptException>(error);
        Assert.Equal(before, File.ReadAllBytes(archive));
        Assert.Equal(MarkOfTheWeb.Internet, marks.Get(archive));
        // 作業用ファイルが残らない
        Assert.Equal(
            new[] { archive, source }.Order(StringComparer.OrdinalIgnoreCase),
            Directory.EnumerateFiles(log.Root).Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void 元に戻すはMotWを付けられなければ書庫を変えず_履歴を残す()
    {
        using var log = new TraceLog();
        var archive = TestArchives.Make(log, sharpZipLib: false);
        var store = Directory.CreateDirectory(log.PathOf("undo")).FullName;
        var history = new UndoHistory();
        var pending = history.Begin(archive, UndoKind.Delete, store);
        Assert.NotNull(pending);
        ZipArchiveWriter.Delete(archive, new HashSet<string> { "b.txt" }, [], CancellationToken.None);
        history.Complete(pending);
        var edited = File.ReadAllBytes(archive);

        using (var marks = new FakeMarks())
        {
            marks.Set(archive, MarkOfTheWeb.Internet);
            Assert.Throws<MarkNotKeptException>(() => history.Undo(archive, out _));
            Assert.Equal(edited, File.ReadAllBytes(archive));
        }

        Assert.Equal(UndoKind.Delete, history.Peek(archive));
        Assert.Equal(UndoOutcome.Restored, history.Undo(archive, out _));
        Assert.Contains("b.txt", TestArchives.Names(archive));
    }

    [Fact]
    public void 理由はディスクの空きでなくMotWのことを言う()
    {
        var reason = Strings.Reason(new MarkNotKeptException("C:\\a.zip"));

        Assert.Contains("Mark of the Web", reason);
    }

    private static (string Target, string Work) Prepare(TraceLog log, string? mark)
    {
        var target = log.PathOf("a.zip");
        var work = log.PathOf("work.tmp");
        File.WriteAllText(target, "OLD");
        if (mark is not null)
        {
            File.WriteAllBytes(target + MarkOfTheWeb.StreamSuffix, Encoding.Latin1.GetBytes(mark));
        }

        File.WriteAllText(work, "NEW");
        return (target, work);
    }

    /// <summary>読み取り専用にして、MotW を書けなくする。</summary>
    private static void MakeUnmarkable(string path) => File.SetAttributes(path, FileAttributes.ReadOnly);

    private static string? ReadMark(string path)
    {
        try
        {
            using var stream = new FileStream(
                path + MarkOfTheWeb.StreamSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream, Encoding.Latin1);
            return reader.ReadToEnd();
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// MotW の読み書きを差し替える。どのファイルにも MotW を付けられない。<see cref="Lies"/> なら、付けたと返すが残さない。
    /// </summary>
    private sealed class FakeMarks : IOriginMark, IDisposable
    {
        private readonly Dictionary<string, string> _marks = new(StringComparer.OrdinalIgnoreCase);

        public FakeMarks() => MarkOfTheWeb.Override = this;

        public bool Lies { get; init; }

        public string Internet => "[ZoneTransfer]\r\nZoneId=3\r\n";

        public void Set(string path, string zone) => _marks[Path.GetFullPath(path)] = zone;

        public string? Get(string path) => TryRead(path);

        public string? TryRead(string path) => _marks.GetValueOrDefault(Path.GetFullPath(path));

        public bool TryApply(string path, string zone) => Lies;

        public void Dispose() => MarkOfTheWeb.Override = null;
    }
}
