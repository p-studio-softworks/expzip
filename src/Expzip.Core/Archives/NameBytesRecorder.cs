using System.Text;

namespace Expzip.Archives;

/// <summary>
/// 名前を読むついでに、渡されたバイト列を決まった量まで覚える (#335)。読み方はそのまま <c>inner</c> に任せる。
/// </summary>
/// <remarks>
/// 名前の読み方を選び直すときの候補を作るのに使う。書庫を読み直さずに済むので、
/// 項目の数の上限などの守り (#294) の内側で集められる。覚えるのは先頭からの量だけで、大きな書庫でも増えない。
/// 同じ並列の展開などから呼ばれることはない (開くときの 1 本の流れだけ)。
/// </remarks>
internal sealed class NameBytesRecorder(Encoding inner) : Encoding
{
    /// <summary>覚えるバイト数の上限。候補を試す量もこれで決まる。</summary>
    public const int Limit = 64 * 1024;

    private readonly List<byte[]> _recorded = [];
    private int _total;

    /// <summary>覚えたバイト列。読み方で読みが変わらない ASCII だけの名前は入れない。</summary>
    public IReadOnlyList<byte[]> Recorded => _recorded;

    private void Record(byte[] bytes, int index, int count)
    {
        if (_total + count > Limit || !HasNonAscii(bytes.AsSpan(index, count)))
        {
            return;
        }

        _recorded.Add(bytes.AsSpan(index, count).ToArray());
        _total += count;
    }

    private static bool HasNonAscii(ReadOnlySpan<byte> bytes)
    {
        foreach (var b in bytes)
        {
            if (b >= 0x80)
            {
                return true;
            }
        }

        return false;
    }

    public override string GetString(byte[] bytes, int index, int count)
    {
        Record(bytes, index, count);
        return inner.GetString(bytes, index, count);
    }

    public override int GetCharCount(byte[] bytes, int index, int count) => inner.GetCharCount(bytes, index, count);

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        Record(bytes, byteIndex, byteCount);
        return inner.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
    }

    public override int GetMaxCharCount(int byteCount) => inner.GetMaxCharCount(byteCount);

    public override int GetByteCount(char[] chars, int index, int count) => inner.GetByteCount(chars, index, count);

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        => inner.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

    public override int GetMaxByteCount(int charCount) => inner.GetMaxByteCount(charCount);
}
