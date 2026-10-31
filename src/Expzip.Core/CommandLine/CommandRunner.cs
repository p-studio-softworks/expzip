using System.IO;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.CommandLine;

/// <summary>圧縮する 1 件。</summary>
/// <param name="Line">一覧ファイルの行番号。<c>--compress</c> で指定したときは <see langword="null"/>。</param>
/// <param name="Source">ソースのフルパス。</param>
/// <param name="Target">作る書庫のフルパス。</param>
internal readonly record struct CompressJob(int? Line, string Source, string Target);

/// <summary>
/// コマンドラインからの圧縮 (#316)。ウィンドウを出さずに動き、結果を終了コードで返す。
/// </summary>
/// <remarks>
/// <para>
/// <b>書庫は隣に作った一時ファイルに書き、すべて入れ終えてから名前を変える。</b>
/// 途中で失敗しても作りかけが残らず、<c>--overwrite</c> でも元の書庫が消えない。
/// 読めないファイルが 1 個でもあれば、その書庫は作らない。欠けた書庫が
/// バックアップとして残ると、必要になるまで気づけないため。
/// </para>
/// <para>
/// <b>1 個の書庫に入るソースは 1 個だけ。</b>同じ書庫を指す行はエラーにする。
/// 書庫の中で名前がぶつかって中身が黙って失われることが、これで起きなくなる。
/// </para>
/// </remarks>
internal static class CommandRunner
{
    /// <summary>すべて成功した。</summary>
    public const int Succeeded = 0;

    /// <summary>一部または全部が失敗した。</summary>
    public const int Failed = 1;

    /// <summary>指定に不備があり、何も圧縮していない。</summary>
    public const int NotRun = 2;

    /// <summary>
    /// ウィンドウを出さずに動く指定かどうか。
    /// </summary>
    /// <remarks>
    /// <c>Expzip.exe 書庫</c> はこれまでどおりウィンドウで開く。<c>--</c> で始まる引数を
    /// 目印にするので、<c>compress</c> という名前のファイルを開けなくなることはない。
    /// </remarks>
    public static bool IsCommand(IEnumerable<string> args)
        => args.Any(static a => a.StartsWith("--", StringComparison.Ordinal) || a == "/?");

    /// <summary>指定どおりに動かす。</summary>
    /// <returns>終了コード。</returns>
    public static int Run(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        var help = false;
        var overwrite = false;
        var compress = false;
        var list = false;
        var values = new List<string>();

        foreach (var arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--help" or "/?":
                    help = true;
                    break;
                case "--overwrite":
                    overwrite = true;
                    break;
                case "--compress":
                    compress = true;
                    break;
                case "--list":
                    list = true;
                    break;
                default:
                    if (arg.StartsWith("--", StringComparison.Ordinal))
                    {
                        error.WriteLine(Strings.CommandUnknownOption(arg));
                        return NotRun;
                    }

                    values.Add(arg);
                    break;
            }
        }

        if (help)
        {
            output.WriteLine(Strings.CommandHelp);
            return Succeeded;
        }

        var problems = new List<string>();
        List<CompressJob> jobs;

        if (compress && !list && values.Count == 2)
        {
            jobs = [];
            var folder = Environment.CurrentDirectory;
            if (TryFullPath(values[0], folder, null, problems) is { } source
                && TryFullPath(values[1], folder, null, problems) is { } target)
            {
                jobs.Add(new CompressJob(null, source, target));
            }
        }
        else if (list && !compress && values.Count == 1)
        {
            jobs = ReadList(values[0], problems);
        }
        else
        {
            error.WriteLine(Strings.CommandWrongArguments);
            return NotRun;
        }

        Validate(jobs, overwrite, problems);

        if (problems.Count > 0)
        {
            foreach (var problem in problems)
            {
                error.WriteLine(problem);
            }

            error.WriteLine(Strings.CommandNothingDone);
            return NotRun;
        }

        var failed = 0;
        foreach (var job in jobs)
        {
            if (!Compress(job, overwrite, output, error))
            {
                failed++;
            }
        }

        if (list)
        {
            output.WriteLine(Strings.CommandSummary(jobs.Count - failed, failed));
        }

