using System.IO.Compression;
using System.Text;
using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 書庫のコメントの書き換えは、書庫をその場で書き換える (#258)。中身、長さの欄、余りの切り詰めの順に書くので、
/// どの段階で止まっても書庫は読める (#348)。止まった段階を作り、開けて、取り出せて、続けて書き換えられることを確かめる。
/// </summary>
public sealed class CommentTests : IDisposable
{
    private const string Old = "OLD COMMENT OLD COMMENT OLD COMMENT OLD COMMENT";
    private const string Longer = "NEW LONGER COMMENT NEW LONGER COMMENT NEW LONGER COMMENT NEW LONGER COMMENT";
    private const string Shorter = "NEW";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"));

    public CommentTests() => Directory.CreateDirectory(_root);

    /// <summary>止まった段階。1: 中身を書いた (途中まで含む)、2: 長さの欄も書いた、3: 切り詰めた (書き終えた)。</summary>
    public static TheoryData<string, string, string, int, int> States => new()
    {
        { "長いものを書きかけ", Old, Longer, 1, 20 },
        { "長いものを書いた", Old, Longer, 1, -1 },
        { "長いものの長さを書いた", Old, Longer, 2, -1 },
        { "短いものを書いた", Old, Shorter, 1, -1 },
        { "短いものの長さを書いた", Old, Shorter, 2, -1 },
        { "短いものを書き終えた", Old, Shorter, 3, -1 },
        { "無かったところに書いた", "", Longer, 1, -1 },
        // 電源断で、ファイルの長さだけが伸びて中身が書かれていないとき
        { "伸びたところが 0 のまま", Old, new string('\0', 120), 1, -1 },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void 止まった書庫を開いて取り出せる(string name, string old, string comment, int stage, int partial)
    {
        var archive = MakeStopped(name, old, comment, stage, partial);

        var contents = ArchiveReader.Open(archive, NameReading.Default);
        Assert.Equal(ArchiveFormat.Zip, contents.Format);

        var output = Directory.CreateDirectory(Path.Combine(_root, name + "-out")).FullName;
        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Zip, NameReading.Default, null, output, false, null, CancellationToken.None);

        Assert.Equal(2, result.Extracted);
        Assert.Empty(result.Failed);
        Assert.Equal(Body("a.txt"), File.ReadAllText(Path.Combine(output, "a.txt")));
        Assert.Equal(Body("docs/b.txt"), File.ReadAllText(Path.Combine(output, "docs", "b.txt")));
    }

    [Theory]
    [MemberData(nameof(States))]
    public void 止まった書庫にコメントを書き直せる(string name, string old, string comment, int stage, int partial)
    {
        var archive = MakeStopped(name, old, comment, stage, partial);

        ZipComment.Write(archive, "AGAIN");

        Assert.Equal("AGAIN", Encoding.UTF8.GetString(ZipComment.ReadBytes(archive)!));
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal(2, zip.Entries.Count);
    }

    [Theory]
    [MemberData(nameof(States))]
    public void 止まった書庫にファイルを足せる(string name, string old, string comment, int stage, int partial)
    {
        var archive = MakeStopped(name, old, comment, stage, partial);
        var source = Path.Combine(_root, name + "-added.txt");
        File.WriteAllText(source, "added");

        var result = ZipArchiveWriter.Add(archive, [source], "", false, null, CancellationToken.None);

        Assert.Equal(1, result.Added);
        using var zip = ZipFile.OpenRead(archive);
        Assert.Equal(3, zip.Entries.Count);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static string Body(string name) => string.Concat(Enumerable.Repeat($"content of {name} ", 200));

    /// <summary>
    /// <see cref="ZipComment"/> の書き方 (中身 → 長さの欄 → 切り詰め) の途中で止まった書庫を作る。
    /// <paramref name="partial"/> が 0 以上なら、中身をその長さまでしか書いていない。
    /// </summary>
    private string MakeStopped(string name, string old, string comment, int stage, int partial)
    {
        var path = Path.Combine(_root, name + ".zip");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            foreach (var entry in new[] { "a.txt", "docs/b.txt" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(entry).Open());
                writer.Write(Body(entry));
            }
        }

        if (old.Length > 0)
        {
            ZipComment.Write(path, old);
        }

        var bytes = File.ReadAllBytes(path);
        var end = FindEnd(bytes);
        var start = end + 22;
        var written = Encoding.UTF8.GetBytes(comment);
        if (partial >= 0)
        {
            written = written[..partial];
        }

        // 1. 中身を古いものの上に重ねる。長ければ伸びる
        var tail = new byte[Math.Max(bytes.Length - start, written.Length)];
        bytes.AsSpan(start).CopyTo(tail);
        written.CopyTo(tail, 0);
        var result = bytes[..start].Concat(tail).ToArray();

        // 2. 長さの欄
        if (stage >= 2)
        {
            BitConverter.TryWriteBytes(result.AsSpan(end + 20), (ushort)written.Length);
        }

        // 3. 余りを切る
        if (stage >= 3)
        {
            result = result[..(start + written.Length)];
        }

        File.WriteAllBytes(path, result);
        return path;
    }

    private static int FindEnd(byte[] bytes)
    {
        for (var i = bytes.Length - 22; i >= 0; i--)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 5 && bytes[i + 3] == 6)
            {
                return i;
            }
        }

        throw new InvalidDataException("no end record");
    }
}
