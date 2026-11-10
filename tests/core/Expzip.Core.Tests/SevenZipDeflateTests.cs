using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Expzip.Archives;
using ICSharpCode.SharpZipLib.Checksum;

namespace Expzip.Tests;

/// <summary>
/// 複数のファイルをまとめて Deflate で圧縮した 7z (#323)。SharpCompress 0.50.7 は 2 個目以降の中身を誤って返した。
/// CRC の無いファイルは確かめる手段が無いので、書き出しまで届いた中身が正しいかを、元のバイト列と比べて確かめる。
/// </summary>
public sealed class SevenZipDeflateTests
{
    /// <summary>大きさと、圧縮の効き方を混ぜる。64 KB の前後は Deflate の窓の境目。</summary>
    private static readonly (string Name, byte[] Data)[] Files =
    [
        ("a.bin", Random(1_000, 1)),
        ("b.txt", Text(65_535)),
        ("c.bin", Random(65_536, 2)),
        ("d.txt", Text(65_537)),
        ("sub/e.bin", Random(1_048_576, 3)),
        ("f.txt", Text(3 * 1_048_576)),
    ];

    public static TheoryData<bool> WithAndWithoutCrc => [true, false];

    [Theory]
    [MemberData(nameof(WithAndWithoutCrc))]
    public void すべて取り出すと元と同じ中身になる(bool crc)
    {
        using var place = new Place();
        var archive = place.Build(crc, b => b.Folder("deflate", Files));

        var result = place.Extract(archive);

        AssertOk(result);
        place.AssertExtracted(Files);
    }

    [Fact]
    public void tarで作った書庫をすべて取り出すと元と同じ中身になる()
    {
        // Windows の tar.exe (libarchive) が作る形。CRC を持つ
        using var place = new Place();
        var archive = place.BuildWithTar(Files);

        var result = place.Extract(archive);

        AssertOk(result);
        place.AssertExtracted(Files);
    }

    public static TheoryData<bool, string[]> Selections => new()
    {
        { true, ["b.txt"] },
        { true, ["b.txt", "c.bin"] },
        { true, ["f.txt"] },
        { false, ["b.txt"] },
        { false, ["b.txt", "c.bin"] },
        { false, ["f.txt"] },
        { false, ["sub/e.bin", "f.txt"] },
    };

