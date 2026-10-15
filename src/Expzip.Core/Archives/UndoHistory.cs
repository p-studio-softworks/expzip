using System.IO;
using System.Runtime.InteropServices;

namespace Expzip.Archives;

/// <summary>元に戻せる書き換えの種類 (#260)。画面に「元に戻す - 移動」のように出す。</summary>
internal enum UndoKind
{
    Move,
    Delete,
    Rename,
    NewFolder,
    Add,
    Password,
    Comment,
    WriteBack,
}

/// <summary>元に戻した結果 (#260)。</summary>
internal enum UndoOutcome
{
    /// <summary>戻した。</summary>
    Restored,

    /// <summary>戻せるものが無い。</summary>
    Nothing,

    /// <summary>
    /// 書き換えたあとに、ほかのソフトが書庫を書き換えていた。戻すとその書き換えが消えるので戻さない。
    /// 履歴は捨てる。
    /// </summary>
    ChangedOutside,
}

/// <summary>
/// 書庫の書き換えを、1 つ前の状態へ戻すための履歴 (#260)。Expzip を閉じるまで持つ。
/// </summary>
/// <remarks>
/// <para>
/// <b>書き換える前の書庫を丸ごと取っておき、戻すときはそれと差し替える。</b>書き換えはどれも
/// 「別のファイルに書いてから、元の書庫と差し替える」作りなので、元の書庫を消さずに取っておけば済む。
/// 中身を 1 つずつ戻す仕組みは要らず、どの操作も同じ方法で戻せる。
/// </para>
/// <para>
/// 取っておくのは<b>ハードリンク</b>で行う。同じドライブなら中身を写さないので一瞬で、場所も取らない。
/// 書き換えると書庫は新しいファイルに差し替わり、リンクのほうに元の中身が残る。
/// 別のドライブ (一時フォルダーと違うドライブにある書庫) や、リンクを作れないドライブではコピーする。
/// 大きな書庫を毎回コピーすると操作のたびに待たされるので、<see cref="CopyLimit"/> を超えるものは取っておかない
/// (その書き換えは戻せない)。
/// </para>
/// <para>
/// コメント (#258) だけは書庫をその場で書き換えるので、ハードリンクでは元の中身が残らない。
/// 書き換える末尾の部分だけを取っておく。
/// </para>
/// </remarks>
internal sealed class UndoHistory
{
    /// <summary>1 つの書庫で戻せる回数。古いものから捨てる。</summary>
    public const int MaxSteps = 20;

    /// <summary>ハードリンクを作れないときに、コピーしてでも取っておく大きさの上限。</summary>
    public const long CopyLimit = 1L * 1024 * 1024 * 1024;

    private readonly Dictionary<string, List<Step>> _steps = new(StringComparer.OrdinalIgnoreCase);
    private int _issued;

