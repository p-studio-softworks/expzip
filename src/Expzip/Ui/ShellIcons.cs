using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Expzip.Ui;

/// <summary>
/// ファイルとフォルダーの絵を、エクスプローラーと同じものにする (#158)。
/// </summary>
/// <remarks>
/// <para>
/// Windows に**拡張子から**絵を尋ねる (<c>SHGetFileInfo</c> に
/// <c>SHGFI_USEFILEATTRIBUTES</c> を付ける)。この形ならファイルが実在しなくてよく、
/// 書庫の中身を取り出すことも読むこともない。拡張子から Windows の登録を引くだけ。
/// </para>
/// <para>
/// 実行ファイル (<c>.exe</c>) の固有の絵は、ファイルそのものが無いと出せない。
/// ここでは Windows の一般的な絵になる。
/// </para>
/// <para>
/// **同じ拡張子は 1 度だけ尋ねて覚えておく。**数千件の一覧で、行ごとに尋ねない。
/// 絵を作るのは画面のスレッドだけなので、鍵を掛けずに覚えておける。
/// </para>
/// </remarks>
internal static class ShellIcons
{
    private const uint FileAttributeNormal = 0x80;
    private const uint FileAttributeDirectory = 0x10;

    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiLargeIcon = 0x0;
    private const uint ShgfiSmallIcon = 0x1;
    private const uint ShgfiUseFileAttributes = 0x10;

    /// <summary>拡張子 (小文字) ごとの絵。尋ねても得られなかったものは null で覚える。</summary>
    private static readonly Dictionary<string, ImageSource?> Files = new(StringComparer.Ordinal);

    private static ImageSource? _folder;
    private static bool _folderAsked;

    /// <summary>
    /// 大きい絵 (32 px) を使うか。表示倍率が 100% を超えるときは、小さい絵 (16 px) を
    /// 引き伸ばすとぼやけるので、大きい絵を縮めて使う。
    /// </summary>
    /// <remarks>
    /// 倍率は最初に尋ねたときに決める。途中で倍率の違う画面へ移っても作り直さない。
    /// 縮める向きなら崩れず、引き伸ばす向きになるのは 100% の画面で始めたときだけ。
    /// </remarks>
    private static bool? _large;

    /// <summary>この名前のファイルの絵。得られなければ <see langword="null"/>。</summary>
    public static ImageSource? ForFile(string name)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();

        if (Files.TryGetValue(extension, out var cached))
        {
            return cached;
        }

        // 拡張子だけを渡す。書庫の中の名前をそのまま渡すと、細工された長い名前や
        // 使えない字をシェルに渡すことになる
        var icon = Ask("file" + extension, FileAttributeNormal);
        Files[extension] = icon;
        return icon;
    }

    /// <summary>フォルダーの絵。得られなければ <see langword="null"/>。</summary>
    public static ImageSource? Folder
    {
        get
        {
            if (!_folderAsked)
            {
                _folderAsked = true;
                _folder = Ask("folder", FileAttributeDirectory);
            }

            return _folder;
        }
    }

    private static ImageSource? Ask(string path, uint attributes)
    {
        var info = default(ShFileInfo);
        var size = UseLarge() ? ShgfiLargeIcon : ShgfiSmallIcon;

        var found = SHGetFileInfo(
            path, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
            ShgfiIcon | size | ShgfiUseFileAttributes);

        if (found == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var image = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

            // 画面のスレッドの外でも読めるように、また変更の見張りを外して軽くするために固める
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    private static bool UseLarge()
    {
        if (_large is { } known)
        {
            return known;
        }

        var scale = Application.Current?.MainWindow is { } window
            ? VisualTreeHelper.GetDpi(window).DpiScaleX
            : 1.0;

        _large = scale > 1.0;
        return _large.Value;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath, uint dwFileAttributes, ref ShFileInfo psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
