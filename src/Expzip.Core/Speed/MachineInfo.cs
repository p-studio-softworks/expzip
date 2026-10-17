using System.IO;
using System.Runtime.InteropServices;
using Expzip.Archives;

namespace Expzip.Speed;

/// <summary>
/// 測った条件として一緒に出す、このパソコンのこと (#269)。
/// </summary>
/// <remarks>
/// 別のパソコンで測った結果と並べたとき、どこが違うから速さが違うのかを読み取れるようにする。
/// どれも分からなければ空のままにし、測ること自体は止めない。
/// </remarks>
internal static class MachineInfo
{
    /// <summary>
    /// CPU の名前を尋ねる関数。画面の側が差し替えられる。<see langword="null"/> なら Windows に尋ねる。
    /// </summary>
    internal static Func<string>? CpuNameOverride { get; set; }

    /// <summary>
    /// バッテリーで動いているかを尋ねる関数。画面の側が差し替えられる。<see langword="null"/> なら Windows に尋ねる。
    /// </summary>
    internal static Func<bool?>? OnBatteryOverride { get; set; }

    /// <summary>CPU の名前。分からなければ空。</summary>
    public static string CpuName()
    {
        if (CpuNameOverride is { } query)
        {
            return query().Trim();
        }

        if (!OperatingSystem.IsWindows())
        {
            return string.Empty;
        }

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");

            // 名前の後ろを空白で埋めてある CPU がある
            return (key?.GetValue("ProcessorNameString") as string)?.Trim() ?? string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or System.Security.SecurityException)
        {
            return string.Empty;
        }
    }

    /// <summary>メモリーの大きさ。分からなければ 0。</summary>
    /// <remarks>
    /// 取り付けた量を出す。.NET が教える使える量は、Windows が取り置く分だけ少なく、
    /// 64 GB のパソコンで 63 GB と出てしまう。
    /// </remarks>
    public static long MemoryBytes()
    {
        if (OperatingSystem.IsWindows() && GetPhysicallyInstalledSystemMemory(out var kilobytes))
        {
            return kilobytes * 1024;
        }

        return Math.Max(0, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
    }

    /// <summary>バッテリーで動いているか。分からなければ <see langword="null"/>。</summary>
    /// <remarks>ノートパソコンはバッテリーで動くと遅くなる設定のことが多い。</remarks>
    public static bool? OnBattery()
    {
        if (OnBatteryOverride is { } query)
        {
            return query();
        }

        if (!OperatingSystem.IsWindows() || !GetSystemPowerStatus(out var status))
        {
            return null;
        }

        return status.ACLineStatus switch
        {
            0 => true,
            1 => false,
            _ => null,
        };
    }

    /// <summary>このフォルダーのあるディスクの種類。</summary>
    public static StorageKind StorageOf(string path)
    {
        try
        {
            if (SeekPenalty.IsNetwork(path))
            {
                return StorageKind.Network;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            return StorageKind.Unknown;
        }

        return SeekPenalty.Of(path) switch
        {
            true => StorageKind.Hdd,
            false => StorageKind.Ssd,
            null => StorageKind.Unknown,
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;

        // 中身は使わないが、構造体の大きさを合わせるために要る
        public byte BatteryFlag;

        public byte BatteryLifePercent;

        public byte SystemStatusFlag;

        public int BatteryLifeTime;

        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicallyInstalledSystemMemory(out long totalMemoryInKilobytes);
}
