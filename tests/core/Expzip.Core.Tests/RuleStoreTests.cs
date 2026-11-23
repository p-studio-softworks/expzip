using System.Text;
using Expzip.Ai;

namespace Expzip.Tests;

/// <summary>
/// ルールのファイルを読めなかったとき、ルールの推定のウィンドウの「適用」で消したり置き換えたりせず、読めた中身を別の名前で残す (#406)。
/// 見本は、設計の確認 (#406) で挙がったもの。
/// </summary>
public sealed class RuleStoreTests : IDisposable
{
    private const string Row = """{ "Kind": "required_entry", "Scope": "root", "Value": "README.md", "Description": "d", "Evidence": "e", "Source": "hand", "Enabled": true }""";

    private static readonly string Valid = $$"""{ "Version": 2, "LearnedFrom": "a.zip", "Rules": [ {{Row}} ] }""";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", "rules-" + Guid.NewGuid().ToString("N"));

    public RuleStoreTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private string Rules => Path.Combine(_folder, "Expzip.rules.json");

    private string[] Copies() => Directory.GetFiles(_folder, "Expzip.rules.unreadable-*.json");

    private RuleLoad Load(bool keepCopy = true) => RuleStore.Load(Rules, keepCopy);

    private static RuleBook NewBook() => new("b.zip", DateTimeOffset.Now,
        [new RuleEntry(ArchiveRule.TryCreate(RuleKind.RequiredEntry, RuleScope.Root, "LICENSE", "d", "e", RuleSource.Ai, string.Empty)!, true)]);

    /// <summary>書く処理を両方呼んで、どちらも書かず、ファイルのバイト列が変わらないことを確かめる。</summary>
    private void AssertNotWritten(RuleLoad load, RuleWriteResult expected)
    {
        var before = File.ReadAllBytes(Rules);
        Assert.Equal(expected, RuleStore.TrySave(NewBook(), load));
        Assert.Equal(expected, RuleStore.TryDelete(load));
        Assert.Equal(before, File.ReadAllBytes(Rules));
    }

    // ------------------------------------------------------------------ 読めるもの

    [Fact]
    public void 正しいファイルは読めて適用もできる()
    {
        File.WriteAllText(Rules, Valid);
        var load = Load();

        Assert.Null(load.Problem);
        Assert.Equal(0, load.Dropped);
        Assert.Single(load.Book!.Rules);

        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(NewBook(), load));
        Assert.Contains("LICENSE", File.ReadAllText(Rules));

