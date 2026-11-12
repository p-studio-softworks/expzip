using System.IO;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Common;
using SharpCompress.Common.Rar;
using SharpCompress.Readers;

namespace Expzip.Archives;

/// <summary>
/// 展開後の大きさが書かれていない RAR の項目 (#336)。SharpCompress 0.50.8 は、圧縮したものでも伸長し終えたところで
/// 大きさが届かないとして失敗するので、読み始める前に断る (展開先のドライブを無駄に使わず、検査で桁外れの大きさを出さない)。
/// </summary>
/// <param name="after">
/// まとめて圧縮した書庫で、大きさが書かれていない項目より後ろにあるか。順に読む道では、その項目を読み飛ばすときに
/// SharpCompress が失敗するので、後ろの項目も読めない。
/// </param>
internal sealed class RarSizeUnknownException(bool after = false)
    : IOException(after ? "This RAR entry follows an entry whose unpacked size is not recorded." : "The unpacked size of this RAR entry is not recorded.")
{
    /// <summary>大きさが書かれていない項目より後ろにあるため読めないのか。</summary>
    public bool After { get; } = after;

    /// <summary>まとめて圧縮した書庫で、最初の大きさが書かれていない項目の位置。無ければ -1。</summary>
    public static int FirstInSolid(RarSession session)
    {
        if (!session.Layout.IsSolid)
        {
            return -1;
        }

        for (var i = 0; i < session.Layout.Items.Count; i++)
        {
            if (session.ItemAt(i).SizeUnknown)
            {
                return i;
            }
        }

        return -1;
    }
}

/// <summary>開いた RAR。SharpCompress の書庫と、見出しを先に読んだ結果と、項目の対応 (#336)。</summary>
internal sealed class RarSession : IDisposable
{
    private readonly List<Stream> _streams;

    public RarSession(RarLayout layout, IArchive archive, List<Stream> streams, List<IArchiveEntry> entries)
    {
        Layout = layout;
        Archive = archive;
        _streams = streams;
        Entries = entries;
    }

    /// <summary>見出しを先に読んだ結果。</summary>
    public RarLayout Layout { get; }

    /// <summary>SharpCompress の書庫。</summary>
    public IArchive Archive { get; }

    /// <summary>SharpCompress の項目。<see cref="RarLayout.Items"/> と同じ順で、同じ数。</summary>
    public IReadOnlyList<IArchiveEntry> Entries { get; }

    /// <summary><paramref name="index"/> 番目の項目の、見出しから読んだもの。</summary>
    public RarItem ItemAt(int index) => Layout.Items[index];

    public void Dispose()
    {
        Archive.Dispose();
        foreach (var stream in _streams)
        {
            stream.Dispose();
        }
    }
}

/// <summary>
/// SharpCompress で RAR を開く入口 (#336)。
/// </summary>
/// <remarks>
/// <para>
/// 開く前に <see cref="RarHeader.Read"/> で見出しを確かめ、Expzip が見つけた分割ファイルだけを、流れの並びで SharpCompress に渡す。
/// SharpCompress に名前から続きを探させないので、確かめたものと読むものが食い違わない。
/// </para>
/// <para>
/// 流れは、読むたびに中止を確かめるストリーム (<see cref="CancellableReadStream"/>) に入れて渡す。SharpCompress の中で読み進めている間も
/// 中止が効く。自己解凍の RAR は、Expzip が見つけた位置から先だけを見せ、SharpCompress にはしるしを探させない。
/// </para>
/// <para>
/// 開いたら、見出しの項目と SharpCompress の項目の数が同じかを確かめる。違えば、どちらかが読み違えているので、壊れているとして止める。
/// RAR5 は名前も突き合わせる。
/// </para>
/// </remarks>
internal static class RarAccess
{
    /// <summary>
    /// 開くときの最初の分割ファイル。2 個目以降の分割ファイルを渡されたら、同じフォルダーの最初のものにする (#336 で決めた)。
    /// </summary>
    /// <exception cref="MissingVolumeException">最初の分割ファイルが無い場合。</exception>
    public static string FirstVolume(string path)
    {
        if (RarVolumes.FirstVolumeOf(path) is not { } first)
        {
            return path;
        }

        return File.Exists(first) ? first : throw new MissingVolumeException(first);
    }

    /// <summary>見出しを確かめてから開く。</summary>
    /// <param name="path">書庫 (分割ファイルのどれでもよい)。</param>
    /// <param name="names">名前の読み方 (#335)。RAR4 の Unicode の印の無い名前だけが使う。</param>
    /// <param name="password">パスワード。対応していないので、今は渡さない (#365)。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <param name="recorder">名前のバイト列を覚えるもの (開くとき、#335)。</param>
    /// <exception cref="DamagedDataException">見出しが壊れている、または項目の数が合わない場合。</exception>
    public static RarSession Open(
        string path, NameReading names, string? password, CancellationToken cancellationToken,
        NameBytesRecorder? recorder = null)
    {
        var layout = RarHeader.Read(FirstVolume(path), cancellationToken);
        var streams = new List<Stream>();
        IArchive? archive = null;

        try
        {
            for (var i = 0; i < layout.Volumes.Count; i++)
            {
                Stream stream = ArchiveFile.OpenRead(layout.Volumes[i]);
                if (i == 0 && layout.Offset > 0)
                {
                    stream = new OffsetStream(stream, layout.Offset);
                }

                streams.Add(new CancellableReadStream(stream, cancellationToken));
            }

            var decoder = (System.Text.Encoding?)recorder ?? names.Encoding;
            var options = new ReaderOptions
            {
                Password = password,
                LookForHeader = false,
                LeaveStreamOpen = true,
                ArchiveEncoding = new ArchiveEncoding
                {
                    CustomDecoder = (bytes, index, count, _) => decoder.GetString(bytes, index, count),
                },
            };

            archive = RarArchive.OpenArchive(streams, options);
            var entries = archive.Entries.ToList();
            Verify(layout, entries);
            return new RarSession(layout, archive, streams, entries);
        }
        catch
        {
            archive?.Dispose();
            foreach (var stream in streams)
            {
                stream.Dispose();
            }

            throw;
        }
    }

    /// <summary>見出しの項目と SharpCompress の項目が同じかを確かめる。</summary>
    private static void Verify(RarLayout layout, List<IArchiveEntry> entries)
    {
        if (entries.Count != layout.Items.Count)
        {
            throw new DamagedDataException(new InvalidDataException("RAR entry count differs."));
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var item = layout.Items[i];
            var entry = entries[i];

            if (item.IsDirectory != entry.IsDirectory)
            {
                throw new DamagedDataException(new InvalidDataException("RAR entry kind differs."));
            }

            if (item.Name is { } name && entry.Key is { } key
                && !string.Equals(ArchivePath.Normalize(name), ArchivePath.Normalize(key), StringComparison.Ordinal))
            {
                throw new DamagedDataException(new InvalidDataException("RAR entry name differs."));
            }

            // リンクかどうかも突き合わせる。食い違えば、リンクとして展開しない側に倒すため、壊れているとして止める
            var isRedirect = entry is RarEntry rar && rar.IsRedir;
            var expectsRedirect = item.Link is not (RarLinkKind.None or RarLinkKind.LegacyUnixSymbolicLink);
            if (isRedirect != expectsRedirect)
            {
                throw new DamagedDataException(new InvalidDataException("RAR link kind differs."));
            }
        }
    }
}
