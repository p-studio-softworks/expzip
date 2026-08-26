using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 書庫内のパスの正規化と安全性の判定。
/// 展開時の拒否 (<see cref="ArchiveExtractor"/>) と一覧表示の警告 (#36) で
/// 同じ判定を使うため、ここに集約している。別々に実装すると、
/// 「一覧では警告が出ないのに展開すると拒否される」といった食い違いが起きる。
/// </summary>
internal static class ArchivePath
{
    /// <summary>
    /// 区切り文字を <c>/</c> に揃える。
    /// ZIP仕様は <c>/</c> と定めているが、DOS時代のツールには <c>\</c> を書くものがあった。
    /// Windowsのファイル名に <c>\</c> は使えないため、区切りとみなして差し支えない。
    /// </summary>
    public static string Normalize(string entryName) => entryName.Replace('\\', '/');

    /// <summary>
    /// 展開したときに展開先の外へ出てしまうパスかどうか。
    /// 展開先に依存しない判定なので、書庫を開いた時点でも使える。
    /// </summary>
    /// <remarks>
    /// 先頭の <c>/</c> は展開先を起点とした相対パスとして扱うため、これだけでは
    /// 外に出るとは判定しない。<c>../</c> で先頭より上に遡る場合と、ドライブ指定や
    /// 絶対パスの場合に <see langword="true"/> を返す。
    /// </remarks>
    public static bool IsEscaping(string entryName)
    {
        var normalized = Normalize(entryName).TrimStart('/');

        if (normalized.Length == 0)
        {
            return true;
        }

        if (Path.IsPathRooted(normalized) || normalized.Contains(':'))
        {
            return true;
        }

        // 階層の深さを数え、一度でも起点より上に出たら外へ出るとみなす
        var depth = 0;
        foreach (var segment in normalized.Split('/'))
        {
            if (segment.Length == 0 || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                depth--;
                if (depth < 0)
                {
                    return true;
                }
            }
            else
            {
                depth++;
            }
        }

        return false;
    }

    /// <summary>
    /// 一覧に警告を出すべきパスかどうか。
    /// <see cref="IsEscaping"/> より広く、結果的に展開先の中に収まる場合でも
    /// <c>..</c> を名前に持つ項目は通常あり得ないので対象にする。
    /// </summary>
    /// <remarks>
    /// <c>.</c> だけの区切りは対象にしない。その場を指すだけで展開先は変わらず、
    /// GNU tar が既定で先頭に付けるため、ふつうの tar が丸ごと警告になってしまう。
    /// </remarks>
    public static bool IsSuspicious(string entryName)
    {
        if (IsEscaping(entryName))
        {
            return true;
        }

        foreach (var segment in Normalize(entryName).Split('/'))
        {
            if (segment == "..")
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// エントリ名を展開先からの相対パスに変換する。
    /// </summary>
    /// <returns>安全な相対パス。展開先の外を指す場合は <see langword="null"/>。</returns>
    public static string? ToSafeRelativePath(string entryName)
    {
        if (IsEscaping(entryName))
        {
            return null;
        }

        return Normalize(entryName).TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
    }
}
