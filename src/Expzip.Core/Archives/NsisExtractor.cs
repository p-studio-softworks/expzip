using System.Buffers.Binary;
using System.IO;

namespace Expzip.Archives;

/// <summary>NSIS インストーラーから中身を取り出す (#68)。</summary>
/// <remarks>
/// <para>
/// 中身は 4バイトの大きさに続いて並んでいて、どの塊がどのファイルかは命令の並びから
/// 分かる (<see cref="NsisReader.ReadLayout"/>)。
/// </para>
/// <para>
/// 取り出し方が2通りある。<b>塊ごとの圧縮</b>なら塊は独立していて、選んだものへ
/// 直に跳べる。<b>まとめ圧縮</b>では中身が1本の流れになっていて、5番目のファイルへ
/// 届くには前の4つを読み飛ばすしかない。7z (#19) と同じ性質。
/// </para>
/// <para>
/// 判定や後始末は他の形式と揃えてある (<see cref="ExtractState"/>)。書庫の外を指す
/// パスは弾き、出所の印を引き継ぎ、中断したら書きかけのファイルを消す。
/// </para>
/// </remarks>
internal static class NsisExtractor
{
    /// <summary>指定したエントリを展開する。引数の意味は <see cref="ArchiveExtractor.Extract"/> と同じ。</summary>
    public static ExtractResult Extract(
        string archivePath,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        bool overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null)
    {
        var state = new ExtractState(
            Path.GetFullPath(destinationDirectory), overwrite, zoneIdentifier, basePath, progress);

        var layout = NsisReader.ReadLayout(archivePath, cancellationToken);

        var targets = layout.Files
            .Where(f => sourceNames is null || sourceNames.Contains(NsisReader.ToArchivePath(f.Name)))
            .ToList();

        // 塊ごとの圧縮なら塊の大きさが分かっているので、進捗はそれで測れる。
        // まとめ圧縮では、読み進めながら足していく
        if (!layout.IsSolid)
        {
            state.TotalBytes = targets.Sum(static f => f.StoredLength);
        }

        Visit(archivePath, layout, targets, (file, size, open) =>
        {
            if (layout.IsSolid)
            {
                state.TotalBytes += size;
            }

            state.Write(
                NsisReader.ToArchivePath(file.Name),
                size >= 0 ? size : file.StoredLength,
                lastWriteTime: null,
                open,
                cancellationToken);
        }, cancellationToken);

        return state.ToResult();
    }

