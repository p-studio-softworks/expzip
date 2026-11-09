using System.IO;
using System.IO.Compression;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>展開の進捗。</summary>
/// <param name="DoneBytes">展開済みのバイト数。</param>
/// <param name="TotalBytes">展開する合計バイト数。</param>
/// <param name="CurrentName">いま処理しているファイル名。</param>
internal readonly record struct ExtractProgress(long DoneBytes, long TotalBytes, string CurrentName)
{
    /// <summary>0〜100 の進捗率。</summary>
    public double Percent => TotalBytes <= 0 ? 100 : (double)DoneBytes / TotalBytes * 100.0;
}

/// <summary>展開の結果。</summary>
/// <param name="Extracted">展開したファイル数。</param>
/// <param name="Skipped">既存ファイルを上書きせず飛ばした数。</param>
/// <param name="Rejected">安全でないパスとして拒否したエントリ名。</param>
/// <param name="Failed">書き出しに失敗したエントリ名とその理由。</param>
/// <param name="Cancelled">利用者の操作で中断した場合は true。</param>
/// <param name="MarkLost">区分 3 以上の MotW を保存できなかったファイルがある (#321)。</param>
/// <param name="SkippedNames">既にあるものを置き換えずに飛ばした項目の名前 (#353)。書庫の順。</param>
internal sealed record ExtractResult(
    int Extracted,
    int Skipped,
    IReadOnlyList<string> Rejected,
    IReadOnlyList<(string Name, string Reason)> Failed,
    bool Cancelled,
    bool MarkLost = false,
    IReadOnlyList<string>? SkippedNames = null)
{
    /// <summary>既にあるものを置き換えずに飛ばした項目の名前。</summary>
    public IReadOnlyList<string> SkippedNames { get; init; } = SkippedNames ?? [];
}

/// <summary>書庫から実ファイルへ展開する。</summary>
internal static class ArchiveExtractor
{
    /// <summary>
    /// 指定したエントリを展開する。
    /// </summary>
    /// <param name="archivePath">書庫ファイルのパス。</param>
    /// <param name="names">
    /// 名前の読み方 (#335)。一覧と同じ名前で項目を選び、同じ名前で書き出すため、一覧を読んだもの
    /// (<see cref="ArchiveContents.Names"/>) を渡す。名前の読み方を選び直す対象でない形式では使わない。
    /// </param>
    /// <param name="sourceNames">
    /// 展開するエントリの <see cref="ArchiveEntry.SourceName"/>。
    /// <see langword="null"/> を渡すと書庫全体を展開する。
    /// </param>
    /// <param name="destinationDirectory">展開先フォルダ。</param>
    /// <param name="overwrite">既存のファイルを上書きするかどうか。</param>
    /// <param name="progress">進捗の通知先。</param>
    /// <param name="cancellationToken">中断用。</param>
    /// <param name="zoneIdentifier">
    /// 書き出したファイルに引き継ぐ MotW (#12)。<see langword="null"/> なら何もしない。
    /// </param>
    /// <param name="basePath">
    /// 書き出し先を決める際に、書庫内パスの先頭から取り除くフォルダ (#48)。
    /// 例えば <c>資料/画像</c> を選んで展開する場合に <c>資料</c> を渡すと、
    /// 展開先には <c>画像\…</c> が並ぶ。<see langword="null"/> なら書庫のルートからの
    /// 階層をそのまま作る。
    /// </param>
    /// <param name="format">書庫の形式 (#19)。中身が ZIP でないものは SharpCompress 側へ回す。</param>
    /// <param name="password">パスワード付きZIPの合言葉 (#20)。要らない書庫では <see langword="null"/>。</param>
    public static ExtractResult Extract(
        string archivePath,
        ArchiveFormat format,
        NameReading names,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        ExtractOverwrite overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier = null,
        string? basePath = null,
        string? password = null)
        => format == ArchiveFormat.Nsis
            ? NsisExtractor.Extract(archivePath, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath)
            : format == ArchiveFormat.Pe
            ? PeReader.Extract(archivePath, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath)
            : format is ArchiveFormat.Cab or ArchiveFormat.CabExe
            ? CabReader.Extract(archivePath, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath, format)
            : format == ArchiveFormat.Msi
            ? MsiReader.Extract(archivePath, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath)
            : format == ArchiveFormat.Burn
            ? BurnReader.Extract(archivePath, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath)
            : format == ArchiveFormat.MsiExe
            ? MsiExeReader.Extract(archivePath, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath)
            : ArchiveFormats.IsCompressedFile(format)
            ? CompressedFile.Extract(archivePath, format, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath)
            : !ArchiveFormats.IsZipBased(format)
            ? SharpArchiveExtractor.Extract(archivePath, format, names, sourceNames, destinationDirectory,
                overwrite, progress, cancellationToken, zoneIdentifier, basePath)
            : password is null
                ? ExtractZip(archivePath, format, names, sourceNames, destinationDirectory, overwrite,
                    progress, cancellationToken, zoneIdentifier, basePath)
                : ZipEncryption.Extract(archivePath, names, password, sourceNames, destinationDirectory,
                    overwrite, progress, cancellationToken, zoneIdentifier, basePath);

