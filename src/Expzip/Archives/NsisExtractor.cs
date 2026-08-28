using System.Buffers.Binary;
using System.IO;

namespace Expzip.Archives;

/// <summary>NSIS 製インストーラーから中身を取り出す (#68)。</summary>
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

        using var source = ArchiveFile.OpenRead(archivePath);

        if (layout.IsSolid)
        {
            ExtractSolid(layout, source, targets, state, cancellationToken);
        }
        else
        {
            // 塊の大きさが分かっているので、進捗はそれで測れる
            state.TotalBytes = targets.Sum(static f => f.StoredLength);

            foreach (var file in targets)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                state.Write(
                    NsisReader.ToArchivePath(file.Name),
                    file.StoredLength,
                    lastWriteTime: null,
                    () => layout.OpenFile(source, file),
                    cancellationToken);
            }
        }

        return state.ToResult();
    }

    /// <summary>まとめ圧縮の書庫から取り出す。</summary>
    /// <remarks>
    /// 1本の流れを前から順に読む。位置の小さい順に並べ替え、次の目当てまでを
    /// 読み飛ばしながら進む。塊の頭の4バイトが、そのファイルの大きさ。
    /// </remarks>
    private static void ExtractSolid(
        NsisLayout layout,
        Stream source,
        List<NsisFile> targets,
        ExtractState state,
        CancellationToken cancellationToken)
    {
        var ordered = targets.OrderBy(static f => f.DataOffset).ToList();

        using var data = layout.OpenDataArea(source);

        long position = 0;

        // 繰り返しの外に出す。中で確保するとループの回数だけ積み上がる
        Span<byte> lead = stackalloc byte[4];

        foreach (var file in ordered)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            // 前から順にしか読めない。行き過ぎていたら戻れないので飛ばす
            if (file.DataOffset < position)
            {
                continue;
            }

            NsisLayout.Skip(data, file.DataOffset - position);
            position = file.DataOffset;

            data.ReadExactly(lead);
            position += 4;

            var size = BinaryPrimitives.ReadUInt32LittleEndian(lead) & 0x7FFFFFFF;

            state.TotalBytes += size;

            // 読める長さを区切って渡す。包みは閉じるときに読み残しを捨てるので、
            // 書き出しが途中で止まっても次のファイルの頭に位置が合う
            state.Write(
                NsisReader.ToArchivePath(file.Name),
                size,
                lastWriteTime: null,
                () => new TakeStream(data, size),
                cancellationToken);

            position += size;
        }
    }

    /// <summary>
    /// 前から順にしか読めない流れから、決まった長さだけを読ませる包み。
    /// </summary>
    /// <remarks>
    /// 閉じるときに読み残しを読み飛ばす。次のファイルの頭に位置を合わせるため。
    /// </remarks>
    private sealed class TakeStream(Stream inner, long length) : Stream
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
            if (disposing && _read < length)
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
