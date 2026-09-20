using System.Collections;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Expzip;

/// <summary>
/// 意味を表す色を、地の明るさに合わせて選ぶ (#121、#122)。
/// </summary>
/// <remarks>
/// <para>
/// 画面の色には出どころが2通りある。地・文字・枠は Fluent のテーマから採っており
/// (<c>ThemeMode="System"</c>)、Windows のダークモードとハイコントラストの
/// どちらにも追随する。保護の緑やルールに合っていない行の地は、こちらで決めている。
/// **こちらの色は勝手には変わらない。**放っておくと地だけが暗くなり、
/// 暗い地に暗い色が乗る、という組み合わせができる。
/// </para>
/// <para>
/// **どちらの組を使うかは、地の明るさそのものから決める** (#122)。Windows の
/// 設定を読みに行かないのは、Fluent が実際に何を選んだかを見るほうが確かなため。
/// ハイコントラストの配色は「黒」「白」の2つではなく、暗いものも明るいものもある
/// (水生は <c>#202020</c>、砂漠は <c>#FFFAEF</c>)。地を測れば、どれであっても外さない。
/// </para>
/// <para>
/// **色は筆ごと差し替える。塗り替えることはできない。**資源辞書に入れた筆には
/// その場で封がされ (<see cref="Freezable.IsFrozen"/>)、以後その色は変えられない。
/// 塗り替えようとすると例外になり、**切り替えの処理がそこで止まる。**
/// </para>
/// <para>
/// 差し替える形になるので、**受け取る側は掴みっぱなしにしない。**XAML では
/// <c>DynamicResource</c>、コードでは <c>SetResourceReference</c> を使う。
/// <c>StaticResource</c> で読んだ所や、筆そのものを代入した所は、
/// 差し替えても古い色のまま残る。
/// </para>
/// <para>
/// **ハイコントラストでは、文字色だけを決めている色を付けない** (#121)。
/// 地がそのとき次第なので組にできない。**色を差し替えるだけでは直らない**のがここで、
/// 行を選ぶと地が入れ替わり、こちらが指定した文字色と食い違う。
/// 付けなければ、地に合った文字色を部品が選ぶ。色での区別は落ちるが、
/// 意味は行の名前と印に入っている (#119)。
/// </para>
/// <para>
/// 地と文字を**組で**決めてあるもの (ルールに合っていない印、アドレスバーで
/// 指している場所) は、ハイコントラストでも残す。組で決めてあれば崩れない。
/// </para>
/// </remarks>
internal sealed class Theme : INotifyPropertyChanged
{
    /// <summary>暗いと見なす明るさの境目。0 が黒、1 が白。</summary>
    private const double DarkBelow = 0.5;


    /// <summary>
    /// Fluent が地に使っている色。ここの明るさで、どちらの組を使うかを決める。
    /// </summary>
    private const string BackgroundKey = "SolidBackgroundFillColorBaseBrush";

    private static ResourceDictionary? resources;

    private bool useAccentColors = true;

    private Theme()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>XAML の引き金から見るための1つきり。</summary>
    public static Theme Instance { get; } = new();

    /// <summary>
    /// こちらで決めた色を使ってよいか。ハイコントラストのときは <see langword="false"/>。
    /// </summary>
    /// <remarks>
    /// **ここで見るのは「色を付けるかどうか」で、どの色かではない。**
    /// ハイコントラストで必要なのは、色を別の色に差し替えることではなく、
    /// **こちらが文字色を決める処理そのものを止めること** (#121)。
    /// 行を選ぶと地が <see cref="SystemColors.HighlightBrush"/> に変わるため、
    /// 窓の地に合う色を指定してあっても、その行では食い違う。
    /// 指定しなければ、地に合った文字色を部品が選ぶ。
    /// </remarks>
    public bool UseAccentColors
    {
        get => useAccentColors;
        private set
        {
            if (useAccentColors == value)
            {
                return;
            }

            useAccentColors = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UseAccentColors)));
        }
    }

    /// <summary>
    /// 地の明るさに合う色を入れ、以後の切り替えにも追いつくようにする。
    /// </summary>
    /// <remarks>
    /// **窓を作る前に呼ぶ。**最初の窓が読み込まれる時点で、キーが揃っている必要がある。
    /// </remarks>
    public static void Start(ResourceDictionary applicationResources)
    {
        resources = applicationResources;
        Apply();

        // Windows の設定が変わったとき。Fluent は開いている窓にも色を配り直すので、
        // **こちらも配り直さないと、片方だけ入れ替わった画面になる**
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
    }

    /// <summary>いま暗い地かどうか。</summary>
    public static bool IsDark => resources is not null && Lightness(resources) < DarkBelow;

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.Color))
        {
            return;
        }

        // 知らせは UI とは別の筋から来るので、UI の側へ渡してから選び直す。
        // **1回の切り替えで何発も来る** (実測で9発)。何度呼ばれても結果は同じ
        Application.Current?.Dispatcher.BeginInvoke(Apply);
    }

    private static void Apply()
    {
        if (resources is null)
        {
            return;
        }

        if (resources[IsDark ? "DarkColors" : "LightColors"] is ResourceDictionary colors)
        {
            foreach (DictionaryEntry entry in colors)
            {
                if (entry.Key is string key && entry.Value is SolidColorBrush brush)
                {
                    Paint(key, brush.Color);
                }
            }
        }

        Instance.UseAccentColors = !SystemParameters.HighContrast;

        if (Instance.UseAccentColors)
        {
            return;
        }

        // 一覧や結果の窓で、行の中に置く文字の色。選ばれていない行にしか出ない
        // (選ばれている行では、そもそも色を指定しない) ので、窓の地に合う色でよい
        Paint("EncryptedBrush", SystemColors.ControlTextColor);
        Paint("WarningBrush", SystemColors.ControlTextColor);
        Paint("CautionBrush", SystemColors.ControlTextColor);
        Paint("RuleBreakBrush", SystemColors.ControlTextColor);

        // ルールに合っていない印は残す。**地と文字を組で決めてあるので、
        // 行を選んでも崩れない。**白黒を入れ替えた塊にすれば、どの配色でも
        // 地との差が最大になり、名前の幅も変わらない (#88)
        Paint("RuleBreakBackBrush", SystemColors.WindowTextColor);
        Paint("RuleBreakTextBrush", SystemColors.WindowColor);

        // アドレスバーで指している場所
        Paint("HoverBackBrush", SystemColors.HighlightColor);
        Paint("HoverTextBrush", SystemColors.HighlightTextColor);
    }

    /// <summary>その名前の筆を、この色のものに差し替える。</summary>
    private static void Paint(string key, Color color)
        => resources![key] = new SolidColorBrush(color);

    private static double Lightness(ResourceDictionary from)
    {
        if (from[BackgroundKey] is not SolidColorBrush brush)
        {
            // 取れなければ明るい地として扱う。これまでの見た目がそのまま出る
            return 1.0;
        }

        var color = brush.Color;
        return (0.2126 * Channel(color.R)) + (0.7152 * Channel(color.G)) + (0.0722 * Channel(color.B));
    }

    /// <summary>sRGB の1色を、明るさの計算に使う値へ直す。</summary>
    private static double Channel(byte value)
    {
        var scaled = value / 255.0;
        return scaled <= 0.03928
            ? scaled / 12.92
            : Math.Pow((scaled + 0.055) / 1.055, 2.4);
    }
}
