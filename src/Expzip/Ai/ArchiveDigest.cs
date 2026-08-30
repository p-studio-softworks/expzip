using System.IO;
using System.Text;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ai;

/// <summary>
/// お手本書庫から、AI に見せる「形」だけを取り出したもの (#25)。
/// </summary>
/// <remarks>
/// <para>
/// **送るのはファイル名とフォルダ構成だけで、ファイルの中身は一切送らない**
/// (仕様書 11.4節、確定方針)。ここで作った文字列がそのまま送られるものであり、
/// 送る前に利用者にそのまま見せる。見せられない形では作らない。
/// </para>
/// <para>
/// **全部は送らない。**この PC にある zip 126 個を数えたところ、エントリ数は
/// 中央値 5、90% が 634 以下、96% が 1000 以下だが、最大は 21,238 個で、
/// 名前だけで 1.5MB になった。丸ごと送ると、ごく一部の書庫のために毎回の
/// やり取りが重くなる。
/// </para>
/// <para>
/// 削り方は「何を読み取ってほしいか」から決めている。
/// </para>
/// <list type="bullet">
///   <item>
///     **フォルダは全部出す**(<see cref="MaxFolders"/> まで)。読み取ってほしいのは
///     構成そのもので、フォルダは数が少ない。最大の書庫でも 183 個しかなかった。
///   </item>
///   <item>
///     **ルート直下のファイルは全部出す**。「ルート直下に README.txt が必須」の
///     ような規則は、ルートを端折ると読み取れない。
///   </item>
///   <item>
///     **深いところのファイルはフォルダごとに数個だけ**。命名の癖を見るのに
///     何百個も要らない。
///   </item>
///   <item>
///     **拡張子ごとの数は端折らない**。名前を間引いても分布は残る。
///   </item>
/// </list>
/// <para>
/// 端折った数は文中に書く。AI に「これで全部だ」と思わせると、出てこなかった
/// 名前を根拠に「この拡張子は含めない」と言い出す。
/// </para>
/// </remarks>
internal sealed record ArchiveDigest(string Text, int Folders, int Files, int Omitted)
{
    /// <summary>出すフォルダの上限。</summary>
    private const int MaxFolders = 200;

    /// <summary>出すファイル名の上限。</summary>
    private const int MaxFiles = 300;

    /// <summary>ルートより下で、1つのフォルダから出すファイル名の数。</summary>
    private const int PerFolder = 5;

    /// <summary>1つのフォルダから出す数。端折った理由を画面に出すのに使う (#70)。</summary>
    public static int PerFolderLimit => PerFolder;

    /// <summary>出すファイル名の合計。端折った理由を画面に出すのに使う (#70)。</summary>
    public static int TotalLimit => MaxFiles;

    /// <summary>実際に送るバイト数。</summary>
    public int Bytes => Encoding.UTF8.GetByteCount(Text);

    /// <summary>書庫の中身から、送る形を組み立てる。</summary>
    public static ArchiveDigest Build(ArchiveContents contents)
    {
        var folders = new List<ArchiveFolder>();
        Collect(contents.Root, folders);
        folders.Sort(static (a, b) => string.CompareOrdinal(a.FullPath, b.FullPath));

        var builder = new StringBuilder();
        builder.Append(Strings.RuleDigestArchive(Path.GetFileName(contents.FilePath)));
        builder.Append('\n');
        builder.Append(Strings.RuleDigestCounts(contents.FileCount, folders.Count));
        builder.Append("\n\n");

        var omitted = 0;

        // フォルダ。読み取ってほしいのは構成なので、ここは端折らない
        builder.Append(Strings.RuleDigestFolders(folders.Count));
        builder.Append('\n');

        if (folders.Count == 0)
        {
            builder.Append(Strings.RuleDigestNoFolders);
            builder.Append('\n');
        }

        foreach (var folder in folders.Take(MaxFolders))
        {
            builder.Append(folder.FullPath);
            builder.Append('\n');
        }

        if (folders.Count > MaxFolders)
        {
            builder.Append(Strings.RuleDigestMoreFolders(folders.Count - MaxFolders));
            builder.Append('\n');
        }

        // 拡張子ごとの数。名前を間引いても、ここに分布が残る
        builder.Append('\n');
        builder.Append(Strings.RuleDigestExtensions);
        builder.Append('\n');

        foreach (var (extension, count) in CountExtensions(contents.Root))
        {
            builder.Append(extension);
            builder.Append(' ');
            builder.Append(count);
            builder.Append('\n');
        }

        // ルート直下のファイル。ここを端折ると「ルートに何が必須か」が読めない
        var budget = MaxFiles;
        var root = contents.Root.Files.Select(static f => f.Name)
            .OrderBy(static n => n, StringComparer.Ordinal).ToList();

        builder.Append('\n');
        builder.Append(Strings.RuleDigestRoot(root.Count));
        builder.Append('\n');

        if (root.Count == 0)
        {
            builder.Append(Strings.RuleDigestNoRootFiles);
            builder.Append('\n');
        }

        foreach (var name in root.Take(budget))
        {
            builder.Append(name);
            builder.Append('\n');
        }

        if (root.Count > budget)
        {
            omitted += root.Count - budget;
            builder.Append(Strings.RuleDigestMoreFiles(root.Count - budget));
            builder.Append('\n');
        }

        budget -= Math.Min(budget, root.Count);

        // 深いところは、フォルダごとに数個ずつ。命名の癖を見るのに何百個も要らない
        builder.Append('\n');
        builder.Append(Strings.RuleDigestSamples(PerFolder));
        builder.Append('\n');

        foreach (var folder in folders.Take(MaxFolders))
        {
            if (folder.Files.Count == 0)
            {
                continue;
            }

            var take = Math.Min(Math.Min(PerFolder, budget), folder.Files.Count);
            omitted += folder.Files.Count - take;

            if (take == 0)
            {
                continue;
            }

            budget -= take;

            var names = folder.Files.Select(static f => f.Name)
                .OrderBy(static n => n, StringComparer.Ordinal).Take(take);

            builder.Append(folder.FullPath);
            builder.Append("/: ");
            builder.Append(string.Join(", ", names));

            if (folder.Files.Count > take)
            {
                builder.Append(' ');
                builder.Append(Strings.RuleDigestMoreHere(folder.Files.Count - take));
            }

            builder.Append('\n');
        }

        if (omitted > 0)
        {
            builder.Append('\n');
            builder.Append(Strings.RuleDigestOmitted(omitted, PerFolder, MaxFiles));
            builder.Append('\n');
        }

        return new ArchiveDigest(
            builder.ToString(), folders.Count, contents.FileCount, omitted);
    }

    /// <summary>ルートを除く全フォルダを、深さ優先で集める。</summary>
    private static void Collect(ArchiveFolder folder, List<ArchiveFolder> into)
    {
        foreach (var child in folder.Folders)
        {
            into.Add(child);
            Collect(child, into);
        }
    }

    /// <summary>拡張子ごとのファイル数。多い順、同数なら名前順。</summary>
    private static List<KeyValuePair<string, int>> CountExtensions(ArchiveFolder root)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        Walk(root);

        return [.. counts.OrderByDescending(static p => p.Value)
            .ThenBy(static p => p.Key, StringComparer.Ordinal)];

        void Walk(ArchiveFolder folder)
        {
            foreach (var file in folder.Files)
            {
                var extension = Path.GetExtension(file.Name);
                var key = extension.Length == 0 ? Strings.RuleDigestNoExtension : extension;
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }

            foreach (var child in folder.Folders)
            {
                Walk(child);
            }
        }
    }
}
