using System.IO;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>扱える書庫の形式 (#19)。</summary>
internal enum ArchiveFormat
{
    /// <summary>書庫として認識できなかった。</summary>
    Unknown,

    /// <summary>ZIP。読み書きとも対応する。</summary>
    Zip,

    /// <summary>7z。読み取りのみ。</summary>
    SevenZip,

    /// <summary>tar。圧縮されたもの (tar.gz / tar.bz2 / tar.xz) を含む。読み取りのみ。</summary>
    Tar,
}

/// <summary>書庫の形式を判別する。</summary>
internal static class ArchiveFormats
{
    /// <summary>拡張子から形式を引くための表。</summary>
    private static readonly (string Extension, ArchiveFormat Format)[] ByExtension =
    [
        (".zip", ArchiveFormat.Zip),
        (".7z", ArchiveFormat.SevenZip),
        (".tar", ArchiveFormat.Tar),
        (".tar.gz", ArchiveFormat.Tar),
        (".tgz", ArchiveFormat.Tar),
        (".tar.bz2", ArchiveFormat.Tar),
        (".tbz", ArchiveFormat.Tar),
        (".tbz2", ArchiveFormat.Tar),
        (".tar.xz", ArchiveFormat.Tar),
        (".txz", ArchiveFormat.Tar),
    ];

    /// <summary>「開く」ダイアログで使う絞り込み。</summary>
    public static string OpenFilter => Strings.OpenFilter;

    /// <summary>
    /// パスの拡張子から形式を判別する。
    /// </summary>
    /// <remarks>
    /// 中身を見ないのは、まだ存在しないファイル (新規作成やドラッグ中の判定) にも
    /// 使うため。実際に開けるかどうかは開いた時点で分かる。
    /// </remarks>
    public static ArchiveFormat FromPath(string path)
    {
        var name = Path.GetFileName(path);

        foreach (var (extension, format) in ByExtension)
        {
            if (name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        return ArchiveFormat.Unknown;
    }

    /// <summary>書庫として開ける拡張子か。</summary>
    public static bool IsArchive(string path) => FromPath(path) != ArchiveFormat.Unknown;

    /// <summary>
    /// 中身を書き換えられる形式か。
    /// 追加・削除・名前の変更・移動・フォルダの作成が使えるかの判断に使う。
    /// </summary>
    /// <remarks>
    /// 7z と tar は読み取りのみ (#19)。SharpCompress は 7z と tar の書き込みにも
    /// 対応しているが、ZIP で行っているような「一部だけ差し替える」書き換えは
    /// できず、書庫全体を作り直すことになる。まず読み取りを確実にする。
    /// </remarks>
    public static bool IsEditable(ArchiveFormat format) => format == ArchiveFormat.Zip;

    /// <summary>画面に出す形式の名前。</summary>
    public static string DisplayName(ArchiveFormat format) => Strings.FormatName(format);
}
