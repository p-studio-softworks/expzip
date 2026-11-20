using System.IO;

namespace Expzip.Archives;

/// <summary>ZIP を読む部品 (.NET、SharpZipLib、SharpCompress) が、そろって同じ目録 (中央ディレクトリ) を読むか (#396)。</summary>
internal enum ZipDirectories
{
    /// <summary>どの部品も同じ目録を読む。</summary>
    Agree,

    /// <summary>部品によって読む目録が違う。一覧と、検査やハッシュ値で別の中身が出る。</summary>
    Split,

    /// <summary>確かめられない。終わりの記録が見つからないなど、どの部品もふつうには開けないもの。</summary>
    Unknown,
}

/// <summary>
/// 部品によって読む目録が違う ZIP の中身を読もうとした (#396)。一覧を見ることだけができる。
/// </summary>
/// <remarks>
/// 読めない書庫として扱われるように、ほかの読めない理由と同じく <see cref="IOException"/> から派生させる。画面には
/// <see cref="Localization.Strings.Reason"/> が日本語の理由を出す。
/// </remarks>
internal sealed class SplitDirectoryException()
    : IOException("The archive has more than one central directory, and different readers would use different ones.");

/// <summary>
/// 先頭に別のデータが付いた ZIP を書き換えようとした (#396)。
/// </summary>
/// <remarks>
/// 書き換えの部品 (SharpZipLib) には先頭を隠さずにファイルを渡す。ZIP64 では読む位置を直さないので、
/// 一覧と別の中央ディレクトリを読み、一覧に無い中身の書庫で元の書庫を置き換えてしまう。
/// </remarks>
internal sealed class PrefixedZipEditException()
    : IOException("A ZIP archive with data in front of it is not rewritten.");
