using System.IO;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>
/// 書庫内のファイルを既定のアプリで開くために取り出す、一時ファイルの置き場 (#12)。
/// </summary>
/// <remarks>
/// <para>
/// 置き場は起動ごとに <c>%TEMP%\Expzip\session-*</c> の下に作り、アプリ終了時に
/// まるごと消す。exe と同じフォルダには置かない。持ち運び先が書き込み不可のことがあり、
/// 書けたとしても取り出したファイルを残すと「インストール不要」の建前が崩れるため。
/// </para>
/// <para>
/// 置き場には使用中であることを示す錠ファイルを作り、開いたまま保持する。
/// 異常終了で消し残した置き場を次回起動時に片付ける際、まだ動いている別の
/// インスタンスの置き場を巻き込んで消さないようにするため。
/// </para>
/// </remarks>
internal sealed class TempWorkspace : IDisposable
{
    private const string RootName = "Expzip";
    private const string SessionPrefix = "session-";
    private const string LockName = ".lock";
    private const string ClosedName = ".closed";

    /// <summary>
    /// 錠ファイルが無い置き場を消し残しとみなすまでの猶予。
    /// 置き場を作ってから錠をかけるまでには僅かに間があり、その隙に別の
    /// インスタンスから消し残しと誤認されるのを防ぐ。
    /// </summary>
    private static readonly TimeSpan GraceBeforeAbandoned = TimeSpan.FromMinutes(5);

    /// <summary>書庫ごとの取り出し先。同じ書庫の2度目以降は同じ場所を使う。</summary>
    private readonly Dictionary<string, string> _byArchive = new(StringComparer.OrdinalIgnoreCase);

    private FileStream? _lockFile;
    private int _issued;

    private TempWorkspace(string directory, FileStream lockFile)
    {
        Directory = directory;
        _lockFile = lockFile;
    }

    /// <summary>この起動で使う置き場。</summary>
    public string Directory { get; }

    /// <summary>全インスタンス共通の親フォルダ。</summary>
    public static string Root => Path.Combine(Path.GetTempPath(), RootName);

    /// <summary>置き場を作る。</summary>
    /// <exception cref="IOException">一時フォルダに書き込めない場合。</exception>
    public static TempWorkspace Create()
    {
        System.IO.Directory.CreateDirectory(Root);

        // 同時に起動された場合や、同じ秒に再起動された場合に名前がぶつかりうる
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var name = $"{SessionPrefix}{Environment.ProcessId}-{DateTime.Now:yyyyMMdd-HHmmss}"
                       + (attempt == 0 ? string.Empty : $"-{attempt}");
            var directory = Path.Combine(Root, name);

            // 既にあるフォルダは使わない。消し残した過去の置き場を引き継いでしまうと、
            // 古い取り出し済みファイルをそのまま開いてしまううえ、片付けの印も
            // 使用中の置き場に残ったままになる。
            if (System.IO.Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                System.IO.Directory.CreateDirectory(directory);

                // CreateNew にして、僅かな時間差で他のインスタンスに先を越された
                // 場合にも横取りしないようにする。
                // DeleteOnClose にしておくと、強制終了でハンドルが閉じられた際にも
                // 錠だけは消える。錠が無く古い置き場は消し残しと判断できる。
                var lockFile = new FileStream(
                    Path.Combine(directory, LockName),
                    FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);

                return new TempWorkspace(directory, lockFile);
            }
            catch (IOException)
            {
            }
        }

