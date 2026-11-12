using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;

namespace Expzip.Archives;

/// <summary>書き出す先に書くときにすること (#372)。</summary>
internal enum ExtractAction
{
    /// <summary>同じ名前のものが無い。置き換えない指定で書く。</summary>
    Write,

    /// <summary>置き換える (すべて置き換える指定、またはこの展開で作ったファイル)。読み取り専用・隠し・システムは置き換えない。</summary>
    Replace,

    /// <summary>確認に挙げて「はい」と答えたファイル。読み取り専用・隠し・システムでも置き換える。</summary>
    ReplaceApproved,

    /// <summary>置き換えずに残す (「いいえ」と答えた、または確認に挙がらなかった)。</summary>
    Skip,

    /// <summary>置き換えられない。フォルダーやファイルを消すことになる。</summary>
    Blocked,
}

/// <summary>置き換えられない理由 (#372)。</summary>
internal enum ExtractBlock
{
    /// <summary>展開先に同じ名前のフォルダーがある。</summary>
    FolderInTheWay,

    /// <summary>途中のフォルダーの名前に、展開先のファイルがある。</summary>
    FileInTheWay,
}

/// <summary>書き出す先に書くときにすることと、置き換えられないときの理由 (#372)。</summary>
internal readonly record struct ExtractDecision(ExtractAction Action, ExtractBlock? Block);

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

    /// <summary>
    /// <paramref name="target"/> に書くときに、どうするか (#372)。書き出しの 3 か所 (ZIP、パスワード付きの ZIP、そのほかの形式) で同じ判断にする。
    /// </summary>
    /// <param name="target">書き出す先。</param>
    public ExtractDecision Decide(string target)
    {
        if (Blocker(target) is { } reason)
        {
            return new ExtractDecision(ExtractAction.Blocked, reason);
        }

        if (!File.Exists(target))
        {
            return new ExtractDecision(ExtractAction.Write, null);
        }

        if (IsApproved(target))
        {
            // 確認に挙げて「はい」と答えたファイル。読み取り専用・隠し・システムでも置き換える
            return new ExtractDecision(ExtractAction.ReplaceApproved, null);
        }

        return Allows(target)
            ? new ExtractDecision(ExtractAction.Replace, null)
            : new ExtractDecision(ExtractAction.Skip, null);
    }

    /// <summary>
    /// 確認に挙げて「はい」と答えたファイルか。置き換える直前にも呼んで、書いている間に差し替えられていないかを確かめる (#372)。
    /// </summary>
    /// <remarks>「すべて置き換える」やこの展開で作ったファイルは含めない。読み取り専用などを置き換えてよいのは、名前を見て「はい」と答えたものだけ。</remarks>
    public bool IsApproved(string target)
        => _approved is not null && Key(target) is { } key && _approved.Contains(key);

    /// <summary>
    /// 置き換えられない理由。展開先に同じ名前のフォルダーがある、または途中のフォルダーの名前にファイルがある (#372)。
    /// </summary>
    /// <remarks>
    /// どちらも、置き換えるにはフォルダーやファイルを消すことになる。1 回では置き換えられず、途中で失敗すると中身が失われる (#320)。
    /// 途中のフォルダーは、既にあるフォルダーが見つかったところで止める。展開先は既にあるので、その外まではたどらない。
    /// </remarks>
    public static ExtractBlock? Blocker(string target)
    {
        if (Directory.Exists(target))
        {
            return ExtractBlock.FolderInTheWay;
        }

        for (var folder = Path.GetDirectoryName(Path.GetFullPath(target));
             folder is not null;
             folder = Path.GetDirectoryName(folder))
        {
            if (Directory.Exists(folder))
            {
                return null;
            }

            if (File.Exists(folder))
            {
                return ExtractBlock.FileInTheWay;
            }
        }

        return null;
    }

    /// <summary>
    /// 書き出しの失敗が「置き換えられなかった」ものか (#372)。既にあるファイルを、アクセス権や使用中のために置き換えられなかった。
    /// それ以外 (壊れている、空きが無いなど) は「失敗した」として知らせる。
    /// </summary>
    public static bool IsNotReplaced(Exception error, string target)
    {
        const int SharingViolation = unchecked((int)0x80070020);
        const int LockViolation = unchecked((int)0x80070021);

        return File.Exists(target)
               && error is UnauthorizedAccessException or IOException { HResult: SharingViolation or LockViolation };
    }

    /// <summary>
    /// <paramref name="target"/> が、この展開で書いたファイルか (#336)。ハードリンクとファイルのコピーの指す先を確かめるのに使う。
    /// 名前ではなくファイルの番号で比べる。展開先に前からあったファイルを指させないため。
    /// </summary>
    public bool WasCreated(string target)
        => Key(target) is { } key && _created.ContainsKey(key);

    /// <summary>この展開で <paramref name="target"/> に書き終えた。</summary>
    public void Created(string target)
    {
        // リンクを展開するときは、すべて置き換える指定でも覚える。リンクの指す先を確かめるのに使う (#336)。
        // ファイルごとに開いて番号を読むので、要らないときは読まない (#328)
        if ((_approved is not null || RecordCreated) && Key(target) is { } key)
        {
            _created.TryAdd(key, 0);
        }
    }

    /// <summary>この展開で書いたファイルを、指定にかかわらず覚えるか (#336)。リンクを展開する書庫で立てる。</summary>
    public bool RecordCreated { get; set; }

    /// <summary>同じファイルかを比べるための値。読めなければ <see langword="null"/>。</summary>
    private static string? Key(string path)
    {
        var full = Path.GetFullPath(path);
        if (!OperatingSystem.IsWindows())
        {
            return full;
        }

        // 属性を読む権利だけで開く。中身は読まず、ほかのプログラムの邪魔もしない。
        // リンクはたどらない。置き換えるのはリンクそのものなので、指す先が無いリンクでも同じものと分かるように (#372)
        using var handle = CreateFileW(
            AtomicFile.LongPathIfNeeded(full), FileReadAttributes, FileShareAll, IntPtr.Zero, OpenExisting,
            FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            return null;
        }

        // 128 ビットの番号 (FILE_ID_INFO) を先に使う。ReFS では 64 ビットの番号が重なることがある (#372)
        var id = new byte[24];
        if (GetFileInformationByHandleEx(handle, FileIdInfo, id, id.Length))
        {
            var volume = BitConverter.ToUInt64(id, 0);
            var file = Convert.ToHexString(id, 8, 16);
            return id.AsSpan(8, 16).ContainsAnyExcept((byte)0) ? $"{volume:X16}:{file}" : null;
        }

        if (!GetFileInformationByHandle(handle, out var info))
        {
            return null;
        }

        // 番号として 0 を返すドライブでは、どのファイルも同じに見える。読めなかったものとして扱う
        return info.IndexHigh == 0 && info.IndexLow == 0
            ? null
            : $"{info.VolumeSerialNumber:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }

    private const int FileIdInfo = 18;
    private const uint FileFlagOpenReparsePoint = 0x00200000;

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, byte[] info, int size);

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
