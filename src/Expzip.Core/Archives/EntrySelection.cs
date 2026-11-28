namespace Expzip.Archives;

/// <summary>
/// 展開する項目 (#401)。ファイル (リンクを含む) の項目と、フォルダーの項目を分けて持つ。
/// </summary>
/// <remarks>
/// <para>
/// 7z と RAR、末尾に <c>/</c> の無い tar のフォルダーの項目の名前は、同じ名前のファイルの項目と同じ文字列になる。
/// 1 つの名前の集まりで渡すと、フォルダーを選んだだけで同じ名前のファイルも展開される。そのため分けて渡す。
/// </para>
/// <para>
/// 書庫全体を展開するときは、この値を作らずに <see langword="null"/> を渡す。空の値は「何も選んでいない」で、書庫全体ではない。
/// 中身の無いフォルダーだけを選ぶと、ファイルは 0 個になるため。
/// </para>
/// </remarks>
public sealed class EntrySelection
{
    /// <summary>ファイル (リンクを含む) の項目の、書庫の中の名前 (<see cref="ArchiveEntry.SourceName"/>)。</summary>
    public IReadOnlySet<string> Files { get; }

    /// <summary>フォルダーの項目の、書庫の中の名前 (<see cref="ArchiveFolder.SourceName"/>)。</summary>
    public IReadOnlySet<string> Folders { get; }

    /// <param name="files">ファイル (リンクを含む) の項目の名前。</param>
    /// <param name="folders">フォルダーの項目の名前。省くとフォルダーの項目は選ばない。</param>
    public EntrySelection(IEnumerable<string> files, IEnumerable<string>? folders = null)
    {
        Files = new HashSet<string>(files, StringComparer.Ordinal);
        Folders = new HashSet<string>(folders ?? [], StringComparer.Ordinal);
    }

    /// <summary>ファイルとフォルダーの項目の数の合計。</summary>
    public int Count => Files.Count + Folders.Count;

    /// <summary>
    /// 一覧で選んだファイルとフォルダーから作る。フォルダーは配下ごと。書庫にフォルダーの項目があるフォルダーは、その名前もフォルダーとして入れる
    /// (中身の無いフォルダーも展開で作るため、#397)。
    /// </summary>
    internal static EntrySelection From(IEnumerable<ArchiveEntry> files, IEnumerable<ArchiveFolder> folders)
    {
        var fileNames = new HashSet<string>(StringComparer.Ordinal);
        var folderNames = new HashSet<string>(StringComparer.Ordinal);

        void Walk(ArchiveFolder folder)
        {
            if (folder.SourceName is { } own)
            {
                folderNames.Add(own);
            }

            foreach (var file in folder.Files)
            {
                fileNames.Add(file.SourceName);
            }

            foreach (var child in folder.Folders)
            {
                Walk(child);
            }
        }

        foreach (var file in files)
        {
            fileNames.Add(file.SourceName);
        }

        foreach (var folder in folders)
        {
            Walk(folder);
        }

        return new EntrySelection(fileNames, folderNames);
    }

    /// <summary>このファイル (リンクを含む) の項目を展開するか。<paramref name="selection"/> が <see langword="null"/> なら書庫全体なので、すべて。</summary>
    internal static bool WantsFile(EntrySelection? selection, string name)
        => selection is null || selection.Files.Contains(name);

    /// <summary>このフォルダーの項目を展開するか。<paramref name="selection"/> が <see langword="null"/> なら書庫全体なので、すべて。</summary>
    internal static bool WantsFolder(EntrySelection? selection, string name)
        => selection is null || selection.Folders.Contains(name);

    /// <summary>
    /// 展開するファイルとフォルダーの項目の、書庫の中の名前。上書きの確認 (<see cref="ArchiveExtractor.FindConflicts"/>) に渡す。
    /// <paramref name="selection"/> が <see langword="null"/> なら書庫全体。
    /// </summary>
    /// <remarks>
    /// 書庫に同じ名前のファイルやリンクがあるフォルダーは作らないので、確認にも挙げない。選んだ範囲ではなく書庫の全体で決める (展開と同じ、#397)。
    /// </remarks>
    /// <returns>ファイルとフォルダーの名前。それぞれ一覧の順。</returns>
    internal static (List<string> Files, List<string> Folders) NamesIn(ArchiveFolder root, EntrySelection? selection)
    {
        var files = new List<string>();
        var folders = new List<string>();
        var everyFile = new List<(string, bool)>();

        void Walk(ArchiveFolder folder)
        {
            if (folder.SourceName is { } own && WantsFolder(selection, own))
            {
                folders.Add(own);
            }

            foreach (var file in folder.Files)
            {
                everyFile.Add((file.SourceName, false));
                if (WantsFile(selection, file.SourceName))
                {
                    files.Add(file.SourceName);
                }
            }

            foreach (var child in folder.Folders)
            {
                Walk(child);
            }
        }

        Walk(root);

        var nonFolders = EmptyFolders.NonFolderNames(everyFile);
        return (files, folders.Where(f => !EmptyFolders.IsShadowed(f, nonFolders)).ToList());
    }

    /// <summary>
    /// ドラッグで使い回す書庫ごとのフォルダーに、前のドラッグで取り出したものが、ファイルとフォルダーを逆にして残っているか (#401)。
    /// 残っていれば、新しい作業用のフォルダーに取り出す。同じ種類で残っているものは、今までどおり使い回す。
    /// </summary>
    /// <param name="root">書庫の中身の木。</param>
    /// <param name="selection">ドラッグする項目。</param>
    /// <param name="chosenFolders">選んだフォルダーの行の、展開先からの相対パス。下にファイルもフォルダーの項目も無いフォルダーも調べるため。</param>
    /// <param name="directory">書庫ごとのフォルダー。</param>
    /// <returns>残っているか。調べる途中で失敗したときも <see langword="true"/>。</returns>
    internal static bool LeftoverInTheWay(
        ArchiveFolder root, EntrySelection selection, IEnumerable<string> chosenFolders, string directory)
    {
        try
        {
            var (files, folders) = NamesIn(root, selection);
            if (ArchiveExtractor.FindConflicts(files, directory, null, folders).Any(static c => c.Block is not null))
            {
                return true;
            }

            return chosenFolders.Any(relative => File.Exists(Path.Combine(directory, relative)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// ドラッグで渡すパスが、選んだ行と同じ種類の実体を持っているかを確かめる (#401、#427)。
    /// 選んだフォルダーが取り出しで作られなかったとき (書庫に同じ名前のファイルがある、#397) は、ここで空のフォルダーとして用意する。
    /// </summary>
    /// <remarks>
    /// ファイルの行に渡すのは、ファイルか、フォルダーを指すシンボリックリンク (リンクの行がそう取り出される) だけ。
    /// ふつうのフォルダーは渡さない。同じ名前のフォルダーの中身を取り出したときにできたものを、ファイルの代わりに渡さないため。
    /// 行がリンクかは書庫の名前では見分けない。リンクと同じ名前のふつうのファイルがあると、取り違えるため。
    /// </remarks>
    /// <param name="path">渡すパス。</param>
    /// <param name="isFolder">選んだ行がフォルダーか。フォルダーの行には、同じ名前のファイルを代わりに渡さない。</param>
    internal static bool DragPathReady(string path, bool isFolder)
    {
        try
        {
            if (!isFolder)
            {
                return File.Exists(path)
                       || (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0);
            }

            if (Directory.Exists(path))
            {
                return true;
            }

            if (File.Exists(path))
            {
                return false;
            }

            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
