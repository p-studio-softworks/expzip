using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 読むたびに中断を確かめる包み (#294)。
/// </summary>
/// <remarks>
/// ライブラリの中で読み進めている間は、呼ぶ側から割り込めない。圧縮された tar の大きな中身を
/// 読み飛ばす間や、7z の見出しを伸長する間も、ここを通るので止められる。
/// </remarks>
internal sealed class CancellableReadStream(Stream inner, CancellationToken cancellationToken) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.Read(buffer, offset, count);
    }

    public override int Read(Span<byte> buffer)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.Read(buffer);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
