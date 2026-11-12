using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace Expzip.Archives;

/// <summary>
/// 分割した RAR の分割ファイルの名前 (#336)。
/// </summary>
/// <remarks>
/// <para>
/// 名前の決まりは 2 通り。新しい形は <c>名前.part1.rar</c>、<c>名前.part2.rar</c> … (桁数は最初の分割ファイルに合わせる)。
/// 古い形は <c>名前.rar</c>、<c>名前.r00</c>、<c>名前.r01</c> … で、<c>r99</c> の次は <c>s00</c>。
/// </para>
/// <para>
/// 続きを探す決まりは、SharpCompress (<c>RarArchiveVolumeFactory</c>) と同じにする。Expzip が見出しを確かめた分割ファイルと、
/// SharpCompress が読む分割ファイルが食い違わないようにするため。開くときは、Expzip が見つけた分割ファイルを流れの並びで渡す。
/// </para>
/// </remarks>
internal static partial class RarVolumes
{
    [GeneratedRegex(@"^(.*\.part)([0-9]+)(\.rar)$", RegexOptions.IgnoreCase)]
    private static partial Regex NewStyle();

    [GeneratedRegex(@"^(.*\.)([r-z{])(ar|[0-9]+)$", RegexOptions.IgnoreCase)]
    private static partial Regex OldStyle();

    /// <summary>
    /// <paramref name="index"/> 番目 (0 から) の分割ファイルのパス。名前の決まりに当たらなければ <see langword="null"/>。
    /// </summary>
    /// <param name="firstVolume">最初の分割ファイル。</param>
    /// <param name="index">何番目か。</param>
    /// <param name="newNaming">使わない。見出しの印ではなく、名前の形で決める (SharpCompress と同じ)。</param>
    public static string? NextVolume(string firstVolume, int index, bool newNaming)
    {
        _ = newNaming;
        var folder = Path.GetDirectoryName(Path.GetFullPath(firstVolume))!;
        var name = Path.GetFileName(firstVolume);

        var match = NewStyle().Match(name);
        if (match.Success)
        {
            var number = (index + 1).ToString(CultureInfo.InvariantCulture).PadLeft(match.Groups[2].Value.Length, '0');
            return Path.Combine(folder, match.Groups[1].Value + number + match.Groups[3].Value);
        }

        match = OldStyle().Match(name);
        if (match.Success)
        {
            if (index == 0)
            {
                return Path.Combine(folder, name);
            }

            var letter = (char)(match.Groups[2].Value[0] + (index - 1) / 100);
            var digits = ((index - 1) % 100).ToString("D2", CultureInfo.InvariantCulture);
            return Path.Combine(folder, match.Groups[1].Value + letter + digits);
        }

        return null;
    }

    /// <summary>
    /// 2 個目以降の分割ファイルなら、最初の分割ファイルのパス (#336 で決めた: 2 個目以降を開いたら最初のものを開き直す)。
    /// 最初の分割ファイルか、分割の名前でなければ <see langword="null"/>。
    /// </summary>
    public static string? FirstVolumeOf(string path)
    {
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var name = Path.GetFileName(path);

        var match = NewStyle().Match(name);
        if (match.Success)
        {
            var digits = match.Groups[2].Value;
            if (int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number > 1)
            {
                return Path.Combine(folder, match.Groups[1].Value + "1".PadLeft(digits.Length, '0') + match.Groups[3].Value);
            }

            return null;
        }

        match = OldStyle().Match(name);
        if (match.Success && match.Groups[3].Value.All(char.IsAsciiDigit))
        {
            // 名前.r00 などの最初は 名前.rar
            return Path.Combine(folder, match.Groups[1].Value + "rar");
        }

        return null;
    }

    /// <summary>古い形の 2 個目以降の分割ファイルの名前か (<c>.r00</c> など)。拡張子からは RAR と分からないので、形式の見分けに使う。</summary>
    public static bool IsOldStyleContinuation(string path)
    {
        var match = OldStyle().Match(Path.GetFileName(path));
        return match.Success && match.Groups[3].Value.All(char.IsAsciiDigit) && match.Groups[3].Value.Length == 2;
    }
}