    /// <summary>
    /// 書き換える前に呼ぶ。いまの書庫を取っておく。取っておけなかったら <see langword="null"/>
    /// (その書き換えは戻せない)。
    /// </summary>
    /// <param name="archivePath">書き換える書庫。</param>
    /// <param name="kind">書き換えの種類。</param>
    /// <param name="storeDirectory">取っておく場所。一時フォルダーの置き場の中。</param>
    public Pending? Begin(string archivePath, UndoKind kind, string storeDirectory)
    {
        try
        {
            var before = FileStamp.Read(archivePath);
            if (before == default)
            {
                return null;
            }

            Snapshot? snapshot = kind == UndoKind.Comment
                ? TailSnapshot.Take(archivePath)
                : FileSnapshot.Take(archivePath, NextStorePath(storeDirectory, archivePath));

            return snapshot is null ? null : new Pending(archivePath, kind, before, snapshot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// 書き換えが済んだら呼ぶ。書庫が変わっていれば履歴に積む。変わっていなければ (取りやめや失敗) 捨てる。
    /// </summary>
    public void Complete(Pending? pending)
    {
        if (pending is null)
        {
            return;
        }

        var after = FileStamp.Read(pending.ArchivePath);
        if (after == default || after == pending.Before)
        {
            pending.Snapshot.Discard();
            return;
        }

        if (!_steps.TryGetValue(pending.ArchivePath, out var steps))
        {
            steps = [];
            _steps[pending.ArchivePath] = steps;
        }

        steps.Add(new Step(pending.Kind, pending.Snapshot, after));

        while (steps.Count > MaxSteps)
        {
            steps[0].Snapshot.Discard();
            steps.RemoveAt(0);
        }
    }

    /// <summary>次に戻す書き換えの種類。戻せるものが無ければ <see langword="null"/>。</summary>
    public UndoKind? Peek(string archivePath)
        => _steps.TryGetValue(archivePath, out var steps) && steps.Count > 0 ? steps[^1].Kind : null;

    /// <summary>いちばん新しい書き換えを戻す。</summary>
    /// <exception cref="IOException">差し替えられなかった場合。履歴はそのまま残る。</exception>
    public UndoOutcome Undo(string archivePath, out UndoKind kind)
    {
        kind = default;
        if (!_steps.TryGetValue(archivePath, out var steps) || steps.Count == 0)
        {
            return UndoOutcome.Nothing;
        }

        var step = steps[^1];
        kind = step.Kind;

        // 書き換えたあとに、ほかのソフトが書き換えていた。戻すとその書き換えが消える
        if (FileStamp.Read(archivePath) != step.After)
        {
            Clear(archivePath);
            return UndoOutcome.ChangedOutside;
        }

        step.Snapshot.Restore(archivePath);
        steps.RemoveAt(steps.Count - 1);
        return UndoOutcome.Restored;
    }

    /// <summary>その書庫の履歴を捨てる。</summary>
    public void Clear(string archivePath)
    {
        if (!_steps.Remove(archivePath, out var steps))
        {
            return;
        }

        foreach (var step in steps)
        {
            step.Snapshot.Discard();
        }
    }

    private string NextStorePath(string directory, string archivePath)
    {
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, $"{++_issued:000}-{Path.GetFileName(archivePath)}");
    }

    /// <summary>書き換える前の状態。<see cref="Begin"/> が返し、<see cref="Complete"/> に渡す。</summary>
    internal sealed class Pending(string archivePath, UndoKind kind, FileStamp before, Snapshot snapshot)
    {
        public string ArchivePath { get; } = archivePath;

        public UndoKind Kind { get; } = kind;

        public FileStamp Before { get; } = before;

        public Snapshot Snapshot { get; } = snapshot;
    }

    private sealed record Step(UndoKind Kind, Snapshot Snapshot, FileStamp After);

    /// <summary>取っておいた状態。</summary>
    internal abstract class Snapshot
    {
        /// <summary>書庫をこの状態に戻す。</summary>
        public abstract void Restore(string archivePath);

        /// <summary>要らなくなったので消す。</summary>
        public abstract void Discard();
    }

    /// <summary>書庫を丸ごと取っておいたもの。ハードリンクかコピー。</summary>
    private sealed class FileSnapshot(string path) : Snapshot
    {
        public static FileSnapshot? Take(string archivePath, string storePath)
        {
            if (HardLink.TryCreate(storePath, archivePath))
            {
                return new FileSnapshot(storePath);
            }

            if (new FileInfo(archivePath).Length > CopyLimit)
            {
                return null;
            }

            File.Copy(archivePath, storePath, overwrite: true);
            return new FileSnapshot(storePath);
        }

        public override void Restore(string archivePath)
        {
            // 置き換えると出所の印が消えるので、付け直す (#211)。
            // ハードリンクなら取っておいた側に印も残っているが、コピーでは残らない
            var zone = MarkOfTheWeb.TryRead(archivePath);
            File.Move(path, archivePath, overwrite: true);
            MarkOfTheWeb.Restore(archivePath, zone);
        }

        public override void Discard()
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 消せなくても、一時フォルダーの置き場ごと閉じるときに消える
            }
        }
    }

    /// <summary>
    /// 書庫の末尾 (終端レコードとコメント) だけを取っておいたもの。コメントの書き換え (#258) 用。
    /// </summary>
    /// <remarks>
    /// 終端レコードは末尾から 64 KB ほどの中にある。その範囲をそのまま取っておけば、
    /// コメントの書き換えで変わるところはすべて入る。
    /// <para>
    /// その場で書き戻すと更新日時が戻した時刻になり、1 つ前の書き換えのあとの状態と合わなくなる。
    /// そのままだと、続けて戻すときに「ほかのソフトで書き換えられた」と取り違えるので、
    /// 書き換える前の更新日時も取っておき、書き戻したあとに付け直す (#261)。
    /// </para>
    /// </remarks>
    private sealed class TailSnapshot(long offset, byte[] tail, DateTime lastWriteUtc) : Snapshot
    {
        private const int TailLength = 22 + ushort.MaxValue;

        public static TailSnapshot Take(string archivePath)
        {
            var lastWriteUtc = File.GetLastWriteTimeUtc(archivePath);
            using var stream = File.OpenRead(archivePath);
            var length = (int)Math.Min(stream.Length, TailLength);
            var offset = stream.Length - length;

            var tail = new byte[length];
            stream.Position = offset;
            stream.ReadExactly(tail);
            return new TailSnapshot(offset, tail, lastWriteUtc);
        }

        public override void Restore(string archivePath)
        {
            using (var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Write, FileShare.Read))
            {
                stream.Position = offset;
                stream.Write(tail);
                stream.SetLength(offset + tail.Length);
                stream.Flush(flushToDisk: true);
            }

            // 閉じたあとに付ける。開いている間に付けても、閉じるときに書き込んだ時刻で上書きされることがある
            File.SetLastWriteTimeUtc(archivePath, lastWriteUtc);
        }

        public override void Discard()
        {
        }
    }

    /// <summary>ハードリンクを作る。作れないドライブや別のドライブでは失敗する。</summary>
    private static class HardLink
    {
        public static bool TryCreate(string link, string target)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    return CreateHardLinkW(link, target, IntPtr.Zero);
                }

                return LinkUnix(target, link) == 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return false;
            }
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

        [DllImport("libc", EntryPoint = "link", SetLastError = true)]
        private static extern int LinkUnix(string existing, string link);
    }
}
