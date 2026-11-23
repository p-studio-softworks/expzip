using Expzip.Configuration;

namespace Expzip.Tests;

/// <summary>
/// Store 版の知らせに、エクスプローラーで開ける実際の場所を出す (#406)。
/// アプリから見た %LOCALAPPDATA%\Expzip は、Windows がパッケージ専用の場所に振り替えていて、エクスプローラーでは見つからない。
/// </summary>
public sealed class DataFolderTests : IDisposable
{
    private const string Local = @"C:\Users\u\AppData\Local";

    private const string Redirected = @"C:\Users\u\AppData\Local\Packages\Pstudio.Expzip_abc\LocalCache\Local";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", "datafolder-" + Guid.NewGuid().ToString("N"));

    public DataFolderTests() => Directory.CreateDirectory(_folder);

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

    private static string Shown(string path, Func<string, bool> exists, bool? same = true)
        => DataFolder.ForDisplay(path, Local, Redirected, exists, (_, _) => same);

    // ------------------------------------------------------------------ 振り替え先を出す

    [Fact]
    public void 振り替え先にあればそちらを出す()
    {
        var shown = Shown(@"C:\Users\u\AppData\Local\Expzip\Expzip.rules.json",
            static p => p == Redirected + @"\Expzip\Expzip.rules.json");

        Assert.Equal(Redirected + @"\Expzip\Expzip.rules.json", shown);
    }

    [Fact]
    public void フォルダーも振り替え先を出す()
    {
        var shown = Shown(@"C:\Users\u\AppData\Local\Expzip", static p => p == Redirected + @"\Expzip");

        Assert.Equal(Redirected + @"\Expzip", shown);
    }

    [Fact]
    public void 大文字小文字が違っても振り替え先を出す()
    {
        var shown = Shown(@"c:\users\U\appdata\local\Expzip\Expzip.rules.json", static _ => true);

        Assert.Equal(Redirected + @"\Expzip\Expzip.rules.json", shown);
    }

    [Fact]
    public void 同じものか比べられなければ振り替え先を出す()
    {
        var shown = Shown(@"C:\Users\u\AppData\Local\Expzip\Expzip.rules.json", static _ => true, same: null);

        Assert.Equal(Redirected + @"\Expzip\Expzip.rules.json", shown);
    }

    // ------------------------------------------------------------------ アプリから見た場所を出す

    [Fact]
    public void 振り替え先に無ければアプリから見た場所を出す()
    {
        var path = @"C:\Users\u\AppData\Local\Expzip\Expzip.settings.json";

        Assert.Equal(path, Shown(path, static _ => false));
    }

    [Fact]
    public void 振り替え先にあっても別のものならアプリから見た場所を出す()
    {
        // 両方に同じ名前のものがあるとき、使われていないほうを出して、そちらを直させたり削除させたりしない
        var path = @"C:\Users\u\AppData\Local\Expzip\Expzip.rules.json";

        Assert.Equal(path, Shown(path, static _ => true, same: false));
    }

    [Fact]
    public void LOCALAPPDATAの外はそのまま()
    {
        var path = @"D:\Tools\Expzip\Expzip.rules.json";
        var asked = false;

        Assert.Equal(path, Shown(path, p => asked = true));
        Assert.False(asked);
    }

    [Fact]
    public void 名前の始まりが同じだけのフォルダーは振り替えない()
    {
        // C:\Users\u\AppData\LocalLow は %LOCALAPPDATA% の中ではない
        var path = @"C:\Users\u\AppData\LocalLow\Expzip\Expzip.rules.json";

        Assert.Equal(path, Shown(path, static _ => true));
    }

    [Fact]
    public void パッケージでなければそのまま()
    {
        // テストはパッケージの外で動く
        var path = Path.Combine(_folder, "Expzip.rules.json");

        Assert.Equal(path, DataFolder.ForDisplay(path));
        Assert.Null(DataFolder.ForDisplay(null));
    }

    // ------------------------------------------------------------------ 同じものか

    [Fact]
    public void 同じファイルは同じと分かる()
    {
        var a = Path.Combine(_folder, "a.json");
        File.WriteAllText(a, "a");

        Assert.True(DataFolder.SameFile(a, Path.Combine(_folder, ".", "a.json")));
    }

    [Fact]
    public void 中身が同じでも別のファイルは別と分かる()
    {
        var a = Path.Combine(_folder, "a.json");
        var b = Path.Combine(_folder, "b.json");
        File.WriteAllText(a, "same");
        File.WriteAllText(b, "same");

        Assert.False(DataFolder.SameFile(a, b));
    }

    [Fact]
    public void フォルダーも比べられる()
    {
        var sub = Directory.CreateDirectory(Path.Combine(_folder, "sub")).FullName;

        Assert.True(DataFolder.SameFile(sub, sub + Path.DirectorySeparatorChar));
        Assert.False(DataFolder.SameFile(sub, _folder));
    }

    [Fact]
    public void ほかのプログラムが共有なしで開いていても比べられる()
    {
        var a = Path.Combine(_folder, "a.json");
        File.WriteAllText(a, "a");
        using var holder = new FileStream(a, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        Assert.True(DataFolder.SameFile(a, a));
    }

    [Fact]
    public void 無ければ比べられない()
    {
        var a = Path.Combine(_folder, "a.json");
        File.WriteAllText(a, "a");

        Assert.Null(DataFolder.SameFile(a, Path.Combine(_folder, "missing.json")));
    }
}
