using System.IO;

namespace Expzip.Archives;

/// <summary>開ける書庫の大きさの上限 (#294)。受け取った書庫を開いただけで落ちないようにする。</summary>
internal static class ArchiveLimits
{
    /// <summary>
    /// フォルダーの階層の上限。これより深い書庫は開かない。
    /// </summary>
    /// <remarks>
    /// 階層が深いとスタックが溢れる。スタックの溢れは受け止められず、ほかのタブごと落ちる。
    /// いちばん浅いところで溢れるのはツリーの表示で、入れ子のレイアウトが再帰になる
    /// (1 段ずつ開いて約 110 段、一気に開いて 192 段で落ちる実測)。
    /// 120 KB の ZIP でも 3 万段にできるので、書庫の大きさでは防げない。
    /// </remarks>
    public const int MaxFolderDepth = 64;

    /// <summary>
    /// 項目 1 個に使うメモリの目安 (名前を除く)。ZIP / 7z で 500 万個を開いて測った値から決めた。
    /// </summary>
    public const int BytesPerEntry = 500;

    /// <summary>一覧に使ってよいメモリの上限。PC のメモリが多くても、これより増やさない。</summary>
    private const long MaxMemoryBudget = 8L * 1024 * 1024 * 1024;

    /// <summary>
    /// 一覧に使ってよいメモリ。PC のメモリの半分まで。
    /// </summary>
    /// <remarks>
    /// 項目の数だけでは決めない。長い名前は圧縮が効くので、数が少なくてもメモリを使い切れる。
    /// </remarks>
    public static long MemoryBudget => Math.Min(MaxMemoryBudget, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 2);

    /// <summary>パスの階層が上限を超えていれば断る。</summary>
    /// <param name="path">書庫の中のパス。</param>
    /// <param name="isFolder">パスの最後もフォルダーかどうか。</param>
    /// <exception cref="ArchiveTooLargeException">上限を超えている場合。</exception>
    public static void CheckDepth(string path, bool isFolder)
    {
        if (path.AsSpan().Count('/') + (isFolder ? 1 : 0) > MaxFolderDepth)
        {
            throw new ArchiveTooLargeException(ArchiveLimit.Depth);
        }
    }

    /// <summary>書庫に書き込む名前の階層が上限を超えていれば断る。開けない書庫を作らない。</summary>
    /// <param name="entryName">書庫の中の名前。フォルダーは末尾が <c>/</c>。</param>
    /// <exception cref="ArchiveTooLargeException">上限を超えている場合。</exception>
    public static void CheckDepthToWrite(string entryName)
    {
        var name = entryName.TrimEnd('/');

        CheckDepth(name, isFolder: name.Length != entryName.Length);
    }
}

/// <summary>
/// ファイル名まで暗号化された書庫 (7z、RAR) で、一覧を読めない (#294、#336)。壊れているのではないので、壊れているとは言わない。
/// </summary>
internal sealed class EncryptedHeaderException(Exception inner) : IOException(inner.Message, inner);

/// <summary>どの上限で断ったか (#294)。</summary>
internal enum ArchiveLimit
{
    /// <summary>項目が多すぎる。名前が長すぎる場合も含む。</summary>
    Entries,

    /// <summary>フォルダーの階層が深すぎる。</summary>
    Depth,

    /// <summary>読んでいる途中でメモリが足りなくなった。</summary>
    Memory,
}

/// <summary>書庫が大きすぎて開けない (#294)。壊れているのではないので、壊れているとは言わない。</summary>
internal sealed class ArchiveTooLargeException(ArchiveLimit limit) : IOException($"Archive limit exceeded: {limit}")
{
    /// <summary>どの上限で断ったか。</summary>
    public ArchiveLimit Limit { get; } = limit;
}
