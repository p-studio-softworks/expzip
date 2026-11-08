using System.IO;
using System.Text;

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
    internal const string StreamSuffix = ":Zone.Identifier";

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
    /// <remarks>
    /// <para>
    /// 判定と置き換えはここ 1 か所で行う (#325)。呼び出し側は、読めない印の原文を受け取らない。
    /// 読めない印をそのまま写すと、Windows が印として扱わないことがあり、警告が消える。
    /// </para>
    /// <para>
    /// 印が無い、その場所が印に対応していないとき以外の失敗は、インターネットから来たものとして扱う。
    /// 別のプログラムが開いたままにして、印を読ませないようにできるため。
    /// </para>
    /// <para>
    /// バイトのまま扱う (<see cref="Encoding.Latin1"/>)。正しい印を、1 バイトも変えずに写すため。
    /// </para>
    /// </remarks>
    /// <returns>
    /// 付いていなければ <see langword="null"/>。読める印なら原文。読めない印なら <see cref="Internet"/>。
    /// </returns>
    public static string? TryRead(string path)
    {
        // 差し替えた側の印は、Windows の規則で判定しない。情報が消えるため
        if (Override is { } mark)
        {
            return mark.TryRead(path);
        }

        try
        {
            // NTFS 以外や印の無いファイルでは開けない。存在確認を別に行わないのは、
            // 副ストリームに対する存在確認が環境によって当てにならないため。
            using var stream = new FileStream(
                path + StreamSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            // 上限より 1 バイト多く読む。ちょうどの印と超えた印を見分けるため
            var buffer = new byte[MaxMarkLength + 1];
            var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);

            if (length > MaxMarkLength)
            {
                return Internet;
            }

            var text = Encoding.Latin1.GetString(buffer, 0, length);
            return ParseZone(text) >= 0 ? text : Internet;
        }
        catch (Exception ex) when (IsNoMark(ex))
        {
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Internet;
        }
    }

    /// <summary>印が無いか、その場所が印に対応していないことを示す失敗か (exFAT など)。</summary>
    private static bool IsNoMark(Exception ex)
        => ex is FileNotFoundException or DirectoryNotFoundException or NotSupportedException or ArgumentException
           || ex is IOException { HResult: InvalidName or InvalidParameter or NotSupportedError };

    /// <summary>
    /// 書庫の印を読む。分割された書庫は、すべての分割ファイルの中でいちばん警戒度の高い印を使う (#321)。
    /// </summary>
    /// <remarks>
    /// USB メモリーを通ると、一部の分割ファイルだけ印が消えることがある。1 個でも付いていれば、付いているものとして扱う。
    /// </remarks>
    public static string? TryReadArchive(string path)
        => SplitVolumes.IsFirstVolume(path)
            ? Strongest(SplitVolumes.Find(path).Select(TryRead))
            : TryRead(path);

    /// <summary>いちばん警戒度の高い印。同じ強さなら、見本より本物の印を残す。</summary>
    public static string? Strongest(IEnumerable<string?> zones)
    {
        string? best = null;

        foreach (var zone in zones)
        {
            if (zone is null)
            {
                continue;
            }

            if (best is null || ZoneOf(zone) > ZoneOf(best) || (ZoneOf(zone) == ZoneOf(best) && best == Internet))
            {
                best = zone;
            }
        }

        return best;
    }

    /// <summary>
    /// インターネットから来たことを示す印か。読めない形の印も、安全側に倒してそう扱う。
    /// </summary>
    public static bool IsFromInternet(string zone) => ZoneOf(zone) >= InternetZone;

    /// <summary>印に書かれた区分の番号。読めなければインターネットと同じ扱いにする。</summary>
    private static int ZoneOf(string zone) => ParseZone(zone) is >= 0 and var number ? number : InternetZone;

    /// <summary>
    /// 印の区分の番号を読む。読めなければ -1。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>確かな書き方だけを読める印とする</b> (#325)。Windows の中で印を読むものは 1 つとは限らないので、広く読まない。
    /// こちらが読めると判定したのに Windows で効かないと、分割された書庫で有効な印を捨ててしまう。
    /// 読めないと判定したものはインターネットの印に置き換わるので、安全側になる。
    /// </para>
    /// <para>
    /// Windows の <c>IZoneIdentifier</c> は、節の中に <c>名前=値</c> でない行があると印全体を読めなくなり、
    /// 名前が空の行では読んだプロセスごと落ちることがある。行頭に空白がある節も節として読む。
    /// <c>ZoneId</c> より前の行の日本語などは、文字によって次の行を読めなくする。そこで、すべての行の書き方を調べる。
    /// </para>
    /// <para>
    /// エクスプローラーが ZIP から取り出したファイルに書く印は、<c>ZoneId</c> の後の <c>ReferrerUrl</c> に書庫のパスを
    /// ANSI コードページのまま書き、最後に NUL を 1 個付ける。これは読める印として残す。<c>ZoneId</c> より後の行の文字は、
    /// 番号の読み方に影響しなかった。
    /// </para>
    /// <para>
    /// 規則は <c>tools/sfx/zone.h</c> と同じ。読める印は次のすべてを満たす。
    /// </para>
    /// <list type="bullet">
    /// <item>NUL は最後の 1 バイトだけ。CR は LF の前だけ</item>
    /// <item>どの行も、空行、<c>[節の名前]</c>、<c>名前=値</c> のどれか。節の行と名前は印字できる ASCII だけ。節の行は空白を含まない。名前は空白で始まらず、空でもない</item>
    /// <item>値は制御文字を含まない。ASCII 以外のバイトを書けるのは、<c>ZoneId</c> の行より後の値だけ</item>
    /// <item><c>[ZoneTransfer]</c> の節が大文字小文字まで同じで 1 つだけあり、その中に <c>ZoneId=数字 1 桁 (0 から 4)</c> が 1 行だけある。大文字小文字や空白が違う <c>ZoneId</c> の行は読めない</item>
    /// </list>
    /// </remarks>
    internal static int ParseZone(string zone)
    {
        var length = zone.Length > 0 && zone[^1] == '\0' ? zone.Length - 1 : zone.Length;
        var inSection = false;
        var sections = 0;
        var found = -1;
        var at = 0;

        while (at < length)
        {
            var start = at;

            while (at < length && zone[at] != '\n')
            {
                if (zone[at] == '\0' || (zone[at] == '\r' && !(at + 1 < length && zone[at + 1] == '\n')))
                {
                    return -1;
                }

                at++;
            }

            var end = at > start && zone[at - 1] == '\r' ? at - 1 : at;
            at++;

            var line = zone[start..end];

            if (line.Length == 0)
            {
                continue;
            }

            if (line[0] == '[')
            {
                // 内側の空白は Windows が除いて読むので、[ ZoneTransfer ] も同じ節になる
                if (line.IndexOf(']', StringComparison.Ordinal) != line.Length - 1 || !IsPrintable(line)
                    || line.Contains(' ', StringComparison.Ordinal))
                {
                    return -1;
                }

                inSection = false;

                if (line.Equals(SectionName, StringComparison.OrdinalIgnoreCase))
                {
                    if (line != SectionName || ++sections > 1)
                    {
                        return -1;
                    }

                    inSection = true;
                }

                continue;
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || line[0] == ' ' || !IsPrintable(line[..eq]))
            {
                return -1;
            }

            for (var i = eq + 1; i < line.Length; i++)
            {
                if (line[i] < ' ' || line[i] == '\x7F' || (line[i] > '\x7F' && found < 0))
                {
                    return -1;
                }
            }

            if (!inSection || !line[..eq].Trim(' ').Equals(ZoneKey, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (found >= 0 || line[..eq] != ZoneKey || line.Length != eq + 2 || line[eq + 1] is < '0' or > '4')
            {
                return -1;
            }

            found = line[eq + 1] - '0';
        }

        return sections == 1 ? found : -1;
    }

    /// <summary>印字できる ASCII だけか。</summary>
    private static bool IsPrintable(string text)
    {
        foreach (var c in text)
        {
            if (c is < ' ' or > '~')
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 付いている印の原文が <paramref name="zone"/> と同じか。
    /// </summary>
    /// <remarks>
    /// <see cref="TryRead"/> は読めない印を見本に置き換えて返すので、書き直しが要るかどうかの比較には使えない (#325)。
    /// </remarks>
    internal static bool HasExactly(string path, string zone)
    {
        if (Override is { } mark)
        {
            return mark.TryRead(path) == zone;
        }

        try
        {
            using var stream = new FileStream(
                path + StreamSuffix, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            var buffer = new byte[MaxMarkLength + 1];
            var length = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            return length <= MaxMarkLength && Encoding.Latin1.GetString(buffer, 0, length) == zone;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or ArgumentException)
        {
            return false;
        }
    }

    private const string SectionName = "[ZoneTransfer]";
    private const string ZoneKey = "ZoneId";
    private const int InternetZone = 3;

    /// <summary>
    /// 読む印の大きさの上限。超えた印はインターネットの印に置き換える。
    /// </summary>
    /// <remarks>
    /// 印はファイルごとに書くので、大きな印を許すとファイルの数だけ増える。展開後の大きさには現れない。
    /// 自己解凍の取り出し部と結合用のプログラム (<c>tools/sfx/zone.h</c>) と同じ値にする。
    /// </remarks>
    internal const int MaxMarkLength = 4096;

    private const int InvalidName = unchecked((int)0x8007007B);
    private const int InvalidParameter = unchecked((int)0x80070057);
    private const int NotSupportedError = unchecked((int)0x80070032);

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
            // 書いている途中の一時ファイル (AtomicFile) にも付ける。本体を削除の権利付きで開いているので、
            // ここで削除を許さないと共有違反になり、印が付かない
            using var stream = new FileStream(
                path + StreamSuffix, FileMode.Create, FileAccess.Write, FileShare.Delete);
            stream.Write(Encoding.Latin1.GetBytes(zone));
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
}
