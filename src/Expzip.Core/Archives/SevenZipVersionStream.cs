using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 見出しのバージョンが 0.2 までの 7z を、0.3 として読ませる流れ (#294)。
/// </summary>
/// <remarks>
/// <para>
/// SharpCompress は、0.2 までの見出しではファイルの属性の長さを確かめない。そのままだと
/// <see cref="SevenZipHeader"/> と属性の読み方がずれて、確かめたものと違うものを読まれる。
/// 0.3 と同じ扱いにすれば読み方がそろい、古い書庫のための別の道を作らずに済む。
/// </para>
/// <para>
/// SharpCompress がこの値を見るのは、属性の長さを確かめるかどうかの判断だけ
/// (<c>ArchiveReader.ReadHeader</c>。<c>ReadDatabase</c> は大きい方の番号が 0 かを見るだけ)。
/// 先頭の CRC が守るのは 12 バイト目以降なので、値を変えても壊れない。
/// </para>
/// <para>
/// 属性の長さが中身と合わない古い書庫は、壊れているものとして断られる。
/// </para>
/// </remarks>
internal sealed class SevenZipVersionStream : Stream
{
    private const int MinorVersionOffset = 7;
    private const byte OldestCheckedMinorVersion = 3;

    private readonly Stream _inner;
    private readonly bool _old;

    public SevenZipVersionStream(Stream inner)
    {
        _inner = inner;

        Span<byte> head = stackalloc byte[MinorVersionOffset + 1];
        var start = inner.Position;
        inner.Position = 0;
        var read = inner.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        inner.Position = start;

        _old = read == head.Length
               && head[..6].SequenceEqual<byte>([0x37, 0x7A, 0xBC, 0xAF, 0x27, 0x1C])
               && head[6] == 0
               && head[MinorVersionOffset] < OldestCheckedMinorVersion;
    }

    public override bool CanRead => _inner.CanRead;

    public override bool CanSeek => _inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => _inner.Length;

    public override long Position
    {
        get => _inner.Position;
        set => _inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    /// <summary>読み方はどれもここを通る。1 つでも元の値を返すと、確かめた読み方とずれる。</summary>
    public override int Read(Span<byte> buffer)
    {
        var start = _inner.Position;
        var read = _inner.Read(buffer);

        if (_old && start <= MinorVersionOffset && start + read > MinorVersionOffset)
        {
            buffer[(int)(MinorVersionOffset - start)] = OldestCheckedMinorVersion;
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

    public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }
}
