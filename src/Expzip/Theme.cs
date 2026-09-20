using System.Windows;
using System.Windows.Media;

namespace Expzip;

/// <summary>
/// ハイコントラストのときに、こちらで決め打ちしている色を引っ込める (#121)。
/// </summary>
/// <remarks>
/// <para>
/// 画面の色には出どころが2通りある。地・文字・枠は Windows から採っており
/// (<see cref="SystemColors"/>)、ハイコントラストにすると一緒に変わる。
/// 保護の緑やルールに合っていない行の地は、こちらで決め打ちしている。
/// **決め打ちのほうは変わらない。**このため、文字色だけが白に変わり、
/// 明るいままの地に白い文字が乗る、という組み合わせができていた。
/// </para>
/// <para>
/// **色を差し替えるだけでは直らない。**行を選ぶと、WPF は地を
/// <see cref="SystemColors.HighlightBrush"/> に変える。ハイコントラスト黒では
/// これが明るい水色になるため、こちらが白い文字を指定すると同じことが起きる。
/// **こちらが文字色を決める処理そのものを止める**のが要る。止めれば、
/// 地に合った文字色を部品が選ぶ。
/// </para>
/// <para>
/// 色での区別は落ちるが、意味は行の名前と印に入っている (#119)。
/// **色を2組に増やすことはしない。**明るい地用と暗い地用を持つと、
/// 同じ意味の色が8つになり、どちらが何かが読み取れなくなる。
/// </para>
/// </remarks>
internal sealed class Theme
{
    private Theme()
    {
    }

    /// <summary>XAML の引き金から見るための1つきり。</summary>
    public static Theme Instance { get; } = new();

    /// <summary>
    /// こちらで決めた色を使ってよいか。ハイコントラストのときは <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// 起動したときのまま変えない。画面の色は <c>{x:Static SystemColors...}</c> で
    /// 取っている箇所も起動時の値のままなので、ここだけ追いかけても揃わない。
    /// 切り替えたときは開き直してもらう。
    /// </remarks>
    public bool UseAccentColors { get; } = !SystemParameters.HighContrast;

    /// <summary>
    /// 決め打ちの色を、ハイコントラストのときだけ Windows の色へ寄せる。
    /// </summary>
    /// <remarks>
    /// 窓を作る前に呼ぶ。<c>StaticResource</c> は読み込んだ時点の値で固まるため。
    /// </remarks>
    public static void Apply(ResourceDictionary resources)
    {
        if (Instance.UseAccentColors)
        {
            return;
        }

        // 一覧や結果の窓で、行の中に置く文字の色。選ばれていない行にしか出ない
        // (選ばれている行では、そもそも色を指定しない) ので、窓の地に合う色でよい
        Set("EncryptedBrush", SystemColors.ControlTextBrush);
        Set("WarningBrush", SystemColors.ControlTextBrush);
        Set("CautionBrush", SystemColors.ControlTextBrush);
        Set("RuleBreakBrush", SystemColors.ControlTextBrush);

        // ルールに合っていない印は残す。**地と文字を組で決めてあるので、
        // 行を選んでも崩れない。**白黒を入れ替えた塊にすれば、どちらの
        // ハイコントラストでも地との差が最大になり、名前の幅も変わらない (#88)
        Set("RuleBreakBackBrush", SystemColors.WindowTextBrush);
        Set("RuleBreakTextBrush", SystemColors.WindowBrush);

        // ツリーの選択を薄い青にしていたのは、既定の濃い青に保護の緑が
        // 沈んだため (#65)。その緑を出さないのだから、差し替える理由も無い
        Set("TreeSelectionBrush", SystemColors.HighlightBrush);
        Set("TreeSelectionTextBrush", SystemColors.HighlightTextBrush);
        Set("TreeInactiveSelectionBrush", SystemColors.InactiveSelectionHighlightBrush);
        Set("TreeInactiveSelectionTextBrush", SystemColors.InactiveSelectionHighlightTextBrush);

        // アドレスバーで指している場所
        Set("HoverBackBrush", SystemColors.HighlightBrush);
        Set("HoverTextBrush", SystemColors.HighlightTextBrush);

        void Set(string key, Brush brush) => resources[key] = brush;
    }
}
