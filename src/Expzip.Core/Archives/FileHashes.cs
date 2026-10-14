using System.Buffers;
using System.IO;
using System.Security.Cryptography;
using ICSharpCode.SharpZipLib.Checksum;

namespace Expzip.Archives;

/// <summary>ハッシュ値の種類 (#259)。</summary>
internal enum HashKind
{
    Crc32,
    Md5,
    Sha1,
    Sha256,
}

/// <summary>
/// ファイル 1 個のハッシュ値 (#259)。配布元が公開している値と照らし合わせるために出す。
/// </summary>
/// <remarks>
/// <para>
/// 配布元が公開するのは SHA-256 が多いが、古い配布物では MD5 や SHA-1、
/// 書庫の中の値と比べるなら CRC32 になる。1 回読むだけで全部を出せるので、まとめて計算する。
/// </para>
/// <para>
/// CRC32 は書庫の中に持っている形式もあるが、ここでは使わず中身から計算する。
/// 書庫に書かれた値と中身が食い違っていたら、中身のほうの値を出すべきなので。
/// 食い違いは書庫検査 (#53) が見つける。
/// </para>
/// </remarks>
internal sealed class FileHashes
{
    private const int BufferSize = 1024 * 1024;

    private FileHashes(IReadOnlyDictionary<HashKind, string> values) => Values = values;

    /// <summary>種類ごとの値。16 進の文字で、CRC32 は大文字、ほかは小文字。</summary>
    public IReadOnlyDictionary<HashKind, string> Values { get; }

    /// <summary>出す順。</summary>
    public static IReadOnlyList<HashKind> Kinds { get; } =
        [HashKind.Sha256, HashKind.Sha1, HashKind.Md5, HashKind.Crc32];

    /// <summary>流れを最後まで読んで、ハッシュ値を計算する。</summary>
    /// <param name="stream">読む中身。</param>
    /// <param name="onRead">読んだバイト数の通知先。進み具合に使う。</param>
    /// <param name="cancellationToken">中断用。</param>
    public static FileHashes Compute(Stream stream, Action<long>? onRead, CancellationToken cancellationToken)
    {
        var crc = new Crc32();
        using var md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = stream.Read(buffer, 0, BufferSize)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var chunk = buffer.AsSpan(0, read);
                crc.Update(new ArraySegment<byte>(buffer, 0, read));
                md5.AppendData(chunk);
                sha1.AppendData(chunk);
                sha256.AppendData(chunk);

                onRead?.Invoke(read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new FileHashes(new Dictionary<HashKind, string>
        {
            [HashKind.Crc32] = crc.Value.ToString("X8"),
            [HashKind.Md5] = Convert.ToHexStringLower(md5.GetHashAndReset()),
            [HashKind.Sha1] = Convert.ToHexStringLower(sha1.GetHashAndReset()),
            [HashKind.Sha256] = Convert.ToHexStringLower(sha256.GetHashAndReset()),
        });
    }

    /// <summary>
    /// 貼り付けられた値を、照らし合わせられる形に整える。整えられなければ <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 配布元の値は <c>sha256sum</c> の出力 (<c>値 *名前</c>) のまま写されることが多い。
    /// 先頭の 16 進の並びだけを使う。<c>certutil</c> は 2 文字ずつ空白で区切るので、
    /// 空白で区切られた 16 進の並びは繋げる。大文字と小文字は区別しない。
    /// </remarks>
    public static string? Normalize(string text)
    {
        var digits = new System.Text.StringBuilder();

        foreach (var token in text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var part = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;

            if (part.Length == 0 || !part.All(Uri.IsHexDigit))
            {
                // 値の後ろに名前が続く形。値はもう取れている
                if (digits.Length > 0)
                {
                    break;
                }

                continue;
            }

            digits.Append(part);
        }

        return digits.Length == 0 ? null : digits.ToString().ToLowerInvariant();
    }

    /// <summary>整えた値と一致する種類。無ければ <see langword="null"/>。</summary>
    public HashKind? Match(string normalized)
    {
        foreach (var kind in Kinds)
        {
            if (string.Equals(Values[kind], normalized, StringComparison.OrdinalIgnoreCase))
            {
                return kind;
            }
        }

        return null;
    }
}
