using System.Diagnostics;
using System.Text;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Tests;

/// <summary>
/// 展開で読むすべてのファイルの MotW を引き継ぐ (#390)。
/// 分割した書庫や、隣のファイルを読む書庫では、開いたファイルのほかにも読むファイルがある。
/// その中の 1 個にだけ MotW があっても、展開したものに MotW が付かなければならない。
/// </summary>
[Collection(nameof(TraceCollection))]
public sealed class SourceMarkTests
{
    private const string Internet = "[ZoneTransfer]\r\nZoneId=3\r\n";
    private const string Trusted = "[ZoneTransfer]\r\nZoneId=2\r\n";

    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", "Rar", name);

    // ------------------------------------------------------------------ RAR

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(6)]
    public void 分割したRARはどの分割ファイルのMotWも引き継ぐ(int marked)
    {
        using var log = new TraceLog();
        var parts = CopyRar(log, "Rar5.multi.part0{0}.rar", 1, 6);
        Mark(parts[marked - 1], Internet);

        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(parts[0], ArchiveFormat.Rar));
    }

    [Fact]
    public void 分割したRARの2個目以降を開いても最初の分割ファイルのMotWを引き継ぐ()
    {
        using var log = new TraceLog();
        var parts = CopyRar(log, "Rar5.multi.part0{0}.rar", 1, 6);
        Mark(parts[0], Internet);

        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(parts[2], ArchiveFormat.Rar));
    }

    [Fact]
    public void 古い形の分割したRARは続きのMotWも引き継ぐ()
    {
        using var log = new TraceLog();
        var first = log.PathOf("Rar2.multi.rar");
        File.Copy(Sample("Rar2.multi.rar"), first);
        var continuation = log.PathOf("Rar2.multi.r01");
        foreach (var number in new[] { "00", "01", "02", "03" })
        {
            File.Copy(Sample($"Rar2.multi.r{number}"), log.PathOf($"Rar2.multi.r{number}"));
        }

        Mark(continuation, Internet);

        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(first, ArchiveFormat.Rar));
    }

    [Fact]
    public void どこにもMotWが無ければ付けない()
    {
        // 正しい書庫を、誤ってインターネットから来たものにしない
        using var log = new TraceLog();
        var parts = CopyRar(log, "Rar5.multi.part0{0}.rar", 1, 6);

        Assert.Null(MarkOfTheWeb.TryReadArchive(parts[0], ArchiveFormat.Rar));
    }

    [Fact]
    public void 警戒度の違うMotWがあれば強いほうを引き継ぐ()
    {
        using var log = new TraceLog();
        var parts = CopyRar(log, "Rar5.multi.part0{0}.rar", 1, 6);
        Mark(parts[0], Trusted);
        Mark(parts[4], Internet);

        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(parts[0], ArchiveFormat.Rar));
    }

    [Fact]
    public void RARの展開で開くファイルはすべてMotWを読むファイルに入っている()
    {
        // 読み取りの側が将来ほかのファイルも開くようになったとき、MotW の側の追い忘れに気づくため
        using var log = new TraceLog();
        var parts = CopyRar(log, "Rar5.multi.part0{0}.rar", 1, 6);

        AssertOpenedAreSources(parts[2], ArchiveFormat.Rar, log.PathOf("out"));
    }

    // ------------------------------------------------------------------ CAB

    [Fact]
    public void 続きのあるCABは最後のCABのMotWも引き継ぐ()
    {
        using var log = new TraceLog();
        var cabs = CabChain(log);
        Mark(cabs[^1], Internet);

        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(cabs[0], ArchiveFormat.Cab));
    }

    [Fact]
    public void 続きのあるCABでどこにもMotWが無ければ付けない()
    {
        using var log = new TraceLog();
        var cabs = CabChain(log);

        Assert.Null(MarkOfTheWeb.TryReadArchive(cabs[0], ArchiveFormat.Cab));
    }

    [Fact]
    public void 分割したCABは分割ファイルと続きのCABのMotWを引き継ぐ()
    {
        // x.cab.001 の分割ファイルと、その中に書かれた続きの CAB の両方を読む
        using var log = new TraceLog();
        var cabs = CabChain(log);
        var bytes = File.ReadAllBytes(cabs[0]);
        var split = cabs[0] + ".001";
        File.WriteAllBytes(split, bytes[..(bytes.Length / 2)]);
        File.WriteAllBytes(cabs[0] + ".002", bytes[(bytes.Length / 2)..]);
        File.Delete(cabs[0]);
        Mark(cabs[^1], Internet);

        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(split, ArchiveFormat.Cab));

        Mark(cabs[^1], Trusted);
        Mark(cabs[0] + ".002", Internet);
        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(split, ArchiveFormat.Cab));
    }

    [Fact]
    public void CABの続きの名前がフォルダーの外を指していても外のファイルは読まない()
    {
        // 名前の長さを変えずに、次の CAB の名前を ..\ で始まるものにする
        using var log = new TraceLog();
        var cabs = CabChain(log, "sub");
        var next = Path.GetFileName(cabs[1]);
        var outside = "..\\" + next[3..];
        Replace(cabs[0], next, outside);
        var escaped = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(cabs[0])!, outside));
        File.WriteAllText(escaped, "outside");

        var sources = ArchiveSources.Of(cabs[0], ArchiveFormat.Cab);

        Assert.DoesNotContain(escaped, sources.Files, StringComparer.OrdinalIgnoreCase);
        // 読み取りも続きを開けないので、挙げきれなかったものとして区分 3 を入れる
        Assert.False(sources.Complete);
        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(cabs[0], ArchiveFormat.Cab));
    }

    [Fact]
    public void CABの続きが前のCABを指していても止まる()
    {
        using var log = new TraceLog();
        var cabs = CabChain(log);
        Replace(cabs[1], Path.GetFileName(cabs[2]), Path.GetFileName(cabs[0]));

        var sources = ArchiveSources.Of(cabs[0], ArchiveFormat.Cab);

        Assert.Equal(2, sources.Files.Count);
    }

    [Fact]
    public void CABの見出しが途中で切れていればインターネットから来たものとして扱う()
    {
        using var log = new TraceLog();
        var cabs = CabChain(log);
        var bytes = File.ReadAllBytes(cabs[0]);
        File.WriteAllBytes(cabs[0], bytes[..20]);

        Assert.False(ArchiveSources.Of(cabs[0], ArchiveFormat.Cab).Complete);
        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(cabs[0], ArchiveFormat.Cab));
    }

    [Fact]
    public void CABの展開で開くファイルはすべてMotWを読むファイルに入っている()
    {
        using var log = new TraceLog();
        var cabs = CabChain(log);

        AssertOpenedAreSources(cabs[0], ArchiveFormat.Cab, log.PathOf("out"));
    }

    // ------------------------------------------------------------------ MSI

    [Fact]
    public void MSIは隣のCABのMotWを引き継ぐ()
    {
        using var log = new TraceLog();
        var msi = MsiSample.Create(log.Root);
        Mark(Path.Combine(log.Root, MsiSample.ExternalCab), Internet);

        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(msi, ArchiveFormat.Msi));
    }

    [Fact]
    public void MSIは圧縮していない隣のファイルのMotWを引き継ぐ()
    {
        using var log = new TraceLog();
        var msi = MsiSample.Create(log.Root);
        Mark(Path.Combine(log.Root, MsiSample.LoosePath), Internet);

        Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(msi, ArchiveFormat.Msi));
    }

    [Fact]
    public void MSIでどこにもMotWが無ければ付けない()
    {
        using var log = new TraceLog();
        var msi = MsiSample.Create(log.Root);

        Assert.Null(MarkOfTheWeb.TryReadArchive(msi, ArchiveFormat.Msi));
    }

    [Fact]
    public void MSIの隣のファイルの名前にコロンがあれば読まない()
    {
        // a.txt::$DATA は a.txt の中身を読むのに、MotW は a.txt::$DATA:Zone.Identifier を探して見つからない。
        // 読み取りと MotW の読み方が食い違わないよう、どちらも開かない
        using var log = new TraceLog();
        var msi = MsiSample.Create(log.Root, looseName: "loose.txt::$DATA");
        var loose = Path.Combine(log.Root, MsiSample.LoosePath);
        Mark(loose, Internet);

        var sources = ArchiveSources.Of(msi, ArchiveFormat.Msi);
        Assert.DoesNotContain(sources.Files, static f => Path.GetFileName(f).Contains(':'));
        Assert.Null(MsiReader.BesidePath(log.Root, MsiSample.LoosePath + "::$DATA"));

        // 展開でも読まない (書き出す名前に : があるので、その前に拒む)
        using (var recording = OpenedFiles.Record())
        {
            ArchiveExtractor.Extract(msi, ArchiveFormat.Msi, NameReading.Default, null, log.PathOf("out"),
                ExtractOverwrite.Only([]), null, CancellationToken.None);
            Assert.DoesNotContain(recording.Paths, static p => p.EndsWith("loose.txt", StringComparison.OrdinalIgnoreCase));
        }

        Assert.False(Directory.EnumerateFiles(log.PathOf("out"), "loose*", SearchOption.AllDirectories).Any());
    }

    [Fact]
    public void MSIの隣のファイルが多すぎればインターネットから来たものとして扱う()
    {
        using var log = new TraceLog();
        var msi = MsiSample.Create(log.Root);
        var saved = ArchiveSources.Limit;
        ArchiveSources.Limit = 2;

        try
        {
            Assert.False(ArchiveSources.Of(msi, ArchiveFormat.Msi).Complete);
            Assert.Equal(Internet, MarkOfTheWeb.TryReadArchive(msi, ArchiveFormat.Msi));
        }
        finally
        {
            ArchiveSources.Limit = saved;
        }
    }

    [Fact]
    public void MSIの展開で開くファイルはすべてMotWを読むファイルに入っている()
    {
        using var log = new TraceLog();
        var msi = MsiSample.Create(log.Root);

        AssertOpenedAreSources(msi, ArchiveFormat.Msi, log.PathOf("out"));
    }

    // ------------------------------------------------------------------ 読めないとき

    [Fact]
    public void 書庫が無くても例外を外へ出さない()
    {
        using var log = new TraceLog();

        Assert.Null(Record.Exception(() => MarkOfTheWeb.TryReadArchive(log.PathOf("none.msi"), ArchiveFormat.Msi)));
    }

    [Fact]
    public void 中止されたら中止を伝える()
    {
        // 中止だけは外へ出す。呼ぶ側が取り出しを同じ中止で止め、中止したことを知らせる
        using var log = new TraceLog();
        var parts = CopyRar(log, "Rar5.multi.part0{0}.rar", 1, 6);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(
            () => MarkOfTheWeb.TryReadArchive(parts[0], ArchiveFormat.Rar, cancellation.Token));
    }

    [Fact]
    public void CABとMSIの展開を書く前に中止しても中止したと伝える()
    {
        // MotW を読む間に中止すると、取り出しは 1 個目を書く前に同じ中止で止まる。「0 個を展開した」にしない
        using var log = new TraceLog();
        var cabs = CabChain(log);
        var msi = MsiSample.Create(log.PathOf("msi"));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        foreach (var (archive, format) in new[] { (cabs[0], ArchiveFormat.Cab), (msi, ArchiveFormat.Msi) })
        {
            var result = ArchiveExtractor.Extract(archive, format, NameReading.Default, null, log.PathOf("out"),
                ExtractOverwrite.Only([]), null, cancellation.Token);
            Assert.True(result.Cancelled, format.ToString());
            Assert.Equal(0, result.Extracted);
        }
    }

    // ------------------------------------------------------------------ 見本の作り方

    private static string[] CopyRar(TraceLog log, string pattern, int from, int to)
    {
        var parts = new List<string>();
        for (var number = from; number <= to; number++)
        {
            var name = string.Format(pattern, number);
            var path = log.PathOf(name);
            File.Copy(Sample(name), path);
            parts.Add(path);
        }

        return [.. parts];
    }

    private static void Mark(string path, string zone)
        => File.WriteAllText(path + MarkOfTheWeb.StreamSuffix, zone, Encoding.Latin1);

    /// <summary>バイト列の中の、同じ長さの文字列を置き換える (CAB の見出しの名前)。</summary>
    private static void Replace(string path, string from, string to)
    {
        Assert.Equal(from.Length, to.Length);
        var bytes = File.ReadAllBytes(path);
        var text = Encoding.Latin1.GetString(bytes);
        var at = text.IndexOf(from + "\0", StringComparison.Ordinal);
        Assert.True(at >= 0, $"{from} が見出しに無い");
        Encoding.Latin1.GetBytes(to).CopyTo(bytes, at);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>makecab で、3 個に続く CAB を作る。</summary>
    private static string[] CabChain(TraceLog log, string folder = "")
    {
        var work = log.PathOf("cabwork");
        var output = folder.Length == 0 ? log.Root : log.PathOf(folder);
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(output);

        var random = new Random(1);
        var lines = new List<string>
        {
            ".Set CabinetNameTemplate=part*.cab",
            $".Set DiskDirectoryTemplate={output}",
            // 512 の倍数でなければ makecab が断る
            ".Set MaxDiskSize=70144",
            ".Set Cabinet=on",
            ".Set Compress=off",
        };
        for (var i = 1; i <= 3; i++)
        {
            var data = new byte[60000];
            random.NextBytes(data);
            var source = Path.Combine(work, $"f{i}.bin");
            File.WriteAllBytes(source, data);
            lines.Add($"\"{source}\" \"f{i}.bin\"");
        }

        var ddf = Path.Combine(work, "chain.ddf");
        File.WriteAllLines(ddf, lines, Encoding.ASCII);
        Run("makecab.exe", $"/F \"{ddf}\"", work);

        var cabs = Directory.GetFiles(output, "part*.cab").Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(3, cabs.Length);
        return cabs;
    }

    internal static void Run(string file, string arguments, string directory)
    {
        using var process = Process.Start(new ProcessStartInfo(file, arguments)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>実際に展開して、開いたファイルがすべて MotW を読むファイルに入っていることを確かめる。</summary>
    private static void AssertOpenedAreSources(string archive, ArchiveFormat format, string destination)
    {
        var sources = ArchiveSources.Of(archive, format);
        Assert.True(sources.Complete);

        IReadOnlyList<string> opened;
        using (var recording = OpenedFiles.Record())
        {
            var result = ArchiveExtractor.Extract(archive, format, NameReading.Default, null, destination,
                ExtractOverwrite.Only([]), null, CancellationToken.None);
            Assert.True(result.Extracted > 0);
            opened = recording.Paths;
        }

        Assert.NotEmpty(opened);
        foreach (var path in opened)
        {
            Assert.Contains(Path.GetFullPath(path), sources.Files, StringComparer.OrdinalIgnoreCase);
        }
    }
}