    /// <summary>
    /// 中身を 1 件ずつ、読める順に渡す。取り出しと検査で共通に使う。
    /// </summary>
    /// <param name="visit">
    /// 1 件ごとに呼ぶ。2 番目は展開後の大きさで、分からなければ -1
    /// (塊ごとの圧縮では、展開してみるまで分からない)。3 番目は中身を読む流れを開く。
    /// 流れは呼び出しの中で閉じる。次の件へ進むと読めなくなる。
    /// </param>
    public static void Visit(
        string archivePath,
        NsisLayout layout,
        IReadOnlyList<NsisFile> targets,
        Action<NsisFile, long, Func<Stream>> visit,
        CancellationToken cancellationToken)
    {
        using var source = ArchiveFile.OpenRead(archivePath);

        if (layout.IsSolid)
        {
            VisitSolid(archivePath, layout, source, targets, visit, cancellationToken);
            return;
        }

        foreach (var file in targets)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            visit(file, -1, () => layout.OpenFile(source, file));
        }
    }

    /// <summary>まとめ圧縮の書庫から、1 件ずつ渡す。</summary>
    /// <remarks>
    /// 1本の流れを前から順に読む。位置の小さい順に並べ替え、次の目当てまでを
    /// 読み飛ばしながら進む。塊の頭の4バイトが、そのファイルの大きさ。
    /// </remarks>
    /// <param name="archivePath">
    /// 同じ中身を別の名前で置く指示で、中身が大きく覚えておけないときに読み直すのに使う。
    /// </param>
    private static void VisitSolid(
        string archivePath,
        NsisLayout layout,
        Stream source,
        IReadOnlyList<NsisFile> targets,
        Action<NsisFile, long, Func<Stream>> visit,
        CancellationToken cancellationToken)
    {
        // 同じ中身を別の名前で置く指示がある (実物で確認: 1 つの塊を 2 つの名前で置いていた)。
        // 位置でまとめて、1 度読んだ中身をそれぞれに渡す。
        // 以前は 2 つ目を「行き過ぎた」として黙って飛ばしていた
        var groups = targets
            .GroupBy(static f => f.DataOffset)
            .OrderBy(static g => g.Key)
            .ToList();

        using var data = layout.OpenDataArea(source);

        long position = 0;

        // 繰り返しの外に出す。中で確保するとループの回数だけ積み上がる
        Span<byte> lead = stackalloc byte[4];

        foreach (var group in groups)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // 前の中身の途中を指している。壊れた並びで、前から順にしか読めないので戻れない
            if (group.Key < position)
            {
                continue;
            }

            NsisLayout.Skip(data, group.Key - position);
            position = group.Key;

            data.ReadExactly(lead);
            position += 4;

            var size = BinaryPrimitives.ReadUInt32LittleEndian(lead) & 0x7FFFFFFF;
            var files = group.ToList();

            if (files.Count > 1 && size <= SharedLimit)
            {
                var content = new byte[size];
                data.ReadExactly(content);

                foreach (var file in files)
                {
                    visit(file, size, () => new MemoryStream(content, writable: false));
                }
            }
            else
            {
                // 読める長さを区切って渡す。包みは閉じるときに読み残しを捨てるので、
                // 書き出しが途中で止まっても次のファイルの頭に位置が合う
                TakeStream? taken = null;
                visit(files[0], size, () => taken = new TakeStream(data, size));

                // 開かれずに飛ばされた件 (上書きしないで残す場合など) は、ここで読み飛ばす。
                // 飛ばさないと、次のファイルの頭に位置が合わない
                if (taken is null)
                {
                    NsisLayout.Skip(data, size);
                }

                // 覚えておくには大きすぎる中身を別の名前でも置くときは、頭から読み直す
                var offset = group.Key;
                foreach (var file in files.Skip(1))
                {
                    visit(file, size, () => ReadAgain(archivePath, layout, offset, size));
                }
            }

            position += size;
        }
    }

    /// <summary>同じ中身を別の名前で置くとき、覚えておく大きさの上限。</summary>
    private const long SharedLimit = 256L * 1024 * 1024;

    /// <summary>まとめ圧縮の流れを別に開き直して、指定の塊を読む。</summary>
    private static Stream ReadAgain(string archivePath, NsisLayout layout, long offset, long size)
    {
        var file = ArchiveFile.OpenRead(archivePath);

        try
        {
            var data = layout.OpenDataArea(file);
            NsisLayout.Skip(data, offset + 4);

            return new TakeStream(data, size, onClose: () =>
            {
                data.Dispose();
                file.Dispose();
            });
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 前から順にしか読めない流れから、決まった長さだけを読ませる包み。
    /// </summary>
    /// <remarks>
    /// 閉じるときに読み残しを読み飛ばす。次のファイルの頭に位置を合わせるため。
    /// <paramref name="onClose"/> を渡した場合は、読み直し用に開いた流れなので、
    /// 読み飛ばさずにそれを呼んで閉じる。
    /// </remarks>
    private sealed class TakeStream(Stream inner, long length, Action? onClose = null) : Stream
    {
        private long _read;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
            => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var left = length - _read;

            if (left <= 0)
            {
                return 0;
            }

            var got = inner.Read(buffer[..(int)Math.Min(buffer.Length, left)]);
            _read += got;
            return got;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin)
            => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
            => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && onClose is not null)
            {
                onClose();
            }
            else if (disposing && _read < length)
            {
                // 読み残しを捨てる。次のファイルの頭に合わせるため
                try
                {
                    NsisLayout.Skip(inner, length - _read);
                    _read = length;
                }
                catch (Exception ex) when (ex is IOException or EndOfStreamException
                                           or InvalidDataException)
                {
                }
            }

            base.Dispose(disposing);
        }
    }
}
