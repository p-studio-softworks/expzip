using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>RAR を読む (#336)。見本は Samples\Rar (SharpCompress のテスト用の書庫)。</summary>
public sealed class RarTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "Samples", "Rar", name);

    private static ArchiveContents OpenArchive(string path)
        => ArchiveReader.Open(path, NameReading.Default, null, CancellationToken.None);

    [Theory]
    [InlineData("Rar.rar", false, false)]
    [InlineData("Rar5.rar", true, false)]
    [InlineData("Rar.solid.rar", false, true)]
    [InlineData("Rar5.solid.rar", true, true)]
    [InlineData("Rar15.rar", false, false)]
    [InlineData("Rar2.rar", false, false)]
    [InlineData("Rar5.crc_blake2.rar", true, false)]
    public void 見出しを読んでSharpCompressと項目が合う(string name, bool rar5, bool solid)
    {
        using var session = RarAccess.Open(Sample(name), NameReading.Default, null, CancellationToken.None);

        Assert.Equal(rar5, session.Layout.IsRar5);
        Assert.Equal(solid, session.Layout.IsSolid);
        Assert.Single(session.Layout.Volumes);
        Assert.Equal(session.Entries.Count, session.Layout.Items.Count);
        Assert.True(session.Layout.MaxDictionary > 0);
    }

    [Theory]
    [InlineData("Rar5.multi.part01.rar", 6)]
    [InlineData("Rar5.multi.part03.rar", 6)]
    [InlineData("Rar.multi.solid.part01.rar", 6)]
    [InlineData("Rar2.multi.rar", 7)]
    [InlineData("Rar2.multi.r02", 7)]
    public void 分割した書庫は最初の分割ファイルから全部たどる(string name, int volumes)
    {
        using var session = RarAccess.Open(Sample(name), NameReading.Default, null, CancellationToken.None);

        Assert.Equal(volumes, session.Layout.Volumes.Count);
        Assert.Equal(session.Entries.Count, session.Layout.Items.Count);
    }

    [Fact]
    public void 分割の途中が欠けていたら開かない()
    {
        // SharpCompress は欠けたところで黙って読むのをやめ、途中までの一覧を出す
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            foreach (var part in new[] { 1, 2, 4, 5, 6 })
            {
                File.Copy(Sample($"Rar5.multi.part0{part}.rar"), Path.Combine(folder, $"Rar5.multi.part0{part}.rar"));
            }

            var error = Assert.Throws<MissingVolumeException>(
                () => RarHeader.Read(Path.Combine(folder, "Rar5.multi.part01.rar"), CancellationToken.None));
            Assert.EndsWith("Rar5.multi.part03.rar", error.Missing, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Theory]
    [InlineData("Rar5.encrypted_filesAndHeader.rar")]
    [InlineData("Rar.encrypted_filesAndHeader.rar")]
    public void 名前まで暗号化した書庫は開かない(string name)
        => Assert.Throws<EncryptedHeaderException>(() => RarHeader.Read(Sample(name), CancellationToken.None));

    [Fact]
    public void 壊れた書庫は壊れていると言う()
        => Assert.Throws<DamagedDataException>(() => RarHeader.Read(Sample("Rar.malformed_512byte.rar"), CancellationToken.None));

    [Fact]
    public void 中身だけ暗号化した書庫は一覧を読める()
    {
        using var session = RarAccess.Open(Sample("Rar5.encrypted_filesOnly.rar"), NameReading.Default, null, CancellationToken.None);
        Assert.Contains(session.Layout.Items, static i => i.Encrypted);
    }

    [Fact]
    public void 圧縮していないコメントを読む()
    {
        var layout = RarHeader.Read(Sample("Rar5.comment.rar"), CancellationToken.None);
        Assert.NotNull(layout.Comment);
    }

    // ------------------------------------------------------------ 一覧と展開

    [Theory]
    [InlineData("Rar.rar")]
    [InlineData("Rar5.rar")]
    [InlineData("Rar.solid.rar")]
    [InlineData("Rar5.solid.rar")]
    [InlineData("Rar15.rar")]
    [InlineData("Rar2.rar")]
    [InlineData("Rar5.crc_blake2.rar")]
    [InlineData("Rar5.multi.part01.rar")]
    [InlineData("Rar.multi.solid.part01.rar")]
    [InlineData("Rar2.multi.rar")]
    public void 見本の書庫を一覧して全部展開できる(string name)
    {
        using var place = new Place();
        Assert.Equal(ArchiveFormat.Rar, ArchiveFormats.Detect(Sample(name)));
        var contents = OpenArchive(Sample(name));
        Assert.True(contents.FileCount > 0);

        var result = ArchiveExtractor.Extract(Sample(name), ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Empty(result.Failed);
        Assert.Empty(result.Rejected);
        Assert.Equal(contents.FileCount, result.Extracted);
        Assert.Equal(contents.FileCount, Directory.EnumerateFiles(place.Output, "*", SearchOption.AllDirectories).Count());
    }

    [Fact]
    public void ソリッドの書庫から1個だけ展開できる()
    {
        using var place = new Place();
        var contents = OpenArchive(Sample("Rar5.solid.rar"));
        var last = Last(contents.Root);

        var result = ArchiveExtractor.Extract(Sample("Rar5.solid.rar"), ArchiveFormat.Rar, NameReading.Default,
            new HashSet<string>([last.SourceName]), place.Output, ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal(1, result.Extracted);
        Assert.Single(Directory.EnumerateFiles(place.Output, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void 中身だけ暗号化した書庫はパスワードに対応していないと言う()
    {
        using var place = new Place();
        var result = ArchiveExtractor.Extract(Sample("Rar5.encrypted_filesOnly.rar"), ArchiveFormat.Rar, NameReading.Default,
            null, place.Output, ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.NotEmpty(result.Failed);
        Assert.All(result.Failed, static f => Assert.Equal(Localization.Strings.PasswordNotSupported, f.Reason));
        Assert.Empty(Directory.EnumerateFiles(place.Output, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public void 名前まで暗号化した書庫は開けないと言う()
    {
        var error = Assert.ThrowsAny<IOException>(() => OpenArchive(Sample("Rar5.encrypted_filesAndHeader.rar")));
        Assert.IsType<EncryptedHeaderException>(error);
    }

    [Fact]
    public void 書庫のコメントを読む()
        => Assert.False(string.IsNullOrEmpty(OpenArchive(Sample("Rar5.comment.rar")).Comment));

    [Fact]
    public void 中身を読む道で全部読める()
    {
        var contents = OpenArchive(Sample("Rar5.solid.rar"));
        var read = 0;
        ArchiveContentReader.Visit(Sample("Rar5.solid.rar"), contents, null, item =>
        {
            using var stream = item.Open();
            stream.CopyTo(Stream.Null);
            read++;
        }, CancellationToken.None);

        Assert.Equal(contents.FileCount, read);
    }

    // ------------------------------------------------------------ 攻撃の見本 (組み立てた RAR5)

    [Fact]
    public void 組み立てた書庫を読める()
    {
        using var place = new Place();
        var archive = new RarSample().File("a.txt", "A").File("d/b.txt", "B").Save(place.PathOf("ok.rar"));

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal(2, result.Extracted);
        Assert.Equal("B", File.ReadAllText(Path.Combine(place.Output, "d", "b.txt")));
    }

    [Fact]
    public void 辞書が1GBちょうどなら開く()
    {
        // 上限は 1 GB まで (#336 で決めた)。メモリーの 4 分の 1 が 1 GB より小さい PC では、そちらが上限になる
        Assert.SkipWhen(RarHeader.DictionaryLimit < 1L << 30, "この PC のメモリーでは 1 GB の辞書が上限を超える");
        using var place = new Place();
        var archive = new RarSample().File("a.txt", "A", dictionaryPower: 13).Save(place.PathOf("dict.rar"));

        Assert.Equal(1L << 30, RarHeader.Read(archive, CancellationToken.None).MaxDictionary);
    }

    [Theory]
    [InlineData(14)] // 2 GB
    [InlineData(15)] // 4 GB
    [InlineData(31)] // RAR 7 の大きな辞書
    public void 辞書が大きすぎる書庫は開かない(int power)
    {
        // SharpCompress は見出しの辞書の大きさをそのまま確保する。1 GB は確保し、2 GB 以上は型の変換であふれる
        using var place = new Place();
        var archive = new RarSample().File("a.txt", "A", dictionaryPower: power).Save(place.PathOf("dict.rar"));

        var error = Assert.Throws<ArchiveTooLargeException>(() => RarHeader.Read(archive, CancellationToken.None));
        Assert.Equal(ArchiveLimit.Memory, error.Limit);
    }

    [Fact]
    public void 見出しのCRCが合わない書庫は壊れていると言う()
    {
        using var place = new Place();
        var archive = new RarSample().File("a.txt", "A").Save(place.PathOf("crc.rar"));
        var bytes = File.ReadAllBytes(archive);
        bytes[^20] ^= 0xFF;
        File.WriteAllBytes(archive, bytes);

        Assert.Throws<DamagedDataException>(() => RarHeader.Read(archive, CancellationToken.None));
    }

    [Fact]
    public void 途中で切れた書庫は壊れていると言う()
    {
        using var place = new Place();
        var archive = new RarSample().File("a.txt", new string('x', 1000)).Save(place.PathOf("cut.rar"));
        var bytes = File.ReadAllBytes(archive);
        File.WriteAllBytes(archive, bytes[..(bytes.Length - 600)]);

        Assert.Throws<DamagedDataException>(() => RarHeader.Read(archive, CancellationToken.None));
    }

    [Fact]
    public void 続きがあると書いてあるのに次の分割ファイルが無ければ開かない()
    {
        using var place = new Place();
        var archive = new RarSample { MoreVolumes = true }.File("a.txt", "A").Save(place.PathOf("x.part1.rar"));

        var error = Assert.Throws<MissingVolumeException>(() => RarHeader.Read(archive, CancellationToken.None));
        Assert.EndsWith("x.part2.rar", error.Missing, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("../evil.txt")]
    [InlineData("a/../../evil.txt")]
    [InlineData("C:/evil.txt")]
    [InlineData("a.txt:Zone.Identifier")]
    public void 展開先の外を指す名前は書き出さない(string name)
    {
        using var place = new Place();
        var archive = new RarSample().File(name, "EVIL").File("ok.txt", "OK").Save(place.PathOf("names.rar"));

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal(1, result.Extracted);
        Assert.False(File.Exists(place.PathOf("evil.txt")));
        Assert.Equal(["ok.txt"], Directory.EnumerateFiles(place.Output, "*", SearchOption.AllDirectories).Select(Path.GetFileName));
    }

    [Fact]
    public void 副見出しは項目として出さない()
    {
        // NTFS の代替データストリーム (STM) の副見出しで、展開したファイルの MotW を消す形 (CVE-2026-58052)
        using var place = new Place();
        var archive = new RarSample()
            .File("a.txt", "A")
            .File("STM", "[ZoneTransfer]\r\nZoneId=0", headerType: 3)
            .Save(place.PathOf("stm.rar"));

        var contents = OpenArchive(archive);
        Assert.Equal(1, contents.FileCount);
    }

    [Theory]
    [InlineData(1, "../../outside.txt")]
    [InlineData(1, "/etc/passwd")]
    [InlineData(2, @"C:\Windows\win.ini")]
    [InlineData(2, @"\\server\share\x")]
    [InlineData(2, @"\??\C:\Windows")]
    [InlineData(3, @"C:\Windows")]
    [InlineData(1, "a.txt:stream")]
    public void 書庫の外を指すリンクは展開しない(int type, string target)
    {
        using var place = new Place();
        var archive = new RarSample()
            .File("a.txt", "A")
            .File("link", redirect: (type, target, type == 3))
            .Save(place.PathOf("links.rar"));

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal(1, result.Extracted);
        Assert.Equal("link", Assert.Single(result.Failed).Name);
        Assert.False(Path.Exists(Path.Combine(place.Output, "link")));
    }

    [Fact]
    public void 書庫の中を指すジャンクションはジャンクションのまま展開する()
    {
        using var place = new Place();
        var archive = new RarSample()
            .File("data", directory: true)
            .File("data/a.txt", "A")
            .File("alias", redirect: (3, "data", true))
            .Save(place.PathOf("junction.rar"));

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Empty(result.Failed);
        var alias = new DirectoryInfo(Path.Combine(place.Output, "alias"));
        Assert.True(alias.Attributes.HasFlag(FileAttributes.ReparsePoint));
        Assert.Equal("A", File.ReadAllText(Path.Combine(alias.FullName, "a.txt")));
    }

    [Fact]
    public void 書庫の中を指すシンボリックリンクは展開するか権限が無いと言う()
    {
        using var place = new Place();
        var archive = new RarSample()
            .File("a.txt", "A")
            .File("sub/link.txt", redirect: (1, "../a.txt", false))
            .Save(place.PathOf("symlink.rar"));

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        var link = Path.Combine(place.Output, "sub", "link.txt");
        if (result.Failed.Count > 0)
        {
            // 開発者モードでない PC では作れない
            Assert.Equal(Localization.Strings.Reason(new LinkPrivilegeException()), Assert.Single(result.Failed).Reason);
            Assert.False(Path.Exists(link));
        }
        else
        {
            Assert.Equal(@"..\a.txt", new FileInfo(link).LinkTarget);
            Assert.Equal("A", File.ReadAllText(link));
        }
    }

    [Fact]
    public void ハードリンクとファイルのコピーは今回展開したファイルを指すものだけ展開する()
    {
        using var place = new Place();
        File.WriteAllText(Path.Combine(place.Output, "old.txt"), "USER");
        var archive = new RarSample()
            .File("a.txt", "A")
            .File("hard.txt", redirect: (4, "a.txt", false))
            .File("copy.txt", redirect: (5, "a.txt", false))
            .File("stolen.txt", redirect: (4, "old.txt", false))
            .Save(place.PathOf("hard.rar"));

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal("A", File.ReadAllText(Path.Combine(place.Output, "hard.txt")));
        Assert.Equal("A", File.ReadAllText(Path.Combine(place.Output, "copy.txt")));
        Assert.Equal("stolen.txt", Assert.Single(result.Failed).Name);
        Assert.False(File.Exists(Path.Combine(place.Output, "stolen.txt")));
    }

    [Fact]
    public void リンクを通って展開先の外に書かせない()
    {
        // 先にリンク (外を指すジャンクション) を置き、その下に書かせる形 (7-Zip の CVE-2025-11001 と同じ形)
        using var place = new Place();
        var archive = new RarSample()
            .File("evil", redirect: (3, @"..\..\outside", true))
            .File("evil/payload.txt", "EVIL")
            .Save(place.PathOf("through.rar"));

        ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.False(Directory.Exists(place.PathOf("outside")));
        Assert.False(new DirectoryInfo(Path.Combine(place.Output, "evil")).Attributes.HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void 一部だけ選んでもリンクの指す先が無ければ展開しない()
    {
        // ハードリンクだけを選んで、指す先のファイルを選んでいなければ失敗にする (#336 で決めた)
        using var place = new Place();
        var archive = new RarSample()
            .File("a.txt", "A")
            .File("hard.txt", redirect: (4, "a.txt", false))
            .Save(place.PathOf("hard.rar"));

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default,
            new HashSet<string>(["hard.txt"]), place.Output, ExtractOverwrite.Only([]), null, CancellationToken.None);

        Assert.Equal(Localization.Strings.LinkTargetNotExtracted, Assert.Single(result.Failed).Reason);
    }

    [Fact]
    public void 一部を選んで展開したときは選んだ範囲の外を指すリンクを展開しない()
    {
        // top を選ぶと top が取り除かれ、top/link は展開先の直下に置かれる。../secret は展開先の 1 つ上を指すことになる
        using var place = new Place();
        var archive = new RarSample()
            .File("secret", directory: true)
            .File("secret/a.txt", "SECRET")
            .File("top/b.txt", "B")
            .File("top/link", redirect: (3, "../secret", true))
            .File("top/inner", redirect: (4, "top/b.txt", false))
            .Save(place.PathOf("base.rar"));

        // 画面と同じく、一覧の名前 (SourceName) で選ぶ
        var top = OpenArchive(archive).Root.Folders.Single(static f => f.Name == "top");
        var selected = top.Files.Select(static f => f.SourceName).ToHashSet();
        Assert.Equal(3, selected.Count);

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default,
            selected, place.Output, ExtractOverwrite.Only([]), null, CancellationToken.None, basePath: "top");

        Assert.Equal("link", Path.GetFileName(Assert.Single(result.Failed).Name));
        Assert.False(Path.Exists(Path.Combine(place.Output, "link")));
        Assert.Equal("B", File.ReadAllText(Path.Combine(place.Output, "inner")));
    }

    [Fact]
    public void 自己解凍のRARはプログラムの中のしるしに惑わされずに本体を見つける()
    {
        using var place = new Place();
        var rar = File.ReadAllBytes(new RarSample().File("a.txt", "A").Save(place.PathOf("inner.rar")));

        // 頭のプログラムに、RAR5 のしるしと壊れた見出しをたまたま含めておく
        byte[] decoy = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, 0x11, 0x22, 0x33, 0x44, 0x05, 0x01, 0x00, 0x00];
        var exe = place.PathOf("sfx.exe");
        File.WriteAllBytes(exe, [.. "MZ"u8, .. new byte[1000], .. decoy, .. new byte[500], .. rar]);

        Assert.Equal(2 + 1000 + decoy.Length + 500, RarHeader.FindOffset(exe));
        Assert.Equal(ArchiveFormat.Rar, ArchiveFormats.Detect(exe));
        var contents = OpenArchive(exe);
        Assert.True(contents.IsSelfExtracting);

        var result = ArchiveExtractor.Extract(exe, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);
        Assert.Equal(1, result.Extracted);
    }

    [Fact]
    public void 自己解凍のRARはRAR4のしるしの後ろの見出しが書庫の見出しでなければ本体と見ない()
    {
        using var place = new Place();
        var rar = File.ReadAllBytes(new RarSample().File("a.txt", "A").Save(place.PathOf("inner.rar")));

        // RAR4 のしるしの後ろに、CRC を確かめない種類 (0x7B) で大きさだけ収まる見出しを置く
        byte[] decoy = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x12, 0x34, 0x7B, 0x00, 0x00, 0x07, 0x00];
        var exe = place.PathOf("sfx.exe");
        File.WriteAllBytes(exe, [.. "MZ"u8, .. new byte[1000], .. decoy, .. new byte[500], .. rar]);

        Assert.Equal(2 + 1000 + decoy.Length + 500, RarHeader.FindOffset(exe));
    }

    [Fact]
    public void RAR4のしるしを含むだけのexeはRARとしない()
    {
        using var place = new Place();
        byte[] decoy = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x00, 0x12, 0x34, 0x7B, 0x00, 0x00, 0x07, 0x00];
        var exe = place.PathOf("tool.exe");
        File.WriteAllBytes(exe, [.. "MZ"u8, .. new byte[1000], .. decoy, .. new byte[500]]);

        Assert.Equal(-1, RarHeader.FindOffset(exe));
    }

    [Fact]
    public void しるしを並べたexeでも本体を探すのに時間がかからない()
    {
        // RAR5 のしるしと、2 MB 近くあると書いた見出しを 4 MB 分並べる。しるしのたびに見出しを読み直すと数分かかる
        using var place = new Place();
        byte[] unit = [0x52, 0x61, 0x72, 0x21, 0x1A, 0x07, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0x7F];
        var bytes = new byte[4 * 1024 * 1024];
        "MZ"u8.CopyTo(bytes);
        for (var at = 2; at + unit.Length <= bytes.Length; at += unit.Length)
        {
            unit.CopyTo(bytes, at);
        }

        var exe = place.PathOf("many.exe");
        File.WriteAllBytes(exe, bytes);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(-1, RarHeader.FindOffset(exe));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"{watch.Elapsed.TotalSeconds:0.0} 秒かかった");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void 大きさが不明な項目は読む前に断りほかのファイルは展開する(bool solid)
    {
        // 前は大きさの合計が桁あふれして、1 個も展開できなかった。SharpCompress は大きさが不明な項目を、
        // 伸長し終えてから失敗とするので、読み始める前に断る (#336 で決めた)
        // まとめて圧縮した書庫は先頭から順に読むので、その項目より後ろも読めない。理由を付けて知らせる
        using var place = new Place();
        var archive = new RarSample { ArchiveFlags = solid ? 0x4UL : 0 }
            .File("before.txt", "B")
            .File("unknown.txt", "U", sizeUnknown: true)
            .File("after.txt", "A")
            .Save(place.PathOf("stdin.rar"));

        var contents = OpenArchive(archive);
        Assert.False(contents.Root.Files.Single(e => e.Name == "unknown.txt").LengthKnown);
        Assert.True(contents.Root.Files.Single(e => e.Name == "after.txt").LengthKnown);

        var result = ArchiveExtractor.Extract(archive, ArchiveFormat.Rar, NameReading.Default, null, place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None);

        var reasons = result.Failed.ToDictionary(f => Path.GetFileName(f.Name), f => f.Reason);
        Assert.Equal("B", File.ReadAllText(Path.Combine(place.Output, "before.txt")));
        Assert.Equal(Expzip.Localization.Strings.Reason(new RarSizeUnknownException()), reasons["unknown.txt"]);
        Assert.False(File.Exists(Path.Combine(place.Output, "unknown.txt")));
        if (solid)
        {
            Assert.Equal(1, result.Extracted);
            Assert.Equal(Expzip.Localization.Strings.Reason(new RarSizeUnknownException(after: true)), reasons["after.txt"]);
            Assert.False(File.Exists(Path.Combine(place.Output, "after.txt")));
        }
        else
        {
            Assert.Equal(2, result.Extracted);
            Assert.Equal("A", File.ReadAllText(Path.Combine(place.Output, "after.txt")));
        }

        Assert.Equal(solid ? 2 : 1, result.Failed.Count);

        // 検査やハッシュ値の道でも、大きさは不明 (-1) で、読めないものは読む前に断る
        var read = new Dictionary<string, (long Size, bool? After)>();
        ArchiveContentReader.Visit(archive, contents, null, item =>
        {
            try
            {
                using var stream = item.Open();
                stream.CopyTo(Stream.Null);
                read[item.Name] = (item.Size, null);
            }
            catch (RarSizeUnknownException ex)
            {
                read[item.Name] = (item.Size, ex.After);
            }
        }, CancellationToken.None);

        Assert.Equal((1L, (bool?)null), read["before.txt"]);
        Assert.Equal((-1L, (bool?)false), read["unknown.txt"]);
        Assert.Equal(solid ? (1L, (bool?)true) : (1L, null), read["after.txt"]);
    }

    [Fact]
    public void 中身がRARでないr00はRARとしない()
    {
        // ZIP の分割 (.z01) も同じ形の名前
        using var place = new Place();
        var path = place.PathOf("x.z01");
        File.WriteAllBytes(path, [0x50, 0x4B, 0x07, 0x08, 0, 0, 0, 0]);

        Assert.NotEqual(ArchiveFormat.Rar, ArchiveFormats.Detect(path));
    }

    [Theory]
    [InlineData("x.part1.rar", 1, "x.part2.rar")]
    [InlineData("x.part01.rar", 9, "x.part10.rar")]
    [InlineData("x.rar", 1, "x.r00")]
    [InlineData("x.rar", 101, "x.s00")]
    public void 分割ファイルの名前は続きを正しく作る(string first, int index, string expected)
        => Assert.Equal(expected, Path.GetFileName(RarVolumes.NextVolume(Path.Combine(@"C:\d", first), index, false)));

    [Theory]
    [InlineData("x.part2.rar", "x.part1.rar")]
    [InlineData("x.part03.rar", "x.part01.rar")]
    [InlineData("x.r05", "x.rar")]
    [InlineData("x.part1.rar", null)]
    [InlineData("x.rar", null)]
    public void 二個目以降の分割ファイルから最初のものを見つける(string name, string? expected)
        => Assert.Equal(expected, RarVolumes.FirstVolumeOf(Path.Combine(@"C:\d", name)) is { } first ? Path.GetFileName(first) : null);

    private static ArchiveEntry Last(ArchiveFolder folder)
        => folder.Files.LastOrDefault() ?? folder.Folders.Select(Last).Last();

    /// <summary>テストごとのフォルダー。リンクをたどらずに消す。</summary>
    private sealed class Place : IDisposable
    {
        private readonly string _root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "ExpzipCoreTests", Guid.NewGuid().ToString("N"))).FullName;

        public Place() => Output = Directory.CreateDirectory(Path.Combine(_root, "work", "out")).FullName;

        public string Output { get; }

        public string PathOf(string name) => Path.Combine(_root, "work", name);

        public void Dispose()
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                "cmd.exe", $"/c rmdir /s /q \"{_root}\"") { UseShellExecute = false, CreateNoWindow = true })!;
            process.WaitForExit();
        }
    }
}
