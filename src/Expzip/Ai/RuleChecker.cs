using System.IO;
using System.Text.RegularExpressions;
using Expzip.Archives;

namespace Expzip.Ai;

/// <summary>決まりを書庫に当てて、守られているかを見る (#25)。</summary>
/// <remarks>
/// <para>
/// **AI は使わない。**当てはめは決まった手順で、こちら側で行う。同じ書庫と
/// 同じ決まりからは必ず同じ答えが出る。
/// </para>
/// <para>
/// 使い道は2つある。1つは推定したての決まりを**お手本自身に当て直すこと**
/// (#25)。お手本から読み取ったはずの決まりを、お手本が破っているなら、
/// それは読み違いなので採らない。もう1つは、確定した決まりを作業中の書庫に
/// 当てて違反箇所を出すこと (#27)。同じ手順でよいので、ここを共用する。
/// </para>
/// </remarks>
internal static class RuleChecker
{
    /// <summary>名前の比べ方。Windows のファイル名に合わせ、大文字小文字は区別しない。</summary>
    private static readonly StringComparison Compare = StringComparison.OrdinalIgnoreCase;

    /// <summary>決まりを1つ当てる。</summary>
    public static RuleResult Check(ArchiveRule rule, ArchiveContents contents) => rule.Kind switch
    {
        RuleKind.RequiredEntry => Required(rule, contents, folderOnly: false),
        RuleKind.RequiredFolder => Required(rule, contents, folderOnly: true),
        RuleKind.ForbiddenExtension => Forbidden(rule, contents, Extension),
        RuleKind.ForbiddenName => Forbidden(rule, contents, Named),
        _ => Pattern(rule, contents),
    };

    /// <summary>決まりをまとめて当てる。</summary>
    public static List<RuleResult> CheckAll(
        IEnumerable<ArchiveRule> rules, ArchiveContents contents)
        => [.. rules.Select(rule => Check(rule, contents))];

    /// <summary>「必ずある」を見る。</summary>
    /// <remarks>
    /// <para>
    /// **場所が指定されていれば、その場所ごとに見る** (#82)。
    /// 「各サブライブラリに CHANGELOG.md を必ず置く」は、**どこか1つに
    /// あれば済む話ではない。**5つのうち1つを消しても守られていることに
    /// なっていた。ルールの説明が「各」と言っているのに、判定は
    /// 「どこかに」だった。
    /// </para>
    /// <para>
    /// 場所ごとに見ると、**足りない場所を指させる**ようにもなる。
    /// 場所が無いときは、これまでどおり書庫のどこかにあればよい。
    /// その場合は指させる場所が無いので、違反は空で返す。
    /// </para>
    /// </remarks>
    private static RuleResult Required(ArchiveRule rule, ArchiveContents contents, bool folderOnly)
    {
        var wanted = rule.Value.Trim('/');

        if (rule.WherePattern is not null)
        {
            var missing = new List<string>();
            var places = 0;

            EachFolder(contents.Root, folder =>
            {
                if (!Here(rule, folder.FullPath))
                {
                    return;
                }

                places++;

                if (!Holds(folder, wanted, folderOnly))
                {
                    missing.Add(folder.FullPath);
                }
            });

            // 場所に合うフォルダが1つも無ければ、あるべきかどうかを言えない
            return new RuleResult(rule, missing.Count == 0, missing, places);
        }

        var deep = wanted.Contains('/');
        var found = false;

        Walk(contents.Root, rule.Scope == RuleScope.Root, (_, path, name, isFolder) =>
        {
            if (!folderOnly || isFolder)
            {
                found |= string.Equals(deep ? path : name, wanted, Compare);
            }
        });

        return new RuleResult(rule, found, [], 1);
    }

    /// <summary>そのフォルダの中に、その名前のものがあるか (#82)。</summary>
    /// <remarks>
    /// 値に <c>/</c> があれば、**その場所からの相対パス**として扱う。
    /// 「各ライブラリの include/usb/ に…」のような、一段深いところも指せる。
    /// </remarks>
    private static bool Holds(ArchiveFolder folder, string wanted, bool folderOnly)
    {
        if (!wanted.Contains('/'))
        {
            return (!folderOnly
                    && folder.Files.Any(f => string.Equals(f.Name, wanted, Compare)))
                || folder.Folders.Any(f => string.Equals(f.Name, wanted, Compare));
        }

        var full = folder.FullPath.Length == 0
            ? wanted
            : folder.FullPath + "/" + wanted;
        var hit = false;

        Walk(folder, rootOnly: false, (_, path, _, isFolder) =>
        {
            if (!folderOnly || isFolder)
            {
                hit |= string.Equals(path, full, Compare);
            }
        });

        return hit;
    }

