using System.IO;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;

namespace Expzip.Archives;

/// <summary>書庫にリンクとして入れる 1 個の、指す先 (#366)。</summary>
/// <param name="Target">書庫に入れる指す先。区切りは <c>/</c>。</param>
/// <param name="PointsToFolder">フォルダーを指すか。</param>
/// <param name="Outside">選んだフォルダーの外を指すか。そのまま入れて、終わったあとに知らせる。</param>
internal sealed record ArchiveLink(string Target, bool PointsToFolder, bool Outside);

/// <summary>ディスクを通さずに、新しい書庫にリンクとして入れるもの (#367)。</summary>
/// <param name="EntryName">新しい書庫の中の名前。</param>
/// <param name="Target">指す先。リンクのあるフォルダーからの相対パスで、区切りは <c>/</c>。新しい書庫の中を指すこと。</param>
/// <param name="PointsToFolder">フォルダーを指すか。</param>
/// <param name="SourcePath">入れられなかったときに名前として出すパス。更新日時もここから読む (無ければ今の日時)。</param>
internal sealed record CarriedLink(string EntryName, string Target, bool PointsToFolder, string SourcePath);

/// <summary>書庫にリンクとして入れたもの (#366)。終わったあとの知らせに使う。</summary>
/// <param name="Name">書庫の中の名前。</param>
/// <param name="Target">書庫に入れた指す先。</param>
/// <param name="Outside">選んだフォルダーの外を指すか。</param>
internal sealed record AddedLink(string Name, string Target, bool Outside);

/// <summary>
/// ZIP の中のリンクの項目 (#366)。
/// </summary>
/// <remarks>
/// <para>
/// ZIP には、Unix のシンボリックリンクを入れる広く使われている決まりがある (Info-ZIP)。作った OS を Unix (3) にし、
/// 外部属性の上位 16 ビットに <c>S_IFLNK</c> を入れ、中身にリンク先のパスを書く。7-Zip はこれをリンクとして展開する。
/// エクスプローラーは印を見ないので、リンク先のパスが書かれた小さなファイルとして展開する。
/// </para>
/// <para>
/// ZIP には、ファイルを指すかフォルダーを指すかを書く場所が無い。Windows ではリンクを作るときに区別が要るので、
/// Expzip の拡張フィールド (<see cref="ExtraFieldId"/>) に書く。名前の末尾に <c>/</c> は付けない。
/// 付けると、一覧でも展開でもフォルダーとして扱われ、中身 (リンク先) が消える (<see cref="ZipEntryKind"/>)。
/// </para>
/// </remarks>
internal static class ZipLinks
{
    /// <summary>Unix (作った OS の番号)。</summary>
    private const int UnixHost = 3;

    /// <summary>種類のビットの範囲 (S_IFMT)。</summary>
    private const int TypeMask = 0xF000;

    /// <summary>シンボリックリンク (S_IFLNK)。</summary>
    private const int SymbolicLinkType = 0xA000;

    /// <summary>外部属性の上位 16 ビット。S_IFLNK と、だれでも読み書きできる印 (0777)。</summary>
    private const int LinkMode = SymbolicLinkType | 0x1FF;

    /// <summary>
    /// Expzip のリンクの拡張フィールドの番号。中身は、バージョン (1 バイト、今は 1) と印 (1 バイト、ビット 0 がフォルダーを指す)。
    /// </summary>
    internal const ushort ExtraFieldId = 0x4C58;

    /// <summary>拡張フィールドのバージョン。</summary>
    private const byte ExtraFieldVersion = 1;

    /// <summary>フォルダーを指す印。</summary>
    private const byte PointsToFolderFlag = 1;

    /// <summary>リンクの項目か。外部属性だけで見る (<see cref="IsLink(int)"/>)。</summary>
    /// <remarks>
    /// 一覧、展開、パスワード付きの展開、書き換えで同じ見分け方にする (#367)。作った OS も見ると、
    /// 作った OS を Unix 以外にした項目が、道によってリンクになったりファイルになったりする。
    /// </remarks>
    public static bool IsLink(ZipEntry entry) => IsLink(entry.ExternalFileAttributes);

