using System.IO;

namespace Expzip.Inspection;

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

    /// <summary>
    /// Mac でダブルクリックすると、内容を見るのではなく実行・導入されてしまう拡張子 (#191)。
    /// Mac で動かしているときだけ足す。Windows 版の確認と検査は変えない。
    /// </summary>
    /// <remarks>
    /// <c>.app</c> はフォルダーなので、一覧で開く対象にならない。<c>.sh</c> や <c>.scpt</c> は
    /// ダブルクリックしてもエディターで開くだけなので含めない。
    /// </remarks>
    private static readonly HashSet<string> MacExecutable = new(StringComparer.OrdinalIgnoreCase)
    {
        // ターミナルで実行されるもの。ターミナルの設定も、開くとコマンドを実行できる
        ".command", ".tool", ".terminal",
        // インストーラー。中のスクリプトが動く
        ".pkg", ".mpkg",
        // 構成プロファイル。設定を書き換える (Windows の .reg に当たる)
        ".mobileconfig",
        // Automator。取り込まれて動く
        ".workflow", ".action",
        // 別の場所を指すもの (Windows の .lnk や .url に当たる)
        ".fileloc", ".webloc", ".inetloc",
        // Web アーカイブ。Safari で開くと中のスクリプトが動く
        ".webarchive",
    };

    /// <summary>開くと実行されうる種類のファイルか。</summary>
    public static bool IsExecutable(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return extension.Length > 0
               && (Executable.Contains(extension) || (OperatingSystem.IsMacOS() && MacExecutable.Contains(extension)));
    }
}
