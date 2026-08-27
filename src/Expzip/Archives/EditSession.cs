namespace Expzip.Archives;

/// <summary>
/// 書庫内の1ファイルを外部のアプリで編集している間の状態 (#16)。
/// </summary>
/// <remarks>
/// 取り出した一時ファイルが書き換わったかどうかは <see cref="FileStamp"/> で見る。
/// </remarks>
internal sealed class EditSession
{
    private FileStamp _applied;

    public EditSession(string archivePath, ArchiveEntry entry, string tempPath, string destinationFolder)
    {
        ArchivePath = archivePath;
        SourceName = entry.SourceName;
        EntryPath = entry.FullPath;
        Name = entry.Name;
        TempPath = tempPath;
        DestinationFolder = destinationFolder;
        _applied = FileStamp.Read(tempPath);
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
        var current = FileStamp.Read(TempPath);

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
        _applied = FileStamp.Read(TempPath);
        HasPendingChanges = false;
    }
}
