using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Expzip.Archives;

/// <summary>ディスクのフォルダーの中の 1 個 (#366)。</summary>
/// <param name="Path">フルパス。</param>
/// <param name="Attributes">属性。リンクそのものの属性で、指す先は見ない。</param>
/// <param name="ReparseTag">リパースポイントの種類。リパースポイントでなければ 0。</param>
internal readonly record struct DiskItem(string Path, FileAttributes Attributes, uint ReparseTag)
{
    /// <summary>フォルダー (またはフォルダーを指すリンク) か。</summary>
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    /// <summary>
    /// 名前の代わり (name surrogate) のリパースポイントか。指す先の中身を持たず、別の場所を指すだけのもの。
    /// シンボリックリンク、ジャンクション、WSL のシンボリックリンクなど。
    /// </summary>
    /// <remarks>
    /// OneDrive などのクラウドのファイル、重複除去、WOF で圧縮されたファイルもリパースポイントだが、
    /// 中身はそこにあるので、ふつうのファイルとして扱う。飛ばすと、OneDrive のフォルダーを圧縮したときにファイルが抜ける。
    /// </remarks>
    public bool IsNameSurrogate
        => (Attributes & FileAttributes.ReparsePoint) != 0 && (ReparseTag & DiskLinks.NameSurrogateBit) != 0;

    /// <summary>
    /// 中身として読めない、アプリの実行用の別名 (AppExecLink) か。WindowsApps の中にある。
    /// </summary>
    public bool IsAppExecLink
        => (Attributes & FileAttributes.ReparsePoint) != 0 && ReparseTag == DiskLinks.AppExecLinkTag;

    /// <summary>シンボリックリンクか。</summary>
    public bool IsSymbolicLink => IsNameSurrogate && ReparseTag == DiskLinks.SymlinkTag;

    /// <summary>ジャンクション (マウントしたボリュームのフォルダーを含む) か。</summary>
    public bool IsJunction => IsNameSurrogate && ReparseTag == DiskLinks.MountPointTag;

    /// <summary>
    /// Windows が古いアプリのためにユーザーのフォルダーに置いた、隠しとシステムの属性の付いたジャンクションか
    /// (「My Music」「Application Data」など)。書庫には入れない (#366)。
    /// </summary>
    public bool IsWindowsCompatibilityJunction
        => IsJunction
           && (Attributes & (FileAttributes.Hidden | FileAttributes.System)) == (FileAttributes.Hidden | FileAttributes.System);
}

/// <summary>ディスクの上の、ファイルやフォルダーの識別子。リンクを通っても同じものなら同じ値になる。</summary>
internal readonly record struct DiskIdentity(uint VolumeSerial, ulong FileIndex);

/// <summary>
/// ディスクの上のリンク (シンボリックリンク、ジャンクション) を、指す先に触れずに調べる (#366)。
/// </summary>
/// <remarks>
/// <para>
/// .NET の <see cref="Directory.GetFiles(string)"/> などはリンクを区別せずに返し、中を読むとリンクをたどる。
/// フォルダーを圧縮するときにリンクの中へ入ると、自分の親を指すリンクで回り続けて落ちる。指す先がフォルダーの外なら、
/// 入れるつもりの無いファイルまで書庫に入る。ここで名前と属性とリパースポイントの種類を 1 回の列挙で得て、リンクを見分ける。
/// </para>
/// <para>
/// リンクの指す先には触れない。存在を確かめることもしない。指す先がネットワークだと、触れただけで接続し、
/// Windows の認証の情報を送ることがあるため。
/// </para>
/// </remarks>
internal static class DiskLinks
{
    /// <summary>リパースポイントの種類のうち、名前の代わりであることを示すビット。</summary>
    internal const uint NameSurrogateBit = 0x20000000;

    /// <summary>シンボリックリンク (IO_REPARSE_TAG_SYMLINK)。</summary>
    internal const uint SymlinkTag = 0xA000000C;

    /// <summary>ジャンクションとマウントしたボリュームのフォルダー (IO_REPARSE_TAG_MOUNT_POINT)。</summary>
    internal const uint MountPointTag = 0xA0000003;

    /// <summary>アプリの実行用の別名 (IO_REPARSE_TAG_APPEXECLINK)。名前の代わりではない。</summary>
    internal const uint AppExecLinkTag = 0x8000001B;

    /// <summary>シンボリックリンクの指す先が相対パスである印 (SYMLINK_FLAG_RELATIVE)。</summary>
    private const uint SymlinkFlagRelative = 1;

