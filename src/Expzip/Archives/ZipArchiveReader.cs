using System.IO;
using System.IO.Compression;
using System.Text;

namespace Expzip.Archives;

/// <summary>書庫の読み込みの進捗。</summary>
/// <param name="DoneEntries">組み立て済みのエントリ数。</param>
/// <param name="TotalEntries">書庫に入っているエントリの総数。</param>
internal readonly record struct OpenProgress(int DoneEntries, int TotalEntries)
{
    /// <summary>0〜100 の進捗率。</summary>
    public double Percent => TotalEntries <= 0 ? 100 : (double)DoneEntries / TotalEntries * 100.0;
}

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
    /// <param name="path">書庫ファイルのパス。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <remarks>
    /// 中断できるのはエントリを組み立てる段階から。その手前の中央ディレクトリの
    /// 読み取りは <see cref="ZipArchive"/> の内部で一息に行われるため割り込めない。
    /// 30万エントリでこの部分が約0.3秒 (#14)。
    /// </remarks>
    /// <exception cref="InvalidDataException">書庫として解釈できない場合。</exception>
    /// <exception cref="IOException">ファイルを読めない場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
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

        var entries = zip.Entries;

        // 進捗通知が多すぎるとUI側が詰まるため、1%刻みに間引く
        var reportStep = Math.Max(1, entries.Count / 100);
        var done = 0;

        foreach (var entry in entries)
        {
            if (++done % reportStep == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new OpenProgress(done, entries.Count));
            }

            var fullName = ArchivePath.Normalize(entry.FullName);

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
                IsPathSuspicious = ArchivePath.IsSuspicious(entry.FullName),
            });

            fileCount++;
            totalLength += entry.Length;
            totalCompressed += entry.CompressedLength;
        }

        cancellationToken.ThrowIfCancellationRequested();
        SortRecursively(root);
        progress?.Report(new OpenProgress(entries.Count, entries.Count));

        return new ArchiveContents
        {
            FilePath = path,
            Root = root,
            FileCount = fileCount,
            TotalLength = totalLength,
            TotalCompressedLength = totalCompressed,
            SuspiciousCount = CountSuspicious(root),
        };
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
            IsPathSuspicious = ArchivePath.IsSuspicious(path),
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