    /// <summary>
    /// 外部属性だけから見て、リンクの項目か。.NET の <c>ZipArchive</c> は作った OS を見せないので、どの道でもこれで見る。
    /// </summary>
    /// <remarks>Windows で作った書庫は上位 16 ビットを 0 にするので、取り違えない。</remarks>
    public static bool IsLink(int externalAttributes) => ((externalAttributes >> 16) & TypeMask) == SymbolicLinkType;

    /// <summary>リンクの項目の印を付ける。中身 (リンク先) は呼び出し側が書く。</summary>
    public static void Mark(ZipEntry entry, bool pointsToFolder)
    {
        entry.HostSystem = UnixHost;
        entry.ExternalFileAttributes = LinkMode << 16;

        var extra = new ZipExtraData(entry.ExtraData);
        extra.AddEntry(ExtraFieldId, [ExtraFieldVersion, pointsToFolder ? PointsToFolderFlag : (byte)0]);
        entry.ExtraData = extra.GetEntryData();
    }

    /// <summary>元の項目のリンクの印を、作り直した項目に写す。リンクでなければ何もしない。</summary>
    /// <remarks>書庫の中で名前を変えたり、パスワード付きの書庫を作り直したりするときに、印が消えないようにする (#366)。</remarks>
    public static void CopyMark(ZipEntry from, ZipEntry to)
    {
        if (IsLink(from))
        {
            Mark(to, PointsToFolder(from));
        }
    }

    /// <summary>リンクの項目がフォルダーを指すか。Expzip の拡張フィールドが無ければ、ファイルを指すとみなす。</summary>
    public static bool PointsToFolder(ZipEntry entry)
    {
        var extra = new ZipExtraData(entry.ExtraData);
        if (!extra.Find(ExtraFieldId) || extra.ValueLength < 2)
        {
            return false;
        }

        var data = extra.GetStreamForTag(ExtraFieldId);
        var version = data?.ReadByte() ?? -1;
        var flags = data?.ReadByte() ?? -1;
        return version >= ExtraFieldVersion && flags >= 0 && (flags & PointsToFolderFlag) != 0;
    }

    /// <summary>リンクの項目の中身 (リンク先のパス)。UTF-8。</summary>
    public static byte[] Content(ArchiveLink link) => Encoding.UTF8.GetBytes(link.Target);

