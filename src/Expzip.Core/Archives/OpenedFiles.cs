namespace Expzip.Archives;

/// <summary>
/// 書庫を読むときに開いたファイルを覚える (#390)。
/// </summary>
/// <remarks>
/// <para>
/// 読み取りの側は、書庫のほかにも分割ファイルや隣のファイルを開く。<see cref="ArchiveSources"/> は、
/// CAB の続きをここで覚えたもので挙げる。読み方を 2 つ持たないため (CAB の見出しを自分で読むと、
/// <c>cabinet.dll</c> と食い違っても気づけない)。
/// </para>
/// <para>
/// テストは、実際の展開で開いたファイルがすべて <see cref="ArchiveSources"/> の挙げたものに入っているかを確かめる。
/// 覚えていないとき (<see cref="Record"/> の外) は何もしない。
/// </para>
/// </remarks>
internal static class OpenedFiles
{
    private static readonly AsyncLocal<Recording?> Current = new();

    /// <summary>ここから先で開いたファイルを覚える。閉じると、前に覚えていたものに戻す。</summary>
    public static Recording Record()
    {
        var recording = new Recording(Current.Value);
        Current.Value = recording;
        return recording;
    }

    /// <summary>書庫のファイルを開いたことを知らせる。</summary>
    public static void Note(string path) => Current.Value?.Add(Path.GetFullPath(path));

    /// <summary>覚えている間に開いたファイル。</summary>
    internal sealed class Recording(Recording? previous) : IDisposable
    {
        private readonly List<string> _paths = [];
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>開いた順。同じものは 1 回だけ。</summary>
        public IReadOnlyList<string> Paths
        {
            get
            {
                lock (_paths)
                {
                    return [.. _paths];
                }
            }
        }

        internal void Add(string path)
        {
            // 展開は並べて書くことがあるので、別のスレッドからも来る
            lock (_paths)
            {
                if (_seen.Add(path))
                {
                    _paths.Add(path);
                }
            }
        }

        public void Dispose() => Current.Value = previous;
    }
}
