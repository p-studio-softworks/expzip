using System.Text;

namespace Expzip.Archives;

/// <summary>
/// 書庫のコメントを、元の書庫のバイト列のまま書き戻すための <see cref="Encoding"/> (#197)。
/// </summary>
/// <remarks>
/// <para>
/// コメントには文字コードの印が無く、読むときに UTF-8 か従来の日本語の文字コードかを
/// 推し量っている (#13)。書くときに同じ推し量りはできない。読んだときのバイト列を覚えておき、
/// 同じ文字列を書くときにそれを返す。覚えていない文字列は UTF-8 で書く。
/// </para>
/// <para>
/// SharpZipLib は、書き換えのたびにコメントを文字列から書き直す。これを渡さないと、
/// 日本語のコメントが UTF-8 で書き直されて、ほかのツールでは化けていた。
/// 1 つの書庫を書き換える間だけ使う。書庫ごとに新しく作る。
/// </para>
/// </remarks>
internal sealed class VerbatimText : Encoding
{
    private readonly Encoding _reader = ZipArchiveReader.EntryNameEncoding;
    private readonly Dictionary<string, byte[]> _known = new(StringComparer.Ordinal);

    public override string GetString(byte[] bytes, int index, int count)
    {
        var text = _reader.GetString(bytes, index, count);
        _known.TryAdd(text, bytes.AsSpan(index, count).ToArray());
        return text;
    }

    public override int GetCharCount(byte[] bytes, int index, int count)
        => _reader.GetCharCount(bytes, index, count);

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
    {
        var written = _reader.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        _known.TryAdd(new string(chars, charIndex, written), bytes.AsSpan(byteIndex, byteCount).ToArray());
        return written;
    }

    public override int GetByteCount(char[] chars, int index, int count)
        => _known.TryGetValue(new string(chars, index, count), out var bytes)
            ? bytes.Length
            : UTF8.GetByteCount(chars, index, count);

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
    {
        if (!_known.TryGetValue(new string(chars, charIndex, charCount), out var known))
        {
            return UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        }

        known.CopyTo(bytes, byteIndex);
        return known.Length;
    }

    public override int GetMaxByteCount(int charCount) => UTF8.GetMaxByteCount(charCount);

    public override int GetMaxCharCount(int byteCount) => _reader.GetMaxCharCount(byteCount);
}
