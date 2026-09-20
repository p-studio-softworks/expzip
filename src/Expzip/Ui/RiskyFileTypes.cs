using System.IO;

namespace Expzip.Ui;

/// <summary>
/// ダブルクリックで開くと、内容を見るのではなくそのまま実行されてしまう拡張子 (#12)。
/// </summary>
/// <remarks>
/// 書庫の中身は差出人の分からないものが多い。一覧上は書類に見えても、実際には
/// 実行ファイルということがある。開く前に一度確認を挟むために使う。
/// なお <c>.docm</c> のようなマクロ付き文書は含めない。開いた先のアプリが
/// マクロの実行可否を自前で尋ねるため、こちらで重ねて聞く必要がない。
/// </remarks>
internal static class RiskyFileTypes
{
    private static readonly HashSet<string> Executable = new(StringComparer.OrdinalIgnoreCase)
    {
        // 実行形式
        ".exe", ".com", ".scr", ".pif", ".msi", ".msp", ".cpl", ".dll", ".ocx",
        // バッチ・スクリプト
        ".bat", ".cmd", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".ps1", ".psm1",
        ".hta", ".jar", ".msc",
        // 設定を書き換えるもの
        ".reg", ".inf",
        // 別のファイルを指すもの。指す先が実行ファイルでも一覧では分からない
        ".lnk", ".url",
    };

    /// <summary>開くと実行されうる種類のファイルか。</summary>
    public static bool IsExecutable(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0 && Executable.Contains(extension);
    }
}
