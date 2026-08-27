using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 頭の一定量を隠して、その先だけを見せる読み取り専用の流れ (#32)。
/// </summary>
/// <remarks>
/// 自己解凍書庫は「取り出すプログラム + 書庫」を繋いだもの。前の塊を隠した流れを
/// 渡せば、読む側は先頭に何が付いていたかを気にしなくてよい。位置を数え直すだけの
/// 薄い包みなので、ZIP でも 7z でも同じものが使える。
/// </remarks>
internal sealed class OffsetStream : Stream
{
    private readonly Stream _inner;
    private readonly long _origin;

    /// <param name="inner">元の流れ。閉じるときに一緒に閉じる。</param>
    /// <param name="origin">隠す長さ。</param>
    /// <remarks>
    /// 渡された流れを隠す長さのところまで進めておく。読む側は開いた直後に
    /// 先頭から読み始めることがあり、そのままだと位置が負になる。
    /// </remarks>
    public OffsetStream(Stream inner, long origin)
    {
        _inner = inner;
        _origin = origin;
        _inner.Position = origin;
    }

    public override bool CanRead => true;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length - _origin;

    public override long Position
    {
        get => _inner.Position - _origin;
        set => _inner.Position = _origin + value;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override long Seek(long offset, SeekOrigin from) => from switch
    {
        SeekOrigin.Begin => _inner.Seek(_origin + offset, SeekOrigin.Begin) - _origin,
        SeekOrigin.Current => _inner.Seek(offset, SeekOrigin.Current) - _origin,
        _ => _inner.Seek(offset, SeekOrigin.End) - _origin,
    };

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
