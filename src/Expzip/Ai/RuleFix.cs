using System.IO;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ai;

/// <summary>決まりに合わせるための直し方、その1つ (#28、仕様書 11.3節の5)。</summary>
/// <remarks>
/// <para>
/// **直し方は決まりの種類から決まる。**「含めない」に引っ掛かったものは取り除く
/// しかなく、「名前の形」に外れたものは名前を変えるしかない。ここで種類を
/// 取り違えると、消さなくてよいものを消すことになる。
/// </para>
/// <para>
/// **直せないものも並べる。**「必ずある」はずのものが無い場合、書庫の中を
/// いじっても直らない。黙って落とすと、報告の窓の件数と合わなくなり、
/// 「残りは直ったのか」が分からなくなる。
/// </para>
/// </remarks>
internal sealed class RuleFix
{
    /// <summary>直す対象の書庫内パス。手で直すしかないものでは空。</summary>
    public required string Path { get; init; }

    /// <summary>対象がフォルダかどうか。</summary>
    public required bool IsFolder { get; init; }

    /// <summary>どう直すか。</summary>
    public required RuleFixKind Kind { get; init; }

    /// <summary>この対象が破っている決まり。</summary>
    public required IReadOnlyList<ArchiveRule> Rules { get; init; }

    /// <summary>付け直す名前。<see cref="RuleFixKind.Rename"/> のときだけ使う。</summary>
    public string NewName { get; set; } = string.Empty;

    /// <summary>この直しを実際に行うか。**既定では行わない**。</summary>
    /// <remarks>
    /// 取り消せない操作なので、既定で入っていると「押したら全部消えた」が起きる。
    /// 選ぶのは人の仕事にする (仕様書 11.4節、無断上書きはしない)。
    /// </remarks>
    public bool Chosen { get; set; }

    /// <summary>いまの名前。</summary>
    public string Name => Path.Length == 0
        ? string.Empty
        : Path[(Path.LastIndexOf('/') + 1)..];

    /// <summary>親フォルダの書庫内パス。ルート直下なら空。</summary>
    public string Parent => Path.LastIndexOf('/') is var cut and >= 0 ? Path[..cut] : string.Empty;

    /// <summary>直し方の名前。画面に出す。</summary>
    public string KindText => Kind switch
    {
        RuleFixKind.Remove => Strings.RuleFixRemove,
        RuleFixKind.Rename => Strings.RuleFixRename,
        _ => Strings.RuleFixManual,
    };

    /// <summary>破っている決まりを一言で書く。</summary>
    public string Reason => string.Join(" / ", Rules.Select(static rule =>
        rule.Description.Length > 0 ? rule.Description : $"{rule.KindText}: {rule.Value}"));
}

/// <summary>直し方の種類 (#28)。</summary>
internal enum RuleFixKind
{
    /// <summary>書庫から取り除く。</summary>
    Remove,

    /// <summary>名前を付け直す。</summary>
    Rename,

    /// <summary>書庫の中をいじっても直らない。手で直すしかない。</summary>
    Manual,
}