    /// <summary>
    /// 書庫の中の名前が、リンクの項目の中を通るか。<c>docs</c> がリンクなら <c>docs/a.txt</c> は通る (#366)。
    /// </summary>
    /// <remarks>
    /// リンクを展開できるツールで、リンクの中を通る項目を展開すると、リンクを通って展開先の外に書かれる。
    /// そういう書庫を作らない。大文字と小文字は区別しない (Windows で同じ場所になるため)。
    /// </remarks>
    public static bool PassesThrough(string entryName, IReadOnlySet<string> linkNames)
    {
        var name = ArchivePath.Normalize(entryName).TrimEnd('/');
        for (var slash = name.IndexOf('/'); slash >= 0; slash = name.IndexOf('/', slash + 1))
        {
            if (linkNames.Contains(name[..slash]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// ディスクのリンクの指す先を、書庫に書くパスに直す (#366)。
    /// </summary>
    /// <param name="linkPath">リンクのパス (選んだフォルダーの中の、たどってきたとおりのパス)。</param>
    /// <param name="target">リンクそのものから読んだ指す先 (<see cref="DiskLinks.ReadTarget"/>)。</param>
    /// <param name="relative">相対パスのシンボリックリンクか。</param>
    /// <param name="pointsToFolder">フォルダーを指すか。</param>
    /// <param name="roots">選んだフォルダー。たどってきたとおりのパスと、実際の場所の両方を渡す。</param>
    /// <returns>書庫に記録できない書き方 (ボリュームの GUID、デバイスの名前など) なら <see langword="null"/>。</returns>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>相対パスは、そのまま入れる。</description></item>
    /// <item><description>
    /// 絶対パスで、選んだフォルダーの中を指すものは、リンクのあるフォルダーからの相対パスに直す。別の PC で展開しても正しい場所を指し、
    /// ユーザー名などが書庫に入らない (#366 で決めた)。
    /// </description></item>
    /// <item><description>
    /// 絶対パスで外を指すものは、<c>C:/…</c> か <c>//サーバー/共有/…</c> の形でそのまま入れ、終わったあとに知らせる。
    /// </description></item>
    /// </list>
    /// 指す先が外かどうかは、ディスクを見ずにパスの文字の上で決める。途中のリンクに誘導されないため。
    /// </remarks>
    public static ArchiveLink? ForArchive(
        string linkPath, string target, bool relative, bool pointsToFolder, IReadOnlyList<string> roots)
    {
        if (target.Length == 0 || target.Any(char.IsControl))
        {
            return null;
        }

        var linkFolder = Path.GetDirectoryName(Path.GetFullPath(linkPath))!;

        if (relative)
        {
            // \foo のようにドライブの根から始まる相対パスは、展開する場所で意味が変わるので入れない
            if (target.StartsWith('\\') || target.StartsWith('/') || Path.IsPathRooted(target))
            {
                return null;
            }

            var resolved = Path.GetFullPath(Path.Combine(linkFolder, target));
            var inside = roots.Any(root => DiskLinks.IsSameOrInside(resolved, root));
            return new ArchiveLink(Slashes(target), pointsToFolder, Outside: !inside);
        }

        if (Absolute(target) is not { } absolute)
        {
            return null;
        }

        // 選んだフォルダーの中なら、リンクのあるフォルダーからの相対パスに直す。そのフォルダーも同じ呼び方 (たどってきたとおり / 実際の場所) にそろえる
        foreach (var root in roots)
        {
            if (!DiskLinks.IsSameOrInside(absolute, root))
            {
                continue;
            }

            var from = MapInto(linkFolder, roots, root);
            if (from is not null)
            {
                var relativeTarget = Path.GetRelativePath(from, absolute);
                if (!Path.IsPathRooted(relativeTarget))
                {
                    return new ArchiveLink(Slashes(relativeTarget), pointsToFolder, Outside: false);
                }
            }
        }

        return new ArchiveLink(Slashes(absolute), pointsToFolder, Outside: true);
    }

    /// <summary>区切りを <c>/</c> にする。Windows でないときは <c>\</c> が名前に使える文字なので、そのままにする。</summary>
    private static string Slashes(string path) => OperatingSystem.IsWindows() ? path.Replace('\\', '/') : path;

    /// <summary>
    /// Windows の内部の形の絶対パスを、ふつうの形に直す。入れてよい形は <c>X:\…</c> と <c>\\サーバー\共有\…</c> だけ。
    /// </summary>
    private static string? Absolute(string target)
    {
        // Windows でないときの絶対パスは / で始まる
        if (!OperatingSystem.IsWindows())
        {
            return target.StartsWith('/') ? Path.GetFullPath(target) : null;
        }

        string path;
        if (target.StartsWith(@"\??\UNC\", StringComparison.OrdinalIgnoreCase))
        {
            path = @"\\" + target[8..];
        }
        else if (target.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            path = target[4..];
        }
        else
        {
            path = target;
        }

        var isDrive = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '\\';
        var isShare = path.StartsWith(@"\\", StringComparison.Ordinal)
                      && !path.StartsWith(@"\\?\", StringComparison.Ordinal)
                      && !path.StartsWith(@"\\.\", StringComparison.Ordinal)
                      && path.Length > 2 && path.IndexOf('\\', 2) > 2;

        if (!isDrive && !isShare)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>リンクのあるフォルダーを、<paramref name="root"/> と同じ呼び方に直す。どの根の中でもなければ <see langword="null"/>。</summary>
    private static string? MapInto(string linkFolder, IReadOnlyList<string> roots, string root)
    {
        foreach (var from in roots)
        {
            if (DiskLinks.IsSameOrInside(linkFolder, from))
            {
                var suffix = Path.GetRelativePath(from, linkFolder);
                return suffix == "." ? root : Path.GetFullPath(Path.Combine(root, suffix));
            }
        }

        return null;
    }

    /// <summary>中身として書く、リンク先のバイト列の CRC を求めるためのもの。</summary>
    public static long Crc(byte[] content)
    {
        var crc = new ICSharpCode.SharpZipLib.Checksum.Crc32();
        crc.Update(content);
        return crc.Value;
    }
}
