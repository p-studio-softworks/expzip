using Expzip.Archives;

namespace Expzip.Tests;

/// <summary>
/// 一部を選んで展開するとき、フォルダーの項目とファイルの項目を分けて選ぶ (#401)。
/// 7z と RAR、末尾に / の無い tar のフォルダーの名前は、同じ名前のファイルと同じ文字列になる。
/// </summary>
public sealed class SameNameSelectionTests : IDisposable
{
    private readonly LinkPlace _place = new();

    public void Dispose() => _place.Dispose();

    private ExtractResult Extract(string archive, ArchiveFormat format, EntrySelection? selection, string? password = null)
        => ArchiveExtractor.Extract(archive, format, NameReading.Default, selection, _place.Output,
            ExtractOverwrite.Only([]), null, CancellationToken.None, password: password);

    private static void AssertClean(ExtractResult result)
    {
        Assert.Empty(result.Rejected);
        Assert.Empty(result.Failed);
        Assert.Empty(result.NotReplaced);
        Assert.Empty(result.SkippedNames);
    }

    private static ArchiveFolder Root(string archive)
        => ArchiveReader.Open(archive, NameReading.Default, null, CancellationToken.None).Root;

    private static (ArchiveFormat Format, string? Password) Parse(string formatName)
        => formatName == "ZipPassword" ? (ArchiveFormat.Zip, "pass") : (Enum.Parse<ArchiveFormat>(formatName), null);

    /// <summary>
    /// 同じ名前のファイル <c>a</c> とフォルダーの項目 <c>a</c>、フォルダーの項目 <c>b</c> とその下のファイル <c>b/x.txt</c>、中身の無いフォルダー <c>e</c>。
    /// </summary>
    private string SameName(ArchiveFormat format, string? password, bool folderFirst = false)
    {
        var path = _place.PathOf("same." + Extension(format));
        switch (format)
        {
            case ArchiveFormat.Zip:
            {
                var zip = new ZipSample();
                if (folderFirst)
                {
                    zip.Folder("a").File("a", "FILE");
                }
                else
                {
                    zip.File("a", "FILE").Folder("a");
                }

                return zip.Folder("b").File("b/x.txt", "X").Folder("e").Save(path, password);
            }
            case ArchiveFormat.SevenZip:
            {
                var sample = new SevenZipSample();
                if (folderFirst)
                {
                    sample.Folder("a").File("a", "FILE");
                }
                else
                {
                    sample.File("a", "FILE").Folder("a");
                }

                return sample.Folder("b").File("b/x.txt", "X").Folder("e").Save(path);
            }
            case ArchiveFormat.Tar:
            {
                // 末尾に / の無いフォルダーの項目
                var tar = new TarSample();
                if (folderFirst)
                {
                    tar.BareFolder("a").File("a", "FILE");
                }
                else
                {
                    tar.File("a", "FILE").BareFolder("a");
                }

                return tar.BareFolder("b").File("b/x.txt", "X").BareFolder("e").Save(path);
            }
            default:
            {
                var rar = new RarSample();
                if (folderFirst)
                {
                    rar.File("a", directory: true).File("a", "FILE");
                }
                else
                {
                    rar.File("a", "FILE").File("a", directory: true);
                }

                return rar.File("b", directory: true).File("b/x.txt", "X").File("e", directory: true).Save(path);
            }
        }
    }

    private static string Extension(ArchiveFormat format)
        => format == ArchiveFormat.SevenZip ? "7z" : format.ToString().ToLowerInvariant();

    private static ArchiveFolder FolderNamed(ArchiveFolder root, string name)
        => root.Folders.Single(f => f.Name == name);