        // 書いた中身で覚え直すので、続けて適用できる
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(NewBook(), load));
        Assert.Equal(RuleWriteResult.Done, RuleStore.TryDelete(load));
        Assert.False(File.Exists(Rules));
        Assert.Empty(Copies());
    }

    [Fact]
    public void ファイルが無ければ適用できる()
    {
        var load = Load();

        Assert.Null(load.Problem);
        Assert.Null(load.Book);
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(NewBook(), load));
        Assert.True(File.Exists(Rules));
    }

    [Fact]
    public void 形式1のファイルも読める()
    {
        File.WriteAllText(Rules, $$"""{ "Version": 1, "Rules": [ {{Row}} ] }""");
        var load = Load();

        Assert.Null(load.Problem);
        Assert.Single(load.Book!.Rules);
    }

    [Fact]
    public void 零バイトのファイルは無いときと同じ()
    {
        File.WriteAllBytes(Rules, []);
        var load = Load();

        Assert.Null(load.Problem);
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(NewBook(), load));
    }

    [Fact]
    public void BOM付きのUTF8も読める()
    {
        File.WriteAllText(Rules, Valid, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var load = Load();

        Assert.Null(load.Problem);
        Assert.Single(load.Book!.Rules);
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(NewBook(), load));
    }

    [Fact]
    public void UTF16も読める()
    {
        File.WriteAllText(Rules, Valid, Encoding.Unicode);
        var load = Load();

        Assert.Null(load.Problem);
        Assert.Single(load.Book!.Rules);
    }

    // ------------------------------------------------------------------ 読めない

    [Fact]
    public void 壊れていれば書かず写しを残す()
    {
        File.WriteAllText(Rules, "{ broken");
        var load = Load();

        var problem = Assert.IsType<RuleLoadProblem>(load.Problem);
        Assert.Equal(RuleLoadFailure.Damaged, problem.Failure);
        Assert.NotNull(problem.KeptPath);
        Assert.Equal("{ broken", File.ReadAllText(problem.KeptPath!));
        AssertNotWritten(load, RuleWriteResult.Unreadable);
    }

    [Fact]
    public void 新しいバージョンの形式は書かず写しを残す()
    {
        var newer = $$"""{ "Version": 3, "Rules": [ {{Row}} ] }""";
        File.WriteAllText(Rules, newer);
        var load = Load();

        // 中身は正しいかもしれないので、壊れているとは知らせない (削除を勧めない)
        var problem = Assert.IsType<RuleLoadProblem>(load.Problem);
        Assert.Equal(RuleLoadFailure.Newer, problem.Failure);
        Assert.Equal(newer, File.ReadAllText(problem.KeptPath!));
        AssertNotWritten(load, RuleWriteResult.Unreadable);
    }

    [Fact]
    public void 新しいバージョンで形が変わっていても新しいバージョンとして知らせる()
    {
        // 番号を上げるのは古い版が読めない形に変えたとき。全体を読んでから比べると「壊れている」になり、削除を勧めてしまう
        var newer = """{ "Version": 3, "Rules": { "sets": [] } }""";
        File.WriteAllText(Rules, newer);
        var load = Load();

        Assert.Equal(RuleLoadFailure.Newer, load.Problem?.Failure);
        Assert.Equal(newer, File.ReadAllText(load.Problem!.KeptPath!));
        AssertNotWritten(load, RuleWriteResult.Unreadable);
    }

    [Fact]
    public void 同じ項目が2つあれば壊れている扱い()
    {
        // あとの "Rules": [] だけを読むと 0 件になり、適用でファイルごと消してしまう
        File.WriteAllText(Rules, $$"""{ "Version": 2, "Rules": [ {{Row}} ], "Rules": [] }""");
        var load = Load();

        Assert.Equal(RuleLoadFailure.Damaged, load.Problem?.Failure);
        Assert.Single(Copies());
        AssertNotWritten(load, RuleWriteResult.Unreadable);
    }

    [Fact]
    public void Rulesの項目が無ければ壊れている扱い()
    {
        // 項目名を小文字で書いたものも、大文字小文字を区別して読むので、Rules の項目が無いものとして扱う
        File.WriteAllText(Rules, $$"""{ "Version": 2, "rules": [ {{Row}} ] }""");
        var load = Load();

        Assert.Equal(RuleLoadFailure.Damaged, load.Problem?.Failure);
        Assert.Single(Copies());
        AssertNotWritten(load, RuleWriteResult.Unreadable);
    }

    [Fact]
    public void 同じ中身なら写しは増えない()
    {
        File.WriteAllText(Rules, "{ broken");
        var first = Load().Problem!.KeptPath;
        var second = Load().Problem!.KeptPath;

        Assert.Equal(first, second);
        Assert.Single(Copies());
    }

    [Fact]
    public void 当てはめからは写しを作らない()
    {
        File.WriteAllText(Rules, "{ broken");
        var load = Load(keepCopy: false);

        Assert.NotNull(load.Problem);
        Assert.Null(load.Problem!.KeptPath);
        Assert.Empty(Copies());
    }

    [Fact]
    public void ほかのプログラムが開いていれば書かない()
    {
        File.WriteAllText(Rules, Valid);
        RuleLoad load;
        using (new FileStream(Rules, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            load = Load();
        }

        var problem = Assert.IsType<RuleLoadProblem>(load.Problem);
        Assert.Equal(RuleLoadFailure.InUse, problem.Failure);
        Assert.Null(problem.KeptPath);
        Assert.Empty(Copies());

        // 閉じたあとに適用しようとしても、元のファイルは消えず、置き換わらない
        AssertNotWritten(load, RuleWriteResult.Unreadable);
        Assert.Equal(Valid, File.ReadAllText(Rules));
    }

    [Fact]
    public void ルールのファイルの名前のフォルダーがあれば書かない()
    {
        Directory.CreateDirectory(Rules);
        var load = Load();

        Assert.Equal(RuleLoadFailure.Inaccessible, load.Problem?.Failure);
        Assert.Equal(RuleWriteResult.Unreadable, RuleStore.TrySave(NewBook(), load));
        Assert.Equal(RuleWriteResult.Unreadable, RuleStore.TryDelete(load));
        Assert.True(Directory.Exists(Rules));
    }

    [Fact]
    public void 大きすぎるファイルは読まず書かない()
    {
        using (var stream = new FileStream(Rules, FileMode.CreateNew))
        {
            stream.SetLength(Expzip.Configuration.GuardedFile.MaxBytes + 1L);
        }

        var load = Load();

        Assert.Equal(RuleLoadFailure.Damaged, load.Problem?.Failure);
        Assert.Null(load.Problem!.KeptPath);
        Assert.Empty(Copies());
        Assert.Equal(RuleWriteResult.Unreadable, RuleStore.TryDelete(load));
        Assert.Equal(Expzip.Configuration.GuardedFile.MaxBytes + 1L, new FileInfo(Rules).Length);
    }

    // ------------------------------------------------------------------ 落とした行

    [Fact]
    public void 読めない行があれば写しを残して知らせる()
    {
        var text = $$"""{ "Version": 2, "Rules": [ {{Row}}, { "Kind": "banana", "Value": "x" }, null ] }""";
        File.WriteAllText(Rules, text);
        var load = Load();

        // 適用はできる。落とした行は写しに残る
        Assert.Null(load.Problem);
        Assert.Single(load.Book!.Rules);
        Assert.Equal(2, load.Dropped);
        Assert.Equal(text, File.ReadAllText(load.DroppedKeptPath!));
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(NewBook(), load));
    }

    [Fact]
    public void 全部の行が落ちても写しが残る()
    {
        var text = """{ "Version": 2, "Rules": [ { "Kind": "banana", "Value": "x" } ] }""";
        File.WriteAllText(Rules, text);
        var load = Load();

        Assert.Empty(load.Book!.Rules);
        Assert.Equal(1, load.Dropped);
        Assert.Equal(text, File.ReadAllText(load.DroppedKeptPath!));
    }

    // ------------------------------------------------------------------ 開いたあとに変わった

    [Fact]
    public void 読んだあとに中身が変わっていれば書かない()
    {
        File.WriteAllText(Rules, Valid);
        var load = Load();

        var other = Valid.Replace("a.zip", "other.zip");
        File.WriteAllText(Rules, other);

        Assert.Equal(RuleWriteResult.Changed, RuleStore.TrySave(NewBook(), load));
        Assert.Equal(RuleWriteResult.Changed, RuleStore.TryDelete(load));
        Assert.Equal(other, File.ReadAllText(Rules));
    }

    [Fact]
    public void 読んだときに無かったファイルが置かれていれば消さない()
    {
        var load = Load();
        File.WriteAllText(Rules, Valid);

        Assert.Equal(RuleWriteResult.Changed, RuleStore.TryDelete(load));
        Assert.Equal(RuleWriteResult.Changed, RuleStore.TrySave(NewBook(), load));
        Assert.Equal(Valid, File.ReadAllText(Rules));
    }

    [Fact]
    public void 適用の瞬間にほかのプログラムが開いていれば書かず開き直させない()
    {
        File.WriteAllText(Rules, Valid);
        var load = Load();

        using (new FileStream(Rules, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(RuleWriteResult.Busy, RuleStore.TrySave(NewBook(), load));
            Assert.Equal(RuleWriteResult.Busy, RuleStore.TryDelete(load));
        }

        // 中身は変わっていないので、閉じたあとにもう一度押せば書ける
        Assert.Equal(Valid, File.ReadAllText(Rules));
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(NewBook(), load));
    }

    [Fact]
    public void 読んだあとに消されていれば書かない()
    {
        File.WriteAllText(Rules, Valid);
        var load = Load();
        File.Delete(Rules);

        Assert.Equal(RuleWriteResult.Changed, RuleStore.TrySave(NewBook(), load));
        Assert.False(File.Exists(Rules));
    }
}
