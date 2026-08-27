using System.IO;

namespace Expzip.Archives;

/// <summary>
/// ファイルが書き換わったかどうかを見るための控え。更新日時と大きさの組で見る。
/// </summary>
/// <remarks>
/// <see cref="FileSystemWatcher"/> を使わないのは、保存の仕方がアプリによって
/// 大きく違うため。その場で書き換えるアプリもあれば、別名で書いてから置き換える
/// アプリもあり、後者では対象のファイルが一度消えて作り直される。パスを定期的に
/// 見に行くほうが、どちらの作法でも取りこぼさない。
/// 取り出したファイルの見張り (#16) と、書庫そのものの見張り (#64) で共通に使う。
/// </remarks>
/// <param name="LastWriteUtc">最終更新日時。</param>
/// <param name="Length">大きさ。</param>
internal readonly record struct FileStamp(DateTime LastWriteUtc, long Length)
{
    /// <summary>いまのファイルの状態を読む。読めない場合は既定値を返す。</summary>
    public static FileStamp Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return default;
        }
    }
}
