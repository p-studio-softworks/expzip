using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Expzip.Archives;

/// <summary>
/// ディスクが HDD かどうかを Windows に尋ねる (#195)。
/// </summary>
/// <remarks>
/// <para>
/// 展開を並列にすると、SSD では 4 倍ほど速くなるが、HDD では<b>約 2 倍遅くなる</b>。
/// 書き込む場所があちこちに飛び、ディスクの針の移動が増えるため。
/// HDD のときは並列にしない。
/// </para>
/// <para>
/// 尋ねるのは「シークに時間がかかるか」(<c>StorageDeviceSeekPenaltyProperty</c>)。
/// 回転するディスクなら true になる。ネットワーク上のフォルダーや、答えの返らない
/// ディスクは分からない (<see langword="null"/>) として扱い、並列にしない。
/// 遅くしないことを優先する。
/// </para>
/// </remarks>
internal static class SeekPenalty
{
    /// <summary>
    /// このパスのあるディスクが HDD なら true、SSD なら false。分からなければ <see langword="null"/>。
    /// </summary>
    public static bool? Of(string path)
    {
        // Windows 以外にはこの問い合わせが無い。そちらは SSD の機械がほとんど
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            return Query(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or ExternalException)
        {
            return null;
        }
    }

    private static bool? Query(string path)
    {
        // フォルダーに割り当てたドライブもあるので、ドライブ文字ではなくボリュームを辿る
        var mountPoint = new char[MaxPath];
        if (!GetVolumePathName(Path.GetFullPath(path), mountPoint, mountPoint.Length))
        {
            return null;
        }

        var volume = new char[MaxPath];
        if (!GetVolumeNameForVolumeMountPoint(new string(mountPoint).TrimEnd('\0'), volume, volume.Length))
        {
            // ネットワーク上のフォルダーはボリュームの名前を持たない
            return null;
        }

        // 末尾の区切りを付けたままだと、ボリュームではなくその中のフォルダーを開いてしまう
        var device = new string(volume).TrimEnd('\0').TrimEnd('\\');

        // 読み書きの権限は要らない。管理者でなくても開ける
        using var handle = CreateFile(
            device, 0, FileShare.ReadWrite, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        var query = new PropertyQuery { PropertyId = SeekPenaltyProperty, QueryType = StandardQuery };
        if (!DeviceIoControl(
                handle, QueryProperty, ref query, Marshal.SizeOf<PropertyQuery>(),
                out var answer, Marshal.SizeOf<SeekPenaltyDescriptor>(), out _, IntPtr.Zero))
        {
            return null;
        }

        return answer.IncursSeekPenalty != 0;
    }

    private const int MaxPath = 1024;

    private const uint QueryProperty = 0x002D1400;

    private const int SeekPenaltyProperty = 7;

    private const int StandardQuery = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyQuery
    {
        public int PropertyId;

        public int QueryType;

        // 中身は使わないが、構造体の大きさを合わせるために要る
        public byte AdditionalParameters;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SeekPenaltyDescriptor
    {
        public uint Version;

        public uint Size;

        public byte IncursSeekPenalty;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetVolumePathNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathName(string fileName, char[] volumePathName, int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode,
        EntryPoint = "GetVolumeNameForVolumeMountPointW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPoint(
        string volumeMountPoint, char[] volumeName, int bufferLength);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(
        string fileName, uint desiredAccess, FileShare shareMode, IntPtr securityAttributes,
        FileMode creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint ioControlCode, ref PropertyQuery inBuffer, int inBufferSize,
        out SeekPenaltyDescriptor outBuffer, int outBufferSize, out int bytesReturned, IntPtr overlapped);
}
