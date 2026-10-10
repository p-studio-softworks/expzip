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

    private const uint ShgfiSysIconIndex = 0x4000;

    /// <summary>48 px の絵の一覧 (<c>SHIL_EXTRALARGE</c>)。</summary>
    private const int ShilExtraLarge = 2;

    /// <summary>256 px の絵の一覧 (<c>SHIL_JUMBO</c>)。</summary>
    private const int ShilJumbo = 4;

    private const int IldTransparent = 1;

    /// <summary>拡張子 (小文字) ごとの絵。尋ねても得られなかったものは null で覚える。</summary>
    private static readonly Dictionary<string, ImageSource?> Files = new(StringComparer.Ordinal);

    /// <summary>大きい絵 (#216)。小さい絵とは別に、要るときだけ尋ねて覚える。</summary>
    private static readonly Dictionary<string, ImageSource?> MediumFiles = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, ImageSource?> JumboFiles = new(StringComparer.Ordinal);

    /// <summary>フォルダーの絵は、拡張子と重ならない鍵で同じ控えに入れる。</summary>
    private const string FolderKey = "\\folder";

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

    /// <summary>
    /// 中アイコン (48) に使う絵 (#216)。表示倍率が 100% を超えるときは、256 px の絵を縮めて使う。
    /// </summary>
    public static ImageSource? MediumFor(string name, bool folder)
        => UseLarge() ? JumboFor(name, folder) : Cached(MediumFiles, name, folder, ShilExtraLarge);

    /// <summary>
    /// 大アイコン (96) と特大アイコン (256) に使う絵 (#216)。256 px の絵を縮めて使う。
    /// </summary>
    /// <remarks>
    /// 大きい絵を持たない種類では、Windows は小さい絵を左上に置いた 256 px の絵を返す
    /// (登録したアプリが 16 px や 32 px の絵しか持たないと、48 px の絵でも同じになる)。
    /// そのまま縮めると小さな絵が隅に寄るので、絵のある所だけを切り出す (<see cref="TrimCorner"/>)。
    /// 出す側は引き伸ばさない (<c>StretchDirection="DownOnly"</c>) ので、切り出した大きさのまま真ん中に出る。
    /// </remarks>
    public static ImageSource? JumboFor(string name, bool folder)
    {
        var key = KeyOf(name, folder);
        if (JumboFiles.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var jumbo = AskFromList(key, folder, ShilJumbo);
        if (jumbo is BitmapSource { PixelWidth: < 256 } trimmed
            && Cached(MediumFiles, name, folder, ShilExtraLarge) is BitmapSource medium
            && medium.PixelWidth > trimmed.PixelWidth)
        {
            // 256 px の絵が無く、48 px の絵のほうが大きく描かれている
            jumbo = medium;
        }

        JumboFiles[key] = jumbo;
        return jumbo;
    }

    private static string KeyOf(string name, bool folder)
        => folder ? FolderKey : Path.GetExtension(name).ToLowerInvariant();

    private static ImageSource? Cached(Dictionary<string, ImageSource?> cache, string name, bool folder, int list)
    {
        var key = KeyOf(name, folder);
        if (!cache.TryGetValue(key, out var icon))
        {
            icon = AskFromList(key, folder, list);
            cache[key] = icon;
        }

        return icon;
    }

    /// <summary>
    /// Windows の絵の一覧から、大きさを決めて絵を取り出す (#216)。
    /// <c>SHGetFileInfo</c> が出せるのは 16 px と 32 px だけなので、大きい絵はこちらで尋ねる。
    /// </summary>
    /// <param name="key">拡張子 (小文字)。フォルダーなら <see cref="FolderKey"/>。</param>
    private static ImageSource? AskFromList(string key, bool folder, int list)
    {
        // 小さい絵と同じく、拡張子だけを渡す
        var info = default(ShFileInfo);
        var found = SHGetFileInfo(
            folder ? "folder" : "file" + key,
            folder ? FileAttributeDirectory : FileAttributeNormal,
            ref info, (uint)Marshal.SizeOf<ShFileInfo>(),
            ShgfiSysIconIndex | ShgfiUseFileAttributes);

        if (found == IntPtr.Zero)
        {
            return null;
        }

        var iid = typeof(IImageList).GUID;
        if (SHGetImageList(list, ref iid, out var images) != 0 || images is null)
        {
            return null;
        }

        if (images.GetIcon(info.iIcon, IldTransparent, out var hIcon) != 0 || hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var image = TrimCorner(Imaging.CreateBitmapSourceFromHIcon(
                hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions()));
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException)
        {
            return null;
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }

    /// <summary>
    /// 絵が左上の隅にしか描かれていなければ、その隅だけを切り出す。
    /// 切り出す大きさは、描かれた所が収まるいちばん小さい絵の大きさ (16、24、32、48 …) にする。
    /// 描かれた所ぴったりに切ると、種類ごとに絵の大きさがばらつく。
    /// </summary>
    private static BitmapSource TrimCorner(BitmapSource bitmap)
    {
        var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        converted.CopyPixels(pixels, stride, 0);

        var right = 0;
        var bottom = 0;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (pixels[y * stride + x * 4 + 3] != 0)
                {
                    right = Math.Max(right, x + 1);
                    bottom = Math.Max(bottom, y + 1);
                }
            }
        }

        var used = Math.Max(right, bottom);
        foreach (var size in (ReadOnlySpan<int>)[16, 24, 32, 48, 64, 96, 128])
        {
            if (used <= size && size * 2 <= Math.Min(width, height))
            {
                var cropped = new CroppedBitmap(bitmap, new Int32Rect(0, 0, size, size));
                cropped.Freeze();
                return cropped;
            }
        }

        return bitmap;
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

    [DllImport("shell32.dll")]
    private static extern int SHGetImageList(
        int iImageList, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IImageList ppv);

    /// <summary>
    /// Windows の絵の一覧 (<c>IImageList</c>)。使うのは <c>GetIcon</c> だけだが、
    /// 呼び出し表の位置を合わせるため、その前のものも並べてある。
    /// </summary>
    [ComImport]
    [Guid("46EB5926-582E-4017-9FDF-E8998DAA0950")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IImageList
    {
        [PreserveSig] int Add(IntPtr hbmImage, IntPtr hbmMask, ref int pi);

        [PreserveSig] int ReplaceIcon(int i, IntPtr hicon, ref int pi);

        [PreserveSig] int SetOverlayImage(int iImage, int iOverlay);

        [PreserveSig] int Replace(int i, IntPtr hbmImage, IntPtr hbmMask);

        [PreserveSig] int AddMasked(IntPtr hbmImage, int crMask, ref int pi);

        [PreserveSig] int Draw(IntPtr pimldp);

        [PreserveSig] int Remove(int i);

        [PreserveSig] int GetIcon(int i, int flags, out IntPtr picon);
    }
}