        return failed == 0 ? Succeeded : Failed;
    }

    /// <summary>一覧ファイルを読む。不備は <paramref name="problems"/> へ足す。</summary>
    private static List<CompressJob> ReadList(string path, List<string> problems)
    {
        var jobs = new List<CompressJob>();

        string fullPath;
        string text;
        try
        {
            fullPath = Path.GetFullPath(path);
            text = CompressList.Decode(File.ReadAllBytes(fullPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            problems.Add(Strings.CommandListUnreadable(path, Strings.Reason(ex)));
            return jobs;
        }

        // 実行したときのフォルダーを基準にすると、タスクスケジューラなどから動かしたときに結果が変わる
        var folder = Path.GetDirectoryName(fullPath)!;
        var any = false;

        foreach (var (line, content) in CompressList.Lines(text))
        {
            any = true;

            if (CompressList.SplitFields(content) is not [var sourceText, var targetText]
                || sourceText.Length == 0 || targetText.Length == 0)
            {
                problems.Add(Strings.CommandAtLine(line, Strings.CommandListFormat));
                continue;
            }

            if (TryFullPath(sourceText, folder, line, problems) is { } source
                && TryFullPath(targetText, folder, line, problems) is { } target)
            {
                jobs.Add(new CompressJob(line, source, target));
            }
        }

        if (!any)
        {
            problems.Add(Strings.CommandListEmpty(path));
        }

        return jobs;
    }

    private static string? TryFullPath(string path, string folder, int? line, List<string> problems)
    {
        try
        {
            return Path.GetFullPath(path, folder);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            problems.Add(AtLine(line, Strings.CommandInvalidPath(path)));
            return null;
        }
    }

    /// <summary>
    /// 圧縮を始める前に、すべての件を確かめる。一覧の途中で止まって半分だけ作られると、
    /// 直してもう一度動かしたときに、作りずみの書庫がエラーになってしまう。
    /// </summary>
    private static void Validate(List<CompressJob> jobs, bool overwrite, List<string> problems)
    {
        // 大文字小文字だけの違いは同じファイル。GetFullPath を通したので書き方の違いは消えている
        var seen = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);

        foreach (var job in jobs)
        {
            void Report(string message) => problems.Add(AtLine(job.Line, message));

            if (!File.Exists(job.Source) && !Directory.Exists(job.Source))
            {
                Report(Strings.CommandSourceNotFound(job.Source));
            }
            else if (Path.GetPathRoot(job.Source) == job.Source)
            {
                // 書庫の中の名前を元のファイル名から作るので、ドライブ全体には名前が付かない
                Report(Strings.CommandSourceIsDrive(job.Source));
            }

            if (!Path.GetExtension(job.Target).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                // 作れるのは ZIP だけ。a.7z という名前の ZIP は作らない
                Report(Strings.CommandTargetNotZip(job.Target));
            }

            if (Directory.Exists(job.Target))
            {
                Report(Strings.CommandTargetIsFolder(job.Target));
            }
            else if (File.Exists(job.Target) && !overwrite)
            {
                Report(Strings.CommandTargetExists(job.Target));
            }

            if (!Directory.Exists(Path.GetDirectoryName(job.Target)))
            {
                Report(Strings.CommandTargetFolderNotFound(job.Target));
            }

            if (!seen.TryAdd(job.Target, job.Line))
            {
                Report(Strings.CommandTargetDuplicate(job.Target, seen[job.Target] ?? 0));
            }

            // ほかの行のソースも見る。そのフォルダーを圧縮する順番によって、中身が変わってしまう
            foreach (var other in jobs)
            {
                if (IsSameOrInside(job.Target, other.Source))
                {
                    Report(Strings.CommandTargetInsideSource(job.Target, other.Source));
                    break;
                }
            }
        }
    }

    private static bool IsSameOrInside(string path, string folder)
    {
        var parent = Path.TrimEndingDirectorySeparator(folder);
        return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
            || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>書庫を 1 個作る。</summary>
    /// <returns>作れたら <see langword="true"/>。</returns>
    private static bool Compress(CompressJob job, bool overwrite, TextWriter output, TextWriter error)
    {
        var temporary = Path.Combine(
            Path.GetDirectoryName(job.Target)!,
            $"{Path.GetFileName(job.Target)}.{Guid.NewGuid():N}.tmp");

        void Fail(string reason) => error.WriteLine(
            AtLine(job.Line, Strings.CommandCreateFailed(job.Target, reason)));

        try
        {
            // 確かめてから時間が経っていることがある。消えたソースは空の書庫になってしまう
            if (!File.Exists(job.Source) && !Directory.Exists(job.Source))
            {
                Fail(Strings.CommandSourceNotFound(job.Source));
                return false;
            }

            ZipArchiveWriter.CreateEmpty(temporary);
            var result = ZipArchiveWriter.Add(
                temporary, [job.Source], string.Empty, replaceExisting: false, progress: null, CancellationToken.None);

            if (result.Failed.Count > 0)
            {
                Fail(Strings.CommandFilesFailed(result.Failed.Count));
                foreach (var (name, reason) in result.Failed)
                {
                    error.WriteLine(Strings.CommandFileFailed(name, reason));
                }

                return false;
            }

            File.Move(temporary, job.Target, overwrite);
            output.WriteLine(AtLine(job.Line, Strings.CommandCreated(job.Target, result.Added)));
            return true;
        }
        catch (Exception ex)
        {
            // どんな失敗でも残りの行を続ける。ダイアログは出さない
            Fail(Strings.Reason(ex));
            return false;
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                error.WriteLine(Strings.CommandFileFailed(temporary, Strings.Reason(ex)));
            }
        }
    }

    private static string AtLine(int? line, string message)
        => line is { } number ? Strings.CommandAtLine(number, message) : message;
}
