using System.IO;

namespace Expzip.Archives;

/// <summary>
/// Windows がファイルの出所を記録する Zone.Identifier ストリーム (通称 Mark of the Web) の読み書き。
/// </summary>
/// <remarks>
/// ネットから落とした書庫には、ブラウザーがこの印を付ける。書庫を開いて中身を
/// 取り出しただけでこの印が消えると、SmartScreen や Office の保護ビューが働かなくなる。
/// そこで、書庫に印が付いていた場合に限り、取り出したファイルにも引き継ぐ (#12)。
/// 自分で作った書庫には印が無いので、その場合は何も付けない。
/// </remarks>
internal static class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";

    /// <summary>ファイルに付いている印を読む。</summary>
    /// <returns>印の内容。付いていない場合や読めない場合は <see langword="null"/>。</returns>
    public static string? TryRead(string path)
    {
        try
        {
            // NTFS 以外や印の無いファイルでは開けない。存在確認を別に行わないのは、
            // 副ストリームに対する存在確認が環境によって当てにならないため。
            using var stream = new FileStream(
                path + StreamSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>読み取った印を別のファイルに引き継ぐ。</summary>
    /// <param name="path">印を付ける先のファイル。</param>
    /// <param name="zone"><see cref="TryRead"/> の戻り値。<see langword="null"/> なら何もしない。</param>
    /// <returns>付けられた場合は true。</returns>
    public static bool TryApply(string path, string? zone)
    {
        if (zone is null)
        {
            return false;
        }

        try
        {
            using var stream = new FileStream(
                path + StreamSuffix, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);

            writer.Write(zone);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            // 印を付けられなくても取り出し自体は済んでいる。
            // 一時フォルダが NTFS でない場合などにここへ来る。
            return false;
        }
    }
}
