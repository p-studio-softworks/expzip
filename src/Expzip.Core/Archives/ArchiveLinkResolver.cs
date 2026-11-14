using System.IO;
using System.IO.Compression;
using System.Text;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>一覧で見つけたリンク (#367)。開くときに、指す先を書庫の中でたどるのに使う。</summary>
/// <param name="Kind">種類。</param>
/// <param name="Target">指す先。中身に書かれている形式 (ZIP、7z、RAR4) では、読むまで <see langword="null"/>。</param>
/// <param name="ToFolder">フォルダーを指すか。書庫に書いてない形式では <see langword="null"/>。</param>
/// <param name="Unsupported">展開に対応していない種類 (7-Zip の <c>-snl</c>、種類の分からない tar のリンク)。</param>
internal sealed record ListedLink(RarLinkKind Kind, string? Target, bool? ToFolder, bool Unsupported = false);

/// <summary>リンクをたどった結果 (#367)。</summary>
/// <param name="File">たどり着いたファイル。</param>
/// <param name="Folder">たどり着いたフォルダー。</param>
/// <param name="Reason">たどれなかった理由。</param>
internal sealed record LinkResolution(ArchiveEntry? File, ArchiveFolder? Folder, string? Reason);

/// <summary>
/// リンクを開くときに、指す先の項目を書庫の中でたどる (#367)。
/// </summary>
/// <remarks>
/// 指す先は展開と同じ決まりで解く (<see cref="LinkExtractor.Resolve"/>)。書庫の外を指すもの、書庫に無い名前を指すものは開かない。
/// 開く前の確認 (実行できるファイルか) と、Expzip のタブで開くかは、たどり着いた項目の名前で決める。
/// リンクの名前 (<c>readme.txt</c>) で決めると、確かめないまま実行できるファイルを開いてしまう。
/// </remarks>
internal static class ArchiveLinkResolver
{
    /// <summary>たどる回数の上限。</summary>
    private const int MaxChain = 40;

    /// <summary>
    /// <paramref name="entry"/> がリンクなら、たどり着いた項目を返す。リンクでなければ <see langword="null"/>。
    /// </summary>
    /// <param name="password">パスワード付きの ZIP の合言葉。中身に書かれた指す先を読むのに使う。</param>
    public static LinkResolution? Resolve(
        ArchiveContents contents, ArchiveEntry entry, string? password, CancellationToken cancellationToken)
    {
        if (!contents.Links.ContainsKey(entry.SourceName))
        {
            return null;
        }

        var files = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        var folders = new Dictionary<string, ArchiveFolder>(StringComparer.OrdinalIgnoreCase);
        Collect(contents.Root, files, folders);

        // まとめて圧縮した 7z は、1 個読むたびにその前を全部伸長する。鎖に出てくるかもしれないリンクの中身を、1 回の読み通しでまとめて読む
        var preloaded = contents.Format == ArchiveFormat.SevenZip
            ? ReadSevenZipAll(contents, cancellationToken)
            : null;

        var current = entry;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var depth = 0; depth < MaxChain; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!contents.Links.TryGetValue(current.SourceName, out var link))
            {
                return new LinkResolution(current, null, null);
            }

            if (!seen.Add(current.SourceName))
            {
                return new LinkResolution(null, null, Strings.LinkLoops);
            }

            if (link.Unsupported)
            {
                return new LinkResolution(null, null, Strings.LinkOpenNotSupported);
            }

            if (ArchiveLinks.Canonical(current.SourceName) is not { } name)
            {
                return new LinkResolution(null, null, Strings.LinkOpenOutside);
            }

            var target = link.Target
                         ?? (preloaded is not null
                             ? preloaded.GetValueOrDefault(current.SourceName)
                             : ReadTarget(contents, current, password, cancellationToken));

            // 中止されたときは、読み取れないとは言わずに中止として返す
            cancellationToken.ThrowIfCancellationRequested();
            if (target is null)
            {
                return new LinkResolution(null, null, Strings.LinkOpenUnreadable);
            }

            // ハードリンクとファイルのコピーの指す先は、書庫の中の名前。シンボリックリンクとジャンクションは、リンクのあるフォルダーからの相対パス
            var resolved = link.Kind is RarLinkKind.HardLink or RarLinkKind.FileCopy
                ? ArchiveLinks.Canonical(target)
                : LinkExtractor.Resolve(name, target);
            if (resolved is null)
            {
                return new LinkResolution(null, null, Strings.LinkOpenOutside);
            }

            var key = Key(resolved);
            var isFile = files.TryGetValue(key, out var file);
            var isFolder = folders.TryGetValue(key, out var folder);
            if (isFile && isFolder)
            {
                return new LinkResolution(null, null, Strings.LinkOpenKindUnknown);
            }

            if (isFolder)
            {
                return link.ToFolder == false
                    ? new LinkResolution(null, null, Strings.LinkOpenKindUnknown)
                    : new LinkResolution(null, folder, null);
            }

            if (!isFile)
            {
                return new LinkResolution(null, null, Strings.LinkOpenOutside);
            }

            // 指す先の末尾が区切りならフォルダーを指すはず。展開と同じく、ファイルなら開かない
            if (link.ToFolder == true || link.Kind == RarLinkKind.Junction || target.EndsWith('/') || target.EndsWith('\\'))
            {
                return new LinkResolution(null, null, Strings.LinkOpenKindUnknown);
            }

            current = file!;
        }

        return new LinkResolution(null, null, Strings.LinkLoops);
    }

    private static string Key(string canonical) => canonical.Normalize(NormalizationForm.FormC);

    private static void Collect(
        ArchiveFolder folder, Dictionary<string, ArchiveEntry> files, Dictionary<string, ArchiveFolder> folders)
    {
        foreach (var file in folder.Files)
        {
            if (ArchiveLinks.Canonical(file.SourceName) is { } name)
            {
                // 同じ名前が 2 つあれば、書庫で後ろのもの (展開と同じ)
                files[Key(name)] = file;
            }
        }

        foreach (var sub in folder.Folders)
        {
            if (ArchiveLinks.Canonical(sub.FullPath) is { } name)
            {
                folders.TryAdd(Key(name), sub);
            }

            Collect(sub, files, folders);
        }
    }

    /// <summary>中身に書かれた指す先を読む (ZIP、7z、RAR4)。読み方は展開と同じ。</summary>
    private static string? ReadTarget(
        ArchiveContents contents, ArchiveEntry entry, string? password, CancellationToken cancellationToken)
    {
        try
        {
            return contents.Format switch
            {
                ArchiveFormat.SevenZip => ReadSevenZip(contents.FilePath, entry.SourceName, cancellationToken),
                ArchiveFormat.Rar => ReadRar(contents, entry.SourceName, cancellationToken),
                _ when ArchiveFormats.IsZipBased(contents.Format) && password is not null
                    => ZipEncryption.ReadLinkTarget(contents.FilePath, contents.Names, password, entry.SourceName, cancellationToken),
                _ when ArchiveFormats.IsZipBased(contents.Format)
                    => ReadZip(contents, entry.SourceName, cancellationToken),
                _ => null,
            };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       || CancellableCopy.IsLibraryFailure(ex))
        {
            return null;
        }
    }

    private static string? ReadZip(ArchiveContents contents, string sourceName, CancellationToken cancellationToken)
    {
        using var stream = ZipPrefix.Open(contents.FilePath);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false, contents.Names.Encoding);
        using var fallback = new ZipMethodFallback(contents.FilePath, contents.Names);

        // 同じ名前が 2 つあれば、後ろのもの
        var entry = zip.Entries.LastOrDefault(e => ArchiveFormats.EntryName(contents.Format, e.FullName) == sourceName);
        return entry is null || !ZipLinks.IsLink(entry.ExternalAttributes)
            ? null
            : ArchiveLinks.ReadTarget(() => fallback.Open(entry.FullName, entry.Open), entry.Length, entry.Crc32,
                cancellationToken, out _);
    }

    /// <summary>7z のリンクの指す先を、書庫を 1 回読み通すあいだにまとめて読む。読み方は展開と同じ。</summary>
    private static Dictionary<string, string?> ReadSevenZipAll(ArchiveContents contents, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        var wanted = contents.Links.Where(static l => l.Value.Target is null && !l.Value.Unsupported)
            .Select(static l => l.Key).ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return result;
        }

        try
        {
            using var archive = SharpArchiveAccess.OpenSevenZip(contents.FilePath);
            using var reader = archive.ExtractAllEntries();
            while (reader.MoveToNextEntry())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = reader.Entry;
                if (entry.Key is not { } key || !wanted.Contains(key) || entry.IsDirectory
                    || ArchiveLinks.SevenZipKind(entry) != SevenZipLink.Symbolic)
                {
                    continue;
                }

                // 同じ名前が 2 つあれば、後ろのもの (一覧と同じ)
                result[key] = ArchiveLinks.ReadTarget(
                    reader.OpenEntryStream, entry.Size, entry.Crc != 0 ? entry.Crc : -1, cancellationToken, out _);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException || CancellableCopy.IsLibraryFailure(ex))
        {
            // 読めたところまでを使う。読めなかったものは「読み取れない」になる
        }

        return result;
    }

    private static string? ReadSevenZip(string path, string sourceName, CancellationToken cancellationToken)
    {
        using var archive = SharpArchiveAccess.OpenSevenZip(path);
        var entry = archive.Entries.LastOrDefault(e => e.Key == sourceName);
        return entry is null || ArchiveLinks.SevenZipKind(entry) != SevenZipLink.Symbolic
            ? null
            : ArchiveLinks.ReadTarget(() => entry.OpenEntryStream(), entry.Size, entry.Crc != 0 ? entry.Crc : -1,
                cancellationToken, out _);
    }

    private static string? ReadRar(ArchiveContents contents, string sourceName, CancellationToken cancellationToken)
    {
        using var session = RarAccess.Open(contents.FilePath, contents.Names, null, cancellationToken);
        for (var i = session.Entries.Count - 1; i >= 0; i--)
        {
            if (session.Entries[i].Key == sourceName && session.ItemAt(i).Link == RarLinkKind.LegacyUnixSymbolicLink)
            {
                var entry = session.Entries[i];
                return ArchiveLinks.ReadTarget(entry.OpenEntryStream, entry.Size, expectedCrc: -1, cancellationToken, out _);
            }
        }

        return null;
    }
}
