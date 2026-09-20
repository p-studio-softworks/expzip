using System.Globalization;

namespace Expzip.Localization;

/// <summary>画面に出す言語 (#23)。</summary>
internal enum UiLanguage
{
    /// <summary>日本語。</summary>
    Japanese,

    /// <summary>英語。</summary>
    English,
}

/// <summary>
/// 画面に出す文言 (#23、仕様書 7章)。
/// </summary>
/// <remarks>
/// <para>
/// .resx とサテライトアセンブリではなく、言語ごとの文字列を並べた表にしている。
/// 理由は3つ。
/// </para>
/// <list type="number">
///   <item>
///     単一ファイルとして発行するため (仕様書 3章)。サテライトを増やさなければ、
///     発行物が exe 1つであることを言語対応で崩さずに済む。
///   </item>
///   <item>
///     翻訳漏れが起きない。片方だけ書くことが文法上できないため、
///     キーはあるが訳が無い、という状態にならない。
///   </item>
///   <item>
///     件数や名前を差し込む文言をメソッドとして書ける。語順や複数形の扱いが
///     言語ごとに違っても、<c>string.Format</c> の番号に頼らず素直に書ける。
///   </item>
/// </list>
/// <para>
/// 対応するのは日本語と英語の2つ。増やす場合は <see cref="UiLanguage"/> と
/// <see cref="Pick"/> を広げる。3言語を超えるなら、この形は割に合わなくなる。
/// </para>
/// </remarks>
internal static partial class Strings
{
    /// <summary>設定ファイルで「OS に合わせる」を表す値。</summary>
    public const string AutoPreference = "auto";

    /// <summary>
    /// いま使っている言語。
    /// </summary>
    /// <remarks>
    /// 変えても、既に画面に出ている文字は入れ替わらない。貼り替えは
    /// <c>MainWindow.ApplyLanguage</c> が行う。切り替えの入口は設定メニューの1箇所
    /// しかないため、変更を知らせる仕組みは置いていない。
    /// </remarks>
    public static UiLanguage Language { get; set; } = FromSystem();

    /// <summary>
    /// 設定ファイルの値から言語を決める。
    /// </summary>
    /// <param name="preference"><c>auto</c> / <c>ja</c> / <c>en</c>。</param>
    /// <remarks>
    /// 設定ファイルは利用者が直接編集できるので、知らない値が入っていても
    /// OS に合わせて動かす。ここで例外にすると設定全体が使えなくなる。
    /// </remarks>
    public static UiLanguage Resolve(string? preference) => preference?.ToLowerInvariant() switch
    {
        "ja" => UiLanguage.Japanese,
        "en" => UiLanguage.English,
        _ => FromSystem(),
    };

    /// <summary>設定ファイルに書き出す値。</summary>
    public static string ToSettingValue(UiLanguage language)
        => language == UiLanguage.Japanese ? "ja" : "en";

    /// <summary>
    /// Windows の表示言語に合わせる。日本語以外はすべて英語で出す。
    /// </summary>
    private static UiLanguage FromSystem()
        => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("ja", StringComparison.OrdinalIgnoreCase)
            ? UiLanguage.Japanese
            : UiLanguage.English;

    /// <summary>
    /// 言語に応じてどちらかを返す。
    /// </summary>
    /// <remarks>
    /// 呼ぶたびに両方の文字列が組み立てられる。画面に出す文言の量では気にならないが、
    /// 1件ごとに呼ぶような場所では、ループの外で1回だけ呼ぶようにする。
    /// </remarks>
    private static string Pick(string japanese, string english)
        => Language == UiLanguage.Japanese ? japanese : english;

    /// <summary>英語の複数形。日本語側では使わない。</summary>
    /// <summary>
    /// 補足を丸括弧でくくる。補足が無ければ何も付けない。
    /// </summary>
    /// <remarks>
    /// 検査の報告 (#53) のように、同じ文言に補足が付く場合と付かない場合がある
    /// ところで使う。空の括弧が残らないようにするため。
    /// </remarks>
    private static string Paren(string? detail)
        => string.IsNullOrEmpty(detail) ? string.Empty : $" ({detail})";

    private static string Plural(long count, string singular, string plural)
        => count == 1 ? singular : plural;
}
