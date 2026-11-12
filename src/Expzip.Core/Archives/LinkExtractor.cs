using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Expzip.Localization;
using Microsoft.Win32.SafeHandles;

namespace Expzip.Archives;

/// <summary>展開する 1 個のリンク (#336)。</summary>
/// <param name="Name">書庫の中の名前。</param>
/// <param name="Kind">種類。</param>
/// <param name="Target">指す先。書庫に入っているまま (区切りは <c>/</c> か <c>\</c>)。</param>
/// <param name="ToFolder">フォルダーを指すか。</param>
internal sealed record LinkRequest(string Name, RarLinkKind Kind, string Target, bool ToFolder);

/// <summary>
/// 書庫に入っているリンクを、リンクのまま展開する (#336)。
/// </summary>
/// <remarks>
/// <para>
/// リンクも書庫の中身の 1 つなので、ファイルと同じく元の形に戻す (#336 で決めた)。コピーで代わりにはしない。
/// ただし、書庫を作った人は、リンクに展開先の外を指させられる。そのリンクの下に後ろの項目を置かれると、
/// その項目を展開先の外に書いてしまう (7-Zip の CVE-2025-11001、CVE-2025-55188 と同じ攻撃)。そこで次を守る。
/// </para>
/// <list type="bullet">
/// <item><description>リンクは、全部のファイルを書き出したあとに展開する。後ろの項目がリンクを通って書かれることが無い。</description></item>
/// <item><description>
/// シンボリックリンクとジャンクションは、書庫の中を指す相対パスのものだけを展開する。指す先は、ディスクを見ずに文字の上で解く。
/// 絶対パス、ドライブ名、ネットワークのパス、<c>\\?\</c>、<c>\??\</c>、<c>:</c> を含むもの、書庫の外に出るもの、
/// 書庫に無い名前を指すものは展開しない。
/// </description></item>
/// <item><description>リンクを置く場所までの途中のフォルダーに、リンク (リパースポイント) があれば展開しない。</description></item>
/// <item><description>
/// ハードリンクとファイルのコピーの指す先は、今回この書庫から展開したファイルだけ。展開先に前からあったファイルは指させない。
/// </description></item>
/// <item><description>同じ名前のものがあれば、ファイルを指すリンクは上書きの確認の結果に従う。フォルダーを指すリンクは展開しない (#372)。</description></item>
/// <item><description>一時的な名前で作ってから、1 回の名前の変更で置く (#320)。MotW は付けない (付けると指す先に書かれる)。</description></item>
/// </list>
/// </remarks>
internal static class LinkExtractor
{
    /// <summary>リンクをまとめて展開する。ファイルを全部書き出したあとに呼ぶ。</summary>
    /// <param name="links">展開するリンク。書庫の順。</param>
    /// <param name="archiveNames">書庫の中のすべての項目の名前 (フォルダーを含む)。指す先が書庫の中かを決める。</param>
    /// <param name="state">展開の途中経過。結果をここに記録する。</param>
    /// <param name="zone">ファイルのコピーに付ける MotW。</param>
    /// <param name="cancellationToken">中断用。</param>
    public static void Extract(
        IReadOnlyList<LinkRequest> links, IReadOnlyCollection<string> archiveNames, ExtractState state, string? zone,
        CancellationToken cancellationToken)
    {
        var inArchive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in archiveNames)
        {
            var path = ArchivePath.Normalize(name).Trim('/');
            for (var current = path; current.Length > 0; current = Parent(current))
            {
                if (!inArchive.Add(current))
                {
                    break;
                }
            }
        }