        throw new IOException(Strings.TempWorkspaceFailed(Root));
    }

    /// <summary>
    /// 指定した書庫から取り出したファイルを置くフォルダ。
    /// 書庫ごとに分けるのは、別々の書庫に同じ名前のファイルが入っていても
    /// 上書きし合わないようにするため。
    /// </summary>
    public string DirectoryFor(string archivePath)
    {
        if (_byArchive.TryGetValue(archivePath, out var existing))
        {
            return existing;
        }

        // 通し番号を付けるのは、書庫名だけでは別の場所にある同名の書庫を
        // 区別できないため。書庫名も残すのは、一時フォルダを覗いたときに
        // どれが何か分かるようにするため。
        var directory = Path.Combine(
            Directory, $"{++_issued:00}-{Sanitize(Path.GetFileNameWithoutExtension(archivePath))}");

        System.IO.Directory.CreateDirectory(directory);
        _byArchive[archivePath] = directory;
        return directory;
    }

    /// <summary>置き場をまるごと消す。</summary>
    public void Dispose()
    {
        // 錠を先に手放さないと置き場を消せない
        _lockFile?.Dispose();
        _lockFile = null;

        if (TryDeleteTree(Directory))
        {
            return;
        }

        // 開いたままのアプリに掴まれていて消しきれなかった
        MarkClosed(Directory);
    }

    /// <summary>
    /// 消しきれなかった置き場に、終わったことを示す印を残す。
    /// 次回起動時に猶予を待たずに片付けられるようにするため。
    /// </summary>
    private static void MarkClosed(string directory)
    {
        try
        {
            File.WriteAllBytes(Path.Combine(directory, ClosedName), []);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or DirectoryNotFoundException)
        {
        }
    }

    /// <summary>
    /// 異常終了などで消し残した過去の置き場を片付ける。
    /// </summary>
    /// <param name="exclude">いま使っている置き場。消さずに残す。</param>
    /// <returns>消せた置き場の数。</returns>
    public static int CleanUpAbandoned(string? exclude)
    {
        string[] candidates;
        try
        {
            candidates = System.IO.Directory.GetDirectories(Root, SessionPrefix + "*");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or DirectoryNotFoundException)
        {
            return 0;
        }

        var removed = 0;
        foreach (var directory in candidates)
        {
            if (exclude is not null
                && string.Equals(directory, exclude, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!IsAbandoned(directory))
            {
                continue;
            }

            if (TryDeleteTree(directory))
            {
                removed++;
            }
            else
            {
                // 消せる分は消えるため、印そのものが消えていることがある。
                // 付け直しておかないと、次回は「出来たばかりの置き場」に見えてしまう。
                MarkClosed(directory);
            }
        }

        return removed;
    }

    /// <summary>まだ動いているインスタンスのものでないと判断できるか。</summary>
    private static bool IsAbandoned(string directory)
    {
        var lockPath = Path.Combine(directory, LockName);

        if (File.Exists(lockPath))
        {
            // 停電などハンドルが閉じられずに終わったときも錠は残る。
            // ファイルがあるかどうかではなく、実際に掴まれているかで判断する。
            try
            {
                using var probe = new FileStream(
                    lockPath, FileMode.Open, FileAccess.Read, FileShare.None);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 使用中。動いているインスタンスの置き場なので触らない
                return false;
            }

            return true;
        }

        // 終了時に消しきれなかった印があれば、猶予を待たずに片付けてよい
        if (File.Exists(Path.Combine(directory, ClosedName)))
        {
            return true;
        }

        // 作られたばかりなら、錠をかける前かもしれないので手を出さない
        try
        {
            return DateTime.UtcNow - System.IO.Directory.GetCreationTimeUtc(directory)
                   > GraceBeforeAbandoned;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 読み取り専用の属性を外す。書庫の中身に読み取り専用のファイルがあった場合や、
    /// 開いた先のアプリが属性を付けた場合、そのままでは消すことも取り出し直すこともできない。
    /// </summary>
    public static void ClearReadOnly(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            var attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.ReadOnly))
            {
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>フォルダを配下ごと消す。</summary>
    /// <returns>すべて消せた場合は true。</returns>
    public static bool TryDeleteTree(string directory)
    {
        if (!System.IO.Directory.Exists(directory))
        {
            return true;
        }

        try
        {
            foreach (var file in System.IO.Directory.EnumerateFiles(
                         directory, "*", SearchOption.AllDirectories))
            {
                ClearReadOnly(file);
            }

            System.IO.Directory.Delete(directory, recursive: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 開いたままのアプリに掴まれているファイルは消せない。
            // 消せた分はそのまま消えており、残りは次回起動時に片付ける。
            return false;
        }
    }

    /// <summary>フォルダ名に使えない文字を落とす。</summary>
    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();

        if (cleaned.Length == 0)
        {
            return "archive";
        }

        // パス全体が長くなりすぎないよう頭を残して切る
        return cleaned.Length <= 40 ? cleaned : cleaned[..40];
    }
}
