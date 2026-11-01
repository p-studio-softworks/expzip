using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Expzip.Archives;

/// <summary>
/// 取り出したファイルに、インターネットから来た印を付けられなかったことを見張る (#321)。
/// </summary>
/// <remarks>
/// 書けたように見えても残らないドライブがあるので、最初の 1 個だけ、置き換えたあとで読み直す。
/// 取り出しの途中のファイルはこの読み方では読めないので、置き換えたあとに行う。並列の取り出しからも呼ばれる。
/// </remarks>
internal sealed class MarkWatch
{
    private int _verified;
    private volatile bool _lost;

    /// <summary>印が保存されなかったファイルがあった。</summary>
    public bool Lost => _lost;

    /// <summary>取り出せたファイルを 1 個知らせる。</summary>
    public void Committed(AtomicFile file, string target)
    {
        if (file.Zone is not { } zone || !MarkOfTheWeb.IsFromInternet(zone))
        {
            return;
        }

        if (file.MarkLost)
        {
            _lost = true;
        }
        else if (Interlocked.Exchange(ref _verified, 1) == 0 && MarkOfTheWeb.TryRead(target) is null)
        {
            _lost = true;
        }
    }
}

/// <summary>
/// 失敗しても元のファイルが残るように書く (#320)。
/// </summary>
/// <remarks>
/// <para>
/// 同じフォルダーの一時的な名前に書き、できあがってから 1 回の名前の変更で置き換える。
/// エラー、中止、強制終了のどれでも、元のファイルか、できあがった新しいファイルのどちらかが残る。
/// 書きかけが本来の名前で残ることもない。
/// </para>
/// <para>
/// 一時ファイルには作った時点で出所の印を付ける。名前は書庫の中の名前から作らず、実行されない拡張子にする。
/// 取り残されても、印の無い実行ファイルにはならない。
/// </para>
/// <para>
/// 電源が切れたときのために、既にあるファイルを置き換える前にだけディスクへ書き切る。
/// 新しく作るファイルでは待たない (小さなファイルをたくさん取り出すと遅くなる)。
/// </para>
/// </remarks>
internal sealed class AtomicFile : IDisposable
{
    private const int SharingViolation = unchecked((int)0x80070020);
    private const int LockViolation = unchecked((int)0x80070021);
    private const int MoveAttempts = 5;

    /// <summary>書いている途中の一時ファイル。固まって終了するときに消す。</summary>
    private static readonly HashSet<string> Active = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Lock Gate = new();
    private static bool _closed;

    private readonly string _target;
    private readonly string _temporary;
    private readonly bool _overwrite;
    private bool _replacing;
    private bool _writingClosed;
    private bool _finished;

    private AtomicFile(string target, string temporary, bool overwrite, string? zone)
    {
        _target = target;
        _temporary = temporary;
        _overwrite = overwrite;

        RefuseProtected(target);
        _replacing = File.Exists(target);

        if (_replacing)
        {
            RefuseUnwritable(target);
        }

        Register(temporary);
        try
        {
            Stream = Open(temporary);

            // 印を先に付ける。アクセス権を写すと、印を書けなくなることがある
            Zone = zone;
            MarkLost = zone is not null && !MarkOfTheWeb.TryApply(temporary, zone);

            if (_replacing)
            {
                CopyAccessRules(target, temporary);
            }
        }
        catch
        {
            Stream?.Dispose();
            Discard(temporary);
            throw;
        }
    }

    /// <summary>書き込む先。</summary>
    public FileStream Stream { get; }

    /// <summary>付けるように渡された印。</summary>
    public string? Zone { get; }

    /// <summary>印を渡されたのに、付けられなかった (exFAT など、印を保存できないドライブ)。</summary>
    public bool MarkLost { get; }

    /// <summary>書き始める。</summary>
    /// <param name="target">できあがったときの名前。</param>
    /// <param name="overwrite">既にあるファイルを置き換えるか。</param>
    /// <param name="zone">付ける出所の印。</param>
    /// <exception cref="UnauthorizedAccessException">読み取り専用、隠し、システムのファイルや、フォルダーがその名前である場合。</exception>
    public static AtomicFile Create(string target, bool overwrite = true, string? zone = null)
        => new(target, TemporaryPathFor(target), overwrite, zone);

