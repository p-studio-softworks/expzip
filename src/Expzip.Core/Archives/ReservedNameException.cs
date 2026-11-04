using System.Runtime.InteropServices;

namespace Expzip.Archives;

/// <summary>
/// この Windows で装置を指す名前のファイルは取り出さない (#328)。
/// </summary>
/// <remarks>
/// <para>
/// その名前で開くと、ファイルではなく装置が開き、書庫の中身が装置へ送られて「展開した」と数えられる。
/// どの名前が装置になるかは Windows のバージョンで違う。Windows 11 では拡張子の無い <c>NUL</c> だけで、
/// <c>CON.txt</c> や <c>COM1</c> はふつうのファイルになる。古い Windows では拡張子が付いていても装置になる。
/// </para>
/// <para>
/// 一覧を持たず、名前を開くときに Windows 自身が使う判定 (<c>RtlIsDosDeviceName_U</c>) に尋ねる。
/// 開いて確かめると、シリアルポートなどの装置がそれだけで動くことがあるので、開かない。
/// 実際に開いたときの結果と同じになることを確かめた (Windows 11、16 の名前 × 6 つの形)。
/// </para>
/// </remarks>
internal sealed class ReservedNameException(string path)
    : IOException($"'{path}' is a reserved device name.")
{
    /// <summary>この Windows で装置を指すパスか。ほかの OS では <see langword="false"/>。</summary>
    public static bool IsReserved(string fullPath)
        => OperatingSystem.IsWindows() && RtlIsDosDeviceName_U(fullPath) != 0;

    [DllImport("ntdll.dll", CharSet = CharSet.Unicode)]
    private static extern uint RtlIsDosDeviceName_U(string path);
}
