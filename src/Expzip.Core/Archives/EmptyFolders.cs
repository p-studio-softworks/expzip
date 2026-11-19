using System.Text;

namespace Expzip.Archives;

/// <summary>
/// 書庫のフォルダーの項目から、中身の無いフォルダーを作るときの決まり (#397)。ZIP、パスワード付きの ZIP、7z、tar、RAR で同じにする。
/// </summary>
/// <remarks>
/// <para>
/// 書庫に同じ名前のファイルやリンクの項目もあるフォルダーの項目は作らない。書庫の中の順によらず、ファイル (またはリンク) を展開する。
/// フォルダーを作ると、ファイルが「同じ名前のフォルダーがある」で書けなくなり、1 回では置き換えられないため。
/// 選んだ範囲ではなく書庫の全体で決める。選び方によってフォルダーが作られたり作られなかったりしないように。
/// </para>
/// <para>
/// 比べ方は、同じ名前の項目を決めるとき (<see cref="ArchiveExtractor.LaterLinks"/>) と同じ。名前を書き出し先と同じ手順で整え、
/// 濁点を合わせた書き方にそろえ、大文字と小文字を区別しない。
/// </para>
/// </remarks>
internal static class EmptyFolders
{
    /// <summary>書庫の中の、フォルダーでない項目 (ファイルとリンク) の名前。</summary>
    /// <param name="entries">書庫のすべての項目の名前と、フォルダーの項目か。</param>
    public static HashSet<string> NonFolderNames(IEnumerable<(string Name, bool IsFolder)> entries)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, isFolder) in entries)
        {
            if (!isFolder && Key(name) is { } key)
            {
                names.Add(key);
            }
        }

        return names;
    }

    /// <summary>同じ名前のファイルやリンクの項目が書庫にあるので、このフォルダーの項目を作らないか。</summary>
    public static bool IsShadowed(string name, HashSet<string> nonFolderNames)
        => Key(name) is { } key && nonFolderNames.Contains(key);

    private static string? Key(string name) => ArchiveLinks.Canonical(name)?.Normalize(NormalizationForm.FormC);
}