    private static ExtractResult ExtractZip(
        string archivePath,
        ArchiveFormat format,
        NameReading names,
        IReadOnlySet<string>? sourceNames,
        string destinationDirectory,
        ExtractOverwrite overwrite,
        IProgress<ExtractProgress>? progress,
        CancellationToken cancellationToken,
        string? zoneIdentifier,
        string? basePath)
    {
        // 展開先の正規化。これを基準に、書庫外へ書き出そうとするエントリを弾く
        var destinationRoot = Path.GetFullPath(destinationDirectory);

        // 自己解凍書庫は先頭にプログラムが付いている。その分を隠して渡す (#32)
        using var stream = ZipPrefix.Open(archivePath);
        using var zip = new ZipArchive(
            stream, ZipArchiveMode.Read, leaveOpen: false, names.Encoding);

        // 名前は一覧に出したものと同じ形で扱う。MSIX では元の名前に戻す (#182)。
        // 並列で取り出すときは書庫を開き直すので、エントリは番号で指す
        var targets = zip.Entries
            .Select((e, i) => (Index: i, Entry: e, Name: ArchiveFormats.EntryName(format, e.FullName)))
            .Where(t => !ZipEntryKind.IsFolder(t.Name))
            .Where(t => sourceNames is null || sourceNames.Contains(t.Name))
            .ToList();

        var tally = new ExtractTally(targets.Sum(static t => t.Entry.Length), progress);

        // 書き出し先を先に決める。書庫の外へ出るものはここで弾く
        var work = new List<(int Index, string Name, long Length, string Target)>();

        foreach (var (index, entry, name) in targets)
        {
            var target = TargetFor(destinationRoot, name, basePath);
            if (target is null)
            {
                tally.Reject(work.Count, name);
                continue;
            }

            work.Add((index, name, entry.Length, target));
        }

        var lanes = CountLanes(archivePath, destinationRoot, work.Select(static w => w.Target).ToList());
        var cursor = -1;

        // 1 本の作業分。空いているエントリを順に取っていく
        void Run(ZipArchive own, CancellationToken token)
        {
            // 標準の実装が復号できない方式のエントリ用 (#66)。要るまで開かない
            using var fallback = new ZipMethodFallback(archivePath, names);

            int i;
            while ((i = Interlocked.Increment(ref cursor)) < work.Count)
            {
                if (token.IsCancellationRequested)
                {
                    tally.Cancel();
                    return;
                }

                var (index, name, length, target) = work[i];
                ExtractOne(own.Entries[index], i, name, target, fallback, overwrite, zoneIdentifier,
                    tally, token);
                tally.Advance(length, name);

                if (tally.Cancelled)
                {
                    return;
                }
            }
        }

        if (lanes == 1)
        {
            Run(zip, cancellationToken);
            return tally.ToResult();
        }

        // どれか 1 本が思わぬ失敗で止まったら、ほかも止める。書きかけはそれぞれが片付ける
        using var halt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var all = Enumerable.Range(0, lanes).Select(lane => Task.Run(() =>
        {
            try
            {
                if (lane == 0)
                {
                    Run(zip, halt.Token);
                    return;
                }

                // 書庫は作業ごとに開く。ひとつの書庫を複数から同時に読むことはできない
                using var ownStream = ZipPrefix.Open(archivePath);
                using var own = new ZipArchive(
                    ownStream, ZipArchiveMode.Read, leaveOpen: false, names.Encoding);
                Run(own, halt.Token);
            }
            catch
            {
                halt.Cancel();
                throw;
            }
        })).ToArray();

        // すべて終わるのを待ってから、最初の失敗をそのまま投げる
        Task.WhenAll(all).GetAwaiter().GetResult();
        return tally.ToResult();
    }

