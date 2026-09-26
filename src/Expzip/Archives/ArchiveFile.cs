using System.IO;

namespace Expzip.Archives;

/// <summary>書庫のファイルを読むための入口。</summary>
/// <remarks>
/// 分割された書庫 (#61) を、読む側に意識させないためにここを通す。断片は
/// ただ切っただけなので、繋いだ流れを渡せば以降の道筋は何も変わらない。
/// </remarks>
internal static class ArchiveFile
{
    /// <summary>書庫を読むための流れを開く。分割されていれば結合して1本に見せる。</summary>
    /// <exception cref="InvalidDataException">断片が揃っていない場合。</exception>
    /// <exception cref="IOException">ファイルを読めない場合。</exception>
    public static Stream OpenRead(string path)
        => SplitVolumes.IsFirstVolume(path)
            ? SplitVolumes.Open(path)
            : File.OpenRead(path);
}
