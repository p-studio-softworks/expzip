using System.IO;

namespace Expzip.Archives;

/// <summary>出所の印を読み書きする方法 (<see cref="MarkOfTheWeb.Override"/>)。</summary>
internal interface IOriginMark
{
    /// <summary>ファイルに付いている印を読む。付いていない場合や読めない場合は <see langword="null"/>。</summary>
    string? TryRead(string path);

    /// <summary>印を付ける。付けられた場合は true。</summary>
    bool TryApply(string path, string zone);

    /// <summary>インターネットから入手したことを表す印の見本。<see cref="TryApply"/> にそのまま渡せる形。</summary>
    string Internet { get; }
}

/// <summary>
/// Windows がファイルの出所を記録する Zone.Identifier ストリーム (通称 Mark of the Web) の読み書き。
/// </summary>
/// <remarks>
/// ネットから落とした書庫には、ブラウザーがこの印を付ける。書庫を開いて中身を
/// 取り出しただけでこの印が消えると、SmartScreen や Office の保護ビューが働かなくなる。
/// そこで、書庫に印が付いていた場合に限り、取り出したファイルにも引き継ぐ (#12)。
/// 自分で作った書庫には印が無いので、その場合は何も付けない。
/// </remarks>
internal static class MarkOfTheWeb
{
    private const string StreamSuffix = ":Zone.Identifier";

    /// <summary>
    /// 印の読み書きの方法。画面の側が差し替えられる。<see langword="null"/> なら Zone.Identifier ストリーム。
    /// </summary>
    internal static IOriginMark? Override { get; set; }

    /// <summary>
    /// インターネットから入手したことを表す印の見本。インターネットから入手した書庫を展開するときと
    /// 同じ印を付けたいときに使う。
    /// </summary>
    public static string Internet => Override?.Internet ?? "[ZoneTransfer]\r\nZoneId=3\r\n";

    /// <summary>ファイルに付いている印を読む。</summary>
    /// <returns>印の内容。付いていない場合や読めない場合は <see langword="null"/>。</returns>
    public static string? TryRead(string path)
    {
        if (Override is { } mark)
        {
            return mark.TryRead(path);
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

        if (Override is { } mark)
        {
            return mark.TryApply(path, zone);
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
    /// 置き換えると、印は元のファイルと一緒に消える。
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
}
