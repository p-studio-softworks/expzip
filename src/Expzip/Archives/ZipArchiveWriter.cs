using System.IO;
using System.IO.Compression;

namespace Expzip.Archives;

/// <summary>ZIP書庫を作成・更新する。</summary>
internal static class ZipArchiveWriter
{
    /// <summary>
    /// 空のZIP書庫を作る。既に同じ名前のファイルがあれば置き換える。
    /// </summary>
    /// <remarks>
    /// 中身が無くてもZIPとしては正しく、終端レコードだけを持つファイルになる。
    /// エントリ名の書き出しは既定 (UTF-8 + EFSフラグ) に任せる。読み取り時に
    /// 使う CP932 の判定は古い書庫を救うためのもので、こちらから作る書庫を
    /// あえて古い形式にする理由は無い。
    /// </remarks>
    /// <exception cref="IOException">ファイルを作成できない場合。</exception>
    public static void CreateEmpty(string path)
    {
        // using で閉じた時点で終端レコードが書かれ、読み取り可能な書庫になる
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
    }
}
