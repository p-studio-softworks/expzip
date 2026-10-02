using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 決まった長さだけを読ませる包み。
/// </summary>
/// <remarks>
/// 塊の切れ目を越えて読み進めないようにする。無圧縮の塊では、これが無いと
/// 次のファイルの中身まで続けて読んでしまう。NSIS (#68) と exe / dll (#183) で使う。
/// </remarks>
internal sealed class BoundedStream(Stream inner, long length) : Stream
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
