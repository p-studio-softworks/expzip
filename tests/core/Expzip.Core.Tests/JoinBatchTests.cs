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
        "アラビア語 \u0627\u0644\u0639\u0631\u0628\u064A\u0629.bin",
        "ヘブライ語 \u05E2\u05D1\u05E8\u05D9\u05EA.bin",
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

    /// <summary>文字の向きを変える制御文字 12 個 (#373)。</summary>
    public static TheoryData<char> BidiControls =>
    [
        '\u202A', '\u202B', '\u202C', '\u202D', '\u202E',
        '\u2066', '\u2067', '\u2068', '\u2069',
        '\u200E', '\u200F', '\u061C',
    ];

    [Theory]
    [MemberData(nameof(BidiControls))]
    public void 向きを変える文字は除きほかの文字は残す(char control)
    {
        // ディスクには何も作らない。偽装した名前の実行ファイルは Defender に止められる (#373)
        var name = $"rtl {control}txt.bin";

        Assert.Equal("rtl txt.bin", Inspection.BidiControl.Strip(name));
        Assert.False(JoinBatch.CanMake(name));
        Assert.Equal(Localization.Strings.SplitBatchBidi, JoinBatch.WhyNot(name));
        Assert.Equal(Path.Combine("dir", "rtl txt.bin.exe"), FileSplitter.JoinerPath(Path.Combine("dir", name)));
    }

    [Theory]
    [InlineData("\u0627\u0644\u0639\u0631\u0628\u064A\u0629.bin")]
    [InlineData("\u05E2\u05D1\u05E8\u05D9\u05EA.bin")]
    [InlineData("zero\u200Bwidth\u2060.bin")]
    [InlineData("line\u2028sep.bin")]
    public void アラビア語やヘブライ語の文字とゼロ幅の文字は除かない(string name)
    {
        Assert.Equal(name, Inspection.BidiControl.Strip(name));
        Assert.True(JoinBatch.CanMake(name));
        Assert.Equal(Path.Combine("dir", name + ".exe"), FileSplitter.JoinerPath(Path.Combine("dir", name)));
    }

    [Fact]
    public void 向きを変える文字を複数含んでも全部除く()
    {
        Assert.Equal("abc.bin", Inspection.BidiControl.Strip("\u202Ea\u2066b\u200Fc\u061C.bin\u202C"));
    }

    [Fact]
    public void 向きを変える文字だけの名前は分割しない()
    {
        using var place = new Place();
        var source = place.Source("\u202E\u202D", 300 * 1024);

        Assert.Throws<JoinerNameException>(() => FileSplitter.JoinerPath(Path.Combine(place.Output, "\u202E\u202D")));
        var error = Assert.Throws<JoinerNameException>(() => FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk));

        Assert.Null(error.Taken);
        Assert.Equal(Localization.Strings.SplitNameOnlyBidi, Localization.Strings.Reason(error));
        Assert.Empty(Directory.EnumerateFileSystemEntries(place.Output));
    }

    [Fact]
    public void 向きを変える文字があると結合用のプログラムの名前から除きバッチファイルは作らない()
    {
        using var place = new Place();
        var name = "rtl \u202Etxt.bin";
        var source = place.Source(name, 300 * 1024);

        var result = FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk, makeBatch: true);

        Assert.Equal("rtl txt.bin.exe", result.JoinerName);
        Assert.Null(result.BatchName);
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*.cmd"));
        Assert.True(File.Exists(Path.Combine(place.Output, name + ".001")));
        Assert.Single(Directory.EnumerateFiles(place.Output, "*.exe"));

        // 結合用のプログラムは、末尾に書いた元の名前で分割ファイルを探し、元の名前で結合する
        var (code, output) = RunJoiner(Path.Combine(place.Output, result.JoinerName));
        Assert.True(code == 0, output);
        Assert.Equal(place.Bytes(300 * 1024), File.ReadAllBytes(Path.Combine(place.Output, name)));
    }

    [Fact]
    public void 除いた名前に別のファイルがあれば何も書かずに止める()
    {
        using var place = new Place();
        var source = place.Source("rtl \u202Etxt.bin", 300 * 1024);
        var other = Path.Combine(place.Output, "rtl txt.bin.exe");
        File.WriteAllText(other, "本人のファイル");

        var listed = Assert.Throws<JoinerNameException>(() => FileSplitter.ExistingOutputs(source, place.Output, FileSplitter.MinimumChunk));
        var error = Assert.Throws<JoinerNameException>(() => FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk));

        Assert.Equal("rtl txt.bin.exe", listed.Taken);
        Assert.Equal(Localization.Strings.SplitJoinerNameTaken("rtl txt.bin.exe"), Localization.Strings.Reason(error));
        Assert.Equal("本人のファイル", File.ReadAllText(other));
        Assert.Single(Directory.EnumerateFileSystemEntries(place.Output));
    }

    [Fact]
    public void 除いた名前に別の元の名前の結合用のプログラムがあれば止める()
    {
        // 《向き》の無い「rtl txt.bin」を分割したときの結合用のプログラム。置き換えると、そちらを元に戻せなくなる
        using var place = new Place();
        var plain = place.Source("rtl txt.bin", 300 * 1024);
        FileSplitter.Split(plain, place.Output, FileSplitter.MinimumChunk);
        var joiner = Path.Combine(place.Output, "rtl txt.bin.exe");
        var before = File.ReadAllBytes(joiner);

        var source = place.Source("rtl \u202Etxt.bin", 200 * 1024);
        Assert.Throws<JoinerNameException>(() => FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk));

        Assert.Equal(before, File.ReadAllBytes(joiner));
        Assert.Empty(Directory.EnumerateFiles(place.Output, "rtl \u202E*"));
    }

    [Fact]
    public void 分割の間に除いた名前のファイルが置かれても置き換えず名前を挙げて止める()
    {
        using var place = new Place();
        var source = place.Source("rtl \u202Etxt.bin", 300 * 1024);
        var other = Path.Combine(place.Output, "rtl txt.bin.exe");
        var placed = new PlaceOnce(other);

        var error = Assert.Throws<JoinerNameException>(
            () => FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk, placed));

        Assert.Equal("rtl txt.bin.exe", error.Taken);
        Assert.Equal("あとから置かれた", File.ReadAllText(other));
        Assert.Single(Directory.EnumerateFileSystemEntries(place.Output));
    }

    /// <summary>最初の経過の知らせで、その名前にファイルを置く。分割の間に別のプログラムが置いた形を作る。</summary>
    private sealed class PlaceOnce(string path) : IProgress<SplitProgress>
    {
        public void Report(SplitProgress value)
        {
            if (!File.Exists(path))
            {
                File.WriteAllText(path, "あとから置かれた");
            }
        }
    }

    [Fact]
    public void 除いた名前にフォルダーがあれば止める()
    {
        using var place = new Place();
        var source = place.Source("rtl \u202Etxt.bin", 300 * 1024);
        Directory.CreateDirectory(Path.Combine(place.Output, "rtl txt.bin.exe"));

        Assert.Throws<JoinerNameException>(() => FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk));
        Assert.Single(Directory.EnumerateFileSystemEntries(place.Output));
    }

    [Fact]
    public void 同じ元のファイルをもう一度分割すると前の結合用のプログラムを尋ねて置き換える()
    {
        using var place = new Place();
        var name = "rtl \u202Etxt.bin";
        var source = place.Source(name, 300 * 1024);
        FileSplitter.Split(source, place.Output, FileSplitter.MinimumChunk);
        var joiner = Path.Combine(place.Output, "rtl txt.bin.exe");

        var existing = FileSplitter.ExistingOutputs(source, place.Output, 256 * 1024);
        var result = FileSplitter.Split(source, place.Output, 256 * 1024);

        Assert.Contains(joiner, existing);
        Assert.Equal(2, result.Parts);
        Assert.Equal("rtl txt.bin.exe", result.JoinerName);
    }

    [Fact]
    public void 置き換えない指定のファイルは名前にあるものを置き換えずに全部をやめる()
    {
        using var place = new Place();
        var first = Path.Combine(place.Output, "first.bin");
        File.WriteAllText(first, "前の first");

        var files = new List<Archives.AtomicFile>
        {
            Archives.AtomicFile.Create(first),
            Archives.AtomicFile.Create(Path.Combine(place.Output, "third.bin"), overwrite: false),
        };
        try
        {
            files[0].Stream.Write("新しい"u8);
            files[1].Stream.Write("新しい"u8);

            // 書いている間に置かれたもの
            File.WriteAllText(Path.Combine(place.Output, "third.bin"), "あとから置かれた");

            Assert.Throws<IOException>(() => Archives.AtomicFile.CommitAll(files));
        }
        finally
        {
            files.ForEach(static f => f.Dispose());
        }

        Assert.Equal("前の first", File.ReadAllText(first));
        Assert.Equal("あとから置かれた", File.ReadAllText(Path.Combine(place.Output, "third.bin")));
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

    /// <summary>結合用のプログラムを動かす。-q で、終わったときのメッセージを出さない。</summary>
    private static (int Code, string Output) RunJoiner(string joiner)
    {
        var info = new ProcessStartInfo(joiner, "-q")
        {
            WorkingDirectory = Path.GetTempPath(),
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(120_000), "結合用のプログラムが終わらない");
        return (process.ExitCode, $"終了コード {process.ExitCode}");
    }

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
            // 文字を除く処理や一覧が壊れていたら、分割する前に止める。一覧を使わずに確かめるので、一覧が壊れても効く。
            // 偽装した名前の exe をディスクに作ると Defender が通知する (#373)
            if (name.Contains('\u202E'))
            {
                try
                {
                    Assert.DoesNotContain('\u202E', FileSplitter.JoinerPath(Path.Combine(Output, name)));
                }
                catch (JoinerNameException)
                {
                    // 名前が向きを変える文字だけのときは、名前を決めずに止まる
                }
            }

            var path = Path.Combine(Root, "src", name);
            File.WriteAllBytes(path, Bytes(size));
            return path;
        }

        /// <summary><see cref="Source"/> が書く中身。</summary>
        public byte[] Bytes(int size)
        {
            var bytes = new byte[size];
            new Random(size).NextBytes(bytes);
            return bytes;
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
