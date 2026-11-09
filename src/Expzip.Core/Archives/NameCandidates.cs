using System.Text;

namespace Expzip.Archives;

/// <summary>名前の読み方の候補 1 つ (#335)。</summary>
/// <param name="CodePage">文字コードの番号。</param>
/// <param name="Common">よく使われるもの。メニューの上に並べる。ほかは「その他」にまとめる。</param>
internal sealed record NameCandidate(int CodePage, bool Common);

/// <summary>
/// 書庫の名前を読める文字コードを選ぶ (#335)。
/// </summary>
/// <remarks>
/// <para>
/// 開いたときに覚えた名前のバイト列 (<see cref="ArchiveContents.LegacyNameBytes"/>) を、候補の文字コードそれぞれで
/// 読めないバイトを例外にする読み方で読む。読めないバイトがあるもの、制御文字が出るものは外す。
/// 文字コードごとに、最初に読めない名前が出たところで次へ移る。
/// </para>
/// <para>
/// 候補に入れないもの: UTF-16 / UTF-32 / UTF-7、EBCDIC (ASCII の名前が別の文字になる)、
/// 切り替えの記号 (ESC) を使うもの (ISO-2022-JP、ISO-2022-KR、HZ。記号は読んだあとに消えるので紛らわしい)。
/// </para>
/// </remarks>
internal static class NameCandidates
{
    /// <summary>よく使うもの。この順にメニューの上に並べる。</summary>
    public static IReadOnlyList<int> CommonCodePages { get; } =
    [
        932,    // 日本語 (Shift_JIS)
        65001,  // UTF-8
        936,    // 中国語 (簡体字)
        950,    // 中国語 (繁体字)
        949,    // 韓国語
        437,    // 西ヨーロッパ (DOS)
        1252,   // 西ヨーロッパ (Windows)
        866,    // ロシア語 (DOS)
        51932,  // 日本語 (EUC-JP)
    ];

    private static readonly HashSet<int> Excluded =
    [
        1200, 1201, 12000, 12001, 65000,            // UTF-16 / UTF-32 / UTF-7
        50220, 50221, 50222, 50225, 50227, 50229,   // ISO-2022 (ESC で切り替える)
        52936,                                      // HZ (~{ で切り替える)
    ];

    /// <summary>読める文字コードの候補。よく使うものが先で、残りは番号の順。</summary>
    public static List<NameCandidate> Find(IReadOnlyList<byte[]> names)
    {
        var result = new List<NameCandidate>();
        if (names.Count == 0)
        {
            return result;
        }

        foreach (var codePage in CommonCodePages)
        {
            if (Reads(codePage, names))
            {
                result.Add(new NameCandidate(codePage, Common: true));
            }
        }

        foreach (var codePage in AllCodePages().Where(c => !CommonCodePages.Contains(c)).Order())
        {
            if (Reads(codePage, names))
            {
                result.Add(new NameCandidate(codePage, Common: false));
            }
        }

        return result;
    }

    /// <summary>この PC で使える文字コードの番号。</summary>
    private static IEnumerable<int> AllCodePages()
        => Encoding.GetEncodings().Select(static e => e.CodePage)
            .Concat(CodePagesEncodingProvider.Instance.GetEncodings().Select(static e => e.CodePage))
            .Distinct()
            .Where(static c => !Excluded.Contains(c));

    /// <summary>すべての名前を、読めないバイトも制御文字も無く読めるか。</summary>
    internal static bool Reads(int codePage, IReadOnlyList<byte[]> names)
    {
        Encoding encoding;
        try
        {
            encoding = Encoding.GetEncoding(codePage, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return false;
        }

        // ASCII の名前がそのまま読めないもの (EBCDIC など) は外す
        if (!SafeGetString(encoding, "a.txt"u8.ToArray(), out var ascii) || ascii != "a.txt")
        {
            return false;
        }

        foreach (var name in names)
        {
            if (!SafeGetString(encoding, name, out var text) || HasControl(text))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SafeGetString(Encoding encoding, byte[] bytes, out string text)
    {
        try
        {
            text = encoding.GetString(bytes);
            return true;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or ArgumentException)
        {
            text = string.Empty;
            return false;
        }
    }

    private static bool HasControl(string text)
    {
        foreach (var c in text)
        {
            if (c < 0x20 || c is >= '\u007F' and <= '\u009F' || c == '�')
            {
                return true;
            }
        }

        return false;
    }
}
