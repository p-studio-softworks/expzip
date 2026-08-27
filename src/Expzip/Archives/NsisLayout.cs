using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace Expzip.Archives;

/// <summary>NSIS の中に入っている1件 (#68)。</summary>
/// <param name="Name">取り出し先の名前。<c>$INSTDIR\…</c> のように変数で始まる。</param>
/// <param name="DataOffset">中身の塊の位置。中身の領域の先頭からの相対。</param>
/// <param name="StoredLength">塊に入っている大きさ (圧縮後)。</param>
internal readonly record struct NsisFile(string Name, uint DataOffset, long StoredLength);

/// <summary>
/// NSIS 製インストーラーの組み立て (#68)。一覧と取り出しで共通に使う。
/// </summary>
/// <param name="DataStart">
/// 中身の領域が始まる位置。ファイルの先頭からの絶対位置。
/// まとめ圧縮では、展開の流れの先頭 (LZMA の設定の直後) を指す。
/// </param>
/// <param name="Files">取り出されるファイル。中身の位置で畳んである。</param>
/// <param name="Properties">
/// まとめ圧縮の LZMA の設定 (5バイト)。まとめ圧縮でなければ空。
/// </param>
/// <param name="HeaderLength">
/// まとめ圧縮のとき、展開の流れの先頭に入っているヘッダの長さ。
/// 中身の領域はその後ろから始まる。
/// </param>
internal sealed record NsisLayout(
    long DataStart,
    IReadOnlyList<NsisFile> Files,
    byte[] Properties,
    long HeaderLength)
{
    /// <summary>まとめ圧縮 (ソリッド) かどうか。</summary>
    /// <remarks>
    /// まとめ圧縮では中身が1本の流れになっていて、塊ごとに独立して展開できない。
    /// 5番目のファイルを取り出すには前の4つを展開して読み飛ばす必要がある。
    /// 7z (#19) と同じ性質。
    /// </remarks>
    public bool IsSolid => Properties.Length > 0;

    /// <summary>
    /// 中身の領域を頭から読める流れを開く。
    /// </summary>
    /// <remarks>
    /// まとめ圧縮では、展開の流れの先頭に <c>[4バイトの長さ][ヘッダ]</c> が入っている。
    /// その分を読み飛ばしてから返す。
    /// </remarks>
    public Stream OpenDataArea(Stream source)
    {
        source.Position = DataStart;

        if (!IsSolid)
        {
            return source;
        }

        var stream = SharpCompress.Compressors.LZMA.LzmaStream.Create(
            Properties, source, leaveOpen: true);

        Skip(stream, 4 + HeaderLength);
        return stream;
    }

    /// <summary>前から順にしか読めない流れを、指定の長さだけ読み飛ばす。</summary>
    public static void Skip(Stream stream, long count)
    {
        var buffer = new byte[64 * 1024];

        while (count > 0)
        {
            var got = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, count));

            if (got <= 0)
            {
                throw new EndOfStreamException();
            }

            count -= got;
        }
    }

    /// <summary>
    /// 中身の塊を開く。展開後を頭から読める流れを返す。
    /// </summary>
    /// <remarks>
    /// 塊は <c>[4バイトの大きさ][中身]</c> の形。大きさの最上位ビットが立っていれば
    /// 圧縮されている。NSIS の deflate は生 (zlib の包みが無い)。
    /// </remarks>
    public Stream OpenFile(Stream source, NsisFile file)
    {
        source.Position = DataStart + file.DataOffset;

        Span<byte> lead = stackalloc byte[4];
        source.ReadExactly(lead);

        var value = BinaryPrimitives.ReadUInt32LittleEndian(lead);
        var stored = value & 0x7FFFFFFF;
        var slice = new BoundedStream(source, stored);

        return (value & 0x80000000) != 0
            ? new DeflateStream(slice, CompressionMode.Decompress)
            : slice;
    }

    /// <summary>
    /// 決まった長さだけを読ませる包み。
    /// </summary>
    /// <remarks>
    /// 塊の切れ目を越えて読み進めないようにする。無圧縮の塊では、これが無いと
    /// 次のファイルの中身まで続けて読んでしまう。
    /// </remarks>
    private sealed class BoundedStream(Stream inner, long length) : Stream
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
    }
}