    /// <summary>フォルダを、根も含めて全部たどる (#82)。</summary>
    private static void EachFolder(ArchiveFolder root, Action<ArchiveFolder> visit)
    {
        visit(root);

        foreach (var child in root.Folders)
        {
            EachFolder(child, visit);
        }
    }

    /// <summary>「含めない」を見る。当たったものが違反。</summary>
    private static RuleResult Forbidden(
        ArchiveRule rule, ArchiveContents contents, Func<ArchiveRule, string, bool> hit)
    {
        var violations = new List<string>();
        var applied = 0;

        Walk(contents.Root, rootOnly: false, (parent, path, name, isFolder) =>
        {
            // 拡張子はファイルだけの話。フォルダ名に「.」が入っていても違反にしない
            if (isFolder && rule.Kind == RuleKind.ForbiddenExtension)
            {
                return;
            }

            if (!Here(rule, parent))
            {
                return;
            }

            applied++;

            if (hit(rule, name))
            {
                violations.Add(path);
            }
        });

        return new RuleResult(rule, violations.Count == 0, violations, applied);
    }

    /// <summary>「名前がこの形」を見る。外れたものが違反。</summary>
    private static RuleResult Pattern(ArchiveRule rule, ArchiveContents contents)
    {
        if (rule.Pattern is not { } pattern)
        {
            return new RuleResult(rule, true, [], 0);
        }

        var violations = new List<string>();
        var applied = 0;

        // 場所が指定されていれば、ルート直下かどうかではなく場所で絞る (#81)
        var rootOnly = rule.WherePattern is null && rule.Scope == RuleScope.Root;

        Walk(contents.Root, rootOnly, (parent, path, name, isFolder) =>
        {
            if ((rule.Scope == RuleScope.Folders && !isFolder)
                || (rule.Scope == RuleScope.Files && isFolder)
                || !Here(rule, parent))
            {
                return;
            }

            applied++;

            try
            {
                if (!pattern.IsMatch(name))
                {
                    violations.Add(path);
                }
            }
            catch (RegexMatchTimeoutException)
            {
                // 時間切れは「守っていない証拠」にはならない。触れずに置く
            }
        });

        return new RuleResult(rule, violations.Count == 0, violations, applied);
    }

    private static bool Extension(ArchiveRule rule, string name)
        => string.Equals(Path.GetExtension(name), rule.Value, Compare);

    private static bool Named(ArchiveRule rule, string name)
        => string.Equals(name, rule.Value, Compare);

    /// <summary>書庫の中を1つずつ見る。ルートだけを見ることもできる。</summary>
    /// <summary>
    /// 書庫の中を歩く。<paramref name="visit"/> には**含むフォルダのパス**、
    /// 項目のパス、名前、フォルダかどうかを渡す (#81)。
    /// </summary>
    private static void Walk(
        ArchiveFolder root, bool rootOnly, Action<string, string, string, bool> visit)
    {
        Step(root);

        void Step(ArchiveFolder folder)
        {
            foreach (var file in folder.Files)
            {
                visit(folder.FullPath, file.FullPath, file.Name, false);
            }

            foreach (var child in folder.Folders)
            {
                visit(folder.FullPath, child.FullPath, child.Name, true);

                if (!rootOnly)
                {
                    Step(child);
                }
            }
        }
    }

    /// <summary>その場所を当てる先とするか (#81)。場所が空なら書庫全体。</summary>
    /// <remarks>
    /// **AI は「どこに」と「どんな形か」を1本の正規表現に繋げて書く。**
    /// 分けて受け取り、分けて当てる。時間切れは当てないほうへ倒す。
    /// </remarks>
    private static bool Here(ArchiveRule rule, string parent)
    {
        if (rule.WherePattern is not { } inside)
        {
            return true;
        }

        try
        {
            return inside.IsMatch(parent);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}

/// <summary>決まりを1つ当てた結果 (#25)。</summary>
/// <param name="Rule">当てた決まり。</param>
/// <param name="Satisfied">守られていたか。</param>
/// <param name="Violations">破っている項目の書庫内パス。「必ずある」では空。</param>
/// <param name="Applied">当てた項目の数。0 なら、この書庫では確かめようがなかった。</param>
internal readonly record struct RuleResult(
    ArchiveRule Rule, bool Satisfied, IReadOnlyList<string> Violations, int Applied);
