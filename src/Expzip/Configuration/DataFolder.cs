using System.IO;
using System.Runtime.InteropServices;

namespace Expzip.Configuration;

/// <summary>設定とルールのファイルを置くフォルダー (#176)。</summary>
/// <remarks>
/// <para>
/// **exe 単体で配る版は exe と同じフォルダー。**フォルダーにコピーするだけで動き、
/// USB メモリーに入れて持ち運べ、消すときはフォルダーごと消せる状態を保つ。
/// </para>
/// <para>
/// **Store 版 (MSIX) は <c>%LOCALAPPDATA%\Expzip</c>。**MSIX はインストール先に
/// 書き込めない。AppData への書き込みは Windows がパッケージ専用の場所へ振り替え、
/// アンインストールで一緒に消える。版ごとにビルドを分けず、起動したときに見分ける。
/// </para>
/// <para>
/// exe 単体の版から乗り換えても、設定は引き継がない。
/// </para>
/// </remarks>
internal static class DataFolder
{
    /// <summary>MSIX として入れられて動いているか。</summary>
    public static bool IsPackaged { get; } = DetectPackaged();

    /// <summary>ファイルを置くフォルダーのパス。</summary>
    public static string Path { get; } = IsPackaged ? PackagedDirectory() : ExeDirectory();

    /// <summary>
    /// パッケージに入っていないときは <c>APPMODEL_ERROR_NO_PACKAGE</c> が返る。
    /// 入っているときは、名前を受ける領域が 0 文字なので足りないと返る。
    /// </summary>
    private static bool DetectPackaged()
    {
        uint length = 0;
        return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
    }

    /// <summary>
    /// 読み書きの前にフォルダーが要る。作れなくてもアプリは止めない
    /// (保存できないだけで、exe の隣に書き込めないときと同じ扱い)。
    /// </summary>
    private static string PackagedDirectory()
    {
        var directory = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Expzip");

        try
        {
            Directory.CreateDirectory(directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return directory;
    }

    /// <summary>
    /// exe が置かれているフォルダー。単一ファイルとして発行した場合、実行時に
    /// 展開される一時フォルダーではなく exe 自身の場所を指す必要があるため
    /// <see cref="Environment.ProcessPath"/> を使う。
    /// </summary>
    private static string ExeDirectory()
    {
        var processPath = Environment.ProcessPath;

        if (!string.IsNullOrEmpty(processPath)
            && System.IO.Path.GetDirectoryName(processPath) is { Length: > 0 } directory)
        {
            return directory;
        }

        return AppContext.BaseDirectory;
    }

    private const int AppModelErrorNoPackage = 15700;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);
}
