using Expzip.Archives;

namespace Expzip.Ai;

/// <summary>
/// AI が指した場所について、**共通の顔ぶれをこちらで数え上げる** (#84)。
/// </summary>
/// <remarks>
/// <para>
/// 実地で、5つのライブラリすべてに CHANGELOG.md / CMakeLists.txt / LICENSE /
/// README.md / idf_component.yml が並んでいるのに、**AI は2つだけ挙げて
/// 代表例で済ませた。**LICENSE を消しても気付けなかった。
/// </para>
/// <para>
/// 頼み方で「1つ残らず挙げて」と押すことはできるが、**次も途中で止まるかも
/// しれない。**AI が網羅するかどうかに依存する限り、確実にはならない。
/// </para>
/// <para>
/// **そしてこれは AI に頼る必要がない仕事である。**「同じ場所に並ぶフォルダ
/// すべてに共通して現れる名前」は積集合を取るだけで出る。判断は要らない。
/// **難しいのは場所を見つけることで、それは AI がやる。**中身を数えるのは
/// こちらがやる。この機能がずっと採ってきた「AI に提案させ、こちらで
/// 確かめる」の延長で、確かめるを補うまで進めたもの。
/// </para>
/// </remarks>
internal static class RuleFiller
{
    /// <summary>共通と見なすのに要るフォルダの数。</summary>
    /// <remarks>
    /// **2つでは偶然かもしれない。**3つ揃って初めて慣習と呼べる。
    /// <para>
    /// この下限には、もう1つ効き目がある。**書庫全体を包んでいるフォルダのように1つしか
    /// 当たらない場所を、自動的に外す。**そこで数え上げると、その書庫にある
    /// ものが全部ルールになり、同義反復の山ができる。
    /// </para>
    /// </remarks>
    private const int MinPlaces = 3;

    /// <summary>補う数の上限。</summary>
    private const int Limit = 20;

    /// <summary>AI が指した場所ごとに、共通の顔ぶれを補う。</summary>
    /// <param name="rules">AI が挙げ、お手本に当て直して通ったルール。</param>
    /// <param name="sample">お手本の書庫。</param>
    /// <returns>補ったルール。すでにあるものは作らない。</returns>
    public static List<ArchiveRule> Fill(
        IReadOnlyList<ArchiveRule> rules, ArchiveContents sample)
    {
        var made = new List<ArchiveRule>();
        var seen = new HashSet<string>(rules.Select(Key), StringComparer.OrdinalIgnoreCase);

        // **場所は AI が挙げたものだけを使う。**こちらで場所を思い付くと、
        // 書庫の形をなぞっただけのルールを量産することになる
        foreach (var rule in Places(rules))
        {
            var folders = new List<ArchiveFolder>();

            Each(sample.Root, folder =>
            {
                if (RuleChecker.Inside(rule, folder.FullPath))
                {
                    folders.Add(folder);
                }
            });

            if (folders.Count < MinPlaces)
            {
                continue;
            }

            foreach (var (name, isFolder) in Common(folders))
            {
                if (made.Count >= Limit)
                {
                    return made;
                }

                // 説明と根拠は持たせない。出すときにそのときの言語で組み立てる (#172)。
                // 数え上げた場所の数だけを渡す
                var built = ArchiveRule.TryCreate(
                    isFolder ? RuleKind.RequiredFolder : RuleKind.RequiredEntry,
                    RuleScope.All, name, string.Empty, string.Empty,
                    RuleSource.Filled, rule.Where);

                if (built is not null && seen.Add(Key(built)))
                {
                    made.Add(built with { Places = folders.Count });
                }
            }
        }

        return made;
    }

    /// <summary>場所を持つルールを、場所ごとに1つずつ。</summary>
    private static IEnumerable<ArchiveRule> Places(IReadOnlyList<ArchiveRule> rules)
        => rules
            .Where(static rule => rule.WherePattern is not null)
            .GroupBy(static rule => rule.Where, StringComparer.Ordinal)
            .Select(static group => group.First());

    /// <summary>
    /// そのフォルダたち**すべて**に現れる名前。ファイルとフォルダは分けて数える。
    /// </summary>
    /// <remarks>
    /// 片方でファイル、片方でフォルダという名前は、共通とは呼べない。分けて取れば
    /// そういうものは自然に落ちる。
    /// </remarks>
    private static List<(string Name, bool IsFolder)> Common(List<ArchiveFolder> folders)
    {
        var found = new List<(string, bool)>();

        foreach (var name in Shared(folders, static f => f.Files.Select(x => x.Name)))
        {
            found.Add((name, false));
        }

        foreach (var name in Shared(folders, static f => f.Folders.Select(x => x.Name)))
        {
            found.Add((name, true));
        }

        return found;
    }

    /// <summary>全部に入っている名前を取る。</summary>
    private static IEnumerable<string> Shared(
        List<ArchiveFolder> folders, Func<ArchiveFolder, IEnumerable<string>> pick)
    {
        var shared = new HashSet<string>(pick(folders[0]), StringComparer.OrdinalIgnoreCase);

        foreach (var folder in folders.Skip(1))
        {
            shared.IntersectWith(pick(folder));

            if (shared.Count == 0)
            {
                break;
            }
        }

        return shared.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>フォルダを、根も含めて全部たどる。</summary>
    private static void Each(ArchiveFolder root, Action<ArchiveFolder> visit)
    {
        visit(root);

        foreach (var child in root.Folders)
        {
            Each(child, visit);
        }
    }

    /// <summary>同じルールかどうかを見るための鍵。</summary>
    private static string Key(ArchiveRule rule)
        => $"{rule.Kind}|{rule.Where}|{rule.Value}";
}