    /// <summary>
    /// フォルダーの中を列挙する。<c>.</c> と <c>..</c> は除く。
    /// </summary>
    /// <exception cref="IOException">フォルダーを読めない場合。</exception>
    /// <exception cref="UnauthorizedAccessException">読む権限が無い場合。</exception>
    public static List<DiskItem> List(string directory)
    {
        if (!OperatingSystem.IsWindows())
        {
            return [.. new DirectoryInfo(directory).EnumerateFileSystemInfos().Select(Portable)];
        }

        var result = new List<DiskItem>();
        var pattern = AtomicFile.LongPathIfNeeded(Path.Combine(directory, "*"));

        using var find = FindFirstFileExW(
            pattern, FindExInfoBasic, out var data, FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);

        if (find.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is ErrorFileNotFound or ErrorNoMoreFiles)
            {
                return result;
            }

            throw ToException(error, directory);
        }

        do
        {
            if (data.FileName is "." or "..")
            {
                continue;
            }

            var attributes = (FileAttributes)data.FileAttributes;
            result.Add(new DiskItem(
                Path.Combine(directory, data.FileName),
                attributes,
                (attributes & FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : 0));
        }
        while (FindNextFileW(find, out data));

        var last = Marshal.GetLastPInvokeError();
        if (last != ErrorNoMoreFiles)
        {
            throw ToException(last, directory);
        }

        return result;
    }

    /// <summary>
    /// ディスクの上の 1 個の、リンクそのものの属性とリパースポイントの種類を読む。指す先には触れない。
    /// </summary>
    /// <returns>読めなければ <see langword="null"/>。</returns>
    public static DiskItem? Describe(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

        if (!OperatingSystem.IsWindows())
        {
            FileSystemInfo info = Directory.Exists(full) ? new DirectoryInfo(full) : new FileInfo(full);
            return info.Exists || info.LinkTarget is not null ? Portable(info) : null;
        }

        using var find = FindFirstFileExW(
            AtomicFile.LongPathIfNeeded(full), FindExInfoBasic, out var data, FindExSearchNameMatch, IntPtr.Zero, 0);

        if (find.IsInvalid)
        {
            return null;
        }

        var attributes = (FileAttributes)data.FileAttributes;
        return new DiskItem(full, attributes, (attributes & FileAttributes.ReparsePoint) != 0 ? data.Reserved0 : 0);
    }

    /// <summary>
    /// リンクの指す先を、リンクそのものから読む。指す先には触れない。
    /// </summary>
    /// <returns>
    /// 指す先と、相対パスかどうか。指す先は Windows の内部の形 (<c>\??\C:\…</c> など) のまま。
    /// 読めなければ <see langword="null"/>。
    /// </returns>
    public static (string Target, bool Relative)? ReadTarget(DiskItem link)
    {
        if (!OperatingSystem.IsWindows())
        {
            // .NET の LinkTarget はリンクそのものを読み、指す先には触れない
            FileSystemInfo info = link.IsDirectory ? new DirectoryInfo(link.Path) : new FileInfo(link.Path);
            return info.LinkTarget is { Length: > 0 } target ? (target, !Path.IsPathRooted(target)) : null;
        }

        using var handle = CreateFileW(
            AtomicFile.LongPathIfNeeded(link.Path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new byte[MaximumReparseDataBufferSize];
        if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer, buffer.Length, out var returned, IntPtr.Zero)
            || returned < 8)
        {
            return null;
        }

        return Parse(buffer.AsSpan(0, returned));
    }

    /// <summary>リパースのデータから、指す先の置き換え名 (SubstituteName) を取り出す。</summary>
    internal static (string Target, bool Relative)? Parse(ReadOnlySpan<byte> data)
    {
        var tag = BinaryPrimitives.ReadUInt32LittleEndian(data);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);

        // 見出し (8 バイト) のあとに、名前の位置と長さが 4 個並ぶ。シンボリックリンクだけ、その後ろに印 (4 バイト) がある
        var headerLength = tag switch
        {
            SymlinkTag => 12,
            MountPointTag => 8,
            _ => -1,
        };

        if (headerLength < 0 || data.Length < 8 + headerLength || 8 + length > data.Length || length < headerLength)
        {
            return null;
        }

