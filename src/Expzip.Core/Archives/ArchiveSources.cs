namespace Expzip.Archives;

/// <summary>
/// 展開で読むかもしれないファイル (#390)。<see cref="MarkOfTheWeb.TryReadArchive"/> は、この全部の MotW を見る。
/// </summary>
/// <param name="Files">読むかもしれないファイルのフルパス。開いた書庫が最初。同じものは 1 回だけ。</param>
/// <param name="Complete">
/// 全部を挙げられたか。見出しが壊れている、表を読めない、数が多すぎる、などで挙げきれなければ <see langword="false"/>。
/// そのときは、インターネットから来たものとして扱う (弱い側に倒れないため)。
/// </param>
internal sealed record ArchiveSources(IReadOnlyList<string> Files, bool Complete)
{
    /// <summary>
    /// 挙げるファイルの数の上限。分割した RAR の上限 (<c>RarHeader</c> の 65536 個) と同じ。
    /// 超えたら挙げるのをやめ、挙げきれなかったものとして扱う。存在しないファイルを大量に指す MSI で、
    /// 1 個ずつ MotW を読みに行って時間がかかるのを防ぐ。テストで小さくする。
    /// </summary>
    internal static int Limit { get; set; } = 65536;

    /// <summary>
    /// 展開で読むかもしれないファイルを、読み取りの側と同じ決め方で挙げる。
    /// </summary>
    /// <param name="path">開いた書庫。</param>
    /// <param name="format">展開に使う形式。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <remarks>
    /// <list type="bullet">
    /// <item><c>.001</c> の分割ファイルは、形式によらず全部 (<see cref="SplitVolumes.Find"/>)。そのうえで形式ごとの続きを足す</item>
    /// <item>RAR: 最初の分割ファイルから、続きの名前のファイルがある限り。見出しは読まない。
    /// 読み取り (<c>RarHeader.Read</c>) は見出しが「続きがある」と言う間だけ進むので、実際に読むものは必ずこの中に入る</item>
    /// <item>CAB: 読み取りの処理を、何も取り出さない指定で走らせて、開いた続きの CAB を覚える (<see cref="OpenedFiles"/>)</item>
    /// <item>MSI: Media 表の隣の CAB (とその続き) と、圧縮していないファイル。MSI のフォルダーの外と、<c>:</c> を含む名前は、
    /// 読み取りも開かないので挙げない</item>
    /// <item>そのほか (CAB が入った exe、Burn、MSI が入った exe など): 書庫だけ。中の CAB は続きを探さない</item>
    /// </list>
    /// </remarks>
    public static ArchiveSources Of(string path, ArchiveFormat format, CancellationToken cancellationToken = default)
    {
        var list = new Collector();
        list.Add(path);

        try
        {
            if (SplitVolumes.IsFirstVolume(path))
            {
                foreach (var volume in SplitVolumes.Find(path))
                {
                    list.Add(volume);
                }
            }

            switch (format)
            {
                case ArchiveFormat.Rar:
                    AddRar(list, path);
                    break;
                case ArchiveFormat.Cab:
                    AddCabChain(list, () => CabReader.Visit(path, Nothing, static (_, _) => { }, cancellationToken), cancellationToken);
                    break;
                case ArchiveFormat.Msi:
                    AddMsi(list, path, cancellationToken);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // 読めない書庫。展開も同じところで失敗することが多いが、読み取りだけが成功する食い違いがあっても
            // 弱い側に倒れないよう、挙げきれなかったものとして扱う
            list.Incomplete = true;
        }

        return new ArchiveSources(list.Files, !list.Incomplete);
    }

    /// <summary>何も取り出さない指定。</summary>
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    private static void AddRar(Collector list, string path)
    {
        var first = RarVolumes.FirstVolumeOf(path) ?? path;
        list.Add(first);

        for (var index = 1; !list.Full; index++)
        {
            // 名前の形で決める (newNaming は使われない)。RarHeader.Read と同じ関数
            if (RarVolumes.NextVolume(first, index, newNaming: true) is not { } next || !File.Exists(next))
            {
                break;
            }

            list.Add(next);
        }
    }

    /// <summary>CAB の読み取りを走らせて、開いた続きの CAB を挙げる。</summary>
    private static void AddCabChain(Collector list, Action read, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var recording = OpenedFiles.Record();
        try
        {
            read();
        }
        finally
        {
            // 途中で失敗しても、そこまでに開いたものは挙げる
            foreach (var opened in recording.Paths)
            {
                list.Add(opened);
            }
        }
    }

    private static void AddMsi(Collector list, string path, CancellationToken cancellationToken)
    {
        var (files, media) = MsiReader.ReadLayout(path, cancellationToken);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path)) ?? string.Empty;

        foreach (var cabinet in media.Select(static m => m.Cabinet).Distinct(StringComparer.Ordinal))
        {
            if (list.Full)
            {
                return;
            }

            if (cabinet.Length == 0 || cabinet.StartsWith('#'))
            {
                // 埋め込まれた CAB は MSI の中にあり、続きは探さない
                continue;
            }

            if (CabReader.SiblingOf(folder, cabinet) is { } beside && File.Exists(beside))
            {
                list.Add(beside);
                AddCabChain(list, () => CabReader.Visit(() => File.OpenRead(beside), folder, Nothing,
                    static (_, _) => { }, cancellationToken), cancellationToken);
            }
        }

        foreach (var file in files)
        {
            if (list.Full)
            {
                return;
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (!file.Compressed && MsiReader.BesidePath(folder, file.SourcePath) is { } beside)
            {
                list.Add(beside);
            }
        }
    }

    /// <summary>挙げたファイル。上限を超えたら、それ以上は足さずに挙げきれなかったことにする。</summary>
    private sealed class Collector
    {
        private readonly List<string> _files = [];
        private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<string> Files => _files;

        public bool Incomplete { get; set; }

        /// <summary>上限に達したか。</summary>
        public bool Full => Incomplete && _files.Count >= Limit;

        public void Add(string path)
        {
            var full = Path.GetFullPath(path);
            if (_seen.Contains(full))
            {
                return;
            }

            if (_files.Count >= Limit)
            {
                Incomplete = true;
                return;
            }

            _seen.Add(full);
            _files.Add(full);
        }
    }
}
