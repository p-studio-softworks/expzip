using System.IO;
using System.IO.Compression;

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
    private const string TempSuffix = ".expzip-tmp";

    /// <summary>
    /// 空のZIP書庫を作る。既に同じ名前のファイルがあれば置き換える。
    /// </summary>
    /// <remarks>
    /// 中身が無くてもZIPとしては正しく、終端レコードだけを持つファイルになる。
    /// エントリ名の書き出しは既定 (UTF-8 + EFSフラグ) に任せる。読み取り時に
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
    /// <param name="compressionLevel">圧縮の強さ (#11)。</param>
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
        CompressionLevel compressionLevel,
        IProgress<AddProgress>? progress,
        CancellationToken cancellationToken)
    {
        var plan = BuildPlan(sourcePaths, destinationFolder);
        var totalBytes = plan.Sum(static p => p.Length);
        long doneBytes = 0;

        var added = 0;
        var replaced = 0;
        var skipped = 0;
        var failed = new List<(string, string)>();
        var cancelled = false;

        var temp = archivePath + TempSuffix;

        try
        {
            File.Copy(archivePath, temp, overwrite: true);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                foreach (var item in plan)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        cancelled = true;
                        break;
                    }

                    try
                    {
                        var existing = zip.GetEntry(item.EntryName);
                        if (existing is not null)
                        {
                            if (!replaceExisting)
                            {
                                skipped++;
                                doneBytes += item.Length;
                                continue;
                            }

                            existing.Delete();
                            replaced++;
                        }
                        else
                        {
                            added++;
                        }

                        if (item.IsDirectory)
                        {
                            // 空のフォルダも構造として残す
                            zip.CreateEntry(item.EntryName);
                        }
                        else
                        {
                            var entry = zip.CreateEntry(item.EntryName, compressionLevel);
                            entry.LastWriteTime = ReadLastWriteTime(item.SourcePath);

                            using var source = File.OpenRead(item.SourcePath);
                            using var destination = entry.Open();
                            CancellableCopy.Copy(source, destination, cancellationToken);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        cancelled = true;
                        break;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                               or ArgumentException or NotSupportedException
                                               or PathTooLongException)
                    {
                        // 1件の失敗で全体を止めない。まとめて報告する
                        failed.Add((item.SourcePath, ex.Message));
                    }

                    doneBytes += item.Length;
                    progress?.Report(new AddProgress(doneBytes, totalBytes, item.EntryName));
                }
            }

            if (cancelled)
            {
                // 中断したときは差し替えない。元の書庫はそのまま
                return new AddResult(0, 0, 0, failed, Cancelled: true);
            }

            File.Move(temp, archivePath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }

        return new AddResult(added, replaced, skipped, failed, Cancelled: false);
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
        var temp = archivePath + TempSuffix;
        var deleted = 0;

        try
        {
            File.Copy(archivePath, temp, overwrite: true);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                // Delete するとコレクションが変わるので、先に対象を確定させる
                var targets = zip.Entries
                    .Where(e => ShouldDelete(e.FullName, fileEntryNames, folderPaths))
                    .ToList();

                if (cancellationToken.IsCancellationRequested)
                {
                    return new DeleteResult(0, Cancelled: true);
                }

                foreach (var entry in targets)
                {
                    entry.Delete();
                    deleted++;
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // 書き換えは済んでいるが差し替えていないので、元の書庫は元のまま
                return new DeleteResult(0, Cancelled: true);
            }

            File.Move(temp, archivePath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }

        return new DeleteResult(deleted, Cancelled: false);
    }

    /// <summary>このエントリが削除の対象かどうか。</summary>
    private static bool ShouldDelete(
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
    private static List<PlanItem> BuildPlan(IReadOnlyList<string> sourcePaths, string destinationFolder)
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
    private static DateTimeOffset ReadLastWriteTime(string path)
    {
        try
        {
            var value = File.GetLastWriteTime(path);

            // ZIPの日時は1980年以降しか表現できない
            return value.Year < 1980 ? DateTimeOffset.Now : new DateTimeOffset(value);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentOutOfRangeException)
        {
            return DateTimeOffset.Now;
        }
    }

    /// <summary>
    /// 書庫内のファイルまたはフォルダの名前を変える (#15)。
    /// </summary>
    /// <param name="archivePath">書庫ファイルのパス。</param>
    /// <param name="oldPath">変更前の書庫内パス。区切りは <c>/</c>、末尾に区切りは付けない。</param>
    /// <param name="newPath">変更後の書庫内パス。</param>
    /// <param name="isFolder">フォルダなら true。配下のエントリもまとめて付け替える。</param>
    /// <param name="compressionLevel">詰め直すときの圧縮の強さ。</param>
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
        CompressionLevel compressionLevel,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
        => Move(archivePath, [new PathChange(oldPath, newPath, isFolder)],
                compressionLevel, progress, cancellationToken);

    /// <summary>
    /// 書庫内の項目をまとめて別の場所へ移す (#43)。名前の変更もこの一種として扱う。
    /// </summary>
    /// <param name="archivePath">書庫ファイルのパス。</param>
    /// <param name="changes">付け替える書庫内パスの組。</param>
    /// <param name="compressionLevel">詰め直すときの圧縮の強さ。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <inheritdoc cref="Rename" path="/remarks"/>
    public static RenameResult Move(
        string archivePath,
        IReadOnlyList<PathChange> changes,
        CompressionLevel compressionLevel,
        IProgress<int>? progress,
        CancellationToken cancellationToken)
    {
        var temp = archivePath + TempSuffix;
        var renamed = 0;

        try
        {
            File.Copy(archivePath, temp, overwrite: true);

            using (var zip = ZipFile.Open(temp, ZipArchiveMode.Update))
            {
                // 付け替える対象を先に確定させる。作成と削除でコレクションが変わるため
                var targets = zip.Entries
                    .Select(e => (Entry: e, NewName: MapAny(e.FullName, changes)))
                    .Where(static x => x.NewName is not null)
                    .ToList();

                foreach (var (entry, newName) in targets)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return new RenameResult(0, Cancelled: true);
                    }

                    var created = zip.CreateEntry(newName!, compressionLevel);
                    CopyTimestamp(entry, created);

                    // フォルダそのものを表すエントリは中身を持たない
                    if (!newName!.EndsWith('/'))
                    {
                        using var source = entry.Open();
                        using var destination = created.Open();
                        CancellableCopy.Copy(source, destination, cancellationToken);
                    }

                    entry.Delete();
                    renamed++;
                    progress?.Report(renamed);
                }
            }

            if (cancellationToken.IsCancellationRequested)
            {
                // 作業用ファイルは書き換わっているが、差し替えていないので元の書庫は無事
                return new RenameResult(0, Cancelled: true);
            }

            File.Move(temp, archivePath, overwrite: true);
        }
        finally
        {
            TryDelete(temp);
        }

        return new RenameResult(renamed, Cancelled: false);
    }

    /// <summary>いずれかの組に当てはめた結果を返す。どれにも当たらなければ null。</summary>
    private static string? MapAny(string entryName, IReadOnlyList<PathChange> changes)
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

    /// <summary>
    /// 更新日時を引き継ぐ。書庫によってはZIPで表せない日付が入っていることがあり、
    /// その場合は読み書きのどちらかで例外になる。名前の変更自体は成立するので無視する。
    /// </summary>
    private static void CopyTimestamp(ZipArchiveEntry from, ZipArchiveEntry to)
    {
        try
        {
            to.LastWriteTime = from.LastWriteTime;
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static void TryDelete(string path)
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
    private readonly record struct PlanItem(string SourcePath, string EntryName, long Length, bool IsDirectory);
}
