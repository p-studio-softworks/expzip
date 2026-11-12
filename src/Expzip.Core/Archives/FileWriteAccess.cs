using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Expzip.Archives;

/// <summary>
/// 読み取り専用のファイルに、ほかの名前 (ハードリンク) があるか、それ自体がリンクなので、読み取り専用を外して置き換えられない (#372)。
/// </summary>
/// <remarks>属性は実体ごとに付くので、外すと「はい」と答えていない名前の属性まで変わる。理由をアクセス権の問題と取り違えないよう、別の型にする。</remarks>
internal sealed class LinkedReadOnlyException(string path) : UnauthorizedAccessException($"Access to the path '{path}' is denied.");

/// <summary>
/// ファイルのアクセス権で、今のユーザーが書き込めるかを調べる (#372)。
/// </summary>
/// <remarks>
/// <para>
/// 読み取り専用のファイルは、書き込みの権利で開いてみると必ず「アクセスが拒否されました」になる。アクセス権で書き込みを禁じた
/// ファイルも同じ理由で失敗するので、開いてみるだけでは見分けられない。「はい」と答えた読み取り専用のファイルを置き換えるときに、
/// ほかのユーザーがアクセス権で守ったファイルまで置き換えないように、アクセス権そのものを Windows に調べさせる (<c>AccessCheck</c>)。
/// </para>
/// <para>
/// 調べるのは、中身を書く権利と属性を書く権利。読み取り専用を気にせずに置き換えるには、属性を書く権利も要る。
/// Windows でないときは調べない (読み取り専用の属性が無い)。
/// </para>
/// </remarks>
internal static class FileWriteAccess
{
    /// <summary>書き込めなければ、開いてみたときと同じ例外を投げる。</summary>
    /// <exception cref="UnauthorizedAccessException">アクセス権で書き込みが禁じられている場合。</exception>
    public static void Require(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        if (Check(path) is false)
        {
            throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
        }
    }

    /// <summary>書き込めるか。調べられなければ <see langword="null"/>。</summary>
    internal static bool? Check(string path)
    {
        const uint Information = OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation;

        var full = AtomicFile.LongPathIfNeeded(Path.GetFullPath(path));
        GetFileSecurityW(full, Information, null, 0, out var needed);
        if (needed == 0)
        {
            return null;
        }

        var descriptor = new byte[needed];
        if (!GetFileSecurityW(full, Information, descriptor, needed, out _))
        {
            return null;
        }

        if (!OpenProcessToken(GetCurrentProcess(), TokenDuplicate | TokenQuery, out var process))
        {
            return null;
        }

        using (process)
        {
            if (!DuplicateToken(process, SecurityImpersonation, out var token))
            {
                return null;
            }

            using (token)
            {
                var mapping = new GenericMapping
                {
                    Read = FileGenericRead,
                    Write = FileGenericWrite,
                    Execute = FileGenericExecute,
                    All = FileAllAccess,
                };

                var desired = FileWriteData | FileWriteAttributes;
                MapGenericMask(ref desired, ref mapping);

                var privileges = new byte[256];
                var privilegesLength = privileges.Length;
                if (!AccessCheck(descriptor, token, desired, ref mapping, privileges, ref privilegesLength,
                        out _, out var granted))
                {
                    return null;
                }

                return granted;
            }
        }
    }

    private const uint OwnerSecurityInformation = 0x1;
    private const uint GroupSecurityInformation = 0x2;
    private const uint DaclSecurityInformation = 0x4;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const int SecurityImpersonation = 2;
    private const uint FileWriteData = 0x0002;
    private const uint FileWriteAttributes = 0x0100;
    private const uint FileGenericRead = 0x00120089;
    private const uint FileGenericWrite = 0x00120116;
    private const uint FileGenericExecute = 0x001200A0;
    private const uint FileAllAccess = 0x001F01FF;

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint Read;
        public uint Write;
        public uint Execute;
        public uint All;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSecurityW(
        string fileName, uint requestedInformation, byte[]? descriptor, uint length, out uint lengthNeeded);

    [DllImport("kernel32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateToken(SafeAccessTokenHandle existing, int level, out SafeAccessTokenHandle duplicate);

    [DllImport("advapi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern void MapGenericMask(ref uint accessMask, ref GenericMapping mapping);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AccessCheck(
        byte[] descriptor, SafeAccessTokenHandle token, uint desired, ref GenericMapping mapping,
        byte[] privileges, ref int privilegesLength, out uint grantedAccess,
        [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);
}
