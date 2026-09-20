using System.Buffers;
using System.IO;

namespace Expzip.Archives;

/// <summary>中断要求を見ながらストリームをコピーする。</summary>
internal static class CancellableCopy
{
    /// <summary><see cref="Stream.CopyTo(Stream)"/> の既定と同じ大きさ。</summary>
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
}