    /// <summary>できあがるまで使う名前。</summary>
    public static string TemporaryPathFor(string target)
        => ZipArchiveWriter.WorkFilePathOverride?.Invoke(target)
           ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(target))!, $"~{Guid.NewGuid():N}.tmp");

    /// <summary>書き終えた。閉じて置き換える。</summary>
    /// <param name="lastWriteTime">付ける更新日時。</param>
    /// <returns>
    /// 置き換えたら <see langword="true"/>。上書きしない指定で、書いている間に同じ名前ができていたら <see langword="false"/>。
    /// </returns>
    public bool Commit(DateTime? lastWriteTime = null)
    {
        lock (Gate)
        {
            ThrowIfClosed();
        }

        // 書いている間に属性が変わることがある
        RefuseProtected(_target);

        if (TryCommitOpen(lastWriteTime))
        {
            Finish();
            return true;
        }

        CloseWriting(lastWriteTime);

        try
        {
            Move(_temporary, _target, _overwrite);
        }
        catch (IOException ex) when (!_overwrite && File.Exists(_target) && !IsSharing(ex))
        {
            return false;
        }

        Finish();
        return true;
    }

    /// <summary>
    /// 複数のファイルをまとめて置き換える。途中で失敗したら、それまでに置き換えたものを元に戻す。
    /// </summary>
    /// <remarks>
    /// 既にあるファイルは、一時的な名前にも見えるようにして (ハードリンク、作れなければコピー) から置き換える。
    /// 本来の名前には、どの時点でも古いか新しいかのファイルがある。強制終了や電源断では、組の一部だけが新しくなり、
    /// 一時的な名前が残ることがある。
    /// </remarks>
    public static void CommitAll(IReadOnlyList<AtomicFile> files)
    {
        lock (Gate)
        {
            ThrowIfClosed();
        }

        foreach (var file in files)
        {
            file.CloseWriting(null);
            RefuseProtected(file._target);
        }

        var done = new List<(string Target, string? Kept)>();
        try
        {
            foreach (var file in files)
            {
                string? kept = null;
                if (File.Exists(file._target))
                {
                    kept = TemporaryPathFor(file._target);
                    if (!UndoHistory.HardLink.TryCreate(kept, file._target))
                    {
                        File.Copy(file._target, kept);
                    }

                    done.Add((file._target, kept));
                }

                Move(file._temporary, file._target, overwrite: kept is not null);
                file.Finish();

                if (kept is null)
                {
                    done.Add((file._target, null));
                }
            }
        }
        catch
        {
            for (var i = done.Count - 1; i >= 0; i--)
            {
                var (target, kept) = done[i];
                try
                {
                    if (kept is null)
                    {
                        Discard(target);
                    }
                    else
                    {
                        Move(kept, target, overwrite: true);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 残りは戻す。最初の失敗の理由を伝える
                }
            }

            throw;
        }

        foreach (var (_, kept) in done)
        {
            if (kept is not null)
            {
                Discard(kept);
            }
        }
    }

    /// <summary>できあがっている作業用ファイルで置き換える。出所の印とアクセス権は置き換える先から引き継ぐ。</summary>
    /// <param name="work">作業用ファイル。同じドライブに置く。</param>
    /// <param name="target">置き換える先。</param>
    /// <param name="overwrite">既にあるファイルを置き換えるか。</param>
    /// <param name="zone">付ける出所の印。省くと、置き換える先に付いている印。</param>
    public static void Replace(string work, string target, bool overwrite = true, string? zone = null)
    {
        lock (Gate)
        {
            ThrowIfClosed();
        }

        if (File.Exists(target))
        {
            RefuseUnwritable(target);
            zone ??= MarkOfTheWeb.TryRead(target);
            CopyAccessRules(target, work);

            using var stream = new FileStream(work, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            stream.Flush(flushToDisk: true);
        }

        // 印を付けると更新日時が変わるので、同じ印が付いていれば付けず、付けたら更新日時を戻す。
        // 元に戻すときの作業用ファイルには、前の読めない印が残っていることがあるので、原文で比べる (#325)
        if (zone is not null && !MarkOfTheWeb.HasExactly(work, zone))
        {
            var written = File.GetLastWriteTimeUtc(work);
            MarkOfTheWeb.TryApply(work, zone);

            try
            {
                File.SetLastWriteTimeUtc(work, written);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 更新日時が今の時刻になるだけで、書庫の中身と印は正しい
            }
        }

        Move(work, target, overwrite);
    }

    /// <summary>文字列を書く。</summary>
    public static void WriteAllText(string target, string text)
    {
        using var file = Create(target);
        file.Stream.Write(new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(text));
        file.Commit();
    }

    /// <summary>ファイルをコピーする。出所の印も写す。</summary>
    public static void Copy(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var file = Create(target, overwrite: true, MarkOfTheWeb.TryRead(source));
        input.CopyTo(file.Stream);
        file.Commit(File.GetLastWriteTime(source));
    }

    /// <summary>
    /// 固まった処理を残して終了するときに呼ぶ。書いている途中の一時ファイルを消し、これからは作らせない。
    /// </summary>
    /// <remarks>
    /// 止まっていた処理が動き出すことがあるので、置き換えも断る。ネットワークの場所では消すのも固まるので、待つ時間を区切る。
    /// 開いている間は消えず、プロセスが終わって閉じたときに消える。
    /// </remarks>
    public static void AbandonAll(TimeSpan wait)
    {
        string[] remaining;
        lock (Gate)
        {
            _closed = true;
            remaining = [.. Active];
        }

        var removing = Task.Run(() =>
        {
            foreach (var path in remaining)
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 消せなければ .tmp が残るだけ
                }
            }
        });

        removing.Wait(wait);
    }

    /// <summary>まだ置き換えていなければ、一時ファイルを消す。</summary>
    public void Dispose()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        try
        {
            // 容量不足で、閉じるときの書き出しに失敗することがある
            Stream.Dispose();
        }
        finally
        {
            Discard(_temporary);
        }
    }

    /// <summary>一時ファイルを開く。名前を変えられるように、削除の権利も付けて開く。</summary>
    private static FileStream Open(string temporary)
    {
        var mode = ZipArchiveWriter.WorkFilePathOverride is null ? FileMode.CreateNew : FileMode.Create;
        const FileShare Share = FileShare.Read | FileShare.Delete;

        return OperatingSystem.IsWindows()
            ? new FileInfo(temporary).Create(
                mode,
                FileSystemRights.Write | FileSystemRights.ReadAttributes | FileSystemRights.Delete | FileSystemRights.Synchronize,
                Share, 4096, FileOptions.None, null)
            : new FileStream(temporary, mode, FileAccess.Write, Share);
    }

    /// <summary>
    /// 開いたまま名前を変えて、閉じる。
    /// </summary>
    /// <remarks>
    /// 閉じてから名前を変えると、変えるときにウイルス対策などがもう一度調べるので遅い。実測で、小さなファイル
    /// 2 万個を 20 本で作るのに、直接書くと 1.8 秒、閉じてから変えると 4.9 秒、開いたまま変えると 2.4 秒。
    /// できないとき (ほかの OS、対応していないドライブなど) は <see langword="false"/> を返す。呼び出し側が閉じてから変える。
    /// </remarks>
    private bool TryCommitOpen(DateTime? lastWriteTime)
    {
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8 || _writingClosed)
        {
            return false;
        }

        var replacing = File.Exists(_target);
        Stream.Flush(flushToDisk: replacing);

        // 書き終えてから付ける。付けたあとは、閉じても書き込んだ時刻で上書きされない
        if (lastWriteTime is { } time && time != default)
        {
            try
            {
                File.SetLastWriteTime(Stream.SafeFileHandle, time);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
            {
                // 範囲外の日付が入っている書庫がある。中身は取り出せている
            }
        }

        if (!RenameOpen(Stream.SafeFileHandle, Path.GetFullPath(_target), _overwrite))
        {
            return false;
        }

        _writingClosed = true;
        Stream.Dispose();
        return true;
    }

    /// <summary>開いているファイルの名前を変える (64 ビットの <c>FILE_RENAME_INFO</c>)。</summary>
    private static bool RenameOpen(Microsoft.Win32.SafeHandles.SafeFileHandle handle, string target, bool overwrite)
    {
        const int FileRenameInfo = 3;
        const int NameOffset = 20;

        // 長いパスは、ファイルを開くときと同じ形にする
        var name = target.Length < 260 || target.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? target
            : target.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + target[2..]
                : @"\\?\" + target;

        var bytes = Encoding.Unicode.GetBytes(name);
        var info = new byte[NameOffset + bytes.Length + 2];
        info[0] = overwrite ? (byte)1 : (byte)0;
        BitConverter.TryWriteBytes(info.AsSpan(16), bytes.Length);
        bytes.CopyTo(info, NameOffset);

        return SetFileInformationByHandle(handle, FileRenameInfo, info, info.Length);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, byte[] info, int size);

    private void CloseWriting(DateTime? lastWriteTime)
    {
        if (_writingClosed)
        {
            return;
        }

        _writingClosed = true;
        _replacing = File.Exists(_target);

        if (_replacing)
        {
            Stream.Flush(flushToDisk: true);
        }

        Stream.Dispose();

        if (lastWriteTime is { } time && time != default)
        {
            try
            {
                File.SetLastWriteTime(_temporary, time);
            }
            catch (Exception ex) when (ex is ArgumentOutOfRangeException or IOException or UnauthorizedAccessException)
            {
                // 範囲外の日付が入っている書庫がある。中身は取り出せている
            }
        }
    }

    private void Finish()
    {
        _finished = true;
        Unregister(_temporary);
    }

    private static void Register(string temporary)
    {
        lock (Gate)
        {
            ThrowIfClosed();
            Active.Add(temporary);
        }
    }

    private static void Unregister(string temporary)
    {
        lock (Gate)
        {
            Active.Remove(temporary);
        }
    }

    private static void ThrowIfClosed()
    {
        if (_closed)
        {
            throw new OperationCanceledException();
        }
    }

    private static void Discard(string path)
    {
        ZipArchiveWriter.TryDelete(path);
        Unregister(path);
    }

    /// <summary>
    /// 読み取り専用、隠し、システムのファイルは置き換えない。
    /// </summary>
    /// <remarks>
    /// 直接書くときは Windows が断るが、名前の変更では隠しファイルやシステムファイルも置き換えられてしまう。
    /// 書庫に入れた名前で、取り出し先の <c>desktop.ini</c> などを置き換えさせない。
    /// </remarks>
    private static void RefuseProtected(string target)
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(target);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return;
        }

        if ((attributes & (FileAttributes.Directory | FileAttributes.ReadOnly
                                                    | FileAttributes.Hidden | FileAttributes.System)) != 0)
        {
            throw new UnauthorizedAccessException($"Access to the path '{target}' is denied.");
        }
    }

    /// <summary>
    /// 書き込みを禁じられたファイルは置き換えない。
    /// </summary>
    /// <remarks>
    /// 名前の変更に要るのはフォルダーの権利なので、ファイルのアクセス権で書き込みを禁じていても置き換えられてしまう。
    /// 切り詰めずに、書き込みの権利で開いてみる。ほかのプログラムが使用中なら、ここでその理由で断る。
    /// </remarks>
    private static void RefuseUnwritable(string target)
    {
        using var probe = new FileStream(
            target, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
    }

    /// <summary>
    /// 既にあるファイルに個別に付けられたアクセス権を、一時ファイルへ写す。
    /// </summary>
    /// <remarks>
    /// 置き換えるとフォルダーのアクセス権に戻るので、閉じていたファイルの新しい中身がほかの利用者に見えることがある。
    /// フォルダーから受け継いだだけなら、一時ファイルも同じなので何もしない。写せなければ置き換えない。
    /// </remarks>
    private static void CopyAccessRules(string target, string temporary)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        FileSecurity rules;
        try
        {
            rules = new FileInfo(target).GetAccessControl(AccessControlSections.Access);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException
                                       or NotSupportedException)
        {
            // アクセス権の無いドライブ (exFAT など)
            return;
        }

        if (!rules.AreAccessRulesProtected
            && rules.GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier)).Count == 0)
        {
            return;
        }

        var file = new FileInfo(temporary);
        var wanted = rules.GetSecurityDescriptorSddlForm(AccessControlSections.Access);

        if (file.GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access)
            != wanted)
        {
            // 読んだものをそのまま渡しても、変更が無いものとして書かれない。作り直して渡す
            var copy = new FileSecurity();
            copy.SetSecurityDescriptorSddlForm(wanted, AccessControlSections.Access);
            file.SetAccessControl(copy);
        }
    }

    /// <summary>名前を変える。ウイルス対策などが一瞬つかんでいることがあるので、短くやり直す。</summary>
    private static void Move(string source, string target, bool overwrite)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                File.Move(source, target, overwrite);
                return;
            }
            catch (Exception ex) when (attempt < MoveAttempts
                                       && (IsSharing(ex) || (ex is UnauthorizedAccessException && overwrite)))
            {
                Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException) when (overwrite && File.Exists(target))
            {
                // ほかのプログラムが使用中のファイルは、置き換えようとすると「アクセスが拒否されました」になる。
                // 本当の理由を伝えるために、開いてみる
                using var probe = new FileStream(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                throw;
            }
        }
    }

    private static bool IsSharing(Exception ex)
        => ex is IOException { HResult: SharingViolation or LockViolation };
}
