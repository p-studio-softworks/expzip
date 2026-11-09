using System.Diagnostics;
using System.IO.Compression;
using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 自己解凍書庫は、既にあるファイルを確かめずに置き換えない (#358)。
/// -q で動かすと尋ねないので、「いいえ」と同じく置き換えずに残し、終了コード 3 を返す。
/// </summary>
public sealed class SfxTests
{
    [Fact]
    public void 既にあるファイルが無ければすべて展開して0を返す()
    {
        using var place = new Place();
        var exe = place.MakeSfx(("a.txt", "NEW A"), ("d/b.txt", "NEW B"));

        Assert.Equal(0, Run(exe));

        Assert.Equal("NEW A", File.ReadAllText(Path.Combine(place.Unpacked, "a.txt")));
        Assert.Equal("NEW B", File.ReadAllText(Path.Combine(place.Unpacked, "d", "b.txt")));
    }

    [Fact]
    public void 既にあるファイルは置き換えずに残して3を返す()
    {
        using var place = new Place();
        var exe = place.MakeSfx(("a.txt", "NEW A"), ("new.txt", "NEW"));
        Directory.CreateDirectory(place.Unpacked);
        File.WriteAllText(Path.Combine(place.Unpacked, "a.txt"), "USER DATA");

        Assert.Equal(3, Run(exe));

        Assert.Equal("USER DATA", File.ReadAllText(Path.Combine(place.Unpacked, "a.txt")));
        Assert.Equal("NEW", File.ReadAllText(Path.Combine(place.Unpacked, "new.txt")));
        Assert.Empty(Directory.EnumerateFiles(place.Unpacked, "~*.tmp"));
    }

    [Fact]
    public void 短い名前や末尾のピリオドの項目でも既にあるファイルを置き換えない()
    {
        // Windows は短い名前 (LONGFI~1.TXT) や末尾のピリオド (e.txt.) を、既にあるファイルと同じものとして開く
        using var place = new Place();
        Directory.CreateDirectory(place.Unpacked);
        var users = Path.Combine(place.Unpacked, "longfilename.txt");
        File.WriteAllText(users, "USER DATA");
        File.WriteAllText(Path.Combine(place.Unpacked, "e.txt"), "USER E");
        var shortName = ExtractionTests.ShortNameOf(users) is { } s ? Path.GetFileName(s) : null;
        Assert.SkipWhen(shortName is null || string.Equals(shortName, "longfilename.txt", StringComparison.OrdinalIgnoreCase),
            "この場所では 8.3 形式の短い名前が作られない");
        var exe = place.MakeSfx((shortName!, "FROM ARCHIVE"), ("e.txt.", "FROM ARCHIVE"));

        Assert.Equal(3, Run(exe));

        Assert.Equal("USER DATA", File.ReadAllText(users));
        Assert.Equal("USER E", File.ReadAllText(Path.Combine(place.Unpacked, "e.txt")));
        Assert.Equal(2, Directory.EnumerateFiles(place.Unpacked).Count());
    }

    /// <summary>-q で動かし、終了コードを返す。</summary>
    private static int Run(string exe)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, "-q")
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
        })!;
        Assert.True(process.WaitForExit(60_000), "自己解凍書庫が終わらない");
        return process.ExitCode;
    }

    /// <summary>テストごとのフォルダー。自己解凍書庫は自分と同じ場所に自分の名前のフォルダーを作る。</summary>
    private sealed class Place : IDisposable
    {
        public Place()
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"))).FullName;
        }

        public string Root { get; }

        public string Unpacked => Path.Combine(Root, "pack");

        public string MakeSfx(params (string Name, string Text)[] entries)
        {
            var zip = Path.Combine(Root, "pack.zip");
            using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
            {
                foreach (var (name, text) in entries)
                {
                    using var writer = new StreamWriter(archive.CreateEntry(name).Open());
                    writer.Write(text);
                }
            }

            var exe = Path.Combine(Root, "pack.exe");
            SfxWriter.Create(zip, exe);
            File.Delete(zip);
            return exe;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 消せないものは一時フォルダーに残るだけ
            }
        }
    }
}
