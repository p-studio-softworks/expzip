using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace Expzip.Archives;

/// <summary>
/// 展開で、既にあるファイルを置き換えてよいか (#353)。
/// </summary>
/// <remarks>
/// <para>
/// 画面の展開では、上書きの確認に挙げたファイルだけを置き換える (<see cref="Only"/>)。
/// 名前の書き方の違い (8.3 形式の短い名前など) で確認に挙がらなかったファイルや、
/// 確認のあとに置かれたファイルは、「上書きする」と答えても置き換えない。
/// </para>
/// <para>
/// この展開の中で作ったファイルは置き換えてよい。同じ名前の項目が 2 つある tar
/// (<c>tar -r</c> で足したもの) では後ろが新しい版で、長い名前と短い名前の 2 項目 (#349) も後ろが上書きする。
/// </para>
/// <para>
/// 同じファイルかは、名前ではなく、Windows がファイルごとに付ける番号 (ドライブの番号とファイルの番号) で比べる。
/// 短い名前で指していても同じと分かり、濁点などの形や大文字小文字だけが違う別のファイル (NTFS は別のものとして持てる) を
/// 取り違えない。番号を読めなければ同じとみなさない (置き換えずに飛ばす)。ほかの OS では、名前をそのまま比べる。
/// </para>
/// </remarks>
internal sealed class ExtractOverwrite
{
    private readonly bool _all;
    private readonly HashSet<string>? _approved;
    private readonly ConcurrentDictionary<string, byte> _created = new(StringComparer.Ordinal);

    private ExtractOverwrite(bool all, HashSet<string>? approved)
    {
        _all = all;
        _approved = approved;
    }

    /// <summary>すべて置き換える、またはどれも置き換えない。</summary>
    public static implicit operator ExtractOverwrite(bool all) => new(all, null);

    /// <summary>挙げたファイルと、この展開の中で作ったファイルだけを置き換える。</summary>
    /// <param name="approved">置き換えてよい既にあるファイルのフルパス。</param>
    public static ExtractOverwrite Only(IEnumerable<string> approved)
        => new(false, new HashSet<string>(approved.Select(Key).OfType<string>(), StringComparer.Ordinal));

    /// <summary>
    /// <paramref name="target"/> に書くときに、既にあるものを置き換えてよいか。
    /// 無いときは、置き換えない指定で書く (書いている間に置かれたものを置き換えない)。
    /// </summary>
    public bool Allows(string target)
    {
        if (_all)
        {
            return true;
        }

        if (_approved is null || !File.Exists(target))
        {
            return false;
        }

        return Key(target) is { } key && (_approved.Contains(key) || _created.ContainsKey(key));
    }

    /// <summary>この展開で <paramref name="target"/> に書き終えた。</summary>
    public void Created(string target)
    {
        if (_approved is not null && Key(target) is { } key)
        {
            _created.TryAdd(key, 0);
        }
    }

    /// <summary>同じファイルかを比べるための値。読めなければ <see langword="null"/>。</summary>
    private static string? Key(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return full;
        }

        // 属性を読む権利だけで開く。中身は読まず、ほかのプログラムの邪魔もしない
        using var handle = CreateFileW(
            AtomicFile.LongPathIfNeeded(full), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        return $"{info.VolumeSerialNumber:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }

    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareAll = 0x00000007;
    private const uint OpenExisting = 3;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle, out ByHandleFileInformation info);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint Attributes;
        public uint CreatedLow, CreatedHigh, AccessedLow, AccessedHigh, WrittenLow, WrittenHigh;
        public uint VolumeSerialNumber;
        public uint SizeHigh, SizeLow;
        public uint Links;
        public uint IndexHigh, IndexLow;
    }
}
