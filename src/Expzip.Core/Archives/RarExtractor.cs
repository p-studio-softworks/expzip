using System.IO;
using System.Text;
using SharpCompress.Archives;
using SharpCompress.Common;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>RAR から実ファイルへ展開する (#336)。</summary>
/// <remarks>
/// <para>
/// 判定や後始末は ZIP や 7z の展開と揃えてある (<see cref="ExtractState"/>)。展開先の外に書き込もうとするパスは書き出さず、
/// 書き出したファイルには更新日時と MotW を移し、中断したら書きかけのファイルを消す。
/// </para>
/// <para>
/// まとめて圧縮した (ソリッド) 書庫は、7z と同じく、2 個以上を取り出すときは先頭から順に読む。1 個ずつ開くと、
/// そのたびに先頭から伸長し直す。SharpCompress も、ソリッドの書庫を順に読む道でしか読めない。ソリッドでない書庫は 1 個ずつ開く。
/// </para>
/// <para>
/// 中身が壊れていないかは、SharpCompress が取り出しの終わりに CRC32 か BLAKE2 で確かめる (合わなければ例外)。
/// Expzip が書き出したあとに CRC を確かめる処理 (#168) は使わない。RAR5 の BLAKE2 の書庫と暗号化した書庫では、
/// 項目の CRC に CRC32 ではない値が入っている。長さを確かめる処理にも頼らない (大きさが書かれていない項目があるため)。
/// </para>
/// <para>
/// リンクは、全部のファイルを書き出したあとに、<see cref="LinkExtractor"/> でリンクのまま展開する。
/// </para>
/// </remarks>
internal static class RarExtractor
{
    /// <summary>RAR4 のシンボリックリンクの指す先として読む中身の上限。</summary>
    private const int MaxLinkTarget = 4096;

    /// <summary>指定したエントリを展開する。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。</summary>
    public static ExtractResult Extract(
        string archivePath,
        NameReading names,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        ExtractOverwrite overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null)
    {
        var state = new ExtractState(
            Path.GetFullPath(destinationDirectory), overwrite, zoneIdentifier, basePath, progress);

        try
        {
            using var session = RarAccess.Open(archivePath, names, null, cancellationToken);
            Run(session, sourceNames, state, zoneIdentifier, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // SharpCompress の中で読み進めている間に中止された。書きかけは AtomicFile が消す
            state.Cancel();
        }
        catch (Exception ex) when (RarReader.Translate(ex) is { } translated)
        {
            state.Fail(Path.GetFileName(archivePath), Strings.Reason(translated));
        }
        catch (Exception ex) when (ex is DamagedDataException or EncryptedHeaderException or MissingVolumeException
                                       or ArchiveTooLargeException or UnsupportedRarException
                                       or IOException or UnauthorizedAccessException)
        {
            state.Fail(Path.GetFileName(archivePath), Strings.Reason(ex));
        }

        return state.ToResult();
    }

    private static void Run(
        RarSession session, IReadOnlySet<string>? sourceNames, ExtractState state, string? zone,
        CancellationToken cancellationToken)
    {
        var entries = session.Entries;
        bool Wanted(int i) => !entries[i].IsDirectory && entries[i].Key is { } key
                              && (sourceNames is null || sourceNames.Contains(key));

        var files = new List<int>();
        var links = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (!Wanted(i))
            {
                continue;
            }

            (session.ItemAt(i).Link == RarLinkKind.None ? files : links).Add(i);
        }

        // まとめて圧縮した書庫では、大きさが書かれていない項目より後ろは読めない (読み飛ばすところで SharpCompress が失敗する)。
        // 読み始める前に理由を付けて外し、手前のものだけを展開する
        var stopAt = RarSizeUnknownException.FirstInSolid(session);
        if (stopAt >= 0)
        {
            bool Behind(int i) => i > stopAt && (session.ItemAt(i).Link is RarLinkKind.None or RarLinkKind.LegacyUnixSymbolicLink);
            foreach (var i in files.Concat(links).Where(Behind))
            {
                state.Fail(entries[i].Key!, Strings.Reason(new RarSizeUnknownException(after: true)));
            }

            files.RemoveAll(Behind);
            links.RemoveAll(Behind);
        }

        // リンクの指す先 (今回展開したファイル) を確かめられるように、書いたファイルを覚える
        if (links.Count > 0)
        {
            state.Overwrite.RecordCreated = true;
        }

        // 書庫に書かれた大きさは作った人が決められるので、合計は上限で止める (桁があふれると展開全体が止まる)
        state.TotalBytes = files.Aggregate(0L, (total, i) => total > long.MaxValue - KnownSize(session, i)
            ? long.MaxValue
            : total + KnownSize(session, i));
        var legacyTargets = new Dictionary<int, string>();
        var legacyWanted = links.Where(i => session.ItemAt(i).Link == RarLinkKind.LegacyUnixSymbolicLink).ToHashSet();

        if (session.Layout.IsSolid && files.Count + legacyWanted.Count > 1)
        {
            ExtractInOrder(session, files.ToHashSet(), legacyWanted, legacyTargets, state, cancellationToken);
        }
        else
        {
            foreach (var i in files)
            {
                if (state.Cancelled)
                {
                    return;
                }

                var entry = entries[i];
                state.Write(entry.Key!, KnownSize(session, i), entry.LastModifiedTime, Opener(session, i, entry.OpenEntryStream), cancellationToken);
            }

            foreach (var i in legacyWanted)
            {
                if (ReadLegacyTarget(entries[i].OpenEntryStream) is { } target)
                {
                    legacyTargets[i] = target;
                }
            }
        }

        if (state.Cancelled || links.Count == 0)
        {
            return;
        }

        var requests = new List<LinkRequest>();
        foreach (var i in links)
        {
            var item = session.ItemAt(i);
            var target = item.Link == RarLinkKind.LegacyUnixSymbolicLink
                ? legacyTargets.GetValueOrDefault(i)
                : item.LinkTarget;

            if (target is null)
            {
                // RAR4 のシンボリックリンクで、中身 (指す先) を読めなかった
                state.Fail(entries[i].Key!, Strings.LinkTargetNotSupported);
                continue;
            }

            requests.Add(new LinkRequest(entries[i].Key!, item.Link, target, item.LinkToFolder));
        }

        var allNames = entries.Where(static e => e.Key is not null).Select(static e => e.Key!).ToList();
        LinkExtractor.Extract(requests, allNames, state, zone, cancellationToken);
    }

    /// <summary>ソリッドの書庫を先頭から順に読み、欲しいものだけを書き出す。</summary>
    private static void ExtractInOrder(
        RarSession session, HashSet<int> files, HashSet<int> legacyLinks, Dictionary<int, string> legacyTargets,
        ExtractState state, CancellationToken cancellationToken)
    {
        using var reader = session.Archive.ExtractAllEntries();
        var index = -1;

        // 欲しいものを読み終えたら、後ろは読まない (大きさが書かれていない項目を読み飛ばすと失敗するため)
        var last = files.Concat(legacyLinks).DefaultIfEmpty(-1).Max();

        try
        {
            while (reader.MoveToNextEntry())
            {
                index++;
                if (state.Cancelled)
                {
                    return;
                }

                // 順に読む道の項目は、一覧の項目と同じ順に並ぶ。数が合わなければ読み違えている
                if (index >= session.Entries.Count)
                {
                    throw new DamagedDataException(new InvalidDataException("RAR entry order differs."));
                }

                var entry = reader.Entry;
                if (files.Contains(index))
                {
                    state.Write(entry.Key!, KnownSize(session, index), entry.LastModifiedTime, Opener(session, index, reader.OpenEntryStream), cancellationToken);
                }
                else if (legacyLinks.Contains(index))
                {
                    if (ReadLegacyTarget(reader.OpenEntryStream) is { } target)
                    {
                        legacyTargets[index] = target;
                    }
                }

                // リダイレクトの項目 (RAR5 のリンク) は中身を持たない。開くと SharpCompress が例外を投げるので開かない

                if (index >= last)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is SharpCompressException or IOException or InvalidDataException
                                       or System.Security.Cryptography.CryptographicException)
        {
            // 塊の伸長に失敗すると、次の項目へ進む段階で例外になる。パスワード付きの書庫もこれに当たる
            state.Fail(Path.GetFileName(session.Layout.Volumes[0]),
                ex is System.Security.Cryptography.CryptographicException or SharpCompress.Common.CryptographicException
                    ? Strings.PasswordNotSupported
                    : Strings.Reason(new DamagedDataException(ex)));
        }
    }

    /// <summary>
    /// 進み具合に使う展開後の大きさ。書かれていないものは 0 にする。SharpCompress はとても大きな値を返すので、
    /// そのまま足すと桁があふれ、ほかのファイルまで展開できなくなる。
    /// </summary>
    private static long KnownSize(RarSession session, int index)
        => session.ItemAt(index).SizeUnknown ? 0 : Math.Max(0, session.Entries[index].Size);

    /// <summary>
    /// 中身を開くもの。展開後の大きさが書かれていない項目は、読み始める前に断る。SharpCompress は伸長し終えてから失敗するので、
    /// そのまま読むと展開先のドライブに全部書いてから消すことになる。
    /// </summary>
    private static Func<Stream> Opener(RarSession session, int index, Func<Stream> open)
        => session.ItemAt(index).SizeUnknown ? static () => throw new RarSizeUnknownException() : open;

    /// <summary>RAR4 の Unix のシンボリックリンクの指す先 (中身) を読む。長すぎるもの、読めないものは <see langword="null"/>。</summary>
    private static string? ReadLegacyTarget(Func<Stream> open)
    {
        try
        {
            using var stream = open();
            var buffer = new byte[MaxLinkTarget + 1];
            var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            // 残りは読み切る。読み切らないと CRC が確かめられない
            stream.CopyTo(Stream.Null);
            return read is 0 or > MaxLinkTarget ? null : Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (Exception ex) when (ex is SharpCompressException or IOException or InvalidDataException)
        {
            return null;
        }
    }
}
