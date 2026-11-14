using System.IO;
using System.IO.Compression;
using ICSharpCode.SharpZipLib.Zip;

// 標準ライブラリにも同じ名前の型があるため、こちら側の名前をはっきりさせる
using SharpZipFile = ICSharpCode.SharpZipLib.Zip.ZipFile;

namespace Expzip.Archives;

/// <summary>ファイル追加の進捗。</summary>
/// <param name="DoneBytes">追加済みのバイト数。</param>
/// <param name="TotalBytes">追加する合計バイト数。</param>
/// <param name="CurrentName">いま処理している書庫内の名前。</param>
internal readonly record struct AddProgress(long DoneBytes, long TotalBytes, string CurrentName)
{
    /// <summary>0〜100 の進捗率。</summary>
    public double Percent => TotalBytes <= 0 ? 100 : (double)DoneBytes / TotalBytes * 100.0;
}

/// <summary>ファイル追加の結果。</summary>
/// <param name="Added">新しく追加した数。</param>
/// <param name="Replaced">既存のエントリを置き換えた数。</param>
/// <param name="Skipped">同名が既にあり、置き換えずに飛ばした数。</param>
/// <param name="Failed">追加できなかったファイルとその理由。</param>
/// <param name="Cancelled">利用者の操作で中断した場合は true。</param>
internal sealed record AddResult(
    int Added,
    int Replaced,
    int Skipped,
    IReadOnlyList<(string Name, string Reason)> Failed,
    bool Cancelled)
{
    /// <summary>リンクとして入れたもの (#366)。指す先がフォルダーの外のものは、終わったあとに知らせる。</summary>
    public IReadOnlyList<AddedLink> Links { get; init; } = [];
}

/// <summary>削除の結果。</summary>
/// <param name="Deleted">削除したエントリ数。</param>
/// <param name="Cancelled">書庫を書き換える前に中断した場合は true。</param>
internal sealed record DeleteResult(int Deleted, bool Cancelled);

/// <summary>書庫内のパスの付け替え。名前の変更にも移動にも使う (#43)。</summary>
/// <param name="OldPath">変更前の書庫内パス。区切りは <c>/</c>、末尾に区切りは付けない。</param>
/// <param name="NewPath">変更後の書庫内パス。</param>
/// <param name="IsFolder">フォルダなら true。配下のエントリもまとめて付け替える。</param>
internal readonly record struct PathChange(string OldPath, string NewPath, bool IsFolder);

/// <summary>名前の変更の結果。</summary>
/// <param name="Renamed">名前を変えたエントリ数。フォルダの場合は配下を含む。</param>
/// <param name="Cancelled">書庫を書き換える前に中断した場合は true。</param>
internal sealed record RenameResult(int Renamed, bool Cancelled);

/// <summary>ZIP書庫を作成・更新する。</summary>
internal static class ZipArchiveWriter
{
    /// <summary>
    /// 作業用ファイルの場所を決める関数。画面の側が差し替えられる。<see langword="null"/> なら既定
    /// (同じフォルダーの、毎回違う名前)。
    /// </summary>
    /// <remarks>
    /// 書き換えは、作業用ファイルに書いてから書庫と差し替える。作業用ファイルを置ける場所や名前に
    /// 決まりのある環境で動かすときに差し替える。
    /// </remarks>
    internal static Func<string, string>? WorkFilePathOverride { get; set; }

    /// <summary>この書庫を書き換えるときの作業用ファイルの場所。</summary>
    internal static string WorkFilePathFor(string archivePath) => AtomicFile.TemporaryPathFor(archivePath);

    /// <summary>
    /// 作業用ファイルの開き方。既にあるファイルには書かない。決まった名前に差し替えてあるときだけ、
    /// 前回の残りがありうるので上書きする。
    /// </summary>
    internal static FileMode WorkFileMode => WorkFilePathOverride is null ? FileMode.CreateNew : FileMode.Create;

    /// <summary>
    /// 書き換えを始める。SharpZipLib の既定は、差し替えるときに書庫を別の名前へ退避するので、
    /// その間に止まると元の名前に書庫が無くなる。作業用ファイルからの 1 回の名前の変更で差し替える (#320)。
    /// </summary>
    private static void BeginUpdate(SharpZipFile zip, bool archiveIsNew = false)
        => zip.BeginUpdate(new WorkFileStorage(zip.Name, WorkFilePathFor(zip.Name), archiveIsNew));

    /// <summary>
    /// 空のZIP書庫を作る。既に同じ名前のファイルがあれば置き換える。
    /// </summary>
    /// <remarks>
    /// 中身が無くてもZIPとしては正しく、終端レコードだけを持つファイルになる。
    /// エントリ名の書き出しはデフォルト (UTF-8 + EFSフラグ) に任せる。読み取り時に
    /// 使う CP932 の判定は古い書庫を救うためのもので、こちらから作る書庫を
    /// あえて古い形式にする理由は無い。
    /// </remarks>
    /// <exception cref="IOException">ファイルを作成できない場合。</exception>
    public static void CreateEmpty(string path)
    {
        // 閉じた時点で終端レコードが書かれ、読み取り可能な書庫になる。失敗しても元のファイルは残る (#320)
        using var file = AtomicFile.Create(path);
        using (new ZipArchive(file.Stream, ZipArchiveMode.Create, leaveOpen: true))
        {
        }

        file.Commit();
    }

    /// <summary>
    /// ディスク上のファイルやフォルダを書庫に追加する。
    /// </summary>
    /// <param name="archivePath">追加先の書庫。</param>
    /// <param name="sourcePaths">追加するファイルまたはフォルダのパス。</param>
    /// <param name="destinationFolder">書庫内の追加先フォルダ。ルートは空文字。</param>
    /// <param name="replaceExisting">同名のエントリがある場合に置き換えるか。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <remarks>
    /// 元の書庫を直接書き換えず、複製した作業用ファイルを更新してから差し替える。
    /// 途中で中断や失敗が起きても元の書庫は無傷で残る。書き込みの失敗で書庫を
    /// 壊すことが最も避けたい事態のため、この手順にしている。
    /// ふだんは <see cref="ZipParallelWriter"/> がコアの数だけ並列に圧縮する (#195)。
    /// そちらで扱えない書庫だけ、ここで 1 つずつ圧縮する。
    /// </remarks>
    /// <param name="exclude">
    /// 追加するフォルダーの中にあっても入れないファイル。作っている書庫そのものが入らないようにする (#320)。
    /// </param>
    public static AddResult Add(
        string archivePath,
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        bool replaceExisting,
        IProgress<AddProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? exclude = null)
        => Add(archivePath, sourcePaths, destinationFolder, replaceExisting, progress, cancellationToken, exclude, archiveIsNew: false);

