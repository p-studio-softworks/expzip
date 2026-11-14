namespace Expzip.Inspection;

/// <summary>
/// 文字の向きを変える制御文字 (Unicode の Bidi_Control の 12 文字)。
/// </summary>
/// <remarks>
/// <para>
/// U+202E (右横書き) を名前の途中に入れると、そこから先が逆さに表示される。
/// <c>写真gpj.exe</c> を <c>写真exe.jpg</c> のように見せる古典的な細工で、一覧の上では画像にしか見えない。
/// </para>
/// <para>
/// 書庫の検査 (#53) と、分割で作る結合用のファイルの名前 (#373) の両方がここを使う。一覧を 1 か所にして、片方だけを直すことを防ぐ。
/// アラビア語やヘブライ語の文字そのものは含めない。並びが変わるのはその文字だけで、ラテン文字の拡張子を偽装できないため。
/// ゼロ幅の文字や U+2028 / U+2029 も並びは変えないので含めない。
/// </para>
/// </remarks>
internal static class BidiControl
{
    /// <summary>文字の向きを変える制御文字か。</summary>
    public static bool Is(char c)
        => c is >= (char)0x202A and <= (char)0x202E   // LRE, RLE, PDF, LRO, RLO
            or >= (char)0x2066 and <= (char)0x2069    // LRI, RLI, FSI, PDI
            or (char)0x200E or (char)0x200F           // LRM, RLM
            or (char)0x061C;                          // ALM

    /// <summary>文字の向きを変える制御文字を含むか。</summary>
    public static bool Contains(string text) => text.Any(Is);

    /// <summary>文字の向きを変える制御文字を除く。ほかの文字はそのまま残す。</summary>
    public static string Strip(string text)
        => Contains(text) ? string.Concat(text.Where(static c => !Is(c))) : text;
}
