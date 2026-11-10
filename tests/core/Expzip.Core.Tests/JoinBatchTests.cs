using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Expzip.Splitting;

namespace Expzip.Tests;

/// <summary>
/// 結合用のバッチファイル (#343)。受け取った人の PC で動くものなので、元のファイル名で命令が走らないこと、
/// 既にあるファイルを置き換えないこと、壊れた分割ファイルから結合したものを残さないことを確かめる。
/// </summary>
public sealed class JoinBatchTests
{
    /// <summary>cmd が命令として読む文字を含む名前。命令が走ると pwned フォルダーができる。</summary>
    public static TheoryData<string> HostileNames =>
    [
        "a & mkdir pwned & .bin",
        "a&mkdir pwned.bin",
        "a ^ b.bin",
        "a ^& mkdir pwned.bin",
        "a !PATH! b.bin",
        "a (b) c.bin",
        "a) & mkdir pwned & (.bin",
        "a,b;c=d.bin",
        "a'b.bin",
        "a`b.bin",
        "-q.bin",
        "日本語のファイル名.bin",
        "表ソ能.bin",
        "emoji \U0001F600.bin",
        "rtl ‮txt.bin",
        "no-extension",
        "two words",
        "a+b.bin",
        "a,,b.bin",
        "a $(mkdir pwned) b.bin",
        "a `b` c.bin",
    ];

    /// <summary>フォルダーの名前にも、cmd が命令として読む文字を入れる。</summary>
    public static TheoryData<string> HostileFolders =>
    [
        "x & mkdir pwned & y",
        "x) & mkdir pwned & (y",
        "x !PATH! y",
        "x ^ y",
    ];