    /// <summary>
    /// 新しい書庫を作り、ファイルやフォルダーを入れる (#327)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <paramref name="archivePath"/> には、呼び出し側が決めた、まだ何も無い名前を渡す (一時的な名前)。
    /// 何かあれば作らずに断る。できあがったら、呼び出し側が <see cref="AtomicFile.Replace"/> で本来の名前に置き換える。
    /// そちらは、本来の名前に書庫があればディスクに書き切ってから置き換えるので、電源断でも元の書庫か新しい書庫が残る。
    /// </para>
    /// <para>
    /// 空の書庫を作ってから <see cref="Add"/> で入れると、最後に空の書庫を置き換えるときにディスクを待っていた。
    /// ここでは、作ったばかりの空の書庫なので待たない。
    /// </para>
    /// </remarks>
    /// <exception cref="IOException"><paramref name="archivePath"/> に既にファイルがある場合など。</exception>
    /// <param name="links">
    /// ディスクを通さずに入れるリンク (#367)。選んだ項目から新しい書庫を作るときに、元の書庫から読んだリンクを入れる。
    /// 開発者モードがオフの PC でも、リンクをリンクのまま入れられる。
    /// </param>
    public static AddResult CreateWith(
        string archivePath,
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        bool replaceExisting,
        IProgress<AddProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? exclude = null,
        IReadOnlyList<CarriedLink>? links = null)
    {
        // 閉じた時点で終端レコードが書かれ、読み取り可能な書庫になる
        using (var stream = new FileStream(archivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (new ZipArchive(stream, ZipArchiveMode.Create))
        {
        }

        return Add(archivePath, sourcePaths, destinationFolder, replaceExisting, progress, cancellationToken, exclude, archiveIsNew: true, links);
    }

    private static AddResult Add(
        string archivePath,
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        bool replaceExisting,
        IProgress<AddProgress>? progress,
        CancellationToken cancellationToken,
        IReadOnlyCollection<string>? exclude,
        bool archiveIsNew,
        IReadOnlyList<CarriedLink>? carried = null)
    {
        // 足す先の書庫そのものも入れない。追加するフォルダーの中に書庫があることがある (#366)
        var failed = new List<(string, string)>();
        List<PlanItem> plan;
        try
        {
            plan = BuildPlan(sourcePaths, destinationFolder, [.. exclude ?? [], archivePath], failed, cancellationToken);

            // 名前は、ファイルと同じく濁点を合わせた書き方にそろえる。そろえないと、リンクと同じ名前のフォルダーの重なりを見落とす
            foreach (var link in carried ?? [])
            {
                var name = link.EntryName.Normalize(System.Text.NormalizationForm.FormC);
                var archived = new ArchiveLink(link.Target, link.PointsToFolder, Outside: false);
                plan.Add(new PlanItem(link.SourcePath, name, ZipLinks.Content(archived).Length, false) { Link = archived });
            }
        }
        catch (OperationCanceledException)
        {
            // たどっている間の中止も、書き換えの途中の中止と同じく「中止した」と返す (#366)
            return new AddResult(0, 0, 0, failed, Cancelled: true);
        }

        CheckPlanDepth(plan);

        if (ZipParallelWriter.TryAdd(archivePath, plan, replaceExisting, progress, cancellationToken, archiveIsNew, failed) is { } fast)
        {
            return fast;
        }

        var added = 0;
        var replaced = 0;
        var skipped = 0;
        var links = new List<AddedLink>();
        var cancelled = false;

        // 実際に中身が読まれるのは CommitUpdate の最中。進み具合もそこで動く
        long readBytes = 0;
        long reportedAt = 0;

        // CreateWith の空の書庫はふつう並列の道で扱えるので、ここを archiveIsNew で通ることはまず無い。
        // 通っても、待たないのは作ったばかりの空の書庫だけ
        using var zip = ZipUpdate.Open(archivePath);

        // リンクと、同じ名前のフォルダーの中の項目が並ぶ書庫を作らない (#366)
        plan = DropLinkConflicts(
            plan, zip.Cast<ZipEntry>().Select(static e => (e.Name, ZipLinks.IsLink(e))), failed);

        var totalBytes = plan.Sum(static p => p.Length);

        BeginUpdate(zip, archiveIsNew);

        foreach (var item in plan)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var found = zip.FindEntry(item.EntryName, ignoreCase: false);

            if (found >= 0)
            {
                if (!replaceExisting)
                {
                    skipped++;
                    continue;
                }

                zip.Delete(zip[found]);
            }

            if (item.IsDirectory)
            {
                // 空のフォルダも構造として残す
                zip.AddDirectory(item.EntryName.TrimEnd('/'));
                added++;
                continue;
            }

            if (item.Link is { } link)
            {
                // 中身はディスクから読まず、指す先のパスを書く (#366)
                var entry = ZipUpdate.NewEntry(item.EntryName, CompressionMethod.Stored, ReadLastWriteTime(item.SourcePath));
                ZipLinks.Mark(entry, link.PointsToFolder);
                zip.Add(new ZipUpdate.BytesSource(ZipLinks.Content(link)), entry);
                links.Add(new AddedLink(item.EntryName, link.Target, link.Outside));

                if (found >= 0)
                {
                    replaced++;
                }
                else
                {
                    added++;
                }

                continue;
            }

            // 読めるかを確かめ、あわせて圧縮するかどうかを決める (#38)。
            // 書き出しが始まってから読めないと分かると、その回の書き換えが
            // まるごと取りやめになり、他の分まで巻き添えになる
            var choice = CompressionChoice.Probe(item.SourcePath);
            if (choice.Error is { } reason)
            {
                failed.Add((item.SourcePath, reason));
                continue;
            }

            // 選んだ場所の中のファイルかも、ここで確かめる (#366)。中身を読むのは CommitUpdate の中で、
            // そこで気付くと追加の全体が取りやめになる (書庫は元のまま残る)
            try
            {
                using (ReadScope.Open(item.Scope, item.SourcePath))
                {
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed.Add((item.SourcePath, Localization.Strings.Reason(ex)));
                continue;
            }

            var name = item.EntryName;

            zip.Add(
                new ZipUpdate.FileSource(item.SourcePath, cancellationToken, item.Scope, read =>
                {
                    readBytes += read;

                    // 読むたびに知らせると細かすぎる。1MB ごとに間引く
                    if (readBytes - reportedAt < ProgressStep && readBytes < totalBytes)
                    {
                        return;
                    }

                    reportedAt = readBytes;
                    progress?.Report(new AddProgress(readBytes, totalBytes, name));
                }),
                ZipUpdate.NewEntry(name, choice.Method, ReadLastWriteTime(item.SourcePath)));

            if (found >= 0)
            {
                replaced++;
            }
            else
            {
                added++;
            }
        }

        if (cancelled || !TryCommit(zip, cancellationToken))
        {
            return new AddResult(0, 0, 0, failed, Cancelled: true);
        }

        return new AddResult(added, replaced, skipped, failed, Cancelled: false) { Links = links };
    }

    /// <summary>
    /// リンクと、同じ名前のフォルダーの中の項目が並ぶことになるものを、追加から外す (#366)。
    /// </summary>
    /// <remarks>
    /// 書庫に <c>docs/a.txt</c> があるところへ <c>docs</c> という名前のリンクを足すと、両方が残る。それをリンクを展開できるツールで
    /// 展開すると、<c>docs/a.txt</c> がリンクを通って展開先の外に書かれる。攻撃に使われる形を、ふつうの操作で作らない。
    /// 大文字と小文字は区別しない (Windows で同じ場所になるため)。外したものは理由を添えて知らせる。
    /// </remarks>
    /// <param name="plan">追加するもの。</param>
    /// <param name="existing">書庫に既にある項目の名前と、リンクかどうか。</param>
    /// <param name="failed">外したものの受け口。</param>
    internal static List<PlanItem> DropLinkConflicts(
        List<PlanItem> plan, IEnumerable<(string Name, bool IsLink)> existing, List<(string, string)> failed)
    {
        var overlap = new LinkOverlap();
        foreach (var (name, isLink) in existing)
        {
            overlap.Note(name, isLink);
        }

        foreach (var item in plan)
        {
            overlap.Note(item.EntryName, item.Link is not null);
        }

        if (!overlap.HasLinks)
        {
            return plan;
        }

        var kept = new List<PlanItem>(plan.Count);
        foreach (var item in plan)
        {
            if (overlap.Conflicts(item.EntryName, item.Link is not null) is not null)
            {
                failed.Add((item.SourcePath, Localization.Strings.LinkOverlapsFolder));
                continue;
            }

            kept.Add(item);
        }

        return kept;
    }

    /// <summary>
    /// 書庫の項目の名前を集めて、リンクと重なる名前を見つける (#366)。大文字と小文字は区別しない (Windows で同じ場所になるため)。
    /// </summary>
    /// <remarks>
    /// 重なりは 3 つ。リンクの中を通る名前 (<c>docs</c> がリンクで <c>docs/a.txt</c>)、リンクと同じ名前のフォルダー
    /// (<c>docs</c> がリンクで <c>docs/</c>)、リンクと大文字と小文字だけが違う名前 (<c>Docs</c> がリンクで <c>docs</c>)。
    /// まったく同じ名前は、置き換えなので重なりとしない。
    /// </remarks>
    private sealed class LinkOverlap
    {
        private readonly HashSet<string> _links = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, HashSet<string>> _spellings = new(StringComparer.OrdinalIgnoreCase);

        public bool HasLinks => _links.Count > 0;

        public void Note(string name, bool isLink)
        {
            var path = ArchivePath.Normalize(name).TrimEnd('/');
            if (isLink)
            {
                _links.Add(path);
            }

            if (!_spellings.TryGetValue(path, out var spellings))
            {
                _spellings[path] = spellings = new HashSet<string>(StringComparer.Ordinal);
            }

            spellings.Add(path);

            // フォルダーそのものの項目は、それ自身も中身のあるフォルダーと同じ扱い
            var start = name.EndsWith('/') ? path : ParentOf(path);
            for (var folder = start; folder.Length > 0; folder = ParentOf(folder))
            {
                if (!_folders.Add(folder))
                {
                    break;
                }
            }
        }

        /// <summary>この名前がリンクと重なるなら、重なるリンクの名前。重ならなければ <see langword="null"/>。</summary>
        public string? Conflicts(string name, bool isLink)
        {
            var path = ArchivePath.Normalize(name).TrimEnd('/');

            for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
            {
                if (_links.Contains(path[..slash]))
                {
                    return path[..slash];
                }
            }

            var sameAsLink = _links.Contains(path);
            if (!isLink && !sameAsLink)
            {
                return null;
            }

            // リンクと同じ名前のフォルダー
            if (_folders.Contains(path))
            {
                return path;
            }

            // 大文字と小文字だけが違う、別の項目
            return _spellings.TryGetValue(path, out var spellings) && spellings.Count > 1 ? path : null;
        }
    }

    /// <summary>経過を知らせる間隔。1回の読み取りごとに出すと細かすぎる。</summary>
    private const long ProgressStep = 1024 * 1024;

    /// <summary>
    /// 書き換えを確定する。中断されたら取りやめる。
    /// </summary>
    /// <remarks>
    /// 中身を読むのは <c>CommitUpdate</c> の最中で、そこに割り込む余地は無い。
    /// 読み取りの側 (<see cref="ZipUpdate.FileSource"/>) が中断を投げると
    /// ここに届く。SharpZipLib は別のファイルへ書いてから差し替えるため、
    /// 取りやめても元の書庫はそのまま残る。
    /// </remarks>
    /// <returns>確定できた場合は true。中断された場合は false。</returns>
    private static bool TryCommit(SharpZipFile zip, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            TryAbort(zip);
            return false;
        }

        try
        {
            Commit(zip);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryAbort(zip);
            return false;
        }
    }

    /// <summary>
    /// 書き換えを確定する。MotW とアクセス権は、差し替えのときに <see cref="AtomicFile.Replace"/> が引き継ぐ (#211)。
    /// </summary>
    private static void Commit(SharpZipFile zip) => zip.CommitUpdate();

    private static void TryAbort(SharpZipFile zip)
    {
        try
        {
            zip.AbortUpdate();
        }
        catch (Exception ex) when (ex is ZipException or IOException or InvalidOperationException)
        {
            // 既に片付いている場合もある。取りやめの失敗で例外を重ねない
        }
    }

    /// <summary>
    /// 書庫の中に空のフォルダを作る (#50)。
    /// </summary>
    /// <param name="archivePath">対象の書庫。</param>
    /// <param name="folderPath">
    /// 作るフォルダの書庫内パス。区切りは <c>/</c>、末尾に区切りは付けない。
    /// </param>
    /// <returns>作れた場合は true。既に同じ場所に同じ名前がある場合は false。</returns>
    /// <remarks>
    /// <para>
    /// ZIPはフォルダを明示的に持たなくてもよく、中身のあるフォルダはファイルの
    /// パスから組み立てられる。空のフォルダはそれでは表せないため、末尾が
    /// <c>/</c> のエントリを1件だけ書き込む。
    /// </para>
    /// <para>
    /// 追加や削除と同じく、複製した作業用ファイルを更新してから差し替える。
    /// 失敗しても元の書庫は無傷で残る。
    /// </para>
    /// </remarks>
    /// <exception cref="IOException">書庫を書き換えられない場合。</exception>
    public static bool CreateFolder(string archivePath, string folderPath)
    {
        var entryName = ArchivePath.Normalize(folderPath) + "/";
        ArchiveLimits.CheckDepthToWrite(entryName);

        using var zip = ZipUpdate.Open(archivePath);

        // 同名のエントリだけでなく配下の有無も見る。中身のあるフォルダは
        // フォルダ自身のエントリを持たないことがあり、それを見落とすと
        // 既にあるフォルダに二重の印を付けてしまう
        var taken = zip.Cast<ZipEntry>().Any(
            e => ArchivePath.Normalize(e.Name)
                .StartsWith(entryName, StringComparison.OrdinalIgnoreCase));

        if (taken)
        {
            return false;
        }

        // リンクの中にフォルダーを作らない (#366)
        CheckLinkOverlap(
            zip.Cast<ZipEntry>().Select(static e => (e.Name, ZipLinks.IsLink(e))).Append((entryName, false)), [entryName]);

        BeginUpdate(zip);
        zip.AddDirectory(entryName.TrimEnd('/'));
        Commit(zip);
        return true;
    }

    /// <summary>
    /// 書庫からエントリを削除する。
    /// </summary>
    /// <param name="archivePath">対象の書庫。</param>
    /// <param name="fileEntryNames">名前が完全に一致するエントリを削除する。</param>
    /// <param name="folderPaths">
    /// このフォルダ自身と配下のエントリをすべて削除する。区切りは <c>/</c> の正規化済みパス。
    /// </param>
    /// <param name="cancellationToken">中断用。</param>
    /// <remarks>
    /// <para>
    /// 追加と同じく、複製した作業用ファイルを更新してから差し替える。
    /// 中断や失敗が起きても元の書庫は無傷で残る。
    /// </para>
    /// <para>
    /// 中断できるのは書庫を書き換え始める前まで。ZIPは1件消すだけでも
    /// 全体を書き直す必要があり、その書き出しは途中で止められない。
    /// </para>
    /// </remarks>
    public static DeleteResult Delete(
        string archivePath,
        IReadOnlySet<string> fileEntryNames,
        IReadOnlyList<string> folderPaths,
        CancellationToken cancellationToken)
    {
        var deleted = 0;

        using var zip = ZipUpdate.Open(archivePath);

        // Delete するとコレクションが変わるので、先に対象を確定させる
        var targets = zip.Cast<ZipEntry>()
            .Where(e => ShouldDelete(e.Name, fileEntryNames, folderPaths))
            .ToList();

        if (cancellationToken.IsCancellationRequested)
        {
            return new DeleteResult(0, Cancelled: true);
        }

        var emptied = FoldersLeftEmpty(
            zip.Cast<ZipEntry>().Select(static e => e.Name), fileEntryNames, folderPaths);

        BeginUpdate(zip);

        foreach (var entry in targets)
        {
            zip.Delete(entry);
            deleted++;
        }

        foreach (var folder in emptied)
        {
            zip.AddDirectory(folder);
        }

        if (!TryCommit(zip, cancellationToken))
        {
            return new DeleteResult(0, Cancelled: true);
        }

        return new DeleteResult(deleted, Cancelled: false);
    }

    /// <summary>
    /// 削除で中身が無くなるフォルダー (#203)。空のフォルダーとして残すために使う。
    /// </summary>
    /// <remarks>
    /// <para>
    /// ZIP はフォルダーそのものを記録しなくてもよく、ファイルの名前から組み立てることが多い。
    /// そのため、フォルダーの中のファイルを全部消すと、フォルダーまで消えてしまう。
    /// エクスプローラーや Finder では、中身を全部消してもフォルダーは空のまま残る。
    /// </para>
    /// <para>
    /// 削除するフォルダーそのものは残さない。そのフォルダーが入っていた親が空になるなら、親を残す。
    /// 奥のフォルダーを残せば、その親も残るので、いちばん奥のものだけを返す。
    /// 区切りは <c>/</c>、末尾に区切りは付けない。
    /// </para>
    /// </remarks>
    internal static List<string> FoldersLeftEmpty(
        IEnumerable<string> entryNames, IReadOnlySet<string> fileEntryNames, IReadOnlyList<string> folderPaths)
    {
        // 残るものが入っているフォルダー (奥から上まで全部)
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var name in entryNames)
        {
            var path = ArchivePath.Normalize(name).TrimEnd('/');

            if (!ShouldDelete(name, fileEntryNames, folderPaths))
            {
                // フォルダーのエントリなら、そのフォルダー自身も中身があるのと同じ扱い
                var start = name.EndsWith('/') ? path : ParentOf(path);
                for (var folder = start; folder.Length > 0; folder = ParentOf(folder))
                {
                    occupied.Add(folder);
                }

                continue;
            }

            // 消えるものの親。削除するフォルダーの中なら、その外まで上がる
            var parent = ParentOf(path);
            while (parent.Length > 0 && ShouldDelete(parent + "/", fileEntryNames, folderPaths))
            {
                parent = ParentOf(parent);
            }

            if (parent.Length > 0)
            {
                candidates.Add(parent);
            }
        }

        var emptied = candidates.Where(c => !occupied.Contains(c)).ToList();
        return emptied
            .Where(c => !emptied.Any(other => other.StartsWith(c + "/", StringComparison.Ordinal)))
            .ToList();

    }