    // ------------------------------------------------------------------ フォルダーを選ぶ

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip), false)]
    [InlineData(nameof(ArchiveFormat.SevenZip), true)]
    [InlineData(nameof(ArchiveFormat.Rar), false)]
    [InlineData(nameof(ArchiveFormat.Rar), true)]
    [InlineData(nameof(ArchiveFormat.Tar), false)]
    [InlineData(nameof(ArchiveFormat.Tar), true)]
    [InlineData(nameof(ArchiveFormat.Zip), false)]
    [InlineData("ZipPassword", false)]
    public void フォルダーだけを選ぶと同じ名前のファイルは展開しない(string formatName, bool folderFirst)
    {
        var (format, password) = Parse(formatName);
        var archive = SameName(format, password, folderFirst);
        var root = Root(archive);

        // 画面と同じく、一覧のフォルダーから選ぶ
        var selection = EntrySelection.From([], [FolderNamed(root, "a")]);
        Assert.Empty(selection.Files);

        var result = Extract(archive, format, selection, password);

        AssertClean(result);
        Assert.Equal(0, result.Extracted);
        // 書庫に同じ名前のファイルがあるので、フォルダーも作らない (#397)
        Assert.Equal(0, result.CreatedFolders);
        Assert.False(Path.Exists(_place.In("a")));
        Assert.False(Path.Exists(_place.In("b")));
        Assert.False(Path.Exists(_place.In("e")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    [InlineData(nameof(ArchiveFormat.Zip))]
    [InlineData("ZipPassword")]
    public void フォルダーを選ぶとその下だけを展開する(string formatName)
    {
        var (format, password) = Parse(formatName);
        var archive = SameName(format, password);
        var root = Root(archive);

        var result = Extract(archive, format, EntrySelection.From([], [FolderNamed(root, "b")]), password);

        AssertClean(result);
        Assert.Equal(1, result.Extracted);
        Assert.Equal("X", File.ReadAllText(_place.In("b/x.txt")));
        Assert.False(Path.Exists(_place.In("a")));
        Assert.False(Path.Exists(_place.In("e")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    [InlineData(nameof(ArchiveFormat.Zip))]
    [InlineData("ZipPassword")]
    public void ファイルを選ぶとファイルだけを展開する(string formatName)
    {
        var (format, password) = Parse(formatName);
        var archive = SameName(format, password);
        var root = Root(archive);

        var result = Extract(archive, format, EntrySelection.From(root.Files.Where(static f => f.Name == "a"), []), password);

        AssertClean(result);
        Assert.Equal(1, result.Extracted);
        Assert.Equal("FILE", File.ReadAllText(_place.In("a")));
        Assert.Equal(0, result.CreatedFolders);
        Assert.False(Path.Exists(_place.In("b")));
        Assert.False(Path.Exists(_place.In("e")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    [InlineData(nameof(ArchiveFormat.Zip))]
    [InlineData("ZipPassword")]
    public void 中身の無いフォルダーだけを選ぶとそれだけを作る(string formatName)
    {
        // ファイルは 0 個。書庫全体の展開と取り違えない
        var (format, password) = Parse(formatName);
        var archive = SameName(format, password);
        var root = Root(archive);

        var result = Extract(archive, format, EntrySelection.From([], [FolderNamed(root, "e")]), password);

        AssertClean(result);
        Assert.Equal(0, result.Extracted);
        Assert.Equal(1, result.CreatedFolders);
        Assert.True(Directory.Exists(_place.In("e")));
        Assert.False(Path.Exists(_place.In("a")));
        Assert.False(Path.Exists(_place.In("b")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    [InlineData(nameof(ArchiveFormat.Zip))]
    [InlineData("ZipPassword")]
    public void 空の選択では何も展開しない(string formatName)
    {
        var (format, password) = Parse(formatName);
        var archive = SameName(format, password);

        var result = Extract(archive, format, new EntrySelection([]), password);

        AssertClean(result);
        Assert.Equal(0, result.Extracted);
        Assert.Equal(0, result.CreatedFolders);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_place.Output));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip), "e")]
    [InlineData(nameof(ArchiveFormat.Rar), "e")]
    [InlineData(nameof(ArchiveFormat.Tar), "e")]
    [InlineData(nameof(ArchiveFormat.Zip), "e/")]
    public void ファイルの側に入れたフォルダーの名前では作らない(string formatName, string name)
    {
        // フォルダーはフォルダーの側でだけ選ぶ。古い渡し方 (1 つの集まりにフォルダーの名前も入れる) では作られない
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var archive = SameName(format, null);

        var result = Extract(archive, format, new EntrySelection([name]));

        AssertClean(result);
        Assert.Equal(0, result.CreatedFolders);
        Assert.False(Path.Exists(_place.In("e")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    public void ファイルだけを選ぶと中身の無いフォルダーは作らない(string formatName)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var archive = SameName(format, null);
        var root = Root(archive);
        var x = FolderNamed(root, "b").Files.Single();

        var result = Extract(archive, format, EntrySelection.From([x], []));

        AssertClean(result);
        Assert.Equal(1, result.Extracted);
        Assert.Equal(0, result.CreatedFolders);
        Assert.False(Path.Exists(_place.In("e")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    public void 同じ名前のフォルダーの項目が2つあっても作るのは1回(string formatName)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var path = _place.PathOf("twice." + Extension(format));
        var archive = format == ArchiveFormat.SevenZip
            ? new SevenZipSample().Folder("e").Folder("e").Save(path)
            : new RarSample().File("e", directory: true).File("e", directory: true).Save(path);

        var result = Extract(archive, format, EntrySelection.From([], [FolderNamed(Root(archive), "e")]));

        AssertClean(result);
        Assert.Equal(1, result.CreatedFolders);
        Assert.True(Directory.Exists(_place.In("e")));
    }

    // ------------------------------------------------------------------ 上書きの確認に渡す名前

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    public void フォルダーを選んだときの確認に同じ名前のファイルは挙がらない(string formatName)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var archive = SameName(format, null);
        var root = Root(archive);
        File.WriteAllText(_place.In("a"), "USER");

        var (files, folders) = EntrySelection.NamesIn(root, EntrySelection.From([], [FolderNamed(root, "a")]));
        var conflicts = ArchiveExtractor.FindConflicts(files, _place.Output, null, folders);
        var result = Extract(archive, format, EntrySelection.From([], [FolderNamed(root, "a")]));

        Assert.Empty(files);
        // 同じ名前のファイルが書庫にあるフォルダーは作らないので、確認にも挙げない (書庫の全体で決める)
        Assert.Empty(folders);
        Assert.Empty(conflicts);
        AssertClean(result);
        Assert.Equal("USER", File.ReadAllText(_place.In("a")));
    }

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip))]
    [InlineData(nameof(ArchiveFormat.Rar))]
    [InlineData(nameof(ArchiveFormat.Tar))]
    public void ファイルを選んだときの確認にはファイルが挙がる(string formatName)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var archive = SameName(format, null);
        var root = Root(archive);
        File.WriteAllText(_place.In("a"), "USER");

        var (files, folders) = EntrySelection.NamesIn(root, EntrySelection.From(root.Files.Where(static f => f.Name == "a"), []));
        var conflicts = ArchiveExtractor.FindConflicts(files, _place.Output, null, folders);

        Assert.Equal(["a"], files);
        Assert.Empty(folders);
        Assert.Single(conflicts, static c => c.Name == "a" && c.Block is null);
    }

    // ------------------------------------------------------------------ ドラッグの書庫ごとのフォルダー

    [Theory]
    [InlineData(nameof(ArchiveFormat.SevenZip), true)]
    [InlineData(nameof(ArchiveFormat.SevenZip), false)]
    [InlineData(nameof(ArchiveFormat.Rar), true)]
    [InlineData(nameof(ArchiveFormat.Rar), false)]
    [InlineData(nameof(ArchiveFormat.Tar), true)]
    [InlineData(nameof(ArchiveFormat.Tar), false)]
    public void 前のドラッグの残りがファイルとフォルダーを逆にしていれば使い回さない(string formatName, bool folderLeft)
    {
        var format = Enum.Parse<ArchiveFormat>(formatName);
        var root = Root(SameName(format, null));
        var folderA = FolderNamed(root, "a");
        var fileA = root.Files.Single(static f => f.Name == "a");

        // 前のドラッグで、片方を取り出してある
        if (folderLeft)
        {
            Directory.CreateDirectory(_place.In("a"));
        }
        else
        {
            File.WriteAllText(_place.In("a"), "FILE");
        }

        var file = EntrySelection.LeftoverInTheWay(root, EntrySelection.From([fileA], []), [], _place.Output);
        var folder = EntrySelection.LeftoverInTheWay(root, EntrySelection.From([], [folderA]), ["a"], _place.Output);

        // 逆の種類が残っているほうだけ、新しいフォルダーに取り出す
        Assert.Equal(folderLeft, file);
        Assert.Equal(!folderLeft, folder);
    }

    [Fact]
    public void 前のドラッグの残りが下のフォルダーで逆になっていても使い回さない()
    {
        var root = Root(SameName(ArchiveFormat.SevenZip, null));
        // b/x.txt の場所に、前に作ったフォルダーが残っている
        Directory.CreateDirectory(_place.In("b/x.txt"));

        Assert.True(EntrySelection.LeftoverInTheWay(root, EntrySelection.From([], [FolderNamed(root, "b")]), ["b"], _place.Output));
    }

    [Fact]
    public void 同じ種類の残りやまだ何も無いフォルダーは使い回す()
    {
        var root = Root(SameName(ArchiveFormat.SevenZip, null));
        var selection = EntrySelection.From([], [FolderNamed(root, "b")]);

        Assert.False(EntrySelection.LeftoverInTheWay(root, selection, ["b"], _place.PathOf("none")));
        Directory.CreateDirectory(_place.In("b"));
        File.WriteAllText(_place.In("b/x.txt"), "X");
        Assert.False(EntrySelection.LeftoverInTheWay(root, selection, ["b"], _place.Output));
    }

    [Fact]
    public void 書庫全体の名前にはすべてのファイルと作るフォルダーが入る()
    {
        var archive = SameName(ArchiveFormat.SevenZip, null);

        var (files, folders) = EntrySelection.NamesIn(Root(archive), null);

        Assert.Equal(["a", "b/x.txt"], files.Order());
        // a は同じ名前のファイルがあるので作らない
        Assert.Equal(["b", "e"], folders.Order());
    }
}
