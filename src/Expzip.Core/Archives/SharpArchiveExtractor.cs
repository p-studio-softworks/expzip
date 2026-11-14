using System.IO;
using SharpCompress.Common;
using SharpCompress.Readers;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>7z / tar 書庫から実ファイルへ展開する (#19)。</summary>
/// <remarks>
/// <para>
/// 判定や後始末は ZIP の展開 (<see cref="ArchiveExtractor"/>) と揃えてある。
/// 書庫の外を指すパスは弾き、書き出したファイルには更新日時と MotW を移し、
/// 中断したら書きかけのファイルを消す。
/// </para>
/// <para>
/// 7z は<b>まとめて圧縮されている (ソリッド)</b> ため、エントリを1件ずつ開くと
/// そのたびに同じ塊を復号し直すことになる。2,000件の書庫で計測したところ
/// 1件ずつでは50.4秒かかり、先頭から順に読む方式では0.14秒だった (370倍)。
/// 2件以上を取り出すときは必ず順に読む。
/// </para>
/// </remarks>
internal static class SharpArchiveExtractor
{
    /// <summary>指定したエントリを展開する。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。</summary>
    public static ExtractResult Extract(
        string archivePath,
        ArchiveFormat format,
        NameReading names,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        ExtractOverwrite overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null,
        LinkOptions? links = null)
    {
        var state = new ExtractState(
            Path.GetFullPath(destinationDirectory), overwrite, zoneIdentifier, basePath, progress) { Links = links };

        try
        {
            if (format == ArchiveFormat.SevenZip)
            {
                ExtractSevenZip(archivePath, sourceNames, state, zoneIdentifier, cancellationToken);
            }
            else
            {
                ExtractTar(archivePath, names, sourceNames, state, zoneIdentifier, cancellationToken);
            }
        }
        catch (Exception ex) when (CancellableCopy.IsLibraryFailure(ex))
        {
            // 開く時点で壊れに気付くと、ライブラリの英語の文のまま画面に出る (#294)
            state.Fail(Path.GetFileName(archivePath), Strings.Reason(new DamagedDataException(ex)));
        }

        return state.ToResult();
    }

    private static void ExtractSevenZip(
        string archivePath, IReadOnlySet<string>? sourceNames, ExtractState state, string? zone,
        CancellationToken cancellationToken)
    {
        var fileCrcs = SharpArchiveAccess.CheckSevenZipContent(archivePath, cancellationToken);

        using var archive = SharpArchiveAccess.OpenSevenZip(archivePath);

        var folderCrcs = SharpArchiveAccess.FolderCrcsByName(archive, fileCrcs);

        // リンクの見分けは、その項目の属性で行う (#367)。名前で引くと、同じ名前のリンクとファイルを取り違える
        var entries = archive.Entries.ToList();
        var kinds = entries.Select(static e => ArchiveLinks.SevenZipKind(e)).ToList();
        // フォルダーの項目は、リンクの属性が付いていてもリンクとしては扱わない (一覧と同じ)
        var linkFlags = entries.Select((e, i) => (e.Key ?? string.Empty, kinds[i] != SevenZipLink.None && !e.IsDirectory)).ToList();
        var laterLink = ArchiveExtractor.LaterLinks(linkFlags);
        var superseded = ArchiveExtractor.Superseded(linkFlags);

        // リンクの指す先が、今回展開したファイルかを確かめられるように、書いたファイルを覚える (#367)
        if (linkFlags.Any(static k => k.Item2))
        {
            state.Overwrite.RecordCreated = true;
        }

        var wantedIndexes = new HashSet<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (e.IsDirectory || e.Key is not { } key || (sourceNames is not null && !sourceNames.Contains(key)))
            {
                continue;
            }

            switch (kinds[i])
            {
                case SevenZipLink.WindowsReparse:
                    // 7-Zip の -snl で入れたリンク。中身は Windows の内部のデータなので、ファイルとしても書き出さない (#377)
                    if (!superseded.Contains(i))
                    {
                        state.FailLink(key, Strings.LinkKindNotExtracted);
                    }

                    continue;
                case SevenZipLink.Symbolic when superseded.Contains(i):
                    continue;
                case SevenZipLink.None when laterLink.Contains(i):
                    // 同じ名前のリンクが後ろにある。そちらが残る
                    continue;
            }

            wantedIndexes.Add(i);
        }

        state.TotalBytes = wantedIndexes.Where(i => kinds[i] == SevenZipLink.None).Sum(i => entries[i].Size);
        var linkTargets = new Dictionary<int, string>();

        void Take(int index, IEntry entry, Func<Stream> open)
        {
            if (kinds[index] == SevenZipLink.None)
            {
                state.Write(
                    entry.Key!, entry.Size, entry.LastModifiedTime, open, cancellationToken,
                    SharpArchiveAccess.SevenZipCrc(entry, folderCrcs), sizeIsExact: true);
            }
            else if (ArchiveLinks.ReadTarget(
                         open, entry.Size, SharpArchiveAccess.SevenZipCrc(entry, folderCrcs), cancellationToken,
                         out var reason) is { } target)
            {
                linkTargets[index] = target;
            }
            else if (!cancellationToken.IsCancellationRequested)
            {
                state.FailLink(entry.Key!, reason);
            }
        }

        // 1 件だけなら、その塊を復号するだけで済むので直接開く
        if (wantedIndexes.Count == 1)
        {
            var only = wantedIndexes.Single();
            Take(only, entries[only], () => entries[only].OpenEntryStream());
        }
        else if (wantedIndexes.Count > 1)
        {
            // 2 件以上は先頭から順に読む。塊ごとに一度だけ復号すれば済む。
            // 順に読む道の項目は別のオブジェクトなので、同じ名前の何番目かで一覧の項目と結び付ける
            var byName = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            for (var i = 0; i < entries.Count; i++)
            {
                if (entries[i].Key is { } key)
                {
                    (byName.TryGetValue(key, out var list) ? list : byName[key] = []).Add(i);
                }
            }

            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            using var reader = archive.ExtractAllEntries();

            // 塊の復号に失敗すると、取り出しの手前 (次のエントリへ進む段階) で例外になる。
            // パスワード付きの書庫がこれに当たるため、1 件ずつの失敗と同じように報告する
            try
            {
                while (reader.MoveToNextEntry())
                {
                    if (state.Cancelled)
                    {
                        return;
                    }

                    var entry = reader.Entry;
                    if (entry.Key is not { } key || !byName.TryGetValue(key, out var indexes))
                    {
                        continue;
                    }

                    var nth = seen.GetValueOrDefault(key);
                    seen[key] = nth + 1;
                    if (nth >= indexes.Count || !wantedIndexes.Contains(indexes[nth]) || entry.IsDirectory)
                    {
                        continue;
                    }

                    // 属性は、今読んでいる項目のもので決める
                    if (ArchiveLinks.SevenZipKind(entry) != kinds[indexes[nth]])
                    {
                        throw new DamagedDataException(new InvalidDataException("7z entry order differs."));
                    }

                    Take(indexes[nth], entry, reader.OpenEntryStream);
                }
            }
            catch (Exception ex) when (IsReadFailure(ex) || ex is DamagedDataException)
            {
                state.Fail(Path.GetFileName(archivePath), Explain(ex));
                return;
            }
        }

        if (state.Cancelled || cancellationToken.IsCancellationRequested || linkTargets.Count == 0)
        {
            return;
        }

        var requests = linkTargets.OrderBy(static p => p.Key)
            .Select(p => new LinkRequest(entries[p.Key].Key!, RarLinkKind.UnixSymbolicLink, p.Value, null, p.Key))
            .ToList();
        LinkExtractor.Extract(
            requests, entries.Select(static e => new LinkScopeEntry(e.Key ?? string.Empty, e.IsDirectory)).ToList(),
            state, zone, cancellationToken);
    }

    private static void ExtractTar(
        string archivePath, NameReading names, IReadOnlySet<string>? sourceNames, ExtractState state, string? zone,
        CancellationToken cancellationToken)
    {
        using var stream = ArchiveFile.OpenRead(archivePath);
        using var reader = SharpArchiveAccess.OpenTarReader(stream, names);

        // ハードリンクの指す先が、今回展開したファイルかを確かめられるように、書いたファイルを覚える (#367)。
        // tar は順に読むので、リンクの項目が出てくるまでリンクがあるか分からない。リンクより前に書いたファイルも覚えておく
        state.Overwrite.RecordCreated = true;

        var all = new List<LinkScopeEntry>();
        var requests = new List<LinkRequest>();

        try
        {
            TarLoop(reader, sourceNames, state, all, requests, cancellationToken);
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            state.Fail(Path.GetFileName(archivePath), Explain(ex));
            return;
        }

        if (state.Cancelled || cancellationToken.IsCancellationRequested || requests.Count == 0)
        {
            return;
        }

        LinkExtractor.Extract(requests, all, state, zone, cancellationToken);
    }

    private static void TarLoop(
        IReader reader, IReadOnlySet<string>? sourceNames, ExtractState state,
        List<LinkScopeEntry> all, List<LinkRequest> requests, CancellationToken cancellationToken)
    {
        while (reader.MoveToNextEntry())
        {
            if (state.Cancelled)
            {
                return;
            }

            var entry = reader.Entry;
            var index = all.Count;
            all.Add(new LinkScopeEntry(entry.Key ?? string.Empty, entry.IsDirectory));

            if (entry.IsDirectory || entry.Key is not { } key)
            {
                continue;
            }

            if (sourceNames is not null && !sourceNames.Contains(key))
            {
                continue;
            }

            // リンクは、ファイルを全部書き出したあとに展開する (#367)。種類は SharpCompress の中の値で決める
            var kind = ArchiveLinks.TarKind(entry);
            if (kind != RarLinkKind.None)
            {
                if (kind == RarLinkKind.LegacyUnixSymbolicLink)
                {
                    // 種類を読めなかった。シンボリックリンクかハードリンクか分からないので展開しない
                    state.FailLink(key, Strings.LinkKindNotExtracted);
                }
                else if (entry.LinkTarget is not { Length: > 0 and <= ArchiveLinks.MaxTarget } target)
                {
                    state.FailLink(key, Strings.LinkTargetUnreadable);
                }
                else
                {
                    requests.Add(new LinkRequest(key, kind, target, kind == RarLinkKind.HardLink ? false : null, index));
                }

                continue;
            }

            // tar は総量を先に知る手立てが無い。取り出した分を足しながら出す
            state.TotalBytes += entry.Size;
            state.Write(key, entry.Size, entry.LastModifiedTime, reader.OpenEntryStream, cancellationToken, sizeIsExact: true);
        }
    }

    /// <summary>書庫そのものを読み進められなくなる類の失敗か。</summary>
    /// <remarks>
    /// 壊れたデータでは、次のエントリへ進む段階で SharpCompress 独自の例外
    /// (ZlibException、DataErrorException など) になる。基の型で受ける (#167)。
    /// </remarks>
    private static bool IsReadFailure(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompressException
            or IOException or InvalidDataException or NotSupportedException;

    /// <summary>失敗の理由を、画面に出せる言葉にする。</summary>
    private static string Explain(Exception ex)
        => ex is System.Security.Cryptography.CryptographicException
            or SharpCompress.Common.CryptographicException
            ? Strings.PasswordNotSupported
            : Strings.Reason(ex);
}