    /// <summary>
    /// 何本並べて展開するかを決める (#195)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// SSD なら、コアの数だけ並べると 4 倍ほど速くなる。HDD では書き込みの場所があちこちに
    /// 飛び、逆に 2 倍ほど遅くなるので 1 本にする。書庫の側が HDD でも、読む場所が飛ぶのは同じ。
    /// HDD かどうか分からないときも 1 本にする。遅くしないことを優先する。
    /// </para>
    /// <para>
    /// 同じ場所へ書き出すエントリが 2 つ以上あるときも 1 本にする (大文字と小文字だけが
    /// 違う名前など)。並べると同じファイルを同時に書こうとして、どちらかが失敗する。
    /// 1 本なら、これまでどおり後のものが上書きするか、飛ばされる。
    /// </para>
    /// <para>
    /// 名前に <c>~</c> と数字が続く部分があるときも 1 本にする (#349)。8.3 形式の短い名前
    /// (<c>LONGFI~1.TXT</c>) は、長い名前と同じファイルを指すが、名前の比べ方では別のものに見える。
    /// 並べると、1 つ目を書いている最中に 2 つ目が来て「アクセスが拒否されました」と失敗する。
    /// Windows が付ける短い名前には必ず <c>~</c> と数字が入る。展開先そのもののパスは見ない (一時フォルダーなどに入っていることがある)。
    /// </para>
    /// <para>
    /// 展開先に前からあるジャンクションなどを通る項目があるときも 1 本にする (#349)。ジャンクションを通る名前と、
    /// その先のフォルダーを通る名前は、同じファイルを指すのに別のものに見える。
    /// </para>
    /// </remarks>
    internal static int CountLanes(string archivePath, string destinationRoot, IReadOnlyList<string> targets)
    {
        if (targets.Count < 2 || Environment.ProcessorCount < 2)
        {
            return 1;
        }

        if (targets.Distinct(StringComparer.OrdinalIgnoreCase).Count() != targets.Count)
        {
            return 1;
        }

        if (targets.Any(t => MayBeShortName(Path.GetRelativePath(destinationRoot, t))))
        {
            return 1;
        }

        if (PassesThroughLink(destinationRoot, targets))
        {
            return 1;
        }

        if (SeekPenalty.Of(destinationRoot) != false || SeekPenalty.Of(archivePath) != false)
        {
            return 1;
        }

        return Math.Min(Environment.ProcessorCount, targets.Count);
    }

