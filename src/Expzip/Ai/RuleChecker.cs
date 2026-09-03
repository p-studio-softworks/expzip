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

    /// <summary>「必ずある」を見る。無ければ違反だが、指させる場所は無い。</summary>
    private static RuleResult Required(ArchiveRule rule, ArchiveContents contents, bool folderOnly)
    {
        var deep = rule.Value.Contains('/');
        var found = false;
        var applied = 0;

        // 場所が指定されていれば、ルート直下かどうかではなく場所で絞る (#81)
        var rootOnly = rule.WherePattern is null && rule.Scope == RuleScope.Root;

        Walk(contents.Root, rootOnly, (parent, path, name, isFolder) =>
        {
            if ((folderOnly && !isFolder) || !Here(rule, parent))
            {
                return;
            }

            // 場所に合う項目が1つも無ければ、あるべきかどうかを言えない
            applied = 1;
            found |= string.Equals(deep ? path : name, rule.Value.Trim('/'), Compare);
        });

        return new RuleResult(rule, found, [], applied);
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