    [Theory]
    [MemberData(nameof(HostileFolders))]
    public void フォルダーの名前でも命令は走らず結合できる(string folder)
    {
        using var place = new Place(folder);
        var (source, batch) = place.Split("data.bin", 300 * 1024);

        var (code, output) = Run(batch);

        Assert.True(code == 0, output);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(place.Output, "data.bin")));
        place.AssertNoPwned();
    }

    [Fact]
    public void うまくいったときはDoneの1行だけを出す()
    {
        // 「見つかりません」が並ぶと、失敗したように見える
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);

        var (code, output) = Run(batch);

        Assert.Equal(0, code);
        Assert.Equal("Done. The file was joined and verified.", output.Trim());
    }

    [Theory]
    [InlineData("findstr.cmd")]
    [InlineData("findstr.bat")]
    [InlineData("certutil.cmd")]
    [InlineData("certutil.bat")]
    [InlineData("more.cmd")]
    [InlineData("more.bat")]
    public void 隣に置かれた同じ名前のプログラムを実行しない(string planted)
    {
        // 置かれたものが動くと、照合を素通りさせられる。動いたら印のファイルを作り、成功を返す
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        File.WriteAllText(Path.Combine(place.Output, planted), "@echo planted>\"%~dp0planted.txt\"\r\n@exit /b 0\r\n");
        var part = Path.Combine(place.Output, "data.bin.002");
        var bytes = File.ReadAllBytes(part);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(part, bytes);

        var (code, output) = Run(batch);

        Assert.False(File.Exists(Path.Combine(place.Output, "planted.txt")), $"{planted} が実行された");
        Assert.Equal(1, code);
        Assert.Contains("does not match", output);
        Assert.False(File.Exists(Path.Combine(place.Output, "data.bin")));
    }

    [Fact]
    public void ダブルクリックと同じ起動でも結合できる()
    {
        using var place = new Place("x & mkdir pwned & y");
        var (source, batch) = place.Split("a & mkdir pwned & .bin", 300 * 1024);

        using var process = Process.Start(new ProcessStartInfo(batch, "-q") { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden })!;
        Assert.True(process.WaitForExit(120_000), "バッチファイルが終わらない");

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(place.Output, "a & mkdir pwned & .bin")));
        place.AssertNoPwned();
    }

    [Fact]
    public void 照合の結果を書くファイルが既にあれば触らずに1を返す()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        var sha = Path.Combine(place.Output, WorkNameIn(batch) + ".sha");
        File.WriteAllText(sha, "USER DATA");

        var (code, output) = Run(batch);

        Assert.Equal(1, code);
        Assert.Contains(".sha) already exists", output);
        Assert.Equal("USER DATA", File.ReadAllText(sha));
        Assert.False(File.Exists(Path.Combine(place.Output, "data.bin")));
    }

    [Theory]
    [InlineData("a $(mkdir pwned) b.bin")]
    [InlineData("a `b` c.bin")]
    public void PowerShellで式になる名前なら手で打つ行を書かない(string name)
    {
        using var place = new Place();
        var (_, batch) = place.Split(name, 300 * 1024);
        var manual = ManualPart(batch);

        Assert.DoesNotContain("copy /b", manual);
        Assert.DoesNotContain("certutil", manual);
        Assert.Contains("7-Zip", manual);
        Assert.Contains("SHA-256: ", manual);
    }

    [Theory]
    [MemberData(nameof(HostileNames))]
    public void どんな名前でも命令は走らず結合できる(string name)
    {
        using var place = new Place();
        var (source, batch) = place.Split(name, 300 * 1024);

        var (code, output) = Run(batch);

        Assert.True(code == 0, output);
        Assert.Equal(File.ReadAllBytes(source), File.ReadAllBytes(Path.Combine(place.Output, name)));
        place.AssertNoPwned();
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*" + JoinBatch.WorkSuffix + "*"));
    }

    [Fact]
    public void 元の名前のファイルが既にあれば置き換えずに1を返す()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        var users = Path.Combine(place.Output, "data.bin");
        File.WriteAllText(users, "USER DATA");

        var (code, output) = Run(batch);

        Assert.Equal(1, code);
        Assert.Contains("already exists", output);
        Assert.Equal("USER DATA", File.ReadAllText(users));
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*" + JoinBatch.WorkSuffix + "*"));
    }

    [Fact]
    public void 作業用の名前が既にあれば触らずに1を返す()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        var work = Path.Combine(place.Output, WorkNameIn(batch));
        File.WriteAllText(work, "USER DATA");

        var (code, output) = Run(batch);

        Assert.Equal(1, code);
        Assert.Contains("work file", output);
        Assert.Equal("USER DATA", File.ReadAllText(work));
        Assert.False(File.Exists(Path.Combine(place.Output, "data.bin")));
    }

    [Fact]
    public void 分割ファイルが足りなければ結合せずに1を返す()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        File.Delete(Path.Combine(place.Output, "data.bin.002"));

        var (code, output) = Run(batch);

        Assert.Equal(1, code);
        Assert.Contains("Part 2 of 3 (.002) was not found", output);
        Assert.False(File.Exists(Path.Combine(place.Output, "data.bin")));
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*" + JoinBatch.WorkSuffix + "*"));
    }

    [Fact]
    public void 分割ファイルが壊れていれば結合したものを残さずに1を返す()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        var part = Path.Combine(place.Output, "data.bin.002");
        var bytes = File.ReadAllBytes(part);
        bytes[100] ^= 0xFF;
        File.WriteAllBytes(part, bytes);

        var (code, output) = Run(batch);

        Assert.Equal(1, code);
        Assert.Contains("does not match", output);
        Assert.False(File.Exists(Path.Combine(place.Output, "data.bin")));
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*" + JoinBatch.WorkSuffix + "*"));
    }

    [Fact]
    public void 分割ファイルの大きさが違えば結合したものを残さずに1を返す()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        File.AppendAllText(Path.Combine(place.Output, "data.bin.003"), "x");

        var (code, _) = Run(batch);

        Assert.Equal(1, code);
        Assert.False(File.Exists(Path.Combine(place.Output, "data.bin")));
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*" + JoinBatch.WorkSuffix + "*"));
    }

    [Fact]
    public void 名前を変えたバッチファイルは分割ファイルを見つけられず1を返す()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        var renamed = Path.Combine(place.Output, "other.cmd");
        File.Move(batch, renamed);

        var (code, output) = Run(renamed);

        Assert.Equal(1, code);
        Assert.Contains("was not found", output);
        Assert.False(File.Exists(Path.Combine(place.Output, "other")));
        Assert.False(File.Exists(Path.Combine(place.Output, "data.bin")));
    }

    [Fact]
    public void どれか1つの分割ファイルにMotWがあれば結合したものにZoneId3を付ける()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        File.WriteAllText(Path.Combine(place.Output, "data.bin.002:Zone.Identifier"), "[ZoneTransfer]\r\nZoneId=4\r\n");

        var (code, output) = Run(batch);

        Assert.True(code == 0, output);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(Path.Combine(place.Output, "data.bin:Zone.Identifier")));
    }

    [Fact]
    public void バッチファイル自身にMotWがあれば結合したものにZoneId3を付ける()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);
        File.WriteAllText(batch + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");

        var (code, output) = Run(batch);

        Assert.True(code == 0, output);
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(Path.Combine(place.Output, "data.bin:Zone.Identifier")));
    }

    [Fact]
    public void MotWが無ければ結合したものにも付けない()
    {
        using var place = new Place();
        var (_, batch) = place.Split("data.bin", 300 * 1024);

        var (code, output) = Run(batch);

        Assert.True(code == 0, output);
        Assert.False(File.Exists(Path.Combine(place.Output, "data.bin:Zone.Identifier")));
    }

    [Fact]
    public void 分割ファイルが1000個を超えても結合できる()
    {
        // 1 行にまとめると行の長さの上限を超える数。1000 個目から 4 桁になる
        using var place = new Place();
        const int count = 1002;
        var name = "many parts with a fairly long name to make the command line long.bin";
        var target = Path.Combine(place.Output, name);
        var all = new MemoryStream();

        for (var i = 1; i <= count; i++)
        {
            var piece = Encoding.ASCII.GetBytes($"piece {i};");
            File.WriteAllBytes(FileSplitter.PartPath(target, i), piece);
            all.Write(piece);
        }

        var batch = target + ".cmd";
        using (var stream = File.Create(batch))
        {
            JoinBatch.Write(stream, name, count, all.Length, SHA256.HashData(all.ToArray()), JoinBatch.NewWorkName());
        }

        var (code, output) = Run(batch);

        Assert.True(code == 0, output);
        Assert.Equal(all.ToArray(), File.ReadAllBytes(target));

        // 手で打つ行はコマンド プロンプトの上限 (8191 文字) を超えるので書かない
        Assert.DoesNotContain("copy /b", ManualPart(batch));
    }

    /// <summary>最後の exit /b より後ろの、人が読む欄。</summary>
    private static string ManualPart(string batch)
    {
        var text = File.ReadAllText(batch, Encoding.UTF8);
        return text[text.IndexOf("exit /b %rc%\r\n", StringComparison.Ordinal)..];
    }

    [Fact]
    public void 実行される部分はASCIIだけで手順の欄に名前が入る()
    {
        using var place = new Place();
        var (_, batch) = place.Split("日本語 & mkdir pwned.bin", 300 * 1024);
        var text = File.ReadAllText(batch, Encoding.UTF8);
        var bytes = File.ReadAllBytes(batch);

        // BOM を付けない。付けると 1 行目が読めない
        Assert.NotEqual(0xEF, bytes[0]);

        var end = text.IndexOf("exit /b %rc%\r\n", StringComparison.Ordinal);
        Assert.True(end > 0);
        var executed = text[..end];
        Assert.All(executed, c => Assert.True(c < 0x80, $"ASCII でない文字: U+{(int)c:X4}"));
        Assert.DoesNotContain("mkdir", executed);

        // 行の終わりは CRLF だけ。LF だけの行があると、cmd の goto がラベルを見落とす
        Assert.DoesNotContain('\n', text.Replace("\r\n", string.Empty));

        var manual = text[end..];
        Assert.Contains("copy /b \"日本語 & mkdir pwned.bin.001\"+\"日本語 & mkdir pwned.bin.002\"+\"日本語 & mkdir pwned.bin.003\" \"日本語 & mkdir pwned.bin\"", manual);

        // 後ろの欄にラベルのように読める行があると、goto で届いてしまう
        Assert.DoesNotContain(manual.Split("\r\n"), line => line.TrimStart(' ', '\t', ';', '=', ',').StartsWith(':'));
    }

    [Theory]
    [InlineData("a %OS% b.bin")]
    [InlineData("100%.bin")]
    [InlineData("a %~dp0 %1 b.bin")]
    public void 名前にパーセントがあるとバッチファイルを作らない(string name)
    {
        // Windows は起動するときにファイル名の %OS% を置き換えるので、バッチファイルが自分を見つけられない
        using var place = new Place();
        var source = place.Source(name, 300 * 1024);

        var result = FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk, makeBatch: true);

        Assert.Null(result.BatchName);
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*.cmd"));
        Assert.True(File.Exists(Path.Combine(place.Output, name + ".exe")));
        Assert.DoesNotContain(FileSplitter.ExistingOutputs(source, place.Output, FileSplitter.MinimumChunk, makeBatch: true),
            static p => p.EndsWith(".cmd", StringComparison.Ordinal));
    }

    [Fact]
    public void デフォルトではバッチファイルを作らない()
    {
        using var place = new Place();
        var source = place.Source("data.bin", 300 * 1024);

        var result = FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk);

        Assert.Null(result.BatchName);
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*.cmd"));
    }

    /// <summary>作業用の名前を、バッチファイルから読む。</summary>
    private static string WorkNameIn(string batch)
        => File.ReadAllLines(batch).Single(l => l.StartsWith("set \"work=", StringComparison.Ordinal))["set \"work=".Length..^1];

    /// <summary>ダブルクリックと同じく cmd /c で動かす。-q で pause しない。</summary>
    private static (int Code, string Output) Run(string batch)
    {
        var info = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/d /c \"\"{batch}\" -q\"",
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };

        // ダブルクリックで起動したときと同じく、今いるフォルダーのプログラムも探す状態で動かす。
        // 開発の PC で付けていることがあり、受け継ぐと、隣に置かれたプログラムを実行する問題が隠れる
        info.Environment.Remove("NoDefaultCurrentDirectoryInExePath");

        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(120_000), "バッチファイルが終わらない");
        return (process.ExitCode, output.Result + error.Result);
    }

    /// <summary>テストごとのフォルダー。</summary>
    private sealed class Place : IDisposable
    {
        public Place(string output = "out")
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"))).FullName;
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            Output = Directory.CreateDirectory(Path.Combine(Root, output)).FullName;
        }

        public string Root { get; }

        public string Output { get; }

        public string Source(string name, int size)
        {
            var path = Path.Combine(Root, "src", name);
            var bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        /// <summary>バッチファイルも作って分割する。300 KB なら 3 個になる。</summary>
        public (string Source, string Batch) Split(string name, int size)
        {
            var source = Source(name, size);
            var result = FileSplitter.Split(source, Output, FileSplitter.MinimumChunk, makeBatch: true);
            Assert.NotNull(result.BatchName);
            return (source, Path.Combine(Output, result.BatchName!));
        }

        public void AssertNoPwned()
        {
            Assert.Empty(Directory.EnumerateDirectories(Root, "pwned", SearchOption.AllDirectories));
            Assert.False(Directory.Exists(Path.Combine(Path.GetTempPath(), "pwned")), "作業フォルダーに pwned ができた");
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }
}
