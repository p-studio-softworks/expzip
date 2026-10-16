using ICSharpCode.SharpZipLib.Zip;

namespace Expzip.Archives;

/// <summary>
/// ZIP のエントリがフォルダーそのものを表すかの判定 (#266)。
/// </summary>
/// <remarks>
/// <para>
/// フォルダーかどうかは<b>名前の末尾が区切り文字か</b>だけで決める。一覧と展開
/// (.NET の <c>ZipArchive</c>) はこの決め方をしている。
/// </para>
/// <para>
/// SharpZipLib の <c>IsFile</c> / <c>IsDirectory</c> は、外部属性に DOS のフォルダーの
/// 印 (0x10) があるときもフォルダーと見なす。それで読む書庫検査やハッシュ値だけが
/// 中身のあるエントリを飛ばすと、一覧に出ていて展開もされるのに調べていない
/// ファイルができる。読む所はすべてここを通して、判定を一覧とそろえる。
/// </para>
/// </remarks>
public static class ZipEntryKind
{
    /// <summary>名前がフォルダーそのものを表すか。</summary>
    public static bool IsFolder(string name) => name.EndsWith('/') || name.EndsWith('\\');

    /// <summary>フォルダーそのものを表すエントリか。</summary>
    public static bool IsFolder(ZipEntry entry) => IsFolder(entry.Name);
}
