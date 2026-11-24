using System.Text;
using System.Text.Json;
using Expzip.Configuration;

namespace Expzip.Tests;

/// <summary>
/// 設定ファイルを読めなかったとき、そのセッションでは保存せず、読めた中身を別の名前で残す (#399)。
/// 見本は、設計の確認 (#399) で挙がったもの。
/// </summary>
public sealed class SettingsStoreTests : IDisposable
{
    private const string Valid = """{ "Language": "en", "AiModel": "m", "RecentArchives": [ "C:\\a.zip" ] }""";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", "settings-" + Guid.NewGuid().ToString("N"));

    public SettingsStoreTests() => Directory.CreateDirectory(_folder);

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

    private string Settings => Path.Combine(_folder, "Expzip.settings.json");

    private string[] Copies() => Directory.GetFiles(_folder, "Expzip.settings.unreadable-*.json");

    private AppSettings Load(bool keepCopy = true) => SettingsStore.Load(Settings, keepCopy);

    // ------------------------------------------------------------------ 読めるもの

    [Fact]
    public void 正しい設定は読めて保存もできる()
    {
        File.WriteAllText(Settings, Valid);
        var settings = Load();

        Assert.Null(settings.LoadProblem);
        Assert.Equal("en", settings.Language);
        Assert.Equal("m", settings.AiModel);

        settings.AiModel = "changed";
        Assert.True(SettingsStore.TrySave(settings, Settings));
        Assert.Contains("changed", File.ReadAllText(Settings));
        Assert.Empty(Copies());
    }

    [Fact]
    public void ファイルが無ければ既定の値で保存もできる()
    {
        var settings = Load();

        Assert.Null(settings.LoadProblem);
        Assert.True(SettingsStore.TrySave(settings, Settings));
        Assert.True(File.Exists(Settings));
    }

    [Fact]
    public void 空のオブジェクトは読めて保存もできる()
    {
        File.WriteAllText(Settings, "{}");
        var settings = Load();

        Assert.Null(settings.LoadProblem);
        Assert.NotNull(settings.RecentArchives);
        Assert.True(SettingsStore.TrySave(settings, Settings));
    }

    [Fact]
    public void 中身の無いファイルは無いときと同じに扱う()
    {
        File.WriteAllBytes(Settings, []);
        var settings = Load();

        Assert.Null(settings.LoadProblem);
        Assert.True(SettingsStore.TrySave(settings, Settings));
        Assert.Empty(Copies());
    }

