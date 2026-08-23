using System.IO;

namespace Expzip.Archives;

/// <summary>形式を問わず書庫を読み込む入口 (#19)。</summary>
internal static class ArchiveReader
{
    /// <summary>書庫を開いて内容を読み取る。形式は拡張子から判断する。</summary>
    /// <param name="path">書庫ファイルのパス。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <exception cref="InvalidDataException">書庫として解釈できない場合。</exception>
    /// <exception cref="IOException">ファイルを読めない場合。</exception>
    /// <exception cref="OperationCanceledException">中断された場合。</exception>
    public static ArchiveContents Open(
        string path,
        IProgress<OpenProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var format = ArchiveFormats.FromPath(path);

        return format switch
        {
            ArchiveFormat.SevenZip or ArchiveFormat.Tar
                => SharpArchiveReader.Open(path, format, progress, cancellationToken),

            // 拡張子で判別できなかったものは ZIP として試す。
            // 拡張子を変えただけの ZIP は珍しくなく、開けるなら開く
            _ => ZipArchiveReader.Open(path, progress, cancellationToken),
        };
    }
}
