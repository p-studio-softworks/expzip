using System.IO;
using System.Text;

namespace Expzip.Archives;

/// <summary>RAR を読み込み、ZIP と同じ形の内容に組み立てる (#336)。</summary>
internal static class RarReader
{
    /// <summary>RAR を開いて内容を読み取る。</summary>
    /// <param name="path">書庫ファイルのパス。分割した書庫なら、どの分割ファイルでもよい (最初のものを開く)。</param>
    /// <param name="names">名前の読み方 (#335)。RAR4 の Unicode の印の無い名前だけが使う。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <exception cref="DamagedDataException">書庫のデータが壊れている場合。</exception>
    /// <exception cref="EncryptedHeaderException">名前まで暗号化した書庫の場合。</exception>
    /// <exception cref="MissingVolumeException">分割の途中が欠けている場合。</exception>
    /// <exception cref="ArchiveTooLargeException">項目が多すぎる、辞書が大きすぎる場合。</exception>
    public static ArchiveContents Open(
        string path, NameReading names, IProgress<OpenProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            return Read(path, names, progress, cancellationToken);
        }
        catch (Exception ex) when (Translate(ex) is { } translated)
        {
            throw translated;
        }
    }

    /// <summary>
    /// SharpCompress の例外を、Expzip の例外にする。ライブラリの英語の文がそのまま画面に出ないように (#167、#294)。
    /// 置き換えないものは <see langword="null"/>。
    /// </summary>
    internal static Exception? Translate(Exception ex) => ex switch
    {
        DamagedDataException or EncryptedHeaderException or MissingVolumeException or ArchiveTooLargeException
            or UnsupportedRarException or OperationCanceledException => null,

        // 一覧を読むだけで出るのは、名前まで暗号化された書庫
        SharpCompress.Common.CryptographicException => new EncryptedHeaderException(ex),

        EndOfStreamException or SharpCompress.Common.SharpCompressException => new DamagedDataException(ex),

        _ when CancellableCopy.IsLibraryFailure(ex) => new DamagedDataException(ex),

        _ => null,
    };

    private static ArchiveContents Read(
        string path, NameReading names, IProgress<OpenProgress>? progress, CancellationToken cancellationToken)
    {
        var first = RarAccess.FirstVolume(path);

        // 選び直すときの候補のために、名前のバイト列を読むついでに覚えておく (#335)。RAR4 の Unicode の印の無い名前だけが通る
        var recorder = new NameBytesRecorder(names.Encoding);
        using var session = RarAccess.Open(first, names, null, cancellationToken, recorder);

        var builder = new ArchiveTreeBuilder(first);
        var entries = session.Entries;
        var reportStep = Math.Max(1, entries.Count / 100);
        var encrypted = false;
        var linkTable = new Dictionary<string, ListedLink>();

        for (var i = 0; i < entries.Count; i++)
        {
            if ((i + 1) % reportStep == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new OpenProgress(i + 1, entries.Count));
            }

            var entry = entries[i];
            if (entry.Key is not { } key)
            {
                continue;
            }

            var time = entry.LastModifiedTime ?? default;
            if (entry.IsDirectory)
            {
                builder.AddFolder(key, time);
                continue;
            }

            encrypted |= entry.IsEncrypted;

            // リンクも一覧ではファイルとして出す。展開ではリンクとして展開する
            // 展開後の大きさが不明なもの (標準入力から作った書庫など) は、大きさを不明として出す
            var sizeUnknown = session.ItemAt(i).SizeUnknown;

            // 開くときに指す先をたどる (#367)。RAR4 のシンボリックリンクの指す先は中身にあるので、そのときに読む
            var item = session.ItemAt(i);
            if (item.Link == RarLinkKind.None)
            {
                linkTable.Remove(key);
            }
            else
            {
                linkTable[key] = item.Link == RarLinkKind.LegacyUnixSymbolicLink
                    ? new ListedLink(item.Link, null, null)
                    : new ListedLink(item.Link, item.LinkTarget, item.LinkToFolder);
            }

            builder.AddFile(key, sizeUnknown ? 0 : Math.Max(0, entry.Size), Math.Max(0, entry.CompressedSize), compressedLengthKnown: true,
                time, entry.IsEncrypted, lengthKnown: !sizeUnknown);
        }

        progress?.Report(new OpenProgress(entries.Count, entries.Count));

        var layout = session.Layout;
        var contents = builder.Build(
            first, ArchiveFormat.Rar, TotalLength(layout.Volumes), encrypted,
            requiresPassword: encrypted,
            isSelfExtracting: layout.Offset > 0,
            comment: Comment(layout, names));

        contents.Names = names;
        contents.Links = linkTable;
        if (!layout.IsRar5)
        {
            contents.LegacyNameBytes = recorder.Recorded;
        }

        return contents;
    }

    /// <summary>書庫のコメント。RAR5 は UTF-8、RAR4 は名前と同じ読み方で読む。</summary>
    private static string? Comment(RarLayout layout, NameReading names)
    {
        if (layout.Comment is not { Length: > 0 } bytes)
        {
            return null;
        }

        return layout.CommentIsUtf8 ? Encoding.UTF8.GetString(bytes) : names.Encoding.GetString(bytes);
    }

    /// <summary>分割ファイルの大きさの合計。読めないものは数えない。</summary>
    private static long TotalLength(IReadOnlyList<string> volumes)
    {
        long total = 0;
        foreach (var volume in volumes)
        {
            try
            {
                total += new FileInfo(volume).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return total;
    }
}
