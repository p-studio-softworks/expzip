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
/// 取り出しでは、同じ名前のものが無ければ本来の名前に直接書く (<see cref="CreateForExtraction"/>、#328)。
/// 壊すおそれのあるファイルが無いので、名前の変更でウイルス対策の検査を待たずに済む。書きかけは Windows が消す。
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

    /// <summary>一時ファイルの名前を決める処理。省くと既定の規則 (~guid.tmp) で決める。</summary>
    public static Func<string, string>? TemporaryPathOverride { get; set; }

    /// <summary>上書きを拒む保護属性の判定を差し替える処理。</summary>
    public static Func<string, FileAttributes, bool>? IsProtectedOverride { get; set; }

    private readonly string _target;
    private readonly string _temporary;
    private readonly bool _overwrite;

    /// <summary>本来の名前に直接書いている (<see cref="CreateForExtraction"/>)。一時的な名前は無い。</summary>
    private readonly bool _direct;
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

    /// <summary>本来の名前に直接書くとき。印を付け、「閉じたら消す」を予約したあとのファイルを受け取る。</summary>
    private AtomicFile(string target, FileStream stream, string? zone, bool markLost)
    {
        _target = target;
        _temporary = target;
        _direct = true;
        Stream = stream;
        Zone = zone;
        MarkLost = markLost;
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

    /// <summary>
    /// 取り出しで書き始める。同じ名前のものが無ければ、本来の名前に直接書く (#328)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 一時的な名前から置き換えると、名前の変更のたびにウイルス対策の検査を待つ。小さなファイルが多いと 1.6 倍遅い。
    /// 同じ名前のものが無ければ、壊すおそれのあるファイルも無いので、本来の名前に新しく作る。
    /// その名前に何かあれば (ファイル、フォルダー、壊れたリンクも)、<see cref="Create"/> と同じ置き換えの道に移る。
    /// 置き換えの確かめ (書き込みの禁止、アクセス権) はそちらで全部通る。
    /// </para>
    /// <para>
    /// 書いている間は「閉じたら消す」を Windows に予約しておき、書き終えてから外す。エラー、中止、落ちた、
    /// 固まって終了させた、のどれでも Windows が書きかけを消す。残るのは電源断と、作ってから予約するまでの短い間に
    /// 終了したとき (0 バイト) だけで、どちらも印は付いている。
    /// 消すのは自分のハンドルが指すファイルなので、書いている間に名前を入れ替えられても、ほかのファイルは消さない。
    /// </para>
    /// </remarks>
    /// <exception cref="ReservedNameException">NUL など、この Windows で装置を指す名前の場合。</exception>
    public static AtomicFile CreateForExtraction(string target, bool overwrite, string? zone)
    {
        // 直接書くときは装置の名前の判定を通らない形 (\\?\) で開くので、先に尋ねる
        if (ReservedNameException.IsReserved(Path.GetFullPath(target)))
        {
            throw new ReservedNameException(target);
        }

        return TryCreateDirect(target, zone) ?? Create(target, overwrite, zone);
    }

    /// <summary>できあがるまで使う名前。</summary>
    public static string TemporaryPathFor(string target)
        => TemporaryPathOverride?.Invoke(target)
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

        if (_direct)
        {
            CommitDirect(lastWriteTime);
            return true;
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

        if (_direct)
        {
            // 「閉じたら消す」を予約してあるので、閉じれば Windows が消す。名前では消さない (入れ替わっていることがある)
            try
            {
                Stream.Dispose();
            }
            catch (IOException)
            {
                // 容量不足で、閉じるときの書き出しに失敗しても、閉じて消える
            }

            return;
        }

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
        var mode = TemporaryPathOverride is null && ZipArchiveWriter.WorkFilePathOverride is null ? FileMode.CreateNew : FileMode.Create;
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

    /// <summary>
    /// 本来の名前に新しく作る。印を付け、「閉じたら消す」を予約して返す。その名前に何かあれば <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// リンクをたどらずに開く (<c>FILE_FLAG_OPEN_REPARSE_POINT</c>) ので、壊れたリンクが置かれていても、その先には作らない。
    /// 削除を共有しない。予約するまでの間に、ほかのプログラムに名前を変えられて、入れ替わったものに印を付けたり消したりしないため。
    /// 予約の間はほかのプログラムがファイルを開けないので、印 (別のストリーム) は予約より前に付ける。中身はその後で書く。
    /// 予約できない場所 (ほかの OS、対応していないドライブ) では、作ったものを消して置き換えの道に移る。
    /// </remarks>
    private static AtomicFile? TryCreateDirect(string target, string? zone)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        lock (Gate)
        {
            ThrowIfClosed();
        }

        var full = Path.GetFullPath(target);
        var handle = CreateFileW(
            LongPath(full), FileGenericWrite | FileReadAttributes | DeleteAccess, FileShareRead,
            IntPtr.Zero, CreateNew, FileAttributeNormal | FileFlagOpenReparsePoint, IntPtr.Zero);

        if (handle.IsInvalid)
        {
            // 既にある、フォルダーがある、作れない、のどれでも置き換えの道に移る。作れない理由はそちらが今どおりに伝える
            handle.Dispose();
            return null;
        }

        try
        {
            // 予約された名前は先に断っているが、装置に当たったら書かない
            if (GetFileType(handle) != FileTypeDisk)
            {
                throw new ReservedNameException(target);
            }

            var markLost = zone is not null && !MarkOfTheWeb.TryApply(full, zone);

            if (!SetDeleteOnClose(handle, true))
            {
                // 作ったのは自分なので、名前で消してよい。この間に入れ替わることはまず無い
                handle.Dispose();
                ZipArchiveWriter.TryDelete(full);
                return null;
            }

            return new AtomicFile(full, new FileStream(handle, FileAccess.Write, 4096), zone, markLost);
        }
        catch
        {
            SetDeleteOnClose(handle, true);
            handle.Dispose();
            throw;
        }
    }

    /// <summary>直接書いたファイルを確定する。日時を付け、「閉じたら消す」を外して閉じる。名前は変えない。</summary>
    private void CommitDirect(DateTime? lastWriteTime)
    {
        // 書き終えてから日時を付ける。付けたあとは、閉じても書き込んだ時刻で上書きされない
        Stream.Flush(flushToDisk: false);

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

        if (!SetDeleteOnClose(Stream.SafeFileHandle, false))
        {
            // 外せなければ、閉じたときに消える。取り出せなかったものとして知らせる
            throw new IOException(
                $"Could not keep '{_target}'.", System.Runtime.InteropServices.Marshal.GetHRForLastWin32Error());
        }

        _finished = true;
        Stream.Dispose();
    }

    /// <summary>「閉じたら消す」を予約する、または外す (<c>FILE_DISPOSITION_INFO</c>)。</summary>
    private static bool SetDeleteOnClose(Microsoft.Win32.SafeHandles.SafeFileHandle handle, bool delete)
    {
        const int FileDispositionInfo = 4;
        return SetFileInformationByHandle(handle, FileDispositionInfo, [delete ? (byte)1 : (byte)0], 1);
    }

    /// <summary>Windows の関数に渡すときに、長いパスでも開ける形にする。</summary>
    private static string LongPath(string full)
        => full.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal)
                ? @"\\?\UNC\" + full[2..]
                : @"\\?\" + full;

    private const uint FileGenericWrite = 0x00120116;
    private const uint FileReadAttributes = 0x00000080;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileShareRead = 0x00000001;
    private const uint CreateNew = 1;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FileTypeDisk = 1;

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint mode, uint flags, IntPtr template);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetFileType(Microsoft.Win32.SafeHandles.SafeFileHandle handle);

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

        var isProtected = IsProtectedOverride is not null
            ? IsProtectedOverride(target, attributes)
            : (attributes & (FileAttributes.Directory | FileAttributes.ReadOnly
                                                        | FileAttributes.Hidden | FileAttributes.System)) != 0;

        if (isProtected)
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
            try
            {
                File.SetUnixFileMode(temporary, File.GetUnixFileMode(target));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

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
