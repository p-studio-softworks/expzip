using System.IO;

namespace Expzip.Archives;

/// <summary>
/// 書庫内の1ファイルを外部のアプリで編集している間の状態 (#16)。
/// </summary>
/// <remarks>
/// 取り出した一時ファイルが書き換わったかどうかは、更新日時と大きさの組で見る。
/// <see cref="FileSystemWatcher"/> を使わないのは、保存の仕方がアプリによって
/// 大きく違うため。その場で書き換えるアプリもあれば、別名で書いてから置き換える
/// アプリもあり、後者では対象のファイルが一度消えて作り直される。パスを定期的に
/// 見に行くほうが、どちらの作法でも取りこぼさない。
/// </remarks>
internal sealed class EditSession
{
    /// <summary>取り出した一時ファイルの状態。ファイルが無い場合は既定値。</summary>
    private readonly record struct Stamp(DateTime LastWriteUtc, long Length);

    private Stamp _applied;

    public EditSession(string archivePath, ArchiveEntry entry, string tempPath, string destinationFolder)
    {
        ArchivePath = archivePath;
        SourceName = entry.SourceName;
        EntryPath = entry.FullPath;
        Name = entry.Name;
        TempPath = tempPath;
        DestinationFolder = destinationFolder;
        _applied = Read(tempPath);
    }

    /// <summary>編集の対象が入っている書庫。</summary>
    public string ArchivePath { get; }

    /// <summary>書庫内での元のエントリ名。</summary>
    public string SourceName { get; }

    /// <summary>書庫内のパス。表示に使う。</summary>
    public string EntryPath { get; }

    /// <summary>ファイル名(パスを含まない)。</summary>
    public string Name { get; }

    /// <summary>取り出した一時ファイルのパス。</summary>
    public string TempPath { get; }

    /// <summary>書庫内で書き戻す先のフォルダ。</summary>
    public string DestinationFolder { get; }

    /// <summary>
    /// 書き換えられたが書庫に反映していない状態かどうか。
    /// 反映を断られた場合も、アプリ終了時に聞き直すためにここで覚えておく。
    /// </summary>
    public bool HasPendingChanges { get; private set; }

    /// <summary>
    /// 一時ファイルが前回見たときから変わっていれば <see langword="true"/>。
    /// </summary>
    /// <remarks>
    /// 変化を見つけた時点で覚え直すため、同じ変更で二度尋ねることはない。
    /// 保存の途中でファイルを掴めないことがあるが、その場合は「まだ変わっていない」
    /// として次回に持ち越す。取り逃しても次の巡回で拾える。
    /// </remarks>
    public bool DetectChange()
    {
        var current = Read(TempPath);

        if (current == default || current == _applied)
        {
            return false;
        }

        _applied = current;
        HasPendingChanges = true;
        return true;
    }

    /// <summary>書庫への反映が済んだことを記録する。</summary>
    public void MarkApplied()
    {
        _applied = Read(TempPath);
        HasPendingChanges = false;
    }

    private static Stamp Read(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? new Stamp(info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }
}