    [Fact]
    public void BOM付きのUTF8も今までどおり読める()
    {
        File.WriteAllText(Settings, Valid, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var settings = Load();

        Assert.Null(settings.LoadProblem);
        Assert.Equal("en", settings.Language);
    }

    [Fact]
    public void UTF16も今までどおり読める()
    {
        File.WriteAllText(Settings, Valid, Encoding.Unicode);
        var settings = Load();

        Assert.Null(settings.LoadProblem);
        Assert.Equal("en", settings.Language);
    }

    [Fact]
    public void 最近開いた書庫は上限までに切る()
    {
        var paths = string.Join(", ", Enumerable.Range(0, 30).Select(i => $"\"C:\\\\{i}.zip\""));
        File.WriteAllText(Settings, $$"""{ "RecentArchives": [ {{paths}}, null ] }""");
        var settings = Load();

        Assert.Null(settings.LoadProblem);
        Assert.Equal(SettingsStore.RecentLimit, settings.RecentArchives.Count);
        Assert.Equal("C:\\0.zip", settings.RecentArchives[0]);
    }

    // ------------------------------------------------------------------ 壊れている

    [Theory]
    [InlineData("""{ "Language": "en", """)]
    [InlineData("null")]
    [InlineData("[1, 2]")]
    [InlineData("""{ "RecentArchives": 3 }""")]
    public void 壊れていれば保存せず中身を別の名前で残す(string text)
    {
        File.WriteAllText(Settings, text);
        var original = File.ReadAllBytes(Settings);
        var settings = Load();

        var problem = Assert.IsType<SettingsLoadProblem>(settings.LoadProblem);
        Assert.Equal(Settings, problem.FilePath);
        Assert.Equal(SettingsLoadFailure.Damaged, problem.Failure);
        Assert.NotNull(problem.KeptPath);
        Assert.Equal(original, File.ReadAllBytes(problem.KeptPath!));
        Assert.Equal(Path.GetDirectoryName(Settings), Path.GetDirectoryName(problem.KeptPath));

        // 保存しようとしても、元のファイルは既定の値で置き換わらない
        settings.RecentArchives.Add("C:\\new.zip");
        Assert.False(SettingsStore.TrySave(settings, Settings));
        Assert.Equal(original, File.ReadAllBytes(Settings));
    }

    [Fact]
    public void 起動のたびに同じ中身の写しを増やさない()
    {
        File.WriteAllText(Settings, "{ broken");
        var first = Load().LoadProblem!.KeptPath;
        var second = Load().LoadProblem!.KeptPath;

        Assert.NotNull(first);
        Assert.Equal(first, second);
        Assert.Single(Copies());
    }

    [Fact]
    public void 前に残した別の中身の写しは消さず上書きもしない()
    {
        var older = Path.Combine(_folder, "Expzip.settings.unreadable-20000101-000000.json");
        File.WriteAllText(older, "{ older");
        File.WriteAllText(Settings, "{ newer");

        var kept = Load().LoadProblem!.KeptPath;

        Assert.NotNull(kept);
        Assert.NotEqual(older, kept);
        Assert.Equal("{ older", File.ReadAllText(older));
        Assert.Equal("{ newer", File.ReadAllText(kept!));
        Assert.Equal(2, Copies().Length);
    }

    [Fact]
    public void コマンドラインからは写しを作らない()
    {
        File.WriteAllText(Settings, "{ broken");
        var settings = Load(keepCopy: false);

        Assert.NotNull(settings.LoadProblem);
        Assert.Null(settings.LoadProblem!.KeptPath);
        Assert.Empty(Copies());
        Assert.False(SettingsStore.TrySave(settings, Settings));
    }

    // ------------------------------------------------------------------ 読めない

    [Fact]
    public void ほかのプログラムが開いていれば保存しない()
    {
        File.WriteAllText(Settings, Valid);
        AppSettings settings;
        using (new FileStream(Settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            settings = Load();
        }

        // 中身は無事かもしれないので、壊れているとは知らせない (削除を勧めない)
        var problem = Assert.IsType<SettingsLoadProblem>(settings.LoadProblem);
        Assert.Equal(SettingsLoadFailure.InUse, problem.Failure);
        Assert.Null(problem.KeptPath);
        Assert.Empty(Copies());

        // 閉じたあとに保存しようとしても、元のファイルは既定の値で置き換わらない
        Assert.False(SettingsStore.TrySave(settings, Settings));
        Assert.Equal(Valid, File.ReadAllText(Settings));
    }

    [Fact]
    public void 一瞬だけ開かれていたなら読み直して読める()
    {
        File.WriteAllText(Settings, Valid);
        var holder = new FileStream(Settings, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var release = Task.Run(async () =>
        {
            await Task.Delay(60);
            holder.Dispose();
        });

        var settings = Load();
        release.Wait();

        Assert.Null(settings.LoadProblem);
        Assert.Equal("en", settings.Language);
    }

    [Fact]
    public void 大きすぎるファイルは読まず保存もしない()
    {
        using (var stream = new FileStream(Settings, FileMode.CreateNew))
        {
            stream.SetLength(SettingsStore.MaxBytes + 1L);
        }

        var settings = Load();

        var problem = Assert.IsType<SettingsLoadProblem>(settings.LoadProblem);
        Assert.Equal(SettingsLoadFailure.Damaged, problem.Failure);
        Assert.Null(problem.KeptPath);
        Assert.Empty(Copies());
        Assert.False(SettingsStore.TrySave(settings, Settings));
        Assert.Equal(SettingsStore.MaxBytes + 1L, new FileInfo(Settings).Length);
    }

    [Fact]
    public void 設定ファイルの名前のフォルダーがあれば保存しない()
    {
        Directory.CreateDirectory(Settings);
        var settings = Load();

        // 起動し直しても読めないので、「ほかのプログラムを閉じて」とは知らせない
        Assert.Equal(SettingsLoadFailure.Inaccessible, settings.LoadProblem?.Failure);
        Assert.False(SettingsStore.TrySave(settings, Settings));
        Assert.True(Directory.Exists(Settings));
    }

    // ------------------------------------------------------------------ 知らない項目 (#409)

    private JsonElement Saved() => JsonDocument.Parse(File.ReadAllText(Settings)).RootElement.Clone();

    private AppSettings LoadAndSave(string text)
    {
        File.WriteAllText(Settings, text);
        var settings = Load();
        Assert.Null(settings.LoadProblem);
        Assert.True(SettingsStore.TrySave(settings, Settings));
        return settings;
    }

    [Theory]
    [InlineData("\"text\"")]
    [InlineData("12")]
    [InlineData("123456789012345678901234567890.12345678901234567890e5")]
    [InlineData("0.1000000000000000055511151231257827")]
    [InlineData("true")]
    [InlineData("null")]
    [InlineData("""{ "a": [ 1, { "b": "c" } ] }""")]
    [InlineData("""[ 1, "two", null, [] ]""")]
    [InlineData("\"日本語\"")]
    public void 新しいバージョンが足した項目は保存で残る(string value)
    {
        var text = $$"""{ "Language": "en", "Added": {{value}} }""";
        LoadAndSave(text);

        var expected = JsonDocument.Parse(text).RootElement;
        Assert.True(JsonElement.DeepEquals(expected.GetProperty("Added"), Saved().GetProperty("Added")));
        Assert.Equal("en", Saved().GetProperty("Language").GetString());

        // 数は読み直さず、書かれた文字のまま残る
        if (expected.GetProperty("Added").ValueKind == JsonValueKind.Number)
        {
            Assert.Equal(value, Saved().GetProperty("Added").GetRawText());
        }

        // 2 回目の保存で増えも変わりもしない
        var first = File.ReadAllBytes(Settings);
        Assert.True(SettingsStore.TrySave(Load(), Settings));
        Assert.Equal(first, File.ReadAllBytes(Settings));
    }

    [Fact]
    public void 測った結果の中に足した項目も保存で残る()
    {
        LoadAndSave("""
            { "SpeedResult": { "Cores": 4, "AddedOuter": 1,
              "Kinds": [ { "FileCount": 2, "AddedInner": "x" }, null ] } }
            """);

        var speed = Saved().GetProperty("SpeedResult");
        Assert.Equal(1, speed.GetProperty("AddedOuter").GetInt32());
        Assert.Equal(4, speed.GetProperty("Cores").GetInt32());
        Assert.Equal("x", speed.GetProperty("Kinds")[0].GetProperty("AddedInner").GetString());
    }

    [Fact]
    public void 測った結果の種類が無くても読める()
    {
        LoadAndSave("""{ "SpeedResult": { "Kinds": null, "Added": 1 } }""");

        Assert.Equal(1, Saved().GetProperty("SpeedResult").GetProperty("Added").GetInt32());
    }

    [Fact]
    public void 知らない項目が無ければ保存する中身は変わらない()
    {
        LoadAndSave(Valid);

        var text = File.ReadAllText(Settings);
        Assert.DoesNotContain("Unknown", text);
        Assert.Equal(JsonSerializer.Serialize(new AppSettings
        {
            Language = "en",
            AiModel = "m",
            RecentArchives = [@"C:\a.zip"],
        }, new JsonSerializerOptions { WriteIndented = true }), text);
    }

    [Fact]
    public void 知らない項目が2回あれば後ろの値の1つにする()
    {
        LoadAndSave("""{ "Added": 1, "Added": 2 }""");

        var text = File.ReadAllText(Settings);
        Assert.Single(Saved().EnumerateObject(), p => p.Name == "Added");
        Assert.Equal(2, Saved().GetProperty("Added").GetInt32());
        Assert.Equal(text.IndexOf("\"Added\"", StringComparison.Ordinal), text.LastIndexOf("\"Added\"", StringComparison.Ordinal));
    }

    [Fact]
    public void 知っている項目が2回あれば今までどおり後ろの値を使う()
    {
        var settings = LoadAndSave("""{ "Language": "ja", "Language": "en" }""");

        Assert.Equal("en", settings.Language);
        Assert.Null(settings.Unknown);
    }

    [Theory]
    [InlineData("""{ "language": "ja", "Language": "en" }""")]
    [InlineData("""{ "SpeedResult": { "cores": 8, "Cores": 4 } }""")]
    [InlineData("""{ "SpeedResult": { "Kinds": [ { "fileCount": 9, "FileCount": 2 } ] } }""")]
    public void 知っている項目と大文字小文字だけ違う名前は残さない(string text)
    {
        LoadAndSave(text);

        var saved = File.ReadAllText(Settings);
        Assert.DoesNotContain("\"language\"", saved);
        Assert.DoesNotContain("\"cores\"", saved);
        Assert.DoesNotContain("\"fileCount\"", saved);
    }

    [Theory]
    [InlineData("$id")]
    [InlineData("$ref")]
    [InlineData("$type")]
    [InlineData("$values")]
    [InlineData("Unknown")]
    [InlineData("unknown")]
    [InlineData("LoadProblem")]
    public void 特別に見える名前もふつうの知らない項目として残る(string name)
    {
        var settings = LoadAndSave($$"""{ "{{name}}": [ 1 ] }""");

        Assert.Null(settings.LoadProblem);
        Assert.Equal(1, Saved().GetProperty(name)[0].GetInt32());
    }

    [Theory]
    [InlineData("""{ "Added": "\uD800" }""")]
    [InlineData("""{ "Added": { "\uDC00": 1 } }""")]
    [InlineData("""{ "SpeedResult": { "Added": "\uD800" } }""")]
    [InlineData("""{ "SpeedResult": { "Kinds": [ { "Added": [ "\uD800" ] } ] } }""")]
    public void 書き戻せない知らない項目は捨てて保存できる(string bad)
    {
        // 対になっていないサロゲートは、中身をほどかずに持つと読めるが、書き出せない
        var text = bad.Insert(1, """ "Kept": 1, "Language": "en", """);
        var settings = LoadAndSave(text);

        Assert.Equal("en", settings.Language);
        Assert.Equal(1, Saved().GetProperty("Kept").GetInt32());
        Assert.DoesNotContain(@"\uD800", File.ReadAllText(Settings), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"\uDC00", File.ReadAllText(Settings), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{ "Language": "\uD800" }""")]
    [InlineData("""{ "\uD800": 1 }""")]
    [InlineData("""{ "SpeedResult": { "\uD800": 1 } }""")]
    public void 知っている項目の値や型の中の名前の書き出せない文字は今までどおり読めない扱いにする(string text)
    {
        // 名前は、知っている項目と比べるために文字にほどかれるので、#409 より前から読めない
        File.WriteAllText(Settings, text);

        Assert.Equal(SettingsLoadFailure.Damaged, Load().LoadProblem?.Failure);
    }

    [Theory]
    [InlineData("""{ "Language": 5 }""")]
    [InlineData("""{ "Language": 5, "Language": "en" }""")]
    [InlineData("""{ "SpeedResult": { "Cores": "four" } }""")]
    public void 知っている項目の型が違えば今までどおり読めない扱いにする(string text)
    {
        File.WriteAllText(Settings, text);
        var settings = Load();

        Assert.Equal(SettingsLoadFailure.Damaged, settings.LoadProblem?.Failure);
        Assert.False(SettingsStore.TrySave(settings, Settings));
        Assert.Equal(text, File.ReadAllText(Settings));
    }

    [Fact]
    public void 深さが上限までなら残し超えれば今までどおり読めない扱いにする()
    {
        // 外側のオブジェクトで 1 段。既定の上限は 64 段
        static string Nested(int depth) => $$"""{ "Added": {{new string('[', depth - 1)}}{{new string(']', depth - 1)}} }""";

        LoadAndSave(Nested(64));
        Assert.Equal(JsonValueKind.Array, JsonDocument.Parse(File.ReadAllText(Settings)).RootElement.GetProperty("Added").ValueKind);

        File.WriteAllText(Settings, Nested(65));
        Assert.Equal(SettingsLoadFailure.Damaged, Load().LoadProblem?.Failure);
    }

    /// <summary>深い入れ子の中に 0 を並べる。字下げで 1 つずつ行になり、何十倍にも膨らむ。</summary>
    private static string Swelling(int zeros, int depth = 63)
        => new string('[', depth - 2) + string.Concat(Enumerable.Repeat("0,", zeros - 1)) + "0" + new string(']', depth - 2);

    [Fact]
    public void 字下げで膨らむ大きな知らない項目は書き出す前に捨てる()
    {
        // 16 MB 以内でも、書き出すと文字列の長さの上限を超えてメモリー不足になる形 (#409 の実装の確認)
        var text = $$"""{ "Language": "en", "Added": {{Swelling((SettingsStore.MaxBytes - 1000) / 2)}} }""";
        Assert.True(Encoding.UTF8.GetByteCount(text) <= SettingsStore.MaxBytes);

        var started = Environment.TickCount64;
        var settings = LoadAndSave(text);

        // 時間は、上限が効かずに何十秒もかかることを見つけるためのもの。遅い PC でも落ちないように広めにとる
        Assert.True(Environment.TickCount64 - started < 20_000);
        Assert.Null(settings.Unknown);
        Assert.Equal("en", Load().Language);
        Assert.False(Saved().TryGetProperty("Added", out _));
    }

    [Fact]
    public void 知っている項目が大きいときは書き出しを途中で止めて知らない項目を捨てる()
    {
        // 測った結果の種類が大量で、知らない項目が 1 つだけ。全体を書き終えてから大きさを見ると、何 GB も使う
        var kinds = string.Join(",", Enumerable.Repeat("{}", 300_000));
        File.WriteAllText(Settings, $$"""{ "Added": 1, "SpeedResult": { "Kinds": [ {{kinds}} ] } }""");

        var started = Environment.TickCount64;
        var settings = Load();

        Assert.True(Environment.TickCount64 - started < 20_000);
        Assert.Null(settings.LoadProblem);
        Assert.Null(settings.Unknown);

        // 知っている項目だけでも 16 MB を超えるので、保存せずに前のファイルを残す
        var before = File.ReadAllBytes(Settings);
        Assert.False(SettingsStore.TrySave(settings, Settings));
        Assert.Equal(before, File.ReadAllBytes(Settings));
    }

    [Fact]
    public void 知らない項目は上限の大きさまでなら残す()
    {
        var value = $"\"{new string('a', SettingsStore.UnknownMaxBytes - "Added".Length - 2)}\"";
        LoadAndSave($$"""{ "Added": {{value}} }""");
        Assert.Equal(value, Saved().GetProperty("Added").GetRawText());

        LoadAndSave($$"""{ "Added": {{value.Insert(1, "a")}} }""");
        Assert.False(Saved().TryGetProperty("Added", out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 知らない項目は上限の数までなら残す(bool inKinds)
    {
        string Text(int count) => inKinds
            ? $$"""{ "SpeedResult": { "Kinds": [ {{string.Join(",", Enumerable.Range(0, count).Select(i => $$"""{ "A{{i}}": 1 }"""))}} ] } }"""
            : $$"""{ {{string.Join(",", Enumerable.Range(0, count).Select(i => $"\"A{i}\": 1"))}} }""";

        LoadAndSave(Text(SettingsStore.UnknownMaxCount));
        Assert.Contains($"\"A{SettingsStore.UnknownMaxCount - 1}\"", File.ReadAllText(Settings));

        LoadAndSave(Text(SettingsStore.UnknownMaxCount + 1));
        Assert.DoesNotContain("\"A0\"", File.ReadAllText(Settings));
    }

    [Fact]
    public void 書き出すと全体で大きすぎるときは知らない項目を捨てて保存できる()
    {
        // 知らない項目は上限の内でも、字下げで膨らみ、知っている項目と合わせると 16 MB を超える
        var model = new string('m', 10 * 1024 * 1024);
        var text = $$"""{ "AiModel": "{{model}}", "Added": {{Swelling(SettingsStore.UnknownMaxBytes / 2 - 100)}} }""";

        var settings = LoadAndSave(text);

        Assert.Null(settings.Unknown);
        Assert.Equal(model, Load().AiModel);
    }

    [Fact]
    public void 多すぎる書き出せない知らない項目も時間をかけずに捨てる()
    {
        var bad = string.Join(",", Enumerable.Range(0, 20_000).Select(i => $$"""{ "B": "\uD800" }"""));
        var started = Environment.TickCount64;
        var settings = LoadAndSave($$"""{ "SpeedResult": { "Kinds": [ {{bad}} ] } }""");

        Assert.True(Environment.TickCount64 - started < 20_000);
        Assert.All(settings.SpeedResult!.Kinds, k => Assert.Null(k.Unknown));
    }

    [Fact]
    public void 保存する中身が大きすぎれば書かずに前の設定を残す()
    {
        File.WriteAllText(Settings, Valid);
        var settings = Load();
        settings.AiModel = new string('a', SettingsStore.MaxBytes);

        Assert.False(SettingsStore.TrySave(settings, Settings));
        Assert.Equal(Valid, File.ReadAllText(Settings));
    }
}
