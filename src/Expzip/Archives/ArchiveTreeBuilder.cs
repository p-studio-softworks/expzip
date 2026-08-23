using System.IO;

namespace Expzip.Archives;

/// <summary>
/// エントリの列からフォルダ階層を組み立てる。形式によらず同じ形にするため、
/// ZIP も 7z も tar もここを通す (#19)。
/// </summary>
/// <remarks>
/// 書庫はフォルダ構造を明示的に持たないことがあるため、階層はエントリのパスから作る。
/// </remarks>
internal sealed class ArchiveTreeBuilder
{
    /// <summary>パスからフォルダを引くための索引。数万エントリでも線形探索にならないようにする。</summary>
    private readonly Dictionary<string, ArchiveFolder> _folders = new(StringComparer.Ordinal);

    private readonly ArchiveFolder _root;

    public ArchiveTreeBuilder(string archivePath)
    {
        _root = new ArchiveFolder
        {
            Name = Path.GetFileName(archivePath),
            FullPath = string.Empty,
            IsExpanded = true,
        };

        _folders[string.Empty] = _root;
    }

    /// <summary>ここまでに加えたファイルの数。</summary>
    public int FileCount { get; private set; }

    /// <summary>展開後サイズの合計。</summary>
    public long TotalLength { get; private set; }

    /// <summary>圧縮後サイズの合計。</summary>
    public long TotalCompressedLength { get; private set; }

    /// <summary>フォルダを作る。中身の無いフォルダを残すために使う。</summary>
    public void AddFolder(string path)
    {
        var normalized = Trim(path);
        if (normalized.Length != 0)
        {
            GetOrCreateFolder(normalized);
        }
    }

    /// <summary>ファイルを加える。途中の階層は必要に応じて作られる。</summary>
    /// <param name="sourceName">書庫内での元のエントリ名。展開時に引き当てるのに使う。</param>
    /// <param name="length">展開後のサイズ。</param>
    /// <param name="compressedLength">圧縮後のサイズ。分からない形式では 0 を渡す。</param>
    /// <param name="compressedLengthKnown">圧縮後のサイズが分かるかどうか。</param>
    /// <param name="lastWriteTime">最終更新日時。</param>
    public void AddFile(
        string sourceName,
        long length,
        long compressedLength,
        bool compressedLengthKnown,
        DateTime lastWriteTime)
    {
        var fullName = Trim(sourceName);

        var separator = fullName.LastIndexOf('/');
        var parentPath = separator < 0 ? string.Empty : fullName[..separator];
        var name = separator < 0 ? fullName : fullName[(separator + 1)..];

        // 名前を持たないエントリは壊れているとみなして飛ばす
        if (name.Length == 0)
        {
            return;
        }

        GetOrCreateFolder(parentPath).Files.Add(new ArchiveEntry
        {
            FullPath = fullName,
            SourceName = sourceName,
            Name = name,
            Length = length,
            CompressedLength = compressedLength,
            CompressedLengthKnown = compressedLengthKnown,
            LastWriteTime = lastWriteTime,
            IsPathSuspicious = ArchivePath.IsSuspicious(sourceName),
        });

        FileCount++;
        TotalLength += length;
        TotalCompressedLength += compressedLength;
    }

    /// <summary>組み立てた内容を返す。</summary>
    /// <param name="archivePath">書庫ファイルのパス。</param>
    /// <param name="format">書庫の形式。</param>
    /// <param name="totalCompressedLength">
    /// エントリごとの圧縮後サイズが分からない形式で、書庫全体の大きさを代わりに使う場合に渡す。
    /// </param>
    /// <param name="hasEncryptedEntries">暗号化されたエントリを含むかどうか。</param>
    /// <param name="requiresPassword">中身の取り出しにパスワードが要るかどうか (#20)。</param>
    /// <param name="usesAes">AES で暗号化されているかどうか (#20)。</param>
    public ArchiveContents Build(
        string archivePath,
        ArchiveFormat format,
        long? totalCompressedLength = null,
        bool hasEncryptedEntries = false,
        bool requiresPassword = false,
        bool usesAes = false)
    {
        SortRecursively(_root);

        return new ArchiveContents
        {
            FilePath = archivePath,
            Format = format,
            Root = _root,
            FileCount = FileCount,
            TotalLength = TotalLength,
            TotalCompressedLength = totalCompressedLength ?? TotalCompressedLength,
            SuspiciousCount = CountSuspicious(_root),
            HasEncryptedEntries = hasEncryptedEntries,
            RequiresPassword = requiresPassword,
            UsesAes = usesAes,
        };
    }

    /// <summary>
    /// エントリ名を書庫内パスの形に整える。
    /// 区切りを <c>/</c> に統一し、tar が付ける先頭の <c>./</c> と末尾の区切りを落とす。
    /// </summary>
    public static string Trim(string entryName)
    {
        var normalized = ArchivePath.Normalize(entryName).TrimEnd('/');

        while (normalized.StartsWith("./", StringComparison.Ordinal))
        {
            normalized = normalized[2..];
        }

        return normalized == "." ? string.Empty : normalized;
    }

    /// <summary>パスが通常ではない項目の数を数える。</summary>
    private static int CountSuspicious(ArchiveFolder folder)
    {
        var count = folder.Files.Count(static f => f.IsPathSuspicious);

        foreach (var child in folder.Folders)
        {
            if (child.IsPathSuspicious)
            {
                count++;
            }

            count += CountSuspicious(child);
        }

        return count;
    }

    /// <summary>指定パスのフォルダを取得する。無ければ途中の階層ごと作る。</summary>
    private ArchiveFolder GetOrCreateFolder(string path)
    {
        if (_folders.TryGetValue(path, out var found))
        {
            return found;
        }

        var separator = path.LastIndexOf('/');
        var parentPath = separator < 0 ? string.Empty : path[..separator];
        var name = separator < 0 ? path : path[(separator + 1)..];

        var parent = GetOrCreateFolder(parentPath);
        var folder = new ArchiveFolder
        {
            Name = name,
            FullPath = path,
            Parent = parent,
            IsPathSuspicious = ArchivePath.IsSuspicious(path),
        };

        parent.Folders.Add(folder);
        _folders[path] = folder;
        return folder;
    }

    /// <summary>表示順を安定させるため、フォルダとファイルを名前順に並べる。</summary>
    private static void SortRecursively(ArchiveFolder folder)
    {
        folder.Folders.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        folder.Files.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        foreach (var child in folder.Folders)
        {
            SortRecursively(child);
        }
    }
}
