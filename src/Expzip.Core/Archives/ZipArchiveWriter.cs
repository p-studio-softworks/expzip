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
    bool Cancelled);

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
    /// <summary>作業用ファイルの拡張子。</summary>
    internal const string TempSuffix = ".expzip-tmp";

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
        // using で閉じた時点で終端レコードが書かれ、読み取り可能な書庫になる
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
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
    /// </remarks>
    public static AddResult Add(
        string archivePath,
        IReadOnlyList<string> sourcePaths,
        string destinationFolder,
        bool replaceExisting,
        IProgress<AddProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = BuildPlan(sourcePaths, destinationFolder);
        var totalBytes = plan.Sum(static p => p.Length);

        var added = 0;
        var replaced = 0;
        var skipped = 0;
        var failed = new List<(string, string)>();
        var cancelled = false;

        // 実際に中身が読まれるのは CommitUpdate の最中。進み具合もそこで動く
        long readBytes = 0;
        long reportedAt = 0;

        using var zip = ZipUpdate.Open(archivePath);
        zip.BeginUpdate();

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

            // 読めるかを確かめ、あわせて圧縮するかどうかを決める (#38)。
            // 書き出しが始まってから読めないと分かると、その回の書き換えが
            // まるごと取りやめになり、他の分まで巻き添えになる
            var choice = CompressionChoice.Probe(item.SourcePath);
            if (choice.Error is { } reason)
            {
                failed.Add((item.SourcePath, reason));
                continue;
            }

            var name = item.EntryName;

            zip.Add(
                new ZipUpdate.FileSource(item.SourcePath, cancellationToken, read =>
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

        return new AddResult(added, replaced, skipped, failed, Cancelled: false);
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
    /// 書き換えを確定する。差し替えに失敗したときは、本当の理由を投げ直す (#106)。
    /// </summary>
    /// <remarks>
    /// SharpZipLib は書庫を退避名へ移せなかったとき、戻す処理で退避先が
    /// 見つからず <see cref="FileNotFoundException"/> を投げ、元の例外を失う。
    /// そのままだと、書庫がほかのプログラムに使われているだけなのに
    /// 「edit.zip.cii2e34b.zyx が見つかりません」と伝えてしまう。
    /// 書庫を占有して開いてみて、開けなければその例外を理由にする。
    /// </remarks>
    private static void Commit(SharpZipFile zip)
    {
        try
        {
            zip.CommitUpdate();
        }
        catch (FileNotFoundException lost) when (
            File.Exists(zip.Name)
            && !string.Equals(lost.FileName, zip.Name, StringComparison.OrdinalIgnoreCase))
        {
            Exception? cause = null;
            try
            {
                using var probe = new FileStream(zip.Name, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                cause = ex;
            }

            if (cause is not null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(cause);
            }

            throw;
        }
    }

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

        zip.BeginUpdate();
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

        zip.BeginUpdate();

        foreach (var entry in targets)
        {
            zip.Delete(entry);
            deleted++;
        }

        if (!TryCommit(zip, cancellationToken))
        {
            return new DeleteResult(0, Cancelled: true);
        }

        return new DeleteResult(deleted, Cancelled: false);
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
    public static IReadOnlyList<string> PlanEntryNames(
        IReadOnlyList<string> sourcePaths, string destinationFolder)
        => BuildPlan(sourcePaths, destinationFolder).Select(static p => p.EntryName).ToList();

    /// <summary>追加するファイルの一覧。フォルダは中身を辿って展開する。</summary>
    internal static List<PlanItem> BuildPlan(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        var prefix = destinationFolder.Length == 0 ? string.Empty : destinationFolder.TrimEnd('/') + "/";
        var plan = new List<PlanItem>();

        foreach (var path in sourcePaths)
        {
            if (Directory.Exists(path))
            {
                AddDirectory(plan, path, prefix + Path.GetFileName(path.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }
            else if (File.Exists(path))
            {
                plan.Add(new PlanItem(path, prefix + Path.GetFileName(path), new FileInfo(path).Length, false));
            }
        }

        return plan;
    }

    private static void AddDirectory(List<PlanItem> plan, string directory, string entryPrefix)
    {
        var files = Directory.GetFiles(directory);
        var subdirectories = Directory.GetDirectories(directory);

        // 空のフォルダは、そのままだと書庫に残らないのでフォルダのエントリを作る
        if (files.Length == 0 && subdirectories.Length == 0)
        {
            plan.Add(new PlanItem(directory, entryPrefix + "/", 0, true));
            return;
        }

        foreach (var file in files)
        {
            plan.Add(new PlanItem(file, entryPrefix + "/" + Path.GetFileName(file),
                new FileInfo(file).Length, false));
        }

        foreach (var subdirectory in subdirectories)
        {
            AddDirectory(plan, subdirectory, entryPrefix + "/" + Path.GetFileName(subdirectory));
        }
    }

    /// <summary>更新日時を読む。読めない場合は現在時刻を使う。</summary>
    private static DateTime ReadLastWriteTime(string path)
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
        var carried = new List<(string NewName, string TempPath, DateTime Stamp)>();

        try
        {
            using (var zip = ZipUpdate.Open(archivePath))
            {
                // 付け替える対象を先に確定させる
                var targets = zip.Cast<ZipEntry>()
                    .Select(e => (Entry: e, NewName: MapAny(e.Name, changes)))
                    .Where(static x => x.NewName is not null)
                    .ToList();

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
                        carried.Add((newName, string.Empty, entry.DateTime));
                        continue;
                    }

                    var holding = Path.GetTempFileName();

                    using (var source = zip.GetInputStream(entry))
                    using (var destination = new FileStream(
                               holding, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        CancellableCopy.Copy(source, destination, cancellationToken);
                    }

                    carried.Add((newName, holding, entry.DateTime));
                }

                zip.BeginUpdate();

                foreach (var (entry, _) in targets)
                {
                    zip.Delete(entry);
                }

                foreach (var (newName, holding, stamp) in carried)
                {
                    if (holding.Length == 0)
                    {
                        zip.AddDirectory(newName.TrimEnd('/'));
                    }
                    else
                    {
                        zip.Add(
                            new ZipUpdate.FileSource(holding, cancellationToken),
                            ZipUpdate.NewEntry(
                                newName, CompressionChoice.Probe(holding).Method, stamp));
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
            foreach (var (_, holding, _) in carried)
            {
                if (holding.Length > 0)
                {
                    TryDelete(holding);
                }
            }
        }

        return new RenameResult(renamed, Cancelled: false);
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
    internal readonly record struct PlanItem(string SourcePath, string EntryName, long Length, bool IsDirectory);
}
