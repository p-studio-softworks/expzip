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
/// 仕様書 11.2節が挙げている例 (「ルート直下に README.txt が必須」
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
    /// <summary>当てはめるときに使う正規表現。<see cref="RuleKind.NamePattern"/> のみ。</summary>
    /// <remarks>
    /// 組み立ては1回だけにする。当てるたびに作り直すと、項目数の分だけ掛かる。
    /// </remarks>
    public Regex? Pattern { get; private init; }

    /// <summary>種類の名前。画面に出す。</summary>
    public string KindText => Kind switch
    {
        RuleKind.RequiredEntry => Strings.RuleKindRequiredEntry,
        RuleKind.RequiredFolder => Strings.RuleKindRequiredFolder,
        RuleKind.ForbiddenExtension => Strings.RuleKindForbiddenExtension,
        RuleKind.ForbiddenName => Strings.RuleKindForbiddenName,
        _ => Strings.RuleKindNamePattern,
    };

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
        RuleKind kind, RuleScope scope, string value, string description, string evidence)
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

        var rule = new ArchiveRule(kind, scope, text, description.Trim(), evidence.Trim());

        if (kind != RuleKind.NamePattern)
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
    /// 先読みなど、後戻りしない書き方で扱えない記法もある。その場合は普通の
    /// 書き方に落とすが、**1回の照合に 100ms の制限を掛ける**。制限に掛かった
    /// ものは違反として扱わない (<see cref="RuleChecker"/>)。
    /// </para>
    /// <para>
    /// **何にでも当たる正規表現は落とす。**<c>.*</c> のようなものは、当てても
    /// 何も見つからない。決まりとして並べると、確かめた気にさせるだけ害がある。
    /// </para>
    /// </remarks>
    private static Regex? TryCompile(string pattern)
    {
        var text = pattern.StartsWith('^') ? pattern : "^" + pattern;
        text = text.EndsWith('$') ? text : text + "$";

        Regex regex;

        try
        {
            regex = new Regex(text, RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
        }
        catch (ArgumentException)
        {
            try
            {
                regex = new Regex(
                    text, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        try
        {
            // 決まりとは呼べない名前。これに当たるなら、何にでも当たっている
            return regex.IsMatch(" zz ") ? null : regex;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }
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
