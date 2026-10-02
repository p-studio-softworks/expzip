using System.Runtime.InteropServices;

namespace Expzip.Ai;

/// <summary>
/// APIキーのような秘密を、その場の利用者だけが読める形にする (#24)。
/// </summary>
/// <remarks>
/// <para>
/// Windows の DPAPI (<c>CryptProtectData</c>) を直に呼ぶ。設定ファイルは exe と
/// 同じフォルダに置く決まりで、人が読める形で書き出している。そこへ APIキーを
/// そのまま書くと、ファイルを手に入れた人が誰でも使えてしまう。
/// </para>
/// <para>
/// <c>System.Security.Cryptography.ProtectedData</c> でも同じことができるが、
/// 別の入れ物を1つ増やすことになる。呼ぶ関数は2つだけなので直に繋ぐ。
/// AMSI (#56) と同じ書き方。
/// </para>
/// <para>
/// **守った鍵は、その利用者のその環境でしか戻せない。**設定ごと別の PC へ
/// 持って行くと読めなくなる。持ち運びを謳っているアプリとしては痛いが、
/// 鍵を平文で持ち運ぶほうが危ない。読めなかったときは入れ直してもらう。
/// </para>
/// </remarks>
internal static class DataProtection
{
    /// <summary>誰の鍵かを取り違えないよう、用途を混ぜ込む。</summary>
    private static readonly byte[] Purpose =
        System.Text.Encoding.UTF8.GetBytes("Expzip.Ai.ApiKey");

    /// <summary>秘密を守った形にする。守れなければ <see langword="null"/>。</summary>
    public static string? Protect(string secret)
    {
        if (secret.Length == 0)
        {
            return null;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(secret);

        try
        {
            return Convert.ToBase64String(Transform(bytes, protect: true));
        }
        catch (Exception ex) when (ex is InvalidOperationException or OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>守った形から戻す。戻せなければ <see langword="null"/>。</summary>
    /// <remarks>
    /// 別の PC や別の利用者では戻せない。それは異常ではなく、そういう約束のもの。
    /// </remarks>
    public static string? Unprotect(string? guarded)
    {
        if (string.IsNullOrEmpty(guarded))
        {
            return null;
        }

        try
        {
            var bytes = Transform(Convert.FromBase64String(guarded), protect: false);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException
                                   or OutOfMemoryException)
        {
            return null;
        }
    }

    private static byte[] Transform(byte[] input, bool protect)
    {
        var inputBlob = default(Blob);
        var purposeBlob = default(Blob);
        var outputBlob = default(Blob);

        var inputHandle = GCHandle.Alloc(input, GCHandleType.Pinned);
        var purposeHandle = GCHandle.Alloc(Purpose, GCHandleType.Pinned);

        try
        {
            inputBlob.Length = input.Length;
            inputBlob.Data = inputHandle.AddrOfPinnedObject();
            purposeBlob.Length = Purpose.Length;
            purposeBlob.Data = purposeHandle.AddrOfPinnedObject();

            var ok = protect
                ? CryptProtectData(ref inputBlob, null, ref purposeBlob,
                    IntPtr.Zero, IntPtr.Zero, LocalMachineOff, ref outputBlob)
                : CryptUnprotectData(ref inputBlob, IntPtr.Zero, ref purposeBlob,
                    IntPtr.Zero, IntPtr.Zero, LocalMachineOff, ref outputBlob);

            if (!ok || outputBlob.Data == IntPtr.Zero)
            {
                throw new InvalidOperationException("DPAPI");
            }

            var result = new byte[outputBlob.Length];
            Marshal.Copy(outputBlob.Data, result, 0, outputBlob.Length);
            return result;
        }
        finally
        {
            if (outputBlob.Data != IntPtr.Zero)
            {
                LocalFree(outputBlob.Data);
            }

            purposeHandle.Free();
            inputHandle.Free();
        }
    }

    /// <summary>その利用者だけが戻せるようにする (計算機ぐるみにはしない)。</summary>
    private const int LocalMachineOff = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public int Length;

        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref Blob input, string? description, ref Blob purpose,
        IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref Blob input, IntPtr description, ref Blob purpose,
        IntPtr reserved, IntPtr prompt, int flags, ref Blob output);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);
}
