using System.Globalization;
using System.IO;

namespace Expzip.Archives;

/// <summary>
/// <c>.001</c> <c>.002</c> … に分けられた書庫の断片 (#61)。
/// </summary>
/// <remarks>
/// <para>
/// この形の分割は**形式ではなく、ただ切っただけ**。順に結合すれば元のファイルに
/// 戻る (実物の3断片で、結合したものが元の ZIP と SHA-256 まで一致することを
/// 確かめてある)。どのツールが切ったかを知る必要がない。
/// </para>
/// <para>
/// 7-Zip は <c>.001</c> を <c>Type = Split</c> として扱い、最後まで取り出せる。
/// Expzip もそれに倣い、開くだけでなく取り出しと検査まで通す。
/// </para>
/// <para>
/// 形式ごとの分割 (RAR の <c>.partN</c>、spanned ZIP の <c>.z01</c>) は別物で、
/// 扱わない。前者はそもそも RAR を読めず、後者は終端レコードに分割の番号が入る
/// 別の仕組みのため。結合用のプログラム (exe) を解析する道も採らない。ツールごとの
/// 独自形式で、追随できない。
/// </para>
/// </remarks>
internal static class SplitVolumes
{
    /// <summary>最初の断片の拡張子。7-Zip も Expzip (#59) も3桁。</summary>
    private const string FirstExtension = ".001";

    /// <summary>番号の桁数。</summary>
    private const int DigitCount = 3;

    /// <summary>
    /// 分割された書庫の1つめの断片か。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 続きの断片があることまで見る。名前が <c>.001</c> で終わるだけのファイルは
    /// 珍しくなく (Windows のログにもある)、それを書庫扱いすると、開けないものを
    /// 開こうとして無駄に読みに行くことになる。
    /// </para>
    /// <para>
    /// <c>.002</c> だけを見ないのは、そこが欠けている場合に「分割書庫ではない」と
    /// 判じてしまい、途中で切れた書庫として読みに行くことになるため。少し先まで
    /// 見て、続きがあるなら分割書庫として扱い、欠けていることを言う。
    /// </para>
    /// </remarks>
    public static bool IsFirstVolume(string path)
    {
        if (!path.EndsWith(FirstExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        for (var number = 2; number < 2 + LookAhead; number++)
        {
            if (File.Exists(NumberedPath(path, number)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 断片を順に並べる。番号が飛んでいたら、その手前で止める。
    /// </summary>
    /// <returns>1つめから続いている断片。1つめが無ければ空。</returns>
    public static IReadOnlyList<string> Find(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        var found = new List<string> { path };

        for (var number = 2; ; number++)
        {
            var next = NumberedPath(path, number);

            if (!File.Exists(next))
            {
                break;
            }

            found.Add(next);
        }

        return found;
    }

    /// <summary>
    /// 番号が飛んでいる場合に、最初に欠けている番号を返す。揃っていれば 0。
    /// </summary>
    /// <remarks>
    /// 揃っていない断片をそのまま結合すると、途中で切れた書庫が出来上がる。中身が
    /// 半分だけ取り出せてしまうより、欠けていることを先に言うほうがよい。
    /// 判断は「続きの番号のファイルが、続きより先にあるか」で行う。
    /// </remarks>
    public static int FirstMissing(string path)
    {
        var found = Find(path);

        if (found.Count == 0)
        {
            return 0;
        }

        // 続きが途切れた次の番号から、しばらく先まで見て、実在するなら抜けている
        var next = found.Count + 1;

        for (var number = next; number < next + LookAhead; number++)
        {
            if (File.Exists(NumberedPath(path, number)))
            {
                return next;
            }
        }

        return 0;
    }

    /// <summary>抜けを探すときに、続きの何番先まで見るか。</summary>
    private const int LookAhead = 20;

    /// <summary>断片を繋いだ1本の流れを開く。</summary>
    /// <exception cref="InvalidDataException">断片が揃っていない場合。</exception>
    /// <exception cref="IOException">断片を読めない場合。</exception>
    public static Stream Open(string path)
    {
        if (FirstMissing(path) is var missing and > 0)
        {
            throw new InvalidDataException(
                Localization.Strings.SplitVolumeMissing(NumberedName(path, missing)));
        }

        var found = Find(path);

        if (found.Count == 0)
        {
            throw new FileNotFoundException(null, path);
        }

        return new ConcatStream(found);
    }

    /// <summary>断片の数。</summary>
    public static int Count(string path) => Find(path).Count;

    /// <summary>番号を差し替えたパス。</summary>
    private static string NumberedPath(string path, int number)
        => path[..^FirstExtension.Length]
           + "." + number.ToString(new string('0', DigitCount), CultureInfo.InvariantCulture);

    /// <summary>番号を差し替えた名前 (フォルダを含まない)。</summary>
    private static string NumberedName(string path, int number)
        => Path.GetFileName(NumberedPath(path, number));
}
