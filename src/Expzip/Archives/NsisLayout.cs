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
/// <param name="DataStart">中身の領域が始まる位置。ファイルの先頭からの絶対位置。</param>
/// <param name="Files">取り出されるファイル。中身の位置で畳んである。</param>
internal sealed record NsisLayout(long DataStart, IReadOnlyList<NsisFile> Files)
{
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
