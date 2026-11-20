using System.Text;
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
}