    /// <summary>8.3 形式の短い名前かもしれない部分 (<c>~</c> と数字) を含むか (#349)。フォルダーの名前も見る。</summary>
    internal static bool MayBeShortName(string relativePath)
    {
        for (var i = 0; i + 1 < relativePath.Length; i++)
        {
            if (relativePath[i] == '~' && char.IsAsciiDigit(relativePath[i + 1]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 書き出す先のフォルダーのうち、展開先に前からあるものに、ジャンクションやリンクがあるか (#349)。
    /// 展開先そのものは見ない。同じフォルダーは 1 回だけ調べる。
    /// </summary>
    internal static bool PassesThroughLink(string destinationRoot, IReadOnlyList<string> targets)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationRoot));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var target in targets)
        {
            var folder = Path.GetDirectoryName(target);
            while (folder is not null && folder.Length > root.Length && seen.Add(folder))
            {
                // まだ無いフォルダーは展開で作るので、リンクではない。無いものに例外を出させないよう、先に確かめる
                try
                {
                    if (Directory.Exists(folder) && (File.GetAttributes(folder) & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 確かめている間に消えた
                }

                folder = Path.GetDirectoryName(folder);
            }
        }

        return false;
    }

    /// <summary>
    /// 項目を書き出す先のフルパス。展開先の外を指すものは <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// 上書きの確認 (画面) と、どの形式の書き出しも、これで決める (#353)。別々に決めると、
    /// tar の先頭の <c>./</c> や <c>a/./b.txt</c> のように、確認では別の名前に見えるのに同じファイルに書く項目ができる。
    /// <see cref="Path.GetFullPath(string)"/> は <c>.</c> と <c>..</c>、末尾のピリオドや空白を Windows と同じに落とす。
    /// 展開先の外かは、その結果で確かめる (Zip Slip 対策)。
    /// </remarks>
    /// <param name="destinationRoot">展開先のフルパス。</param>
    /// <param name="entryName">書庫の中の名前。</param>
    /// <param name="basePath">先頭から取り除くフォルダー (<see cref="StripBase"/>)。</param>
    public static string? TargetFor(string destinationRoot, string entryName, string? basePath)
    {
        var relative = ArchivePath.ToSafeRelativePath(StripBase(ArchiveTreeBuilder.Trim(entryName), basePath));
        if (relative is null)
        {
            return null;
        }

        var target = Path.GetFullPath(Path.Combine(destinationRoot, relative));
        return IsInside(destinationRoot, target) ? target : null;
    }

    /// <summary>
    /// 展開すると置き換わる、展開先に既にあるファイル (#353)。上書きの確認に挙げる。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 書き出す先は <see cref="TargetFor"/> で決める (書き出しと同じ)。展開先を丸ごと数えず、項目が書き込むフォルダーだけを
    /// 1 回ずつ数える。関係の無い大きなフォルダーを待たずに済み、読めないフォルダー (「ドキュメント」の中の隠しの
    /// ジャンクションなど) があっても止まらない。数えられなかったフォルダーは、1 件ずつあるかを確かめる。
    /// </para>
    /// <para>
    /// ここで見つからなかった既にあるファイル (8.3 形式の短い名前で指すものなど) は、確認に挙がらないが、
    /// 書き出しでも置き換えない (<see cref="ExtractOverwrite.Only"/>)。
    /// </para>
    /// </remarks>
    /// <returns>項目の名前と、置き換わるファイルのフルパス。書庫の順。</returns>
    public static List<(string Name, string Target)> FindConflicts(
        IEnumerable<string> entryNames, string destinationDirectory, string? basePath)
    {
        var conflicts = new List<(string Name, string Target)>();
        var root = Path.GetFullPath(destinationDirectory);
        if (!Directory.Exists(root))
        {
            return conflicts;
        }

        var listings = new Dictionary<string, HashSet<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in entryNames)
        {
            var target = TargetFor(root, name, basePath);
            if (target is null)
            {
                continue;
            }

            var folder = Path.GetDirectoryName(target)!;
            if (!listings.TryGetValue(folder, out var names))
            {
                names = ListFileNames(folder);
                listings[folder] = names;
            }

            var exists = names is null
                ? File.Exists(target)
                : names.Contains(Path.GetFileName(target).Normalize(System.Text.NormalizationForm.FormC));
            if (exists)
            {
                conflicts.Add((name, target));
            }
        }

        return conflicts;
    }

    /// <summary>フォルダーの中のファイルの名前。フォルダーが無ければ空、数えられなければ <see langword="null"/>。</summary>
    private static HashSet<string>? ListFileNames(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
            {
                return [];
            }

            // NAS などは濁点を分けた形で名前を返すことがある。書庫の名前と同じ形に揃えて比べる
            return new HashSet<string>(
                Directory.EnumerateFiles(folder).Select(static f => Path.GetFileName(f).Normalize(System.Text.NormalizationForm.FormC)),
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>1 件を書き出す。結果は <paramref name="tally"/> に記録する。</summary>
    private static void ExtractOne(
        ZipArchiveEntry entry,
        int order,
        string name,
        string target,
        ZipMethodFallback fallback,
        ExtractOverwrite overwrite,
        string? zoneIdentifier,
        ExtractTally tally,
        CancellationToken cancellationToken)
    {
        try
        {
            var replace = overwrite.Allows(target);
            if (!replace && File.Exists(target))
            {
                tally.Skip(order, name);
                return;
            }

            var directory = Path.GetDirectoryName(target);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 書庫に MotW が付いていた場合は、書き出したファイルにも引き継ぐ。
            // MotW が消えると SmartScreen や保護ビューが働かなくなる (#12)
            using var source = fallback.Open(entry.FullName, entry.Open);
            using var file = AtomicFile.CreateForExtraction(target, replace, zoneIdentifier);

            // 暗号化された項目はこの道では読めない。照合する前に開く時点で断られる
            CancellableCopy.CopyContent(
                source, file.Stream, cancellationToken,
                entry.IsEncrypted ? -1 : entry.Crc32);

            if (file.Commit(entry.LastWriteTime.LocalDateTime))
            {
                tally.Extract();
                tally.Marks.Committed(file, target);
                overwrite.Created(target);
            }
            else
            {
                tally.Skip(order, name);
            }
        }
        catch (OperationCanceledException)
        {
            // 書きかけは AtomicFile が消す。同じ名前の既にあるファイルには触れていない (#320)
            tally.Cancel();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or PathTooLongException or InvalidDataException)
        {
            // 読めなかったものは残さない。中身が途中までのファイルは、
            // 見た目が正常なだけに何も残らないより悪い (#66)。
            // 1件の失敗で全体を止めない。まとめて報告する
            tally.Fail(order, name, Describe(ex));
        }
    }

    /// <summary>
    /// 展開の結果と進み具合を数える。並列で展開するときは、どの作業からも呼ばれる。
    /// </summary>
    private sealed class ExtractTally(long totalBytes, IProgress<ExtractProgress>? progress)
    {
        private readonly Lock _lock = new();
        private readonly List<(int Order, string Name)> _rejected = [];
        private readonly List<(int Order, string Name, string Reason)> _failed = [];
        private readonly List<(int Order, string Name)> _skippedNames = [];
        private int _extracted;
        private long _doneBytes;
        private volatile bool _cancelled;

        // 進捗通知が多すぎると UI 側が詰まるため、1%刻みに間引く
        private readonly long _reportStep = Math.Max(1, totalBytes / 100);
        private long _nextReport;

        public bool Cancelled => _cancelled;

        public MarkWatch Marks { get; } = new();

        public void Extract() => Interlocked.Increment(ref _extracted);

        public void Skip(int order, string name)
        {
            lock (_lock)
            {
                _skippedNames.Add((order, name));
            }
        }

        public void Cancel() => _cancelled = true;

        public void Reject(int order, string name)
        {
            lock (_lock)
            {
                _rejected.Add((order, name));
            }
        }

        public void Fail(int order, string name, string reason)
        {
            lock (_lock)
            {
                _failed.Add((order, name, reason));
            }
        }

        public void Advance(long bytes, string name)
        {
            lock (_lock)
            {
                _doneBytes += bytes;

                if (progress is not null && (_doneBytes >= _nextReport || _doneBytes >= totalBytes))
                {
                    _nextReport = _doneBytes + _reportStep;
                    progress.Report(new ExtractProgress(_doneBytes, totalBytes, name));
                }
            }
        }

        public ExtractResult ToResult()
        {
            lock (_lock)
            {
                // 並列で展開すると、終わる順はまちまちになる。書庫の順に並べ直して報告する
                return new ExtractResult(
                    _extracted,
                    _skippedNames.Count,
                    _rejected.OrderBy(static r => r.Order).Select(static r => r.Name).ToList(),
                    _failed.OrderBy(static f => f.Order).Select(static f => (f.Name, f.Reason)).ToList(),
                    _cancelled,
                    Marks.Lost,
                    _skippedNames.OrderBy(static s => s.Order).Select(static s => s.Name).ToList());
            }
        }
    }

    /// <summary>失敗の理由を、利用者に読める文にする (#66)。</summary>
    /// <remarks>
    /// ライブラリの文言は英語で出てくる。言語の切り替え (#23) から漏れるため、
    /// 心当たりのあるものは自前の文に置き換える。
    /// </remarks>
    private static string Describe(Exception ex)
        => ex is InvalidDataException or NotSupportedException
           && ex.Message.Contains("compress", StringComparison.OrdinalIgnoreCase)
            ? Strings.UnsupportedCompressionMethod
            : Strings.Reason(ex);

    /// <summary>
    /// 書庫内パスの先頭から、指定のフォルダを取り除く。
    /// 選んだフォルダを展開先の最上位にするために使う (#48)。
    /// </summary>
    public static string StripBase(string entryName, string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return entryName;
        }

        var normalized = ArchivePath.Normalize(entryName);
        var prefix = basePath + "/";

        return normalized.StartsWith(prefix, StringComparison.Ordinal)
            ? normalized[prefix.Length..]
            : normalized;
    }

    /// <summary>中断時の後始末。消せなくても中断自体は成立するので握りつぶす。</summary>
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

    /// <summary>解決済みのパスが指定フォルダの配下にあるか。</summary>
    internal static bool IsInside(string root, string candidate)
    {
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        return candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

}
