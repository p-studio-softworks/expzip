using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 終わりを超えて読み続けたら例外にする流れ (#294)。
/// </summary>
/// <remarks>
/// SharpCompress の LZMA2 は入力の終わりを確かめないので、途中で切れたデータを渡すと、読めないまま
/// 同じ処理を繰り返す。読み手がライブラリの中にいるので、中断も閉じる操作も効かない。正しい読み手は
/// 終わりに何度も読みに来ないので、回数で見分ける。
/// </remarks>
/// <param name="inner">元の流れ。</param>
/// <param name="allowedEndReads">終わりで何も読めなかったことを許す回数。0 なら最初の 1 回で例外にする。</param>
internal sealed class EndGuardStream(Stream inner, int allowedEndReads = EndGuardStream.DefaultAllowedEndReads) : Stream
{
    /// <summary>形式の判定などは、終わりに達したあとも数回は読みに来る。</summary>
    public const int DefaultAllowedEndReads = 64;

    private int _endReads;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set
        {
            inner.Position = value;
            _endReads = 0;
        }
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = inner.Read(buffer);

        if (read > 0)
        {
            _endReads = 0;
        }
        else if (buffer.Length > 0 && ++_endReads > allowedEndReads)
        {
            throw new EndOfStreamException();
        }

        return read;
    }

    public override int ReadByte()
    {
        Span<byte> one = stackalloc byte[1];
        return Read(one) == 0 ? -1 : one[0];
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _endReads = 0;
        return inner.Seek(offset, origin);
    }

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
