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

    // ------------------------------------------------------------------ 知らない言葉 (#421)

    /// <summary>種類が required_entry の行を 1 つ作る。当てる先と出所は渡したまま (null なら項目を書かない)。</summary>
    private static string RowWith(string? scope, string? source, string value = "x")
    {
        var row = new Dictionary<string, object?> { ["Kind"] = "required_entry", ["Value"] = value };
        if (scope is not null)
        {
            row["Scope"] = scope;
        }

        if (source is not null)
        {
            row["Source"] = source;
        }

        return System.Text.Json.JsonSerializer.Serialize(row);
    }

    [Fact]
    public void 知らない当てる先と出所の行は落として写しを残し適用で書き戻さない()
    {
        // 書庫全体・AI として読むと、「適用」で all / ai と書き直されて意味が変わる
        var text = $$"""{ "Version": 2, "Rules": [ {{Row}}, {{RowWith("everything", "hand", "SCOPE.md")}}, {{RowWith("files", "import", "SOURCE.md")}} ] }""";
        File.WriteAllText(Rules, text);
        var load = Load();

        Assert.Null(load.Problem);
        Assert.Equal("README.md", Assert.Single(load.Book!.Rules).Rule.Value);
        Assert.Equal(2, load.Dropped);
        Assert.Equal(text, File.ReadAllText(load.DroppedKeptPath!));

        // 適用すると、落とした行はファイルに無く、写しにだけ残る
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(load.Book!, load));
        var saved = File.ReadAllText(Rules);
        Assert.DoesNotContain("SCOPE.md", saved);
        Assert.DoesNotContain("SOURCE.md", saved);
        Assert.DoesNotContain("everything", saved);
        Assert.Contains("SCOPE.md", File.ReadAllText(load.DroppedKeptPath!));
    }

    [Fact]
    public void 似た文字の当てる先と出所は知っている言葉として読まない()
    {
        // 小文字にしたり幅を詰めたりすると知っている言葉に見えるもの。ASCII だけで比べる
        string[] lookAlikes =
        [
            "ＦＩＬＥＳ",                                  // 全角
            "fi" + (char)0x200B + "les",                  // 幅の無い文字
            "files" + (char)0x0000,                       // NUL
            "F" + (char)0x0130 + "LES",                   // トルコ語の大きな İ
            "r" + (char)0x043E + "ot",                    // キリル文字の о
        ];

        foreach (var word in lookAlikes)
        {
            var text = $$"""{ "Version": 2, "Rules": [ {{Row}}, {{RowWith(word, null)}}, {{RowWith("files", word.Replace("files", "hand", StringComparison.Ordinal))}} ] }""";
            File.WriteAllText(Rules, text);
            var load = Load(keepCopy: false);

            Assert.Null(load.Problem);
            Assert.Single(load.Book!.Rules);
            Assert.Equal(2, load.Dropped);
        }

        // ケルビンの記号は、言語によらない小文字化で k になる。ASCII でなければ知らない言葉
        Assert.Null(RuleWords.TrySource("ha" + (char)0x212A));
        Assert.Null(RuleWords.ToKind("forbidden_name" + (char)0x212A));
    }

    [Fact]
    public void 当てる先と出所は大文字小文字と前後の空白を区別しない()
    {
        var text = $$"""{ "Version": 2, "Rules": [ {{RowWith(" Files ", "Hand", "a")}}, {{RowWith("ROOT", " hand ", "b")}}, {{RowWith("folders", "FILLED", "c")}} ] }""";
        File.WriteAllText(Rules, text);
        var load = Load();

        Assert.Equal(0, load.Dropped);
        var rules = load.Book!.Rules.Select(static entry => entry.Rule).ToList();
        Assert.Equal(new[] { RuleScope.Files, RuleScope.Root, RuleScope.Folders }, rules.Select(static r => r.Scope));
        Assert.Equal(new[] { RuleSource.Hand, RuleSource.Hand, RuleSource.Filled }, rules.Select(static r => r.Source));
    }

    [Fact]
    public void 当てる先と出所が無いか空なら書庫全体とAIとして読む()
    {
        // 手で書いたファイルで省いたもの。0.6.0 までと同じ読み方
        var text = $$"""{ "Version": 2, "Rules": [ {{RowWith(null, null, "a")}}, {{RowWith("", "", "b")}}, {{RowWith("  ", "  ", "c")}} ] }""";
        File.WriteAllText(Rules, text);
        var load = Load();

        Assert.Equal(0, load.Dropped);
        Assert.Equal(3, load.Book!.Rules.Count);
        Assert.All(load.Book.Rules, static entry => Assert.Equal(RuleScope.All, entry.Rule.Scope));
        Assert.All(load.Book.Rules, static entry => Assert.Equal(RuleSource.Ai, entry.Rule.Source));
        Assert.Empty(Copies());
    }

    [Fact]
    public void 当てる先が文字列でなければ壊れている扱い()
    {
        File.WriteAllText(Rules, """{ "Version": 2, "Rules": [ { "Kind": "required_entry", "Scope": 1, "Value": "x" } ] }""");
        var load = Load();

        Assert.Equal(RuleLoadFailure.Damaged, load.Problem?.Failure);
        AssertNotWritten(load, RuleWriteResult.Unreadable);
    }

    [Fact]
    public void 当てはめでは知らない言葉の行を落としても写しを作らない()
    {
        File.WriteAllText(Rules, $$"""{ "Version": 2, "Rules": [ {{RowWith("everything", null)}} ] }""");
        var load = Load(keepCopy: false);

        Assert.Null(load.Problem);
        Assert.Empty(load.Book!.Rules);
        Assert.Equal(1, load.Dropped);
        Assert.Null(load.DroppedKeptPath);
        Assert.Empty(Copies());
    }

    [Fact]
    public void AIの答えの知らない当てる先は今までどおり書庫全体()
    {
        // AI の答えはお手本に当て直して確かめる (#80) ので、ファイルとは読み方を分けている
        Assert.Equal(RuleScope.All, RuleWords.ToScope("everything"));
        Assert.Null(RuleWords.TryScope("everything"));
    }

    // ------------------------------------------------------------------ 知らない項目 (#421)

    /// <summary>読んで、読んだ決まりのまま適用し、書いたファイルを JSON として返す。</summary>
    private System.Text.Json.Nodes.JsonObject LoadAndApply(string text, out RuleLoad load)
    {
        File.WriteAllText(Rules, text);
        load = Load();
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(load.Book!, load));
        return System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Rules))!.AsObject();
    }

    [Fact]
    public void 知らない項目は適用で頭とその行に書き戻す()
    {
        // 新しいバージョンの Expzip が足した項目。古いバージョンで適用しても消さない
        var text = """
            { "Version": 2, "Team": { "name": "設計", "tags": [ "a", 1, true ] },
              "Rules": [
                { "Kind": "required_entry", "Scope": "root", "Value": "A.md", "Priority": 3, "Extra": null },
                { "Kind": "required_entry", "Scope": "root", "Value": "B.md" } ] }
            """;
        var saved = LoadAndApply(text, out var load);

        Assert.False(load.UnknownDiscarded);
        Assert.Empty(Copies());
        Assert.Equal("設計", (string?)saved["Team"]!["name"]);
        Assert.Equal(3, saved["Team"]!["tags"]!.AsArray().Count);
        var rows = saved["Rules"]!.AsArray();
        Assert.Equal(3, (int?)rows[0]!["Priority"]);
        Assert.True(rows[0]!.AsObject().ContainsKey("Extra"));
        Assert.Null(rows[0]!["Extra"]);
        Assert.False(rows[1]!.AsObject().ContainsKey("Priority"));

        // 書き戻したものは、もう一度読んでも同じく持つ
        Assert.Equal(3, (int?)LoadAndApply(File.ReadAllText(Rules), out _)["Rules"]!.AsArray()[0]!["Priority"]);
    }

    [Fact]
    public void 行を消せばその行の知らない項目も消える()
    {
        var text = """
            { "Version": 2, "Rules": [
                { "Kind": "required_entry", "Value": "A.md", "Mine": "a" },
                { "Kind": "required_entry", "Value": "B.md", "Mine": "b" } ] }
            """;
        File.WriteAllText(Rules, text);
        var load = Load();
        var kept = load.Book! with { Rules = [load.Book!.Rules[1]] };

        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(kept, load));
        var saved = File.ReadAllText(Rules);
        Assert.DoesNotContain("\"a\"", saved);
        Assert.Contains("\"Mine\": \"b\"", saved);
    }

    [Fact]
    public void 行に知っている項目と大文字小文字だけ違う名前があればその行を落とす()
    {
        // 項目名は区別して読むので、当てる先が無い扱いで書庫全体になり、適用で "Scope": "all" と書き直される
        var text = $$"""{ "Version": 2, "Rules": [ {{Row}}, { "Kind": "required_entry", "scope": "files", "Value": "x" } ] }""";
        File.WriteAllText(Rules, text);
        var load = Load();

        Assert.Single(load.Book!.Rules);
        Assert.Equal(1, load.Dropped);
        Assert.Equal(text, File.ReadAllText(load.DroppedKeptPath!));
    }

    [Fact]
    public void 頭に知っている項目と大文字小文字だけ違う名前があれば捨てて知らせる()
    {
        var text = $$"""{ "Version": 2, "learnedFrom": "x.zip", "Team": "t", "Rules": [ {{Row}} ] }""";
        var saved = LoadAndApply(text, out var load);

        Assert.True(load.UnknownDiscarded);
        Assert.Equal(0, load.Dropped);
        Assert.Equal(text, File.ReadAllText(load.DroppedKeptPath!));
        Assert.False(saved.ContainsKey("learnedFrom"));
        Assert.Equal("t", (string?)saved["Team"]);
    }

    [Fact]
    public void 知らない項目が多すぎれば全部捨てて知らせる()
    {
        // 頭と全部の行を合わせて数える
        var head = string.Join(", ", Enumerable.Range(0, 600).Select(static i => $"\"H{i}\": {i}"));
        var row = string.Join(", ", Enumerable.Range(0, 401).Select(static i => $"\"R{i}\": {i}"));
        var text = $$"""{ "Version": 2, {{head}}, "Rules": [ { "Kind": "required_entry", "Value": "x", {{row}} } ] }""";
        var saved = LoadAndApply(text, out var load);

        Assert.True(load.UnknownDiscarded);
        Assert.NotNull(load.DroppedKeptPath);
        Assert.False(saved.ContainsKey("H0"));
        Assert.False(saved["Rules"]!.AsArray()[0]!.AsObject().ContainsKey("R0"));
    }

    [Fact]
    public void 知らない項目が大きすぎれば全部捨てて知らせる()
    {
        var value = new string('a', Expzip.Configuration.UnknownItems.MaxBytes);
        var saved = LoadAndApply($$"""{ "Version": 2, "Big": "{{value}}", "Small": 1, "Rules": [ {{Row}} ] }""", out var load);

        Assert.True(load.UnknownDiscarded);
        Assert.False(saved.ContainsKey("Big"));
        Assert.False(saved.ContainsKey("Small"));
    }

    [Fact]
    public void 書き出せない知らない項目は捨てて知らせる()
    {
        // 対になっていないサロゲート。持っていると、適用のたびに失敗する
        var text = $$"""{ "Version": 2, "Bad": "\uD800", "Good": 1, "Rules": [ {{Row}} ] }""";
        var saved = LoadAndApply(text, out var load);

        Assert.True(load.UnknownDiscarded);
        Assert.False(saved.ContainsKey("Bad"));
        Assert.Equal(1, (int?)saved["Good"]);
    }

    [Fact]
    public void 知らない項目の名前が重なれば壊れている扱い()
    {
        File.WriteAllText(Rules, """{ "Version": 2, "Rules": [ { "Kind": "required_entry", "Value": "x", "X": 1, "X": 2 } ] }""");
        Assert.Equal(RuleLoadFailure.Damaged, Load().Problem?.Failure);

        File.WriteAllText(Rules, """{ "Version": 2, "X": { "a": 1, "a": 2 }, "Rules": [] }""");
        Assert.Equal(RuleLoadFailure.Damaged, Load().Problem?.Failure);
    }

    [Fact]
    public void 知っている項目だけで大きすぎるなら知らない項目は捨てず書かない()
    {
        // 詰めて書いた行を大量に並べたもの。字下げで 16 MB を超えるのは知っている項目のせいなので、知らない項目を捨てて知らせない
        var rows = string.Join(",", Enumerable.Range(0, 250_000).Select(static i => $$"""{"Kind":"required_entry","Value":"x{{i}}"}"""));
        var text = $$"""{"Version":2,"Team":"t","Rules":[{{rows}}]}""";
        Assert.True(Encoding.UTF8.GetByteCount(text) < Expzip.Configuration.GuardedFile.MaxBytes);
        File.WriteAllText(Rules, text);
        var load = Load();

        Assert.False(load.UnknownDiscarded);
        Assert.Null(load.DroppedKeptPath);
        Assert.NotNull(load.Book!.Unknown);

        Assert.Equal(RuleWriteResult.TooLarge, RuleStore.TrySave(load.Book, load));
        Assert.Equal(text, File.ReadAllText(Rules));
    }

    [Fact]
    public void 当てはめでは知らない項目を持たず写しも作らない()
    {
        var head = string.Join(", ", Enumerable.Range(0, 1001).Select(static i => $"\"H{i}\": {i}"));
        File.WriteAllText(Rules, $$"""{ "Version": 2, {{head}}, "Rules": [ { "Kind": "required_entry", "Value": "x", "Mine": 1 } ] }""");
        var load = Load(keepCopy: false);

        Assert.Null(load.Book!.Unknown);
        Assert.Null(load.Book.Rules[0].Unknown);
        Assert.False(load.UnknownDiscarded);
        Assert.Empty(Copies());
    }

    // ------------------------------------------------------------------ 書く中身の大きさ (#421)

    [Fact]
    public void 書く中身が大きすぎれば書かず前のファイルを残す()
    {
        // 次に読むと大きすぎて読めなくなる。字下げで、読んだときより大きくなることもある
        File.WriteAllText(Rules, Valid);
        var load = Load();

        var huge = new RuleBook("b.zip", DateTimeOffset.Now,
            [new RuleEntry(ArchiveRule.TryCreate(RuleKind.RequiredEntry, RuleScope.Root, "LICENSE",
                new string('a', Expzip.Configuration.GuardedFile.MaxBytes), "e", RuleSource.Hand, string.Empty)!, true)]);

        Assert.Equal(RuleWriteResult.TooLarge, RuleStore.TrySave(huge, load));
        Assert.Equal(Valid, File.ReadAllText(Rules));
        Assert.Empty(Directory.GetFiles(_folder, "*.tmp"));

        // 前のファイルのままなので、続けてふつうの大きさで適用できる
        Assert.Equal(RuleWriteResult.Done, RuleStore.TrySave(NewBook(), load));
    }
}