/// <summary>決まりに合わせるための直し方を組み立てる (#28)。</summary>
/// <remarks>
/// <para>
/// **フォルダの移動は出さない。**決まりの種類は5つあるが (#25)、そのどれも
/// 「別の場所へ移せば直る」ものではない。移す先を決める根拠がどこにも無いのに
/// 移動を勧めると、直った気にさせるだけになる。必要になったら、
/// 「このフォルダの下にある」という種類の決まりを増やしてから足す。
/// </para>
/// </remarks>
internal static class RuleFixer
{
    /// <summary>当てた結果から、直し方を組み立てる。</summary>
    public static List<RuleFix> Propose(RuleAudit audit, ArchiveContents contents)
    {
        var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Collect(contents.Root, folders);

        var fixes = new List<RuleFix>();

        // 直せないものを先に出す。件数が合わないと、残りが直ったのか分からない
        foreach (var rule in audit.Unmet)
        {
            fixes.Add(new RuleFix
            {
                Path = string.Empty,
                IsFolder = rule.Kind == RuleKind.RequiredFolder,
                Kind = RuleFixKind.Manual,
                Rules = [rule],
            });
        }

        foreach (var (path, rules) in audit.Broken.OrderBy(
            static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            fixes.Add(new RuleFix
            {
                Path = path,
                IsFolder = folders.Contains(path),
                Kind = Decide(rules),
                Rules = rules,
            });
        }

        return fixes;
    }

    /// <summary>
    /// 破っている決まりから、直し方を決める。
    /// </summary>
    /// <remarks>
    /// **取り除くほうが強い。**「含めない」と「名前の形」の両方に引っ掛かって
    /// いる場合、名前を変えても含めてはいけないものであることは変わらない。
    /// </remarks>
    private static RuleFixKind Decide(IReadOnlyList<ArchiveRule> rules)
    {
        if (rules.Any(static rule => rule.Kind
                is RuleKind.ForbiddenExtension or RuleKind.ForbiddenName))
        {
            return RuleFixKind.Remove;
        }

        return rules.Any(static rule => rule.Kind == RuleKind.NamePattern)
            ? RuleFixKind.Rename
            : RuleFixKind.Manual;
    }

    /// <summary>
    /// その名前に付け直してよいかを見る。
    /// </summary>
    /// <returns>付け直せないときは、その理由。付け直せるなら <see langword="null"/>。</returns>
    /// <remarks>
    /// **決まりを直すつもりで、別の決まりを破らせない。**形に合う名前でも、
    /// 含めないことにした拡張子や名前になっていれば、直したことにならない。
    /// </remarks>
    public static string? WhyNot(
        RuleFix fix, string newName, RuleAudit audit, ArchiveContents contents)
    {
        var name = newName.Trim();

        if (name.Length == 0)
        {
            return Strings.RuleFixNeedsName;
        }

        // 書庫内の区切りと、ファイル名に使えない字を弾く
        if (name.Contains('/') || name.Contains('\\')
            || name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            return Strings.RuleFixBadName;
        }

        if (string.Equals(name, fix.Name, StringComparison.Ordinal))
        {
            return Strings.RuleFixSameName;
        }

        // 破っていた決まりを、その名前が満たすかどうか
        foreach (var rule in fix.Rules)
        {
            if (rule.Kind == RuleKind.NamePattern && rule.Pattern is { } pattern
                && !pattern.IsMatch(name))
            {
                return Strings.RuleFixStillBroken(rule.Value);
            }
        }

        // ほかの決まりを新しく破らないかどうか
        foreach (var result in audit.Results)
        {
            var rule = result.Rule;

            if (rule.Kind == RuleKind.ForbiddenName
                && string.Equals(name, rule.Value, StringComparison.OrdinalIgnoreCase))
            {
                return Strings.RuleFixWouldBreak(rule.Value);
            }

            if (rule.Kind == RuleKind.ForbiddenExtension && !fix.IsFolder
                && string.Equals(
                    System.IO.Path.GetExtension(name), rule.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                return Strings.RuleFixWouldBreak(rule.Value);
            }
        }

        return Taken(fix, name, contents) ? Strings.RuleFixTaken : null;
    }

    /// <summary>
    /// 同じ場所に、その名前のものが既にあるか。
    /// </summary>
    /// <remarks>
    /// **自分自身は数えない。**大文字小文字を区別せずに見るため、`report.docx` を
    /// `Report.docx` に直すような場合、自分に当たって「既にある」と言ってしまう。
    /// </remarks>
    private static bool Taken(RuleFix fix, string name, ArchiveContents contents)
    {
        var parent = Find(contents.Root, fix.Parent);

        if (parent is null)
        {
            return false;
        }

        return parent.Folders.Any(f => Clashes(f.Name, f.FullPath))
            || parent.Files.Any(f => Clashes(f.Name, f.FullPath));

        bool Clashes(string other, string path)
            => string.Equals(other, name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(path, fix.Path, StringComparison.OrdinalIgnoreCase);
    }

    private static ArchiveFolder? Find(ArchiveFolder folder, string fullPath)
    {
        if (string.Equals(folder.FullPath, fullPath, StringComparison.OrdinalIgnoreCase))
        {
            return folder;
        }

        foreach (var child in folder.Folders)
        {
            if (Find(child, fullPath) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static void Collect(ArchiveFolder folder, HashSet<string> into)
    {
        foreach (var child in folder.Folders)
        {
            into.Add(child.FullPath);
            Collect(child, into);
        }
    }
}