    [Theory]
    [MemberData(nameof(Selections))]
    public void 選んだものだけを取り出しても元と同じ中身になる(bool crc, string[] names)
    {
        using var place = new Place();
        var archive = place.Build(crc, b => b.Folder("deflate", Files));

        var result = place.Extract(archive, new HashSet<string>(names));

        AssertOk(result);
        place.AssertExtracted([.. Files.Where(f => names.Contains(f.Name))]);
        Assert.Equal(names.Length, Directory.EnumerateFiles(place.Output, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void はいと答えると既にあるファイルが正しい中身に置き換わる()
    {
        using var place = new Place();
        var archive = place.Build(crc: false, b => b.Folder("deflate", Files));
        var target = Path.Combine(place.Output, "c.bin");
        File.WriteAllText(target, "OLD");

        var conflicts = ArchiveExtractor.FindConflicts([.. Files.Select(f => f.Name)], place.Output, null);
        var result = place.Extract(archive, overwrite: ExtractOverwrite.Only(conflicts.Select(c => c.Target)));

        AssertOk(result);
        place.AssertExtracted(Files);
    }

    [Fact]
    public void いいえと答えて最初のファイルを飛ばしても後ろのファイルは正しい()
    {
        using var place = new Place();
        var archive = place.Build(crc: false, b => b.Folder("deflate", Files));
        var target = Path.Combine(place.Output, "a.bin");
        File.WriteAllText(target, "OLD");

        var result = place.Extract(archive, overwrite: ExtractOverwrite.Only([]));

        Assert.Equal(["a.bin"], result.SkippedNames);
        Assert.Equal("OLD", File.ReadAllText(target));
        place.AssertExtracted(Files[1..]);
    }

    [Theory]
    [InlineData("../evil.bin")]
    [InlineData("NUL")]
    public void 最初のファイルが書き出さない名前でも後ろのファイルは正しい(string first)
    {
        using var place = new Place();
        (string, byte[])[] files = [(first, Random(5_000, 9)), .. Files];
        var archive = place.Build(crc: false, b => b.Folder("deflate", files));

        var result = place.Extract(archive);

        Assert.True(result.Rejected.Count + result.Failed.Count == 1, Describe(result));
        place.AssertExtracted(Files);
        Assert.False(File.Exists(Path.Combine(place.Root, "evil.bin")));
    }

    [Fact]
    public void 中身を読む道でもすべてのファイルが元と同じになる()
    {
        // 検査とハッシュが使う道 (ArchiveContentReader)。CRC が無いので、読めたバイト列だけが頼り
        using var place = new Place();
        var archive = place.Build(crc: false, b => b.Folder("deflate", Files));
        var contents = ArchiveReader.Open(archive, NameReading.Default);
        var seen = new Dictionary<string, byte[]>();

        ArchiveContentReader.Visit(archive, contents, null, item =>
        {
            using var stream = item.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            seen[item.Name] = copy.ToArray();
        }, CancellationToken.None);

        Assert.Equal(Files.Length, seen.Count);
        foreach (var (name, data) in Files)
        {
            Assert.True(seen.TryGetValue(name, out var got), name);
            Assert.True(data.AsSpan().SequenceEqual(got), $"{name} の中身が違う");
        }
    }

    [Fact]
    public void Deflateと無圧縮の塊の間に空のファイルとフォルダーがあっても正しい()
    {
        using var place = new Place();
        (string, byte[])[] first = [("x1.bin", Random(70_000, 11)), ("x2.txt", Text(80_000))];
        (string, byte[])[] second = [("y1.bin", Random(30_000, 12)), ("y2.txt", Text(40_000))];
        var archive = place.Build(crc: false, b => b
            .Folder("deflate", first)
            .Empty("empty.txt")
            .Directory("dir")
            .Folder("copy", second));

        var result = place.Extract(archive);

        // 7z の空のフォルダーは、もともと作らない。ここでは前後のファイルの中身だけを見る
        AssertOk(result);
        place.AssertExtracted([.. first, .. second, ("empty.txt", [])]);
    }

    /// <summary>
    /// 壊し方と、CRC を書くか。Deflate のデータの中を書き換えても、伸長がエラーにならないことがある。
    /// そのときに見つける手段は CRC だけなので、"corrupt" は CRC のある書庫で確かめる (LZMA2 でも同じ)。
    /// </summary>
    public static TheoryData<string, bool> Damages => new()
    {
        { "short", false },
        { "corrupt", true },
        { "cut", false },
        { "truncated", false },
        { "truncated", true },
    };

    [Theory]
    [MemberData(nameof(Damages))]
    public void 壊れた書庫は止まらずに壊れていると言い既にあるファイルを残す(string damage, bool crc)
    {
        using var place = new Place();
        var archive = place.Build(crc, b =>
        {
            b.Folder("deflate", Files);
            switch (damage)
            {
                case "short":
                    // 伸長した中身が、見出しに書いた大きさの合計より短い
                    b.UnpackSizeDelta = 10_000;
                    break;
                case "corrupt":
                    // Deflate のデータが途中で壊れている
                    b.Tamper = packed =>
                    {
                        for (var i = packed.Length / 2; i < packed.Length / 2 + 64; i++)
                        {
                            packed[i] ^= 0x5A;
                        }

                        return packed;
                    };
                    break;
                case "cut":
                    // 見出しに書いた大きさは合っているが、圧縮データの後ろ半分が 0 で埋まっている
                    b.Tamper = packed => [.. packed.AsSpan(0, packed.Length / 2), .. new byte[packed.Length - (packed.Length / 2)]];
                    break;
                case "truncated":
                    // 圧縮データそのものが途中で終わっている。見出しの圧縮後の大きさも、それに合わせる
                    b.Tamper = packed => packed[..(packed.Length / 2)];
                    break;
            }
        });
        // すべての名前に既にあるファイルを置き、「はい」で取り出す
        Directory.CreateDirectory(Path.Combine(place.Output, "sub"));
        foreach (var (name, _) in Files)
        {
            File.WriteAllText(Path.Combine(place.Output, name), "USER DATA");
        }

        var result = place.Extract(archive, overwrite: true);

        Assert.True(result.Failed.Count > 0, Describe(result));
        Assert.Contains(result.Failed, f => f.Reason.Contains("壊れて", StringComparison.Ordinal) || f.Reason.Contains("damaged", StringComparison.Ordinal));

        // 壊れていると言ったファイルは元のまま。それ以外は、元のままか正しい中身のどちらか
        foreach (var (name, data) in Files)
        {
            var got = File.ReadAllBytes(Path.Combine(place.Output, name));
            var kept = Encoding.ASCII.GetString(got) == "USER DATA";
            if (result.Failed.Any(f => f.Name == name))
            {
                Assert.True(kept, $"{name} が壊れた中身で置き換わった");
            }
            else
            {
                Assert.True(kept || data.AsSpan().SequenceEqual(got), $"{name} の中身が違う");
            }
        }

        Assert.Empty(Directory.EnumerateFiles(place.Output, "~*.tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void 伸長した中身が見出しより長くても余りを捨てて正しく取り出す()
    {
        using var place = new Place();
        (string, byte[])[] files = [.. Files[..4]];
        var tail = Text(5_000);
        var archive = place.Build(crc: false, b =>
        {
            b.Folder("deflate", files);
            b.ExtraUnpacked = tail;
        });

        var result = place.Extract(archive);

        AssertOk(result);
        place.AssertExtracted(files);
    }

    public static TheoryData<string, string[]?> OtherMethods => new()
    {
        { "LZMA", null },
        { "LZMA", ["b.txt"] },
        { "LZMA", ["c.bin", "d.txt"] },
        { "LZMA2", null },
        { "LZMA2", ["b.txt"] },
        { "LZMA2", ["f.txt"] },
    };

    [Theory]
    [MemberData(nameof(OtherMethods))]
    public void ほかの方式の7zも前と同じに読める(string method, string[]? names)
    {
        // 0.50.8 の直しは、方式によらず 7z を順に読むときの動きを変えた。LZMA と LZMA2 も前と同じに読めることを見る
        using var place = new Place();
        var archive = Path.Combine(place.Root, $"{method}.7z");
        using (var file = File.Create(archive))
        using (var writer = SharpCompress.Writers.WriterFactory.OpenWriter(
                   file, SharpCompress.Common.ArchiveType.SevenZip,
                   new SharpCompress.Writers.WriterOptions(Enum.Parse<SharpCompress.Common.CompressionType>(method))))
        {
            foreach (var (name, data) in Files)
            {
                writer.Write(name, new MemoryStream(data), DateTime.Now);
            }
        }

        var result = place.Extract(archive, names is null ? null : new HashSet<string>(names));

        AssertOk(result);
        place.AssertExtracted([.. Files.Where(f => names is null || names.Contains(f.Name))]);
    }

    private static void AssertOk(ExtractResult result)
        => Assert.True(result.Failed.Count == 0 && result.Rejected.Count == 0 && !result.Cancelled, Describe(result));

    private static string Describe(ExtractResult result)
        => $"取り出した {result.Extracted} / 失敗 [{string.Join(", ", result.Failed.Select(f => $"{f.Name}: {f.Reason}"))}] / 断った [{string.Join(", ", result.Rejected)}]";

    private static byte[] Random(int size, int seed)
    {
        var bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    private static byte[] Text(int size)
    {
        var text = new StringBuilder();
        for (var i = 0; text.Length < size; i++)
        {
            text.Append("line ").Append(i).Append(": the quick brown fox jumps over the lazy dog\n");
        }

        return Encoding.ASCII.GetBytes(text.ToString(0, size));
    }

    /// <summary>テストごとのフォルダー。</summary>
    private sealed class Place : IDisposable
    {
        public Place()
        {
            Root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"))).FullName;
            Output = Directory.CreateDirectory(Path.Combine(Root, "out")).FullName;
        }

        public string Root { get; }

        public string Output { get; }

        public string Build(bool crc, Action<SevenZipBuilder> configure)
        {
            var builder = new SevenZipBuilder { Crc = crc };
            configure(builder);
            var path = Path.Combine(Root, $"test-{Guid.NewGuid():N}.7z");
            File.WriteAllBytes(path, builder.ToArray());
            return path;
        }

        public string BuildWithTar((string Name, byte[] Data)[] files)
        {
            var source = Directory.CreateDirectory(Path.Combine(Root, "src")).FullName;
            foreach (var (name, data) in files)
            {
                var path = Path.Combine(source, name);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, data);
            }

            var archive = Path.Combine(Root, "tar.7z");
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "tar.exe"))
            {
                WorkingDirectory = source,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var arg in new[] { "--format", "7zip", "--options", "7zip:compression=deflate", "-cf", archive })
            {
                info.ArgumentList.Add(arg);
            }

            foreach (var (name, _) in files)
            {
                info.ArgumentList.Add(name);
            }

            using var process = Process.Start(info)!;
            Assert.True(process.WaitForExit(60_000), "tar.exe が終わらない");
            Assert.Equal(0, process.ExitCode);
            return archive;
        }

        /// <summary>取り出す。ライブラリの中で止まらなくなったら落とす。</summary>
        public ExtractResult Extract(string archive, IReadOnlySet<string>? names = null, ExtractOverwrite? overwrite = null)
        {
            var task = Task.Run(() => ArchiveExtractor.Extract(
                archive, ArchiveFormat.SevenZip, NameReading.Default, names, Output, overwrite ?? false, null, CancellationToken.None));
            Assert.True(task.Wait(TimeSpan.FromSeconds(60)), "取り出しが終わらない");
            return task.Result;
        }

        public void AssertExtracted((string Name, byte[] Data)[] files)
        {
            foreach (var (name, data) in files)
            {
                var path = Path.Combine(Output, name.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(path), $"{name} ができていない");
                Assert.True(data.AsSpan().SequenceEqual(File.ReadAllBytes(path)), $"{name} の中身が違う");
            }

            Assert.Empty(Directory.EnumerateFiles(Output, "~*.tmp", SearchOption.AllDirectories));
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

/// <summary>
/// テストのための小さな 7z の書き出し。見出しは圧縮しない。塊は Deflate か無圧縮で、塊ごとに複数のファイルを入れられる。
/// CRC の有り無しと、壊し方を選べる。
/// </summary>
internal sealed class SevenZipBuilder
{
    private readonly List<object> _parts = [];

    /// <summary>ファイルごとの CRC を書くか。</summary>
    public bool Crc { get; init; } = true;

    /// <summary>最後の塊の、見出しに書く伸長後の大きさに足す値。</summary>
    public long UnpackSizeDelta { get; set; }

    /// <summary>最後の塊の、見出しに書かずに中身の後ろに足すバイト列。</summary>
    public byte[]? ExtraUnpacked { get; set; }

    /// <summary>最後の塊の圧縮データを書き換える。</summary>
    public Func<byte[], byte[]>? Tamper { get; set; }

    public SevenZipBuilder Folder(string method, (string Name, byte[] Data)[] files)
    {
        _parts.Add(new FolderPart(method, files));
        return this;
    }

    public SevenZipBuilder Empty(string name)
    {
        _parts.Add(new EmptyPart(name, false));
        return this;
    }

    public SevenZipBuilder Directory(string name)
    {
        _parts.Add(new EmptyPart(name, true));
        return this;
    }

    public byte[] ToArray()
    {
        var folders = _parts.OfType<FolderPart>().ToList();
        var packed = new List<byte[]>();
        var unpackSizes = new List<long>();

        for (var i = 0; i < folders.Count; i++)
        {
            var last = i == folders.Count - 1;
            var raw = folders[i].Files.SelectMany(f => f.Data).ToArray();
            var declared = raw.LongLength + (last ? UnpackSizeDelta : 0);
            if (last && ExtraUnpacked is not null)
            {
                raw = [.. raw, .. ExtraUnpacked];
            }

            var data = folders[i].Method == "deflate" ? Deflate(raw) : raw;
            if (last && Tamper is not null)
            {
                data = Tamper(data);
            }

            packed.Add(data);
            unpackSizes.Add(declared);
        }

        var h = new MemoryStream();
        h.WriteByte(0x01); // Header
        h.WriteByte(0x04); // MainStreamsInfo

        h.WriteByte(0x06); // PackInfo
        Number(h, 0);
        Number(h, (ulong)packed.Count);
        h.WriteByte(0x09);
        foreach (var p in packed)
        {
            Number(h, (ulong)p.Length);
        }

        h.WriteByte(0x00);

        h.WriteByte(0x07); // UnpackInfo
        h.WriteByte(0x0B);
        Number(h, (ulong)folders.Count);
        h.WriteByte(0x00);
        foreach (var folder in folders)
        {
            Number(h, 1);
            if (folder.Method == "deflate")
            {
                h.Write([0x03, 0x04, 0x01, 0x08]);
            }
            else
            {
                h.Write([0x01, 0x00]);
            }
        }

        h.WriteByte(0x0C);
        foreach (var size in unpackSizes)
        {
            Number(h, (ulong)size);
        }

        h.WriteByte(0x00);

        h.WriteByte(0x08); // SubStreamsInfo
        h.WriteByte(0x0D);
        foreach (var folder in folders)
        {
            Number(h, (ulong)folder.Files.Length);
        }

        h.WriteByte(0x09);
        foreach (var folder in folders)
        {
            foreach (var file in folder.Files[..^1])
            {
                Number(h, (ulong)file.Data.Length);
            }
        }

        if (Crc)
        {
            // 塊の CRC は書かないので、すべてのファイルの CRC をここに書く
            h.WriteByte(0x0A);
            h.WriteByte(0x01);
            foreach (var file in folders.SelectMany(f => f.Files))
            {
                var crc = new Crc32();
                crc.Update(file.Data);
                h.Write(BitConverter.GetBytes((uint)crc.Value));
            }
        }

        h.WriteByte(0x00);
        h.WriteByte(0x00);

        // ファイル。中身のあるファイルは塊の順に並ぶ
        var entries = new List<(string Name, bool HasStream, bool Dir)>();
        foreach (var part in _parts)
        {
            if (part is FolderPart f)
            {
                entries.AddRange(f.Files.Select(x => (x.Name, true, false)));
            }
            else if (part is EmptyPart e)
            {
                entries.Add((e.Name, false, e.Dir));
            }
        }

        h.WriteByte(0x05); // FilesInfo
        Number(h, (ulong)entries.Count);

        var empties = entries.Where(e => !e.HasStream).ToList();
        if (empties.Count > 0)
        {
            var emptyStream = Bits(entries.Select(e => !e.HasStream));
            h.WriteByte(0x0E);
            Number(h, (ulong)emptyStream.Length);
            h.Write(emptyStream);

            var emptyFile = Bits(empties.Select(e => !e.Dir));
            h.WriteByte(0x0F);
            Number(h, (ulong)emptyFile.Length);
            h.Write(emptyFile);
        }

        var names = new MemoryStream();
        names.WriteByte(0x00);
        foreach (var entry in entries)
        {
            names.Write(Encoding.Unicode.GetBytes(entry.Name + "\0"));
        }

        h.WriteByte(0x11);
        Number(h, (ulong)names.Length);
        h.Write(names.ToArray());

        if (entries.Any(e => e.Dir))
        {
            var attributes = new MemoryStream();
            attributes.Write([0x01, 0x00]);
            foreach (var entry in entries)
            {
                attributes.Write(BitConverter.GetBytes(entry.Dir ? 0x10u : 0x80u));
            }

            h.WriteByte(0x15);
            Number(h, (ulong)attributes.Length);
            h.Write(attributes.ToArray());
        }

        h.WriteByte(0x00);
        h.WriteByte(0x00);

        var header = h.ToArray();
        var body = packed.SelectMany(p => p).ToArray();

        var start = new byte[20];
        BitConverter.TryWriteBytes(start.AsSpan(0), (ulong)body.Length);
        BitConverter.TryWriteBytes(start.AsSpan(8), (ulong)header.Length);
        BitConverter.TryWriteBytes(start.AsSpan(16), Crc32Of(header));

        var output = new MemoryStream();
        output.Write([(byte)'7', (byte)'z', 0xBC, 0xAF, 0x27, 0x1C, 0x00, 0x04]);
        output.Write(BitConverter.GetBytes(Crc32Of(start)));
        output.Write(start);
        output.Write(body);
        output.Write(header);
        return output.ToArray();
    }

    private static byte[] Deflate(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, CompressionLevel.Optimal, leaveOpen: true))
        {
            deflate.Write(raw);
        }

        return output.ToArray();
    }

    private static uint Crc32Of(byte[] data)
    {
        var crc = new Crc32();
        crc.Update(data);
        return (uint)crc.Value;
    }

    private static byte[] Bits(IEnumerable<bool> values)
    {
        var list = values.ToList();
        var bytes = new byte[(list.Count + 7) / 8];
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i])
            {
                bytes[i / 8] |= (byte)(0x80 >> (i % 8));
            }
        }

        return bytes;
    }

    /// <summary>7z の数の書き方。先頭のバイトの上位のビットで、続くバイトの数を表す。</summary>
    private static void Number(Stream stream, ulong value)
    {
        byte first = 0;
        byte mask = 0x80;
        int i;
        for (i = 0; i < 8; i++)
        {
            if (value < (1UL << (7 * (i + 1))))
            {
                first |= (byte)(value >> (8 * i));
                break;
            }

            first |= mask;
            mask >>= 1;
        }

        stream.WriteByte(first);
        for (; i > 0; i--)
        {
            stream.WriteByte((byte)value);
            value >>= 8;
        }
    }

    private sealed record FolderPart(string Method, (string Name, byte[] Data)[] Files);

    private sealed record EmptyPart(string Name, bool Dir);
}
