using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace Expzip.Archives;

/// <summary>
/// ZIP の書庫全体に付いたコメントを読み書きする (#258)。
/// </summary>
/// <remarks>
/// <para>
/// コメントは書庫の末尾の終端レコードの後ろにある。文字コードの印は無いので、名前と同じ
/// 判定 (#13。UTF-8 として正しければ UTF-8、でなければ CP932) で読む。
/// </para>
/// <para>
/// <b>書くときは終端レコードだけを書き換える。</b>中のファイルには触らないので、
/// パスワード付きの書庫でも作り直しにならず、パスワードも要らない。
/// 書く順番は「コメントの中身 → 長さの欄 → 余りを切る」。どこで止まっても、
/// 終端レコードが指す長さの分だけはファイルに入っているので、書庫として読める
/// (末尾に余りがあっても読める)。
/// </para>
/// <para>
/// ファイルごとのコメントは扱わない。書庫全体のコメントだけ。
/// </para>
/// </remarks>
internal static class ZipComment
{
    private const uint EndSignature = 0x06054b50;
    private const int EndLength = 22;
    private const int SearchLength = EndLength + ushort.MaxValue;

    /// <summary>CP932。登録できない環境では <see langword="null"/>。</summary>
    private static readonly Encoding? LegacyJapanese = TryGetLegacyJapanese();

    /// <summary>コメントを読む。無ければ空の文字。読めなければ <see langword="null"/>。</summary>
    public static string? Read(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);

            if (Locate(stream) is not { } end)
            {
                return null;
            }

            var bytes = new byte[end.CommentLength];
            stream.Position = end.Offset + EndLength;
            stream.ReadExactly(bytes);

            return ZipArchiveReader.EntryNameEncoding.GetString(bytes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or EndOfStreamException)
        {
            return null;
        }
    }

    /// <summary>
    /// 画面に出す形にする。端末向けの色の指定などに使われる制御文字は、
    /// そのまま出さずに見える記号 (␛ など) に置き換える。改行とタブは残す。
    /// </summary>
    public static string ForDisplay(string comment)
    {
        var text = new StringBuilder(comment.Length);

        for (var i = 0; i < comment.Length; i++)
        {
            var c = comment[i];

            if (c == '\r' && i + 1 < comment.Length && comment[i + 1] == '\n')
            {
                text.Append("\r\n");
                i++;
            }
            else if (c is '\r' or '\n')
            {
                // 改行が LF だけ (Linux で作ったもの) でも、画面では改行に見えるようにする
                text.Append("\r\n");
            }
            else if (c == '\t')
            {
                text.Append(c);
            }
            else if (c < 0x20)
            {
                text.Append((char)(0x2400 + c));
            }
            else if (c == 0x7F)
            {
                text.Append('␡');
            }
            else if (c is >= '\u0080' and <= '\u009F')
            {
                text.Append('�');
            }
            else
            {
                text.Append(c);
            }
        }

        return text.ToString();
    }

    /// <summary>書くときのバイト列にする。</summary>
    /// <remarks>
    /// CP932 で表せるなら CP932 で書く。コメントには文字コードの印が無く、日本語のコメントを
    /// UTF-8 で書くと古いツールでは化けるため。表せない文字 (絵文字など) を含むときだけ
    /// UTF-8 にする。どちらでも、読むときの判定で元の文字に戻る。
    /// 制御文字は書かない。改行とタブだけは残す。
    /// </remarks>
    public static byte[] Encode(string text)
    {
        var clean = new string(text.Where(static c => c is '\r' or '\n' or '\t' || !char.IsControl(c)).ToArray());

        if (LegacyJapanese is not null)
        {
            try
            {
                var bytes = LegacyJapanese.GetBytes(clean);

                // CP932 として正しく戻り、かつ UTF-8 と取り違えられないものだけ
                if (LegacyJapanese.GetString(bytes) == clean
                    && ZipArchiveReader.EntryNameEncoding.GetString(bytes) == clean)
                {
                    return bytes;
                }
            }
            catch (EncoderFallbackException)
            {
            }
        }

        return Encoding.UTF8.GetBytes(clean);
    }

    /// <summary>コメントとして書ける長さか。終端レコードの欄は 2 バイト。</summary>
    public static bool Fits(string text) => Encode(text).Length <= ushort.MaxValue;

    /// <summary>
    /// コメントを書き換える。中のファイルには触らない。
    /// </summary>
    /// <exception cref="InvalidDataException">終端レコードが見つからない、または長すぎる場合。</exception>
    /// <exception cref="IOException">書き込めない場合。</exception>
    public static void Write(string path, string text)
    {
        var bytes = Encode(text);
        if (bytes.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("The comment is too long.");
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

        if (Locate(stream) is not { } end)
        {
            throw new InvalidDataException("The end of central directory record was not found.");
        }

        var start = end.Offset + EndLength;

        // 中身を先に書く。長さの欄はまだ古い値なので、ここで止まっても古い長さの分は読める
        stream.Position = start;
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);

        Span<byte> length = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)bytes.Length);
        stream.Position = end.Offset + 20;
        stream.Write(length);
        stream.Flush(flushToDisk: true);

        // 前のコメントのほうが長かったときの余りを切る
        stream.SetLength(start + bytes.Length);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>終端レコードの位置と、コメントの長さ。</summary>
    private readonly record struct End(long Offset, int CommentLength);

    /// <summary>
    /// 終端レコードを後ろから探す。見分け方は自己解凍書庫の判定 (<see cref="ZipPrefix"/>) と同じ。
    /// </summary>
    /// <remarks>
    /// コメントの中に同じ並びが入っていることがあるため、見つけた位置がコメントの長さと
    /// 辻褄が合うところまで確かめる。
    /// </remarks>
    private static End? Locate(Stream stream)
    {
        var length = stream.Length;
        if (length < EndLength)
        {
            return null;
        }

        var window = (int)Math.Min(length, SearchLength);
        var buffer = new byte[window];
        stream.Position = length - window;
        stream.ReadExactly(buffer);

        for (var i = window - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != EndSignature)
            {
                continue;
            }

            var comment = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 20));
            var offset = length - window + i;

            if (offset + EndLength + comment <= length)
            {
                return new End(offset, comment);
            }
        }

        return null;
    }

    private static Encoding? TryGetLegacyJapanese()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(
                932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
