using System.IO;
using ICSharpCode.SharpZipLib.Zip;
using ICSharpCode.SharpZipLib.Zip.Compression;

namespace Expzip.Archives;

/// <summary>1件をどう詰めるかの判断 (#38)。</summary>
/// <param name="Error">読めない理由。読めるなら <see langword="null"/>。</param>
/// <param name="Method">使う圧縮方式。</param>
internal readonly record struct PackingChoice(string? Error, CompressionMethod Method);

/// <summary>
/// ファイルごとに圧縮するかどうかを決める (#38)。
/// </summary>
/// <remarks>
/// <para>
/// <b>利用者に選ばせない。</b> 書庫全体に一律で掛ける設定にすると、写真とログが
/// 混ざったフォルダでは必ずどちらかが損をする。7-Zip と同じく、
/// ファイルごとに決める。
/// </para>
/// <para>
/// <b>実測</b>(#38)。既に圧縮されているものは deflate をかけても<b>1バイトも縮まず</b>
/// (むしろわずかに増える)、時間だけかかる。
/// </para>
/// <list type="table">
/// <item><description>すでに圧縮ずみ 64MB: 縮み -0.0%、格納 185ms 対 圧縮 1,567ms (8.5倍)</description></item>
/// <item><description>ログ 35.6MB: 縮み 99.7%、格納 97ms 対 圧縮 133ms (1.4倍)</description></item>
/// <item><description>実行ファイル 7.6MB: 縮み 54.3%、格納 39ms 対 圧縮 661ms (17倍)</description></item>
/// </list>
/// <para>
/// 判断は<b>先頭の一部を実際に圧縮してみて</b>決める。拡張子の表で決めないのは、
/// 中身が名前どおりとは限らないため。表は増え続けるうえ、知らない拡張子を外す。
/// </para>
/// </remarks>
internal static class CompressionChoice
{
    /// <summary>試しに圧縮してみる量。これだけ読めば傾向は分かる。</summary>
    private const int SampleSize = 64 * 1024;

    /// <summary>
    /// この割合まで縮まないなら格納する。
    /// </summary>
    /// <remarks>
    /// 3% では時間に見合わない。既に圧縮されているものは 1.00 前後、
    /// ふつうの実行ファイルで 0.46、ログで 0.003 になる。取り違えようがない開きがある。
    /// </remarks>
    private const double KeepIfSmallerThan = 0.97;

    /// <summary>
    /// 読めるかどうかを確かめ、あわせて圧縮方式を決める。
    /// </summary>
    /// <remarks>
    /// 読めるかの確認と合わせて1回で済ませる。追加のたびに2度開くのは、
    /// 何万件もあるフォルダでは無駄になる。
    /// </remarks>
    public static PackingChoice Probe(string path)
    {
        try
        {
            using var source = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            var sample = new byte[SampleSize];
            var read = source.ReadAtLeast(sample, sample.Length, throwOnEndOfStream: false);

            // 空のファイルは圧縮のしようがない
            return new PackingChoice(null, read == 0 || !Shrinks(sample, read)
                ? CompressionMethod.Stored
                : CompressionMethod.Deflated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or PathTooLongException)
        {
            return new PackingChoice(ex.Message, CompressionMethod.Deflated);
        }
    }

    /// <summary>この中身は圧縮する価値があるか。</summary>
    private static bool Shrinks(byte[] sample, int length)
    {
        var deflater = new Deflater(Deflater.DEFAULT_COMPRESSION, noZlibHeaderOrFooter: true);
        deflater.SetInput(sample, 0, length);
        deflater.Finish();

        var output = new byte[length + 64];
        var written = 0;

        while (!deflater.IsFinished && written < output.Length)
        {
            var n = deflater.Deflate(output, written, output.Length - written);
            if (n == 0)
            {
                break;
            }

            written += n;
        }

        // 出しきれなかった = 元より大きい。縮まないということ
        return deflater.IsFinished && written < length * KeepIfSmallerThan;
    }
}
