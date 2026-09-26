using System.Text.RegularExpressions;
using Expzip.Localization;

namespace Expzip.Ai;

/// <summary>書庫の作り方の決まりごと、その1つ (#25)。</summary>
/// <remarks>
/// <para>
/// **決められる形をあらかじめ絞ってある。**AI に自由な文章で書かせると、
/// 人が読む分にはもっともらしくても、あとで書庫に当てて違反を見つけること
/// (#27) ができない。当てられない決まりは、
/// 提案としても役に立たない。
/// </para>
/// <para>
/// 仕様書 10.2節が挙げている例 (「ルート直下に README.txt が必須」
/// 「フォルダ名は `日付_案件名` 形式」「`.tmp`/`.bak` は含めない」) が、
/// この5つで過不足なく書ける。足りなくなったら増やす。
/// </para>
/// </remarks>
/// <param name="Kind">どの種類の決まりか。</param>
/// <param name="Scope">どこに当てはめるか。</param>
/// <param name="Value">名前・拡張子・正規表現。種類によって意味が変わる。</param>
/// <param name="Description">人に見せる説明。AI が書く。</param>
/// <param name="Evidence">お手本のどこから読み取ったか。AI が書く。</param>
internal sealed record ArchiveRule(
    RuleKind Kind, RuleScope Scope, string Value, string Description, string Evidence)
{
    /// <summary>この決まりを最後に決めたのは誰か (#26)。</summary>
    /// <remarks>
    /// AI が挙げたものと、人が入れた・直したものを見分けられるようにする。
    /// **提案であることを画面で明示する**ために要る (仕様書 10.5節)。
    /// </remarks>
    public RuleSource Source { get; init; } = RuleSource.Ai;

    /// <summary>当てはめるときに使う正規表現。<see cref="RuleKind.NamePattern"/> のみ。</summary>
    /// <remarks>
    /// 組み立ては1回だけにする。当てるたびに作り直すと、項目数の分だけ掛かる。
    /// </remarks>
    public Regex? Pattern { get; private init; }

    /// <summary>
    /// 当てる場所。項目を**含むフォルダ**の書庫内パスの形 (#81)。空なら書庫全体。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **「どこに」と「どんな形か」を分けるために足した。**これが無いと、AI は
    /// 両方を1本の正規表現に繋げて書く。実地では
    /// <c>^[^/]+/libraries/usb_host_[a-z0-9_]+$</c> のようなものが返ってきた。
    /// 繋がったままでは「全項目がこの形」としか読めず、libraries の外にある
    /// 33 個のフォルダが違反になって捨てられていた。
    /// </para>
    /// <para>
    /// 分ければ「<c>^[^/]+/libraries$</c> の中では、名前が
    /// <c>usb_host_[a-z0-9_]+</c> の形」と読める。**当てる先がそこだけに絞られる。**
    /// </para>
    /// </remarks>
    public string Where { get; private init; } = string.Empty;

    /// <summary>場所の正規表現。<see cref="Where"/> が空なら <see langword="null"/>。</summary>
    public Regex? WherePattern { get; private init; }

    /// <summary>
    /// 数え上げた場所の数 (#172)。<see cref="RuleSource.Filled"/> のみ。
    /// 持っていなければ 0。
    /// </summary>
    /// <remarks>
    /// 根拠の文を出すときに組み立て直すために持つ。数ではなく出来上がった文を
    /// 持つと、あとで言語を切り替えても書いたときの言葉のまま残る。
    /// </remarks>
    public int Places { get; init; }

    /// <summary>種類の名前。画面に出す。</summary>
    public string KindText => Kind switch
    {
        RuleKind.RequiredEntry => Strings.RuleKindRequiredEntry,
        RuleKind.RequiredFolder => Strings.RuleKindRequiredFolder,
        RuleKind.ForbiddenExtension => Strings.RuleKindForbiddenExtension,
        RuleKind.ForbiddenName => Strings.RuleKindForbiddenName,
        RuleKind.RequiredPattern => Strings.RuleKindRequiredPattern,
        _ => Strings.RuleKindNamePattern,
    };

    /// <summary>人に見せる説明。画面に出す (#172)。</summary>
    /// <remarks>
    /// <para>
    /// **Expzip が補った分は、出すときに組み立てる。**こちらが書く文なので、
    /// そのときの言語で書ける。作ったときの文を持ち回すと、言語を切り替えても
    /// 書いたときの言葉のまま残る。言い方を直したときも、古い文が残り続ける。
    /// </para>
    /// <para>
    /// **AI と人が書いた分は、書かれたとおりに出す。**AI 自身の言葉をこちらで
    /// 訳すと、AI が言っていないことを AI 名義にすることになる (#84、#89)。
    /// </para>
    /// <para>
    /// ファイルを手で直して補った分に自分の言葉を書きたい場合は、出どころ
    /// (<c>source</c>) を <c>hand</c> にする。そのままでは組み立て直される。
    /// </para>
    /// </remarks>
    public string DescriptionText => Source == RuleSource.Filled
        ? Strings.RuleFilledSays(Value)
        : Description;

    /// <summary>お手本のどこから読み取ったか。画面に出す (#172)。</summary>
    /// <remarks>
    /// 補った分は数え上げた場所の数から組み立てる。**数を持っていない古い
    /// ファイルは、保存されている文をそのまま出す。**読み取り直せば数が入る。
    /// </remarks>
    public string EvidenceText => Source == RuleSource.Filled && Places > 0
        ? Strings.RuleFilledSaw(Places)
        : Evidence;

    /// <summary>出どころの名前。画面に出す。</summary>
    public string SourceText => Source switch
    {
        RuleSource.Hand => Strings.RuleSourceHand,
        RuleSource.Filled => Strings.RuleSourceFilled,
        _ => Strings.RuleSourceAi,
    };

    /// <summary>当てる場所。画面に出す (#81)。決まっていなければ書庫全体。</summary>
    public string WhereText => Where.Length == 0 ? Strings.RuleWhereAnywhere : Where;

    /// <summary>当てはめる先の名前。画面に出す。</summary>
    public string ScopeText => Scope switch
    {
        RuleScope.Root => Strings.RuleScopeRoot,
        RuleScope.Folders => Strings.RuleScopeFolders,
        RuleScope.Files => Strings.RuleScopeFiles,
        _ => Strings.RuleScopeAll,
    };

    /// <summary>
    /// 中身を確かめて、当てられる形になっていれば決まりを作る。
    /// </summary>
    /// <remarks>
    /// <para>
    /// **形にならないものは黙って落とす。**AI の答えなので、空の値や、
    /// 正規表現として成り立たない文字列が来ることがある。落とした数は
    /// 呼び出し側が数えて利用者に見せる。
    /// </para>
    /// </remarks>
    public static ArchiveRule? TryCreate(
        RuleKind kind, RuleScope scope, string value, string description, string evidence,
        RuleSource source = RuleSource.Ai, string where = "")
    {
        var text = value.Trim();

        if (text.Length == 0)
        {
            return null;
        }

        // 拡張子は「.txt」の形に揃える。AI は「txt」とも「*.txt」とも書く
        if (kind == RuleKind.ForbiddenExtension)
        {
            text = "." + text.TrimStart('*').TrimStart('.');
        }

        var rule = new ArchiveRule(kind, scope, text, description.Trim(), evidence.Trim())
        {
            Source = source,
        };

        // 場所は任意。書いてあって組み立てられないなら、当てる先が定まらないので落とす
        var place = where.Trim();

        if (place.Length > 0)
        {
            if (TryCompile(place, rejectLoose: false) is not { } inside)
            {
                return null;
            }

            rule = rule with { Where = place, WherePattern = inside };
        }

        // 形を使う種類は、正規表現を組み立てておく (#83)
        if (kind is not (RuleKind.NamePattern or RuleKind.RequiredPattern))
        {
            return rule;
        }

        return TryCompile(text) is { } pattern ? rule with { Pattern = pattern } : null;
    }

    /// <summary>正規表現を組み立てる。組み立てられない・緩すぎるものは <see langword="null"/>。</summary>
    /// <remarks>
    /// <para>
    /// **まず後戻りしない書き方 (<see cref="RegexOptions.NonBacktracking"/>) で試す。**
    /// AI が書いた正規表現をそのまま当てるので、書き方によっては項目1つに
    /// 何秒も掛かることがある。後戻りしない書き方なら、その心配がない。
    /// </para>
    /// <para>
    /// 先読みや後方参照など、後戻りしない書き方で扱えない記法もある。その場合は
    /// 普通の書き方に落とすが、**1回の照合に 100ms の制限を掛ける**。制限に
    /// 掛かったものは違反として扱わない (<see cref="RuleChecker"/>)。
    /// </para>
    /// <para>
    /// **落とすときの例外は1種類ではない** (#79)。書けない記法は
    /// <see cref="ArgumentException"/> だが、後方参照のように「書けるが後戻り
    /// しない書き方では扱えない」ものは <see cref="NotSupportedException"/> で来る。
    /// 片方だけ受けていたため、後方参照を含む正規表現が返るとアプリの側まで
    /// 例外が抜けていた。
    /// </para>
    /// <para>
    /// **何にでも当たる正規表現は落とす。**<c>.*</c> のようなものは、当てても
    /// 何も見つからない。決まりとして並べると、確かめた気にさせるだけ害がある。
    /// </para>
    /// </remarks>
    /// <param name="rejectLoose">
    /// 何にでも当たるものを落とすか。**場所には掛けない** (#81)。名前の形と違い、
    /// 場所が広いこと自体は誤りではない。書庫全体を指すなら空にすればよいので、
    /// わざわざ広い形を書いてきた場合も、その通りに当てる。
    /// </param>
    private static Regex? TryCompile(string pattern, bool rejectLoose = true)
    {
        var text = pattern.StartsWith('^') ? pattern : "^" + pattern;
        text = text.EndsWith('$') ? text : text + "$";

        Regex regex;

        try
        {
            regex = new Regex(text, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // 後方参照 (\1 など) を含むと NotSupportedException になる。
            // 書けない記法は ArgumentException とは別の型で来るので、両方受ける (#79)
            try
            {
                regex = new Regex(
                    text, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (Exception inner) when (inner is ArgumentException or NotSupportedException)
            {
                return null;
            }
        }

        if (!rejectLoose)
        {
            return regex;
        }

        try
        {
            // ルールとは呼べない名前。これに当たるなら、何にでも当たっている
            return regex.IsMatch(" zz ") ? null : regex;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
}

/// <summary>その決まりを最後に決めたのは誰か (#26)。</summary>
internal enum RuleSource
{
    /// <summary>AI が挙げた。</summary>
    Ai,

    /// <summary>人が入れた、または直した。</summary>
    Hand,

    /// <summary>
    /// AI が指した場所を、こちらで数え上げて補った (#84)。
    /// </summary>
    /// <remarks>
    /// **AI が言っていないことを AI 名義にしない。**この機能はずっと
    /// 「AI の提案と、こちらの検証を分けて見せる」ことで成り立っている。
    /// 混ぜると、その線が一本崩れる。
    /// </remarks>
    Filled,
}

/// <summary>決まりの種類 (#25)。</summary>
internal enum RuleKind
{
    /// <summary>この名前のものが必ずある。</summary>
    RequiredEntry,

    /// <summary>このフォルダが必ずある。</summary>
    RequiredFolder,

    /// <summary>この拡張子を含めない。</summary>
    ForbiddenExtension,

    /// <summary>この名前のものを含めない。</summary>
    ForbiddenName,

    /// <summary>名前がこの形をしている。</summary>
    NamePattern,

    /// <summary>この形のものが必ずある (#83)。</summary>
    /// <remarks>
    /// <see cref="RequiredEntry"/> は名前しか取らない。「examples の各フォルダに
    /// <c>.ino</c> が1つはある」のように、**名前が場所ごとに違うもの**は
    /// 書けなかった。<see cref="NamePattern"/> は「その形**だけ**」を言うので、
    /// 空のフォルダは素通りする。「1つはある」は別の種類が要る。
    /// </remarks>
    RequiredPattern,
}

/// <summary>決まりを当てはめる先 (#25)。</summary>
internal enum RuleScope
{
    /// <summary>ルート直下だけ。</summary>
    Root,

    /// <summary>すべてのフォルダ。</summary>
    Folders,

    /// <summary>すべてのファイル。</summary>
    Files,

    /// <summary>すべての項目。</summary>
    All,
}

/// <summary>種類と当てる先を、言葉と行き来させる (#25、#26)。</summary>
/// <remarks>
/// **AI に頼むときと、ファイルに残すときで同じ言葉を使う。**別々に持つと、
/// 片方だけ増やしたときに、読めるのに保存できない決まりができてしまう。
/// ファイルは利用者が直接開いて直せる形にしてあるので、言葉は短く読めるものにする。
/// </remarks>
internal static class RuleWords
{
    /// <summary>言葉から種類へ。知らない言葉なら <see langword="null"/>。</summary>
    public static RuleKind? ToKind(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "required_entry" => RuleKind.RequiredEntry,
        "required_folder" => RuleKind.RequiredFolder,
        "forbidden_extension" => RuleKind.ForbiddenExtension,
        "forbidden_name" => RuleKind.ForbiddenName,
        "name_pattern" => RuleKind.NamePattern,
        "required_pattern" => RuleKind.RequiredPattern,
        _ => null,
    };

    /// <summary>種類から言葉へ。</summary>
    public static string FromKind(RuleKind kind) => kind switch
    {
        RuleKind.RequiredEntry => "required_entry",
        RuleKind.RequiredFolder => "required_folder",
        RuleKind.ForbiddenExtension => "forbidden_extension",
        RuleKind.ForbiddenName => "forbidden_name",
        RuleKind.RequiredPattern => "required_pattern",
        _ => "name_pattern",
    };

    /// <summary>言葉から当てる先へ。読み取れなければ、いちばん広いものにする。</summary>
    public static RuleScope ToScope(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "root" => RuleScope.Root,
        "folders" => RuleScope.Folders,
        "files" => RuleScope.Files,
        _ => RuleScope.All,
    };

    /// <summary>当てる先から言葉へ。</summary>
    public static string FromScope(RuleScope scope) => scope switch
    {
        RuleScope.Root => "root",
        RuleScope.Folders => "folders",
        RuleScope.Files => "files",
        _ => "all",
    };
}
