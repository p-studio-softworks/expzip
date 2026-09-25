using System.Buffers;
using System.IO;

namespace Expzip.Archives;

/// <summary>中断要求を見ながらストリームをコピーする。</summary>
internal static class CancellableCopy
{
    /// <summary><see cref="Stream.CopyTo(Stream)"/> のデフォルトと同じ大きさ。</summary>
    private const int BufferSize = 81920;

    /// <summary>
    /// 中断要求を確認しながらコピーする。
    /// </summary>
    /// <remarks>
    /// <see cref="Stream.CopyTo(Stream)"/> は途中で止められないため、大きな
    /// ファイルを1つ処理している間ずっと中断できなくなってしまう (#37)。
    /// </remarks>
    /// <exception cref="OperationCanceledException">中断が要求された場合。</exception>
    public static void Copy(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// 書庫の中身を書き出す。中身を読んでいる途中の失敗は、データが壊れているものとして投げ直す (#167)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 開けた後で読めなくなったなら、圧縮方式には対応していて、データのほうがおかしい。
    /// 知らない方式は開く時点で断られる。
    /// </para>
    /// <para>
    /// 壊れたデータに当たったときの例外は、ライブラリと方式ごとにばらばら
    /// (標準の Deflate は「unsupported compression method」の
    /// <see cref="InvalidDataException"/>、SharpZipLib は <c>StreamDecodingException</c>、
    /// SharpCompress は <c>ZlibException</c> や <c>DataErrorException</c>)。型で追いかけると
    /// 拾い漏れて画面ごと止まり、文で見分けると「対応していない圧縮方式」と取り違える。
    /// </para>
    /// </remarks>
    /// <exception cref="DamagedDataException">中身を読めなかった場合。</exception>
    /// <exception cref="OperationCanceledException">中断が要求された場合。</exception>
    public static void CopyContent(Stream source, Stream destination, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            int read;
            while ((read = ReadContent(source, buffer)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                destination.Write(buffer, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int ReadContent(Stream source, byte[] buffer)
    {
        try
        {
            return source.Read(buffer, 0, buffer.Length);
        }
        catch (Exception ex) when (IsDamage(ex))
        {
            throw new DamagedDataException(ex);
        }
    }

    /// <summary>読み取りの失敗のうち、データが壊れていると見てよいもの。</summary>
    /// <remarks>
    /// 書庫のファイルそのものを読めない (ほかのプログラムが使用中など) ときは、
    /// そちらの理由のほうが役に立つので残す。途中で尽きた場合 (<see cref="EndOfStreamException"/>) は
    /// 書庫が途中で切れているので、壊れている側に入れる。
    /// パスワードの誤りは別の知らせ方があるので触らない (#20)。
    /// </remarks>
    private static bool IsDamage(Exception ex)
        => ex is EndOfStreamException
           || ex is not (OperationCanceledException or OutOfMemoryException
               or IOException or UnauthorizedAccessException or NotSupportedException
               or System.Security.Cryptography.CryptographicException
               or SharpCompress.Common.CryptographicException);
}

/// <summary>書庫の中身を読んでいる途中で、データが壊れていると分かった (#167)。</summary>
/// <remarks>
/// <see cref="IOException"/> から派生させ、今ある受け止め方にそのまま乗せる
/// (<see cref="InvalidDataException"/> は派生できない)。展開の失敗を拾う所は、どこも
/// <see cref="IOException"/> を拾っている。
/// 文は固定にする。元の文をそのまま持つと、「compress」を含むかで方式の違いを
/// 見分けている所 (#66) に取り違えられる。
/// </remarks>
internal sealed class DamagedDataException(Exception inner)
    : IOException("The archive data is damaged.", inner);