    /// <summary>
    /// 移動で中身が無くなる、移す元のフォルダー (#203)。空のフォルダーとして残すために使う。
    /// 削除と同じく、中身を全部ほかへ移すと、元のフォルダーまで消えてしまうため。
    /// </summary>
    /// <param name="namesAfter">移したあとに書庫にあるエントリの名前 (移したものは新しい名前)。</param>
    internal static List<string> FoldersLeftEmptyByMove(
        IEnumerable<string> namesAfter, IReadOnlyList<PathChange> changes)
    {
        var occupied = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in namesAfter)
        {
            var path = ArchivePath.Normalize(name).TrimEnd('/');
            var start = name.EndsWith('/') ? path : ParentOf(path);
            for (var folder = start; folder.Length > 0; folder = ParentOf(folder))
            {
                occupied.Add(folder);
            }
        }

        return changes
            .Select(static c => ParentOf(c.OldPath))
            .Where(parent => parent.Length > 0 && !occupied.Contains(parent))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string ParentOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    /// <summary>このエントリが削除の対象かどうか。</summary>
    internal static bool ShouldDelete(
        string entryName, IReadOnlySet<string> fileEntryNames, IReadOnlyList<string> folderPaths)
    {
        if (fileEntryNames.Contains(entryName))
        {
            return true;
        }

        var normalized = ArchivePath.Normalize(entryName);
        var trimmed = normalized.TrimEnd('/');

        foreach (var folder in folderPaths)
        {
            // フォルダ自身のエントリと、その配下すべて
            if (trimmed == folder || normalized.StartsWith(folder + "/", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 追加した場合に書庫内で使われる名前の一覧。
    /// 追加を始める前に、既存のエントリと衝突するかどうかを調べるために使う。
    /// </summary>
    /// <summary>追加する名前の階層を、書き始める前に確かめる (#294)。</summary>
    internal static void CheckPlanDepth(List<PlanItem> plan)
    {
        foreach (var item in plan)
        {
            ArchiveLimits.CheckDepthToWrite(item.IsDirectory ? item.EntryName.TrimEnd('/') + "/" : item.EntryName);
        }
    }

    public static IReadOnlyList<string> PlanEntryNames(
        IReadOnlyList<string> sourcePaths, string destinationFolder, CancellationToken cancellationToken = default)
        => BuildPlan(sourcePaths, destinationFolder, cancellationToken: cancellationToken)
            .Select(static p => p.EntryName).ToList();

    /// <summary>
    /// 追加するファイルの一覧。フォルダーは中身をたどって展開する。
    /// </summary>
    /// <param name="sourcePaths">選んだファイルとフォルダー。</param>
    /// <param name="destinationFolder">書庫の中の追加先。</param>
    /// <param name="exclude">
    /// 入れないファイル。作っている書庫そのものなど (#320)。実際の場所で比べるので、リンクを通って選んだフォルダーの中にあっても入れない (#366)。
    /// </param>
    /// <param name="failed">入れられないものと理由の受け口。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <remarks>
    /// <para>
    /// フォルダーの中にあるシンボリックリンクとジャンクションの中には入らず、リンクとして入れる (#366)。中に入ると、
    /// 自分の親を指すリンクで回り続けて落ちる。指す先がフォルダーの外なら、入れるつもりの無いファイルまで書庫に入る。
    /// </para>
    /// <para>
    /// 選んだもの自体がリンクなら、指す先をたどる。選んだ人はその中身を入れるつもりだと考える (#366 で決めた)。
    /// その中にあるリンクはリンクとして入れるので、回り続けない。
    /// </para>
    /// </remarks>
    /// <exception cref="ArchiveTooLargeException">フォルダーの階層が深すぎる場合。</exception>
    internal static List<PlanItem> BuildPlan(
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        IReadOnlyCollection<string>? exclude = null,
        List<(string, string)>? failed = null,
        CancellationToken cancellationToken = default)
    {
        var prefix = destinationFolder.Length == 0 ? string.Empty : destinationFolder.TrimEnd('/') + "/";
        var plan = new List<PlanItem>();
        failed ??= [];

        var excluded = new HashSet<string>(
            (exclude ?? []).Select(DiskLinks.Canonical), StringComparer.OrdinalIgnoreCase);

        foreach (var path in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (Directory.Exists(path))
            {
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

                // 書庫の中の名前を元のフォルダーの名前から作るので、ドライブ全体には名前が付かない。コマンドラインでも断っている
                if (string.Equals(Path.GetPathRoot(root), root, StringComparison.OrdinalIgnoreCase))
                {
                    failed.Add((path, Localization.Strings.DriveCannotBeAdded));
                    continue;
                }

                var scope = ScopeOf(root);
                string[] roots = scope is null ? [root] : [root, scope.Path];
                var walk = new Walk(plan, failed, excluded, scope, roots, cancellationToken);

                // 入れないファイルと比べる場所は、入れないファイルと同じ形 (ドライブ文字) でそろえる。
                // 読む場所の確かめ (scope) はドライブ文字の無いボリュームで GUID の形になることがある
                walk.AddDirectory(root, DiskLinks.Canonical(root),
                    prefix + EntryNameOf(root), DiskLinks.IdentityOf(root) is { } id ? [id] : []);
            }
            else if (File.Exists(path))
            {
                var scope = ScopeOf(path);
                var canonical = scope?.Path ?? DiskLinks.Canonical(path);
                if (excluded.Contains(canonical))
                {
                    continue;
                }

                // 利用者が選んだものは、.DS_Store でもそのまま入れる。リンクなら指す先を入れるので、大きさも指す先から取る (#366)
                plan.Add(new PlanItem(path, prefix + EntryNameOf(path), LengthOf(path), false)
                {
                    Scope = scope,
                });
            }
        }

        return plan;
    }

    /// <summary>選んだものの実際の場所。読むときに、その中のものを読んでいるかを確かめる (#366)。</summary>
    private static ReadScope? ScopeOf(string path)
        => DiskLinks.FinalPathOf(path) is { } dos ? new ReadScope(dos, ByGuid: false)
            : DiskLinks.FinalPathOf(path, byGuid: true) is { } guid ? new ReadScope(guid, ByGuid: true)
            : null;

    /// <summary>ファイルの大きさ。リンクなら指す先の大きさ (<see cref="FileInfo.Length"/> はリンクそのものの大きさを返す)。</summary>
    private static long LengthOf(string path)
    {
        var info = new FileInfo(path);
        if ((info.Attributes & FileAttributes.ReparsePoint) == 0)
        {
            return info.Length;
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return stream.Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return info.Length;
        }
    }

    /// <summary>
    /// ディスクの名前から、書庫に入れる名前を作る。濁点や半濁点が分かれていない形 (NFC) に揃える (#202)。
    /// macOS で作られたファイルの名前は「ガ」を「カ」と「゛」の 2 文字で持っていることがあり、そのまま入れると
    /// Windows で開いたときに濁点が離れて見える。Windows のファイル名はふつう元から NFC なので変わらない
    /// </summary>
    private static string EntryNameOf(string path)
        => Path.GetFileName(path).Normalize(System.Text.NormalizationForm.FormC);

    /// <summary>
    /// フォルダーの中にあっても書庫に入れないファイルか (#202)。macOS の Finder がフォルダーごとに作る
    /// .DS_Store (表示の設定などを持つ隠しファイル) は、Windows で開くと知らないファイルに見える
    /// </summary>
    private static bool IsFolderClutter(string file)
        => string.Equals(Path.GetFileName(file), ".DS_Store", StringComparison.OrdinalIgnoreCase);

    /// <summary>選んだフォルダー 1 個をたどる。</summary>
    /// <param name="Plan">追加するものの受け口。</param>
    /// <param name="Failed">入れられないものと理由の受け口。</param>
    /// <param name="Excluded">入れないファイルの実際の場所。</param>
    /// <param name="Scope">選んだフォルダーの実際の場所。</param>
    /// <param name="Roots">選んだフォルダーの、たどってきたとおりのパスと実際の場所。リンクが中を指すかを決めるのに使う。</param>
    /// <param name="CancellationToken">中断用。</param>
    private sealed record Walk(
        List<PlanItem> Plan,
        List<(string, string)> Failed,
        HashSet<string> Excluded,
        ReadScope? Scope,
        IReadOnlyList<string> Roots,
        CancellationToken CancellationToken)
    {
        /// <param name="directory">たどってきたとおりのパス。</param>
        /// <param name="canonicalDirectory">実際の場所。入れないファイルと比べるのに使う。</param>
        /// <param name="entryPrefix">書庫の中の名前。</param>
        /// <param name="ancestors">たどっている途中の親フォルダーの識別子。</param>
        public void AddDirectory(string directory, string canonicalDirectory, string entryPrefix, DiskIdentity[] ancestors)
        {
            CancellationToken.ThrowIfCancellationRequested();

            // たどる途中で止める。書き始める前の確かめ (CheckPlanDepth) を待つと、その前にスタックが溢れることがある
            ArchiveLimits.CheckDepthToWrite(entryPrefix + "/");

            // 項目の数も、たどる途中で確かめる。NAS などで同じ中身が何度も見えると、メモリーを使い切って落ちる
            if ((long)Plan.Count * ArchiveLimits.BytesPerEntry > ArchiveLimits.MemoryBudget)
            {
                throw new ArchiveTooLargeException(ArchiveLimit.Entries);
            }

            List<DiskItem> items;
            try
            {
                items = DiskLinks.List(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Failed.Add((directory, Localization.Strings.Reason(ex)));
                return;
            }

            // .DS_Store だけが入ったフォルダーも、空のフォルダーとして残す。Windows が置いた互換のためのジャンクションは数えない
            var kept = items.Where(static i => !i.IsWindowsCompatibilityJunction
                                               && (i.IsDirectory || !IsFolderClutter(i.Path))).ToList();

            if (kept.Count == 0)
            {
                // 空のフォルダーは、そのままだと書庫に残らないのでフォルダーのエントリを作る
                Plan.Add(new PlanItem(directory, entryPrefix + "/", 0, true));
                return;
            }

            // 今までどおり、ファイルを先に、フォルダーを後に並べる
            foreach (var item in kept.Where(static i => !i.IsDirectory || i.IsNameSurrogate || i.IsAppExecLink))
            {
                AddItem(item, canonicalDirectory, entryPrefix + "/" + EntryNameOf(item.Path));
            }

            foreach (var item in kept.Where(static i => i.IsDirectory && !i.IsNameSurrogate && !i.IsAppExecLink))
            {
                var identity = DiskLinks.IdentityOf(item.Path);
                if (identity is { } id && ancestors.Contains(id))
                {
                    // NAS などでは、リンクがふつうのフォルダーに見えることがある。同じフォルダーに 2 度入らない
                    Failed.Add((item.Path, Localization.Strings.FolderContainsItself));
                    continue;
                }

                AddDirectory(
                    item.Path, Path.Join(canonicalDirectory, Path.GetFileName(item.Path)),
                    entryPrefix + "/" + EntryNameOf(item.Path),
                    identity is { } found ? [.. ancestors, found] : ancestors);
            }
        }

        private void AddItem(DiskItem item, string canonicalDirectory, string entryName)
        {
            if (item.IsAppExecLink || (item.IsNameSurrogate && !item.IsSymbolicLink && !item.IsJunction))
            {
                // WSL のシンボリックリンクなど。ZIP にリンクとして書く決まりが無く、中身としても読めない
                Failed.Add((item.Path, Localization.Strings.LinkKindNotSupported));
                return;
            }

            if (item.IsNameSurrogate)
            {
                // 中には入らず、リンクとして入れる。指す先には触れない (#366)
                var link = DiskLinks.ReadTarget(item) is { } read
                    ? ZipLinks.ForArchive(item.Path, read.Target, read.Relative, item.IsDirectory, Roots)
                    : null;

                if (link is null)
                {
                    Failed.Add((item.Path, Localization.Strings.LinkTargetNotSupported));
                    return;
                }

                Plan.Add(new PlanItem(item.Path, entryName, ZipLinks.Content(link).Length, false) { Link = link });
                return;
            }

            if (Excluded.Contains(Path.Join(canonicalDirectory, Path.GetFileName(item.Path))))
            {
                return;
            }

            long length;
            try
            {
                length = new FileInfo(item.Path).Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                length = 0;
            }

            Plan.Add(new PlanItem(item.Path, entryName, length, false) { Scope = Scope });
        }
    }

    /// <summary>更新日時を読む。読めない場合は現在時刻を使う。</summary>
    internal static DateTime ReadLastWriteTime(string path)
    {
        try
        {
            var value = File.GetLastWriteTime(path);

            // ZIPの日時は1980年以降しか表現できない
            return value.Year < 1980 ? DateTime.Now : value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentOutOfRangeException)
        {
            return DateTime.Now;
        }
    }

    /// <summary>
    /// 書庫内のファイルまたはフォルダの名前を変える (#15)。
    /// </summary>
    /// <param name="archivePath">書庫ファイルのパス。</param>
    /// <param name="oldPath">変更前の書庫内パス。区切りは <c>/</c>、末尾に区切りは付けない。</param>
    /// <param name="newPath">変更後の書庫内パス。</param>
    /// <param name="isFolder">フォルダなら true。配下のエントリもまとめて付け替える。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <remarks>
    /// <para>
    /// ZIPのエントリ名を直接書き換える手段が <see cref="System.IO.Compression"/> には無い。
    /// 新しい名前でエントリを作り、中身を移してから元を消す形になるため、
    /// **名前を変えたエントリは圧縮し直される**。圧縮後のサイズが変わることがあるが、
    /// 中身は変わらない。書庫全体を作り直すわけではないので、対象外のエントリは
    /// そのまま持ち越される。
    /// </para>
    /// <para>
    /// 他の更新と同じく作業用ファイル上で行い、最後に差し替える。
    /// 途中で失敗しても元の書庫はそのまま残る。
    /// </para>
    /// </remarks>
    public static RenameResult Rename(
        string archivePath,
        string oldPath,
        string newPath,
        bool isFolder,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
        => Move(archivePath, [new PathChange(oldPath, newPath, isFolder)],
                progress, cancellationToken);

    /// <summary>
    /// 書庫内の項目をまとめて別の場所へ移す (#43)。名前の変更もこの一種として扱う。
    /// </summary>
    /// <param name="archivePath">書庫ファイルのパス。</param>
    /// <param name="changes">付け替える書庫内パスの組。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <inheritdoc cref="Rename" path="/remarks"/>
    public static RenameResult Move(
        string archivePath,
        IReadOnlyList<PathChange> changes,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var renamed = 0;
        var carried = new List<(string NewName, string TempPath, DateTime Stamp, ZipEntry Original)>();

        try
        {
            using (var zip = ZipUpdate.Open(archivePath))
            {
                // 付け替える対象を先に確定させる
                var targets = zip.Cast<ZipEntry>()
                    .Select(e => (Entry: e, NewName: MapAny(e.Name, changes)))
                    .Where(static x => x.NewName is not null)
                    .ToList();

                // 動かすフォルダーの中身まで含めて、移したあとの名前で確かめる (#294)
                foreach (var (_, newName) in targets)
                {
                    ArchiveLimits.CheckDepthToWrite(newName!);
                }

                // 移したあとに、リンクの中を通る名前ができないか (#366)
                CheckLinkOverlap(
                    zip.Cast<ZipEntry>().Select(e => (MapAny(e.Name, changes) ?? e.Name, ZipLinks.IsLink(e))),
                    targets.Select(static t => t.NewName!));

                // 名前を変えるものの中身だけ、いったん外へ取り出す。
                // 書き換えの最中に同じ書庫から読むのは避けたいため。
                // 対象外のエントリには触らないので、そのまま写される
                foreach (var (entry, newName) in targets)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new RenameResult(0, Cancelled: true);
                    }

                    if (newName!.EndsWith('/'))
                    {
                        // フォルダそのものを表すエントリは中身を持たない
                        carried.Add((newName, string.Empty, entry.DateTime, entry));
                        continue;
                    }

                    var holding = Path.GetTempFileName();

                    using (var source = zip.GetInputStream(entry))
                    using (var destination = new FileStream(
                               holding, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        CancellableCopy.Copy(source, destination, cancellationToken);
                    }

                    carried.Add((newName, holding, entry.DateTime, entry));
                }

                var remaining = zip.Cast<ZipEntry>()
                    .Where(e => MapAny(e.Name, changes) is null)
                    .Select(static e => e.Name)
                    .Concat(targets.Select(static t => t.NewName!));
                var emptied = FoldersLeftEmptyByMove(remaining, changes);

                BeginUpdate(zip);

                foreach (var (entry, _) in targets)
                {
                    zip.Delete(entry);
                }

                foreach (var folder in emptied)
                {
                    zip.AddDirectory(folder);
                }

                foreach (var (newName, holding, stamp, original) in carried)
                {
                    if (holding.Length == 0)
                    {
                        zip.AddDirectory(newName.TrimEnd('/'));
                    }
                    else
                    {
                        // リンクの印も写す。写さないと、リンク先のパスが書かれたふつうのファイルに変わる (#366)
                        var moved = ZipUpdate.NewEntry(
                            newName, ZipLinks.IsLink(original) ? CompressionMethod.Stored : CompressionChoice.Probe(holding).Method, stamp);
                        ZipLinks.CopyMark(original, moved);
                        zip.Add(new ZipUpdate.FileSource(holding, cancellationToken), moved);
                    }

                    renamed++;
                    progress?.Report(renamed);
                }

                if (!TryCommit(zip, cancellationToken))
                {
                    return new RenameResult(0, Cancelled: true);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return new RenameResult(0, Cancelled: true);
        }
        finally
        {
            foreach (var (_, holding, _, _) in carried)
            {
                if (holding.Length > 0)
                {
                    TryDelete(holding);
                }
            }
        }

        return new RenameResult(renamed, Cancelled: false);
    }

    /// <summary>
    /// 書き換えたあとの書庫に、リンクの中を通る名前や、中に項目のあるリンクができないかを確かめる (#366)。
    /// </summary>
    /// <param name="namesAfter">書き換えたあとの、すべての項目の名前と、リンクかどうか。</param>
    /// <param name="changed">この操作で新しくできる名前。</param>
    /// <remarks>
    /// 止めるのは、この操作で新しくできる名前が重なりに関わるときだけ。ほかのツールで作った、もともと重なりのある書庫でも、
    /// 関係の無いところの編集は止めない。
    /// </remarks>
    /// <exception cref="LinkOverlapException">できてしまう場合。書き換える前に止める。</exception>
    internal static void CheckLinkOverlap(IEnumerable<(string Name, bool IsLink)> namesAfter, IEnumerable<string> changed)
    {
        var all = namesAfter.ToList();
        var overlap = new LinkOverlap();
        foreach (var (name, isLink) in all)
        {
            overlap.Note(name, isLink);
        }

        if (!overlap.HasLinks)
        {
            return;
        }

        var touched = new HashSet<string>(
            changed.Select(static n => ArchivePath.Normalize(n).TrimEnd('/')), StringComparer.OrdinalIgnoreCase);

        foreach (var (name, isLink) in all)
        {
            var path = ArchivePath.Normalize(name).TrimEnd('/');
            if (overlap.Conflicts(name, isLink) is { } link && (touched.Contains(path) || touched.Contains(link)))
            {
                throw new LinkOverlapException();
            }
        }
    }

    /// <summary>いずれかの組に当てはめた結果を返す。どれにも当たらなければ null。</summary>
    internal static string? MapAny(string entryName, IReadOnlyList<PathChange> changes)
    {
        foreach (var change in changes)
        {
            if (MapName(entryName, change.OldPath, change.NewPath, change.IsFolder) is { } mapped)
            {
                return mapped;
            }
        }

        return null;
    }

    /// <summary>
    /// エントリ名を付け替えた結果を返す。対象外なら <see langword="null"/>。
    /// </summary>
    private static string? MapName(string entryName, string oldPath, string newPath, bool isFolder)
    {
        var normalized = ArchivePath.Normalize(entryName);
        var isDirectoryEntry = normalized.EndsWith('/');
        var bare = isDirectoryEntry ? normalized.TrimEnd('/') : normalized;

        if (string.Equals(bare, oldPath, StringComparison.Ordinal))
        {
            return isDirectoryEntry ? newPath + "/" : newPath;
        }

        // フォルダなら配下もまとめて付け替える
        if (isFolder && bare.StartsWith(oldPath + "/", StringComparison.Ordinal))
        {
            var suffix = bare[oldPath.Length..];
            return isDirectoryEntry ? newPath + suffix + "/" : newPath + suffix;
        }

        return null;
    }

    internal static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>追加する1件分。</summary>
    /// <param name="SourcePath">ディスク上のパス。</param>
    /// <param name="EntryName">書庫内での名前。区切りは <c>/</c>。</param>
    /// <param name="Length">ファイルの大きさ。進捗の計算に使う。</param>
    /// <param name="IsDirectory">空のフォルダを表すエントリなら true。</param>
    internal readonly record struct PlanItem(string SourcePath, string EntryName, long Length, bool IsDirectory)
    {
        /// <summary>リンクとして入れるなら、その指す先 (#366)。中身はディスクから読まず、指す先のパスを書く。</summary>
        public ArchiveLink? Link { get; init; }

        /// <summary>
        /// 読んでよい場所。開いたあとで実際の場所がこの中かを確かめる (#366)。
        /// 確かめてから読むまでの間にリンクに差し替えられても、外のファイルを書庫に入れない。
        /// </summary>
        public ReadScope? Scope { get; init; }
    }
}

/// <summary>読んでよい場所 (#366)。</summary>
/// <param name="Path">選んだフォルダーかファイルの実際の場所。</param>
/// <param name="ByGuid">ドライブ文字ではなく、ボリュームの GUID で表した場所か。</param>
internal sealed record ReadScope(string Path, bool ByGuid)
{
    /// <summary>
    /// 読むために開く。開いたあとで、実際の場所がこの中かを確かめる。
    /// </summary>
    /// <exception cref="IOException">開けない場合や、実際の場所がこの外だった場合。</exception>
    public FileStream Open(string path, int bufferSize = 4096)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);
        try
        {
            var final = DiskLinks.FinalPath(stream.SafeFileHandle, ByGuid);
            if (final is null || !DiskLinks.IsSameOrInside(final, Path))
            {
                throw new FileChangedWhileReadingException();
            }

            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>読むために開く。場所が分からないものは、今までどおりそのまま開く。</summary>
    public static FileStream Open(ReadScope? scope, string path, int bufferSize = 4096)
        => scope?.Open(path, bufferSize)
           ?? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, bufferSize);
}

/// <summary>書き換えると、リンクと同じ名前のフォルダーの中に項目ができてしまう (#366)。書き換える前に止める。</summary>
internal sealed class LinkOverlapException() : IOException("A link would overlap a folder in the archive.");

/// <summary>読もうとしたファイルが、選んだフォルダーの外のものに変わっていた (#366)。</summary>
internal sealed class FileChangedWhileReadingException() : IOException("The file was replaced while reading.");
