using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Expzip.Archives;

/// <summary>
/// Windows がファイルの出所を記録する Zone.Identifier ストリーム (通称 Mark of the Web) の読み書き。
/// Mac では、同じ役目の com.apple.quarantine を読み書きする (#191)。
/// </summary>
/// <remarks>
/// <para>
/// ネットから落とした書庫には、ブラウザーがこの印を付ける。書庫を開いて中身を
/// 取り出しただけでこの印が消えると、SmartScreen や Office の保護ビューが働かなくなる。
/// そこで、書庫に印が付いていた場合に限り、取り出したファイルにも引き継ぐ (#12)。
/// 自分で作った書庫には印が無いので、その場合は何も付けない。
/// </para>
/// <para>
/// Mac では、印の付いたファイルを開くと Gatekeeper が確かめる。Mac 標準の展開 (アーカイブユーティリティ) も
/// 書庫の印を中身に引き継ぐ。印の中身 (いつ、どのアプリで落としたか) はそのまま写す。
/// </para>
/// </remarks>
internal static class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";

    private const string QuarantineAttribute = "com.apple.quarantine";

    /// <summary>ファイルに付いている印を読む。</summary>
    /// <returns>印の内容。付いていない場合や読めない場合は <see langword="null"/>。</returns>
    public static string? TryRead(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            return TryReadQuarantine(path);
        }

        try
        {
            // NTFS 以外や印の無いファイルでは開けない。存在確認を別に行わないのは、
            // 副ストリームに対する存在確認が環境によって当てにならないため。
            using var stream = new FileStream(
                path + StreamSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);

            var text = reader.ReadToEnd();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>読み取った印を別のファイルに引き継ぐ。</summary>
    /// <param name="path">印を付ける先のファイル。</param>
    /// <param name="zone"><see cref="TryRead"/> の戻り値。<see langword="null"/> なら何もしない。</param>
    /// <returns>付けられた場合は true。</returns>
    public static bool TryApply(string path, string? zone)
    {
        if (zone is null)
        {
            return false;
        }

        if (OperatingSystem.IsMacOS())
        {
            return TryApplyQuarantine(path, zone);
        }

        try
        {
            using var stream = new FileStream(
                path + StreamSuffix, FileMode.Create, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream);

            writer.Write(zone);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            // 印を付けられなくても取り出し自体は済んでいる。
            // 一時フォルダが NTFS でない場合などにここへ来る。
            return false;
        }
    }

    /// <summary>
    /// 書庫を作業用のファイルで置き換えたあと、置き換える前に付いていた印を付け直す (#211)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 置き換えると、Windows の副ストリームも Mac の拡張属性も、元のファイルと一緒に消える。
    /// 付け直さないと、ネットから落とした書庫に 1 回書き込んだだけで印の無い書庫になり、
    /// そのあと取り出したファイルにも印が引き継がれない。
    /// </para>
    /// <para>
    /// 印が残っているときは付け直さない。付けると書庫の更新日時が変わるため。
    /// 書庫を読み直す前に呼ぶので、外での書き換え (#64) と取り違えることは無い。
    /// </para>
    /// </remarks>
    /// <param name="path">置き換えた書庫。</param>
    /// <param name="zone">置き換える前に <see cref="TryRead"/> で読んだ印。</param>
    public static void Restore(string path, string? zone)
    {
        if (zone is not null && TryRead(path) != zone)
        {
            TryApply(path, zone);
        }
    }

    /// <summary>Mac の印 (拡張属性) を読む。付いていない場合や読めない場合は <see langword="null"/>。</summary>
    private static string? TryReadQuarantine(string path)
    {
        try
        {
            var size = getxattr(path, QuarantineAttribute, null, 0, 0, 0);
            if (size <= 0)
            {
                return null;
            }

            var buffer = new byte[size];
            var read = getxattr(path, QuarantineAttribute, buffer, (nuint)buffer.Length, 0, 0);
            if (read <= 0)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, (int)read).TrimEnd('\0');
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Mac の印 (拡張属性) を付ける。付けられなくても取り出し自体は済んでいる。</summary>
    private static bool TryApplyQuarantine(string path, string zone)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(zone);
            return setxattr(path, QuarantineAttribute, bytes, (nuint)bytes.Length, 0, 0) == 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    // Mac の拡張属性の読み書き (man getxattr)。Windows では呼ばない
    [DllImport("libc", SetLastError = true)]
    private static extern nint getxattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[]? value, nuint size, uint position, int options);

    [DllImport("libc", SetLastError = true)]
    private static extern int setxattr(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        byte[] value, nuint size, uint position, int options);
}
