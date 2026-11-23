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
    /// <summary>
    /// 下の 2 つを初めて決めるときの錠 (#398)。設定は起動のときに裏のスレッドで読むので、
    /// UI のスレッドと同時に初めて呼ばれることがありうる。<c>bool?</c> は 1 回で読み書きできる
    /// 値ではないため、守らないと「決めた」と「パッケージではない」を半分ずつ読み、
    /// Store 版で exe のフォルダーを置き場所と取り違えるおそれがある
    /// </summary>
    private static readonly Lock Deciding = new();

    private static bool? _isPackaged;
    private static string? _path;

    /// <summary>
    /// フォルダーの場所を決める関数。画面の側が差し替えられる。<see langword="null"/> なら上の既定。
    /// <see cref="Path"/> を最初に読む前に設定する。
    /// </summary>
    internal static Func<string>? PathOverride { get; set; }

    /// <summary>MSIX として入れられて動いているか。</summary>
    public static bool IsPackaged
    {
        get
        {
            lock (Deciding)
            {
                return _isPackaged ??= DetectPackaged();
            }
        }
    }

    /// <summary>ファイルを置くフォルダーのパス。</summary>
    public static string Path
    {
        get
        {
            lock (Deciding)
            {
                return _path ??= PathOverride?.Invoke() ?? (IsPackaged ? AppDataDirectory() : ExeDirectory());
            }
        }
    }

    /// <summary>
    /// 知らせに出す場所 (#406)。エクスプローラーで開ける、実際の場所にする。
    /// </summary>
    /// <remarks>
    /// <para>
    /// Store 版では、アプリから見た <c>%LOCALAPPDATA%\Expzip</c> の中身は、Windows が
    /// <c>%LOCALAPPDATA%\Packages\パッケージファミリー名\LocalCache\Local\Expzip</c> に振り替えている。
    /// アプリから見た場所をそのまま出すと、エクスプローラーでは見つからない。
    /// </para>
    /// <para>
    /// 振り替え先に同じ名前のファイルかフォルダーがあり、それがアプリから見た場所と同じもの
    /// (ボリュームとファイルの番号が同じ) のときだけ、振り替え先を出す。違うものだったときは、アプリから見た場所を出す。
    /// アプリから見た場所と振り替え先の両方に同じ名前のものがあると、使われていないほうを出して、
    /// そちらを直させたり削除させたりしかねないため。開けなくて比べられないときは、振り替え先を出す。
    /// </para>
    /// <para>
    /// 読み書きに使う場所は変えない。出すだけ。
    /// </para>
    /// </remarks>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(path))]
    public static string? ForDisplay(string? path)
    {
        if (path is null || !IsPackaged || PackageFamilyName() is not { } family)
        {
            return path;
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var redirected = System.IO.Path.Combine(localAppData, "Packages", family, "LocalCache", "Local");
        return ForDisplay(path, localAppData, redirected, System.IO.Path.Exists, SameFile);
    }

    /// <summary>知らせに出す場所を求める。テストは場所と、確かめ方を渡して呼ぶ。</summary>
    /// <param name="path">アプリから見た場所。</param>
    /// <param name="localAppData">アプリから見た <c>%LOCALAPPDATA%</c>。</param>
    /// <param name="redirected">振り替え先 (<c>…\LocalCache\Local</c>)。</param>
    /// <param name="exists">ファイルかフォルダーがあるか。</param>
    /// <param name="sameFile">2 つの場所が同じものか。比べられなければ null。</param>
    internal static string ForDisplay(
        string path, string localAppData, string redirected, Func<string, bool> exists, Func<string, string, bool?> sameFile)
    {
        try
        {
            var full = System.IO.Path.GetFullPath(path);
            var root = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(localAppData))
                       + System.IO.Path.DirectorySeparatorChar;
            if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }

            var candidate = System.IO.Path.Combine(redirected, full[root.Length..]);
            if (!exists(candidate))
            {
                return path;
            }

            return sameFile(full, candidate) == false ? path : candidate;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException
                                   or PathTooLongException or IOException or UnauthorizedAccessException)
        {
            // 求められなければ、アプリから見た場所のまま出す。知らせを出せなくするよりよい
            return path;
        }
    }

    /// <summary>2 つの場所が同じファイルかフォルダーか。ボリュームとファイルの番号で比べる。開けなければ null。</summary>
    internal static bool? SameFile(string a, string b)
        => FileId(a) is { } first && FileId(b) is { } second ? first == second : null;

    /// <summary>
    /// ボリュームとファイルの番号。属性を読む権限だけで開くので、ほかのプログラムが共有なしで開いていても読める。
    /// フォルダーも開ける。開けなければ null。
    /// </summary>
    private static (uint Volume, uint High, uint Low)? FileId(string path)
    {
        using var handle = CreateFileW(
            path, FileReadAttributes, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero,
            FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return (info.VolumeSerialNumber, info.FileIndexHigh, info.FileIndexLow);
    }

    /// <summary>パッケージファミリー名。パッケージに入っていないか、求められなければ null。</summary>
    private static string? PackageFamilyName()
    {
        uint length = 0;
        if (GetCurrentPackageFamilyName(ref length, null) != ErrorInsufficientBuffer || length == 0)
        {
            return null;
        }

        var name = new char[length];
        return GetCurrentPackageFamilyName(ref length, name) == 0
            ? new string(name, 0, (int)length - 1)
            : null;
    }

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
    /// 利用者ごとのアプリのデータの場所 (LocalApplicationData) の下の Expzip。Store 版の置き場所。
    /// 読み書きの前にフォルダーが要る。作れなくてもアプリは止めない
    /// (保存できないだけで、exe の隣に書き込めないときと同じ扱い)。
    /// </summary>
    internal static string AppDataDirectory()
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

    private const int ErrorInsufficientBuffer = 122;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    private const uint FileReadAttributes = 0x80;

    /// <summary>フォルダーを開くのに要る。</summary>
    private const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle file, out ByHandleFileInformation information);

    /// <summary>BY_HANDLE_FILE_INFORMATION。日時 (FILETIME) は 4 バイトずつの 2 つとして並べる。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    /// <summary>長さは終わりの NUL を含む。</summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref uint packageFamilyNameLength, char[]? packageFamilyName);
}
