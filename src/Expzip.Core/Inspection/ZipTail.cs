using System.Buffers.Binary;
using System.IO;
using Expzip.Archives;

namespace Expzip.Inspection;

/// <summary>
/// ZIP の末尾にある終端レコード (End of Central Directory) を直に読む (#54)。
/// </summary>
/// <remarks>
/// 切り詰められた書庫と、末尾に余分なデータが続く書庫を見つけるために使う。
/// どちらもライブラリは黙って読み進めてしまい、検査に引っかからない。
/// </remarks>
internal static class ZipTail
{
    /// <summary>終端レコードの署名。</summary>
    private const uint Signature = 0x06054b50;

    /// <summary>終端レコードの固定部の長さ。</summary>
    private const int RecordLength = 22;

    /// <summary>書庫コメントの上限 (65,535) と固定部を足した探索範囲。</summary>
    private const int SearchLength = RecordLength + 0xFFFF;

    /// <summary>読み取った終端レコードの中身。</summary>
    /// <param name="Found">終端レコードが見つかったか。</param>
    /// <param name="TrailingBytes">終端レコードの後ろに続く、書庫ではないデータの長さ。</param>
    /// <param name="CentralDirectoryTruncated">中央ディレクトリがファイルの外まで伸びているか。</param>
    public readonly record struct Info(
        bool Found, long TrailingBytes, bool CentralDirectoryTruncated);

    /// <summary>書庫の末尾を調べる。読めない場合は「見つからなかった」を返す。</summary>
    public static Info Read(string path)
    {
        try
        {
            using var stream = ArchiveFile.OpenRead(path);

            var length = stream.Length;
            if (length < RecordLength)
            {
                return new Info(false, 0, false);
            }

            var window = (int)Math.Min(length, SearchLength);
            var buffer = new byte[window];
            stream.Position = length - window;
            stream.ReadExactly(buffer);

            // 後ろから署名を探す。コメントの中に同じ並びが入っていることがあるため、
            // 見つけた位置がコメントの長さと辻褄が合うところまで確かめる
            for (var i = window - RecordLength; i >= 0; i--)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i)) != Signature)
                {
                    continue;
                }

                var comment = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(i + 20));
                var start = length - window + i;
                var end = start + RecordLength + comment;

                if (end > length)
                {
                    continue;
                }

                var directorySize = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 12));
                var directoryOffset = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(i + 16));

                // ZIP64 の書庫はここに 0xFFFFFFFF が入り、本当の値は別のレコードにある。
                // その場合は位置の照合を見送る
                var truncated = directoryOffset != uint.MaxValue
                                && directorySize != uint.MaxValue
                                && (long)directoryOffset + directorySize > length;

                return new Info(true, length - end, truncated);
            }

            return new Info(false, 0, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or EndOfStreamException)
        {
            return new Info(false, 0, false);
        }
    }
}
