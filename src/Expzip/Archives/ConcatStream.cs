using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 複数のファイルを順に結合して、1本の読み取り用の流れに見せる (#61)。
/// </summary>
/// <remarks>
/// <para>
/// 分割された書庫の断片 (<c>.001</c> <c>.002</c> …) を結合するために使う。断片は
/// ただ切っただけなので、順に結合すれば元のファイルそのものになる。
/// </para>
/// <para>
/// 開いたままにする実体は常に1つだけにしている。断片の数は書庫の大きさと
/// 分割の大きさ次第でいくらでも増えるため、全部を開いたままにすると
/// 4GB を 100KB で切った場合に4万個の実体を抱えることになる。
/// </para>
/// </remarks>
internal sealed class ConcatStream : Stream
{
    private readonly string[] _paths;

    /// <summary>各断片が全体の中で始まる位置。末尾に全体の長さが入る。</summary>
    private readonly long[] _starts;

    private FileStream? _current;
    private int _index = -1;
    private long _position;
    private bool _closed;

    /// <param name="paths">結合する順に並んだ断片のパス。</param>
    /// <exception cref="IOException">断片を読めない場合。</exception>
    public ConcatStream(IReadOnlyList<string> paths)
    {
        ArgumentOutOfRangeException.ThrowIfZero(paths.Count);

        _paths = [.. paths];
        _starts = new long[paths.Count + 1];

        // 長さは先に全部数えておく。位置を求めるたびに実体を開き直さずに済む
        for (var i = 0; i < paths.Count; i++)
        {
            _starts[i + 1] = _starts[i] + new FileInfo(paths[i]).Length;
        }
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => _starts[^1];

    public override long Position
    {
        get => _position;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _position = value;
        }
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_closed, this);

        var done = 0;

        // 断片の境目をまたぐ読み出しは、断片ごとに分けて繰り返す
        while (done < buffer.Length && _position < Length)
        {
            var index = IndexOf(_position);
            var stream = Use(index);
            stream.Position = _position - _starts[index];

            // この断片から取れる分だけに切る
            var room = (int)Math.Min(
                buffer.Length - done, _starts[index + 1] - _position);
            var read = stream.Read(buffer.Slice(done, room));

            if (read <= 0)
            {
                // 断片が数えたときより短い。外で削られたか差し替えられた
                break;
            }

            done += read;
            _position += read;
        }

        return done;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        _position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => throw new ArgumentOutOfRangeException(nameof(origin)),
        };

        ArgumentOutOfRangeException.ThrowIfNegative(_position);
        return _position;
    }

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
        => throw new NotSupportedException();

    /// <summary>その位置を含む断片の番号。</summary>
    private int IndexOf(long position)
    {
        // 断片は数十個までが現実的な範囲。二分探索にする意味がない
        for (var i = 0; i < _paths.Length; i++)
        {
            if (position < _starts[i + 1])
            {
                return i;
            }
        }

        return _paths.Length - 1;
    }

    /// <summary>その断片を開く。既に開いていればそのまま使う。</summary>
    private FileStream Use(int index)
    {
        if (_index == index && _current is not null)
        {
            return _current;
        }

        _current?.Dispose();
        _current = File.OpenRead(_paths[index]);
        _index = index;
        return _current;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed = true;
            _current?.Dispose();
            _current = null;
            _index = -1;
        }

        base.Dispose(disposing);
    }
}