        var body = data.Slice(8, length);
        var offset = BinaryPrimitives.ReadUInt16LittleEndian(body);
        var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(body[2..]);
        var relative = tag == SymlinkTag && (BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) & SymlinkFlagRelative) != 0;

        var names = body[headerLength..];
        if (nameLength == 0 || nameLength % 2 != 0 || offset % 2 != 0 || offset + nameLength > names.Length)
        {
            return null;
        }

        var target = System.Text.Encoding.Unicode.GetString(names.Slice(offset, nameLength));
        return target.Contains('\0') ? null : (target, relative);
    }

    /// <summary>
    /// フォルダーの識別子を読む。リンクはたどる (NAS などで、リンクがふつうのフォルダーに見えるときにも同じ値になる)。
    /// </summary>
    /// <returns>読めなければ <see langword="null"/>。</returns>
    public static DiskIdentity? IdentityOf(string path)
    {
        // Windows でないときは識別子を読む手立てが .NET に無い。リンクの中には入らないので、回り続けることは無い
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var handle = CreateFileW(
            AtomicFile.LongPathIfNeeded(path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics, IntPtr.Zero);

        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        // ファイルの番号を持たないファイルシステムは 0 を返す。それで比べると、どのフォルダーも同じものに見える
        var index = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow;
        return index == 0 ? null : new DiskIdentity(info.VolumeSerialNumber, index);
    }

    /// <summary>
    /// 開いたファイルやフォルダーの、実際の場所。リンクをたどった先のパスになる。
    /// </summary>
    /// <param name="handle">開いたもの。</param>
    /// <param name="byGuid">ドライブ文字ではなく、ボリュームの GUID で表すか。ドライブ文字の無いボリュームのため。</param>
    /// <returns>取れなければ <see langword="null"/>。<c>\\?\</c> は外し、ネットワークは <c>\\サーバー\共有</c> の形にする。</returns>
    public static string? FinalPath(SafeFileHandle handle, bool byGuid)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var buffer = new char[1024];
        while (true)
        {
            var length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, byGuid ? VolumeNameGuid : VolumeNameDos);
            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return Untag(new string(buffer, 0, (int)length));
            }

            buffer = new char[length + 1];
        }
    }

    /// <summary>パスの実際の場所。存在しない部分は、存在するいちばん深いフォルダーの実際の場所の後ろに付ける。</summary>
    /// <remarks>作る前の書庫や一時ファイルのように、まだ無いものの実際の場所を比べるのに使う。</remarks>
    public static string Canonical(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var suffix = string.Empty;

        for (var current = full; !string.IsNullOrEmpty(current); )
        {
            if (FinalPathOf(current) is { } final)
            {
                return suffix.Length == 0 ? final : Path.Join(final, suffix);
            }

            var parent = Path.GetDirectoryName(current);
            if (parent is null)
            {
                break;
            }

            var name = Path.GetFileName(current);
            suffix = suffix.Length == 0 ? name : Path.Join(name, suffix);
            current = parent;
        }

        return full;
    }

    /// <summary>パスを開いて実際の場所を取る。リンクはたどる。開けなければ <see langword="null"/>。</summary>
    /// <remarks>
    /// Windows でないときは <see langword="null"/>。そのときは、読むときの場所の確かめ (<see cref="ReadScope"/>) をせず、
    /// 入れないファイルとはパスの文字で比べる。
    /// </remarks>
    public static string? FinalPathOf(string path, bool byGuid = false)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        using var handle = CreateFileW(
            AtomicFile.LongPathIfNeeded(path), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagBackupSemantics, IntPtr.Zero);

        return handle.IsInvalid ? null : FinalPath(handle, byGuid);
    }

    /// <summary><paramref name="path"/> が <paramref name="folder"/> と同じか、その中か。大文字と小文字は区別しない。</summary>
    public static bool IsSameOrInside(string path, string folder)
    {
        // ドライブの根 (E:\) は区切りを外せないので、そのまま前に付く部分として使う。区切りを重ねると、中のものが外に見える
        var parent = Path.TrimEndingDirectorySeparator(folder);
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
               || path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Windows でないときの 1 個。シンボリックリンクは、リパースポイントの種類をシンボリックリンクとして持たせる。
    /// </summary>
    private static DiskItem Portable(FileSystemInfo info)
    {
        var isLink = info.LinkTarget is not null;
        var attributes = info.Attributes | (isLink ? FileAttributes.ReparsePoint : 0);
        return new DiskItem(info.FullName, attributes, isLink ? SymlinkTag : 0);
    }

    private static string Untag(string path)
        => path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + path[8..]
            : path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..]
            : path;

    private static Exception ToException(int error, string path)
        => error is ErrorAccessDenied
            ? new UnauthorizedAccessException(new System.ComponentModel.Win32Exception(error).Message)
            : new IOException(new System.ComponentModel.Win32Exception(error).Message, unchecked((int)0x80070000 | error));

    private const int ErrorFileNotFound = 2;
    private const int ErrorAccessDenied = 5;
    private const int ErrorNoMoreFiles = 18;
    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const uint FindFirstExLargeFetch = 2;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FsctlGetReparsePoint = 0x000900A8;
    private const int MaximumReparseDataBufferSize = 16 * 1024;
    private const uint VolumeNameDos = 0x0;
    private const uint VolumeNameGuid = 0x1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Win32FindData
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint Reserved0;
        public uint Reserved1;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string FileName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string AlternateFileName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    private sealed class FindHandle() : SafeHandleZeroOrMinusOneIsInvalid(ownsHandle: true)
    {
        protected override bool ReleaseHandle() => FindClose(handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern FindHandle FindFirstFileExW(
        string fileName, int infoLevel, out Win32FindData data, int searchOp, IntPtr searchFilter, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(FindHandle find, out Win32FindData data);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr find);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string fileName, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, IntPtr input, int inputSize, byte[] output, int outputSize,
        out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation info);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] path, uint length, uint flags);
}