        foreach (var link in links)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                if (ExtractOne(link, inArchive, state, zone, cancellationToken) is { } reason)
                {
                    state.Fail(link.Name, reason);
                }
            }
            catch (Exception ex) when (ExtractOverwrite.IsNotReplaced(ex, ArchiveExtractor.TargetFor(state.Root, link.Name, state.BasePath) ?? string.Empty))
            {
                state.NotReplace(link.Name, Strings.Reason(ex));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                           or NotSupportedException or PathTooLongException)
            {
                state.Fail(link.Name, Strings.Reason(ex));
            }
        }
    }

    /// <summary>1 個を展開する。展開しなかったときは理由を返す。</summary>
    private static string? ExtractOne(
        LinkRequest link, HashSet<string> inArchive, ExtractState state, string? zone, CancellationToken cancellationToken)
    {
        var path = ArchiveExtractor.TargetFor(state.Root, link.Name, state.BasePath);
        if (path is null)
        {
            return Strings.LinkUnsafeName;
        }

        // リンクを置く場所までの途中にリンクがあれば、それを通って外に置かれることがある
        if (HasLinkOnTheWay(Path.GetDirectoryName(path)!, state.Root))
        {
            return Strings.LinkThroughLink;
        }

        var linkInArchive = ArchivePath.Normalize(link.Name).Trim('/');

        if (link.Kind is RarLinkKind.HardLink or RarLinkKind.FileCopy)
        {
            return ExtractCopyOrHardLink(link, path, state, zone, cancellationToken);
        }

        // シンボリックリンクとジャンクション: 書庫の中を指す相対パスだけ。一部を選んで展開したときは、選んだ範囲の中を指すものだけ。
        // 範囲の外を指すと、選んだフォルダーを取り除いて置いたときに、展開先の外を指すことになる
        if (Resolve(linkInArchive, link.Target) is not { } resolved || !inArchive.Contains(resolved)
            || !InsideBase(resolved, state.BasePath))
        {
            return Strings.LinkPointsOutside;
        }

        var toFolder = link.Kind == RarLinkKind.Junction || link.ToFolder;
        var decision = state.Overwrite.Decide(path);

        if (Directory.Exists(path) || File.Exists(path))
        {
            // フォルダーを指すリンクは 1 回では置き換えられない。ファイルを指すリンクは上書きの確認に従う (#372)
            if (toFolder || decision.Action is ExtractAction.Blocked)
            {
                state.NotReplace(link.Name, Strings.LinkNameTaken);
                return null;
            }

            if (decision.Action is ExtractAction.Skip)
            {
                return null;
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var relative = RelativeTarget(linkInArchive, resolved);
        var temporary = AtomicFile.TemporaryPathFor(path);

        if (link.Kind == RarLinkKind.Junction)
        {
            // ジャンクションは絶対パスしか持てない。展開先の中の、指す先のフォルダーを指す
            var absolute = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, relative));
            CreateJunction(temporary, absolute);
        }
        else
        {
            CreateSymbolicLink(temporary, relative, toFolder);
        }

        try
        {
            MoveLink(temporary, path, replace: decision.Action is ExtractAction.Replace or ExtractAction.ReplaceApproved);
        }
        catch
        {
            RemoveLink(temporary, toFolder);
            throw;
        }

        state.CountExtracted();
        return null;
    }

    /// <summary>ハードリンクとファイルのコピー。指す先は、今回この書庫から展開したファイル。</summary>
    private static string? ExtractCopyOrHardLink(
        LinkRequest link, string path, ExtractState state, string? zone, CancellationToken cancellationToken)
    {
        // RAR5 のハードリンクとファイルのコピーの指す先は、書庫の中の名前。一部を選んで展開したときは、選んだ範囲の中のものだけ
        var source = InsideBase(ArchivePath.Normalize(link.Target).Trim('/'), state.BasePath)
            ? ArchiveExtractor.TargetFor(state.Root, link.Target, state.BasePath)
            : null;
        if (source is null || !File.Exists(source) || !state.Overwrite.WasCreated(source))
        {
            return Strings.LinkTargetNotExtracted;
        }

        var decision = state.Overwrite.Decide(path);
        switch (decision.Action)
        {
            case ExtractAction.Blocked:
                state.NotReplace(link.Name, Strings.ExtractBlocked(decision.Block!.Value));
                return null;
            case ExtractAction.Skip:
                return null;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (link.Kind == RarLinkKind.HardLink)
        {
            var temporary = AtomicFile.TemporaryPathFor(path);
            CreateHardLink(temporary, source);

            try
            {
                MoveLink(temporary, path, replace: decision.Action != ExtractAction.Write);
            }
            catch
            {
                ZipArchiveWriter.TryDelete(temporary);
                throw;
            }

            state.CountExtracted();
            return null;
        }

        // ファイルのコピー: 中身を写したふつうのファイル (UnRAR と同じ)。ほかのファイルと同じく MotW を付けて置き換える
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var file = AtomicFile.CreateForExtraction(
                   path, decision.Action != ExtractAction.Write, zone,
                   decision.Action == ExtractAction.ReplaceApproved ? state.Overwrite.IsApproved : null))
        {
            CancellableCopy.Copy(input, file.Stream, cancellationToken);
            if (!file.Commit(File.GetLastWriteTime(source)))
            {
                return null;
            }
        }

        state.Overwrite.Created(path);
        state.CountExtracted();
        return null;
    }

    /// <summary>
    /// 指す先を、書庫の中の名前として解く。書庫の外に出るもの、解けない形のものは <see langword="null"/>。
    /// </summary>
    /// <param name="link">リンクの書庫の中の名前。</param>
    /// <param name="target">書庫に入っている指す先。</param>
    internal static string? Resolve(string link, string target)
    {
        if (target.Length == 0 || target.Any(char.IsControl))
        {
            return null;
        }

        var text = target.Replace('\\', '/');

        // 絶対パス、ドライブ名、ネットワーク、\\?\ と \??\、代替データストリーム
        if (text.StartsWith('/') || text.Contains(':') || text.StartsWith("??/", StringComparison.Ordinal))
        {
            return null;
        }

        var parts = new List<string>();
        var folder = Parent(link);
        if (folder.Length > 0)
        {
            parts.AddRange(folder.Split('/'));
        }

        foreach (var part in text.Split('/'))
        {
            switch (part)
            {
                case "" or ".":
                    continue;
                case "..":
                    if (parts.Count == 0)
                    {
                        return null;
                    }

                    parts.RemoveAt(parts.Count - 1);
                    break;
                default:
                    // 末尾の点や空白、予約名など、Windows で別の名前になるものは扱わない
                    if (part.EndsWith('.') || part.EndsWith(' ') || ReservedNameException.IsReserved(@"C:\" + part))
                    {
                        return null;
                    }

                    parts.Add(part);
                    break;
            }
        }

        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>書庫の中の名前が、選んだ範囲 (<paramref name="basePath"/>) の中か。範囲が無ければ常に中。</summary>
    private static bool InsideBase(string name, string? basePath)
    {
        if (string.IsNullOrEmpty(basePath))
        {
            return true;
        }

        var top = ArchivePath.Normalize(basePath).Trim('/');
        return name.StartsWith(top + "/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>ハードリンクを作る。作れない理由は Windows の答えで分ける。</summary>
    /// <exception cref="IOException">作れない場合。ドライブが対応していなければ、その理由の文。</exception>
    private static void CreateHardLink(string link, string existing)
    {
        if (!OperatingSystem.IsWindows())
        {
            if (!UndoHistory.HardLink.TryCreate(link, existing))
            {
                throw new IOException(Strings.HardLinkNotSupported);
            }

            return;
        }

        if (CreateHardLinkW(AtomicFile.LongPathIfNeeded(link), AtomicFile.LongPathIfNeeded(existing), IntPtr.Zero))
        {
            return;
        }

        var error = Marshal.GetLastPInvokeError();

        // 関数が無い、対応していない、引数が正しくない (FAT32、exFAT、ネットワークのドライブなど)
        throw error is 1 or 50 or 87
            ? new HardLinkNotSupportedException()
            : new IOException(new System.ComponentModel.Win32Exception(error).Message, unchecked((int)0x80070000 | error));
    }

    /// <summary>リンクのあるフォルダーから、指す先への相対パス (Windows の区切り)。</summary>
    private static string RelativeTarget(string link, string resolved)
    {
        var from = Parent(link);
        var relative = Path.GetRelativePath(from.Length == 0 ? "." : from.Replace('/', '\\'), resolved.Replace('/', '\\'));
        return relative;
    }

    /// <summary>途中のフォルダー (展開先より下) にリンクがあるか。</summary>
    private static bool HasLinkOnTheWay(string folder, string root)
    {
        var top = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        for (var current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
             current.Length > top.Length;
             current = Path.GetDirectoryName(current)!)
        {
            if (DiskLinks.Describe(current) is { } item && (item.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string Parent(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    // ---------------------------------------------------------------- 作る、置く

    /// <summary>シンボリックリンクを作る。開発者モードなら管理者でなくても作れる指定を付ける。</summary>
    /// <exception cref="LinkPrivilegeException">この PC でシンボリックリンクを作る権限が無い場合。</exception>
    private static void CreateSymbolicLink(string path, string target, bool toFolder)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.CreateSymbolicLink(path, target.Replace('\\', '/'));
            return;
        }

        const uint Directory = 0x1;
        const uint AllowUnprivileged = 0x2;
        if (!CreateSymbolicLinkW(AtomicFile.LongPathIfNeeded(path), target, (toFolder ? Directory : 0) | AllowUnprivileged))
        {
            var error = Marshal.GetLastPInvokeError();
            throw error == ErrorPrivilegeNotHeld
                ? new LinkPrivilegeException()
                : new IOException(new System.ComponentModel.Win32Exception(error).Message, unchecked((int)0x80070000 | error));
        }
    }

    /// <summary>ジャンクションを作る (フォルダーを作り、リパースのデータを書く)。管理者でなくても作れる。</summary>
    private static void CreateJunction(string path, string absoluteTarget)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(path, absoluteTarget);
            return;
        }

        Directory.CreateDirectory(path);
        try
        {
            using var handle = CreateFileW(
                AtomicFile.LongPathIfNeeded(path), GenericWrite, 0, IntPtr.Zero, OpenExisting,
                FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
            if (handle.IsInvalid)
            {
                throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            // REPARSE_DATA_BUFFER (IO_REPARSE_TAG_MOUNT_POINT)。置き換え名は \??\ 付き、表示名はふつうのパス
            var substitute = Encoding.Unicode.GetBytes(@"\??\" + absoluteTarget);
            var print = Encoding.Unicode.GetBytes(absoluteTarget);
            var pathBuffer = substitute.Length + 2 + print.Length + 2;
            var data = new byte[8 + 8 + pathBuffer];
            BitConverter.TryWriteBytes(data.AsSpan(0), DiskLinks.MountPointTag);
            BitConverter.TryWriteBytes(data.AsSpan(4), (ushort)(8 + pathBuffer));
            BitConverter.TryWriteBytes(data.AsSpan(8), (ushort)0);
            BitConverter.TryWriteBytes(data.AsSpan(10), (ushort)substitute.Length);
            BitConverter.TryWriteBytes(data.AsSpan(12), (ushort)(substitute.Length + 2));
            BitConverter.TryWriteBytes(data.AsSpan(14), (ushort)print.Length);
            substitute.CopyTo(data, 16);
            print.CopyTo(data, 16 + substitute.Length + 2);

            if (!DeviceIoControl(handle, FsctlSetReparsePoint, data, data.Length, IntPtr.Zero, 0, out _, IntPtr.Zero))
            {
                throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }
        }
        catch
        {
            Directory.Delete(path);
            throw;
        }
    }

    /// <summary>作ったリンクを本来の名前に置く。リンクそのものを動かし、指す先はたどらない。</summary>
    private static void MoveLink(string temporary, string path, bool replace)
    {
        if (!OperatingSystem.IsWindows())
        {
            File.Move(temporary, path, replace);
            return;
        }

        const uint ReplaceExisting = 0x1;
        if (!MoveFileExW(AtomicFile.LongPathIfNeeded(temporary), AtomicFile.LongPathIfNeeded(path), replace ? ReplaceExisting : 0))
        {
            var error = Marshal.GetLastPInvokeError();
            throw new IOException(new System.ComponentModel.Win32Exception(error).Message, unchecked((int)0x80070000 | error));
        }
    }

    /// <summary>置けなかったリンクを消す。リンクそのものだけを消し、指す先には触れない。</summary>
    private static void RemoveLink(string path, bool toFolder)
    {
        try
        {
            if (toFolder || Directory.Exists(path))
            {
                Directory.Delete(path);
            }
            else
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private const int ErrorPrivilegeNotHeld = 1314;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;
    private const uint FileFlagOpenReparsePoint = 0x00200000;
    private const uint FsctlSetReparsePoint = 0x000900A4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool CreateSymbolicLinkW(string link, string target, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string from, string to, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeFileHandle CreateFileW(
        string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device, uint code, byte[] input, int inputSize, IntPtr output, int outputSize,
        out int returned, IntPtr overlapped);
}

/// <summary>展開先のドライブがハードリンクに対応していない (#336)。FAT32、exFAT、ネットワークのドライブなど。</summary>
internal sealed class HardLinkNotSupportedException() : IOException("The destination drive does not support hard links.");

/// <summary>この PC でシンボリックリンクを作る権限が無い (#336)。管理者か開発者モードでないと作れない。</summary>
internal sealed class LinkPrivilegeException() : IOException("A required privilege is not held to create a symbolic link.");
