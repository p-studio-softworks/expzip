using System.IO;
using System.IO.Compression;
using System.Text;

namespace Expzip.Archives;

/// <summary>ZIP書庫を読み込み、フォルダ階層に組み立てる。</summary>
internal static class ZipArchiveReader
{
    /// <summary>従来の日本語書庫で使われてきたコードページ。</summary>
    private const int LegacyJapaneseCodePage = 932;

    private static Encoding? _entryNameEncoding;

    /// <summary>
    /// エントリ名の解釈に使う <see cref="Encoding"/>。
    /// CP932は.NET Core以降 既定では登録されていないため、初回に取得を試みる。
    /// 展開時も同じ解釈でなければエントリを引き当てられないため、共有している。
    /// </summary>
    internal static Encoding EntryNameEncoding
    {
        get
        {
            if (_entryNameEncoding is not null)
            {
                return _entryNameEncoding;
            }

            Encoding legacy;
            try
            {
                legacy = Encoding.GetEncoding(LegacyJapaneseCodePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // プロバイダーが登録されていない環境ではUTF-8のみで動作させる。
                // 古い書庫のファイル名は化けるが、書庫自体は開ける。
                legacy = Encoding.UTF8;
            }

            return _entryNameEncoding = new ArchiveEntryNameEncoding(legacy);
        }
    }

    /// <summary>ZIP書庫を開いて内容を読み取る。</summary>
    /// <exception cref="InvalidDataException">書庫として解釈できない場合。</exception>
    /// <exception cref="IOException">ファイルを読めない場合。</exception>
    public static ArchiveContents Open(string path)
    {
        var root = new ArchiveFolder
        {
            Name = Path.GetFileName(path),
            FullPath = string.Empty,
            IsExpanded = true,
        };

        // パスからフォルダを引くための索引。数万エントリでも線形探索にならないようにする。
        var folders = new Dictionary<string, ArchiveFolder>(StringComparer.Ordinal)
        {
            [string.Empty] = root,
        };

        var fileCount = 0;
        long totalLength = 0;
        long totalCompressed = 0;

        using var stream = File.OpenRead(path);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, EntryNameEncoding);

        foreach (var entry in zip.Entries)
        {
            var fullName = NormalizeSeparators(entry.FullName);

            // 末尾が区切り文字のエントリはフォルダそのものを表す
            if (fullName.EndsWith('/'))
            {
                GetOrCreateFolder(folders, fullName.TrimEnd('/'));
                continue;
            }

            var separator = fullName.LastIndexOf('/');
            var parentPath = separator < 0 ? string.Empty : fullName[..separator];
            var name = separator < 0 ? fullName : fullName[(separator + 1)..];

            // 名前を持たないエントリは壊れているとみなして飛ばす
            if (name.Length == 0)
            {
                continue;
            }

            var parent = GetOrCreateFolder(folders, parentPath);
            parent.Files.Add(new ArchiveEntry
            {
                FullPath = fullName,
                SourceName = entry.FullName,
                Name = name,
                Length = entry.Length,
                CompressedLength = entry.CompressedLength,
                LastWriteTime = ReadLastWriteTime(entry),
            });

            fileCount++;
            totalLength += entry.Length;
            totalCompressed += entry.CompressedLength;
        }

        SortRecursively(root);

        return new ArchiveContents
        {
            FilePath = path,
            Root = root,
            FileCount = fileCount,
            TotalLength = totalLength,
            TotalCompressedLength = totalCompressed,
        };
    }

    /// <summary>
    /// 区切り文字を <c>/</c> に揃える。
    /// ZIP仕様は <c>/</c> と定めているが、DOS時代のツールには <c>\</c> を書くものがあった。
    /// Windowsのファイル名に <c>\</c> は使えないため、区切りとみなして差し支えない。
    /// </summary>
    private static string NormalizeSeparators(string name)
        => name.Replace('\\', '/');

    /// <summary>指定パスのフォルダを取得する。無ければ途中の階層ごと作る。</summary>
    private static ArchiveFolder GetOrCreateFolder(Dictionary<string, ArchiveFolder> folders, string path)
    {
        if (folders.TryGetValue(path, out var found))
        {
            return found;
        }

        var separator = path.LastIndexOf('/');
        var parentPath = separator < 0 ? string.Empty : path[..separator];
        var name = separator < 0 ? path : path[(separator + 1)..];

        var parent = GetOrCreateFolder(folders, parentPath);
        var folder = new ArchiveFolder
        {
            Name = name,
            FullPath = path,
            Parent = parent,
        };

        parent.Folders.Add(folder);
        folders[path] = folder;
        return folder;
    }

    /// <summary>
    /// 最終更新日時を読む。書庫によっては範囲外の日付が入っており、
    /// <see cref="ZipArchiveEntry.LastWriteTime"/> が例外を投げることがある。
    /// </summary>
    private static DateTime ReadLastWriteTime(ZipArchiveEntry entry)
    {
        try
        {
            return entry.LastWriteTime.LocalDateTime;
        }
        catch (ArgumentOutOfRangeException)
        {
            return default;
        }
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
