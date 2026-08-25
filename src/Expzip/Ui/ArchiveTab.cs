using System.ComponentModel;
using System.IO;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 1つの書庫を開いているタブ (#22)。
/// </summary>
/// <remarks>
/// ツリーと一覧の部品はタブごとに作らず、1組を使い回して中身を差し替える。
/// タブごとに持つのは「どの書庫の、どこを、どの順で見ているか」だけ。
/// 部品を複製すると、書庫の数だけ仮想化されていない要素が積み上がる。
/// </remarks>
internal sealed class ArchiveTab(ArchiveContents contents) : INotifyPropertyChanged
{
    private ArchiveContents _contents = contents;

    /// <summary>このタブが開いている書庫。</summary>
    public ArchiveContents Contents
    {
        get => _contents;
        set
        {
            _contents = value;
            Notify(nameof(Contents));
            Notify(nameof(Title));
            Notify(nameof(FilePath));
        }
    }

    /// <summary>一覧に出している書庫内フォルダ。</summary>
    public ArchiveFolder CurrentFolder { get; set; } = contents.Root;

    /// <summary>並び順。タブごとに覚える (仕様書 5.2)。</summary>
    public EntryColumn SortColumn { get; set; } = EntryColumn.Name;

    /// <summary>並び順が降順かどうか。</summary>
    public bool SortDescending { get; set; }

    /// <summary>
    /// 一覧で選んでいた項目の名前。タブを離れるときに控え、戻ったら選び直す (仕様書 5.2)。
    /// </summary>
    public IReadOnlyList<string> SelectedNames { get; set; } = [];

    /// <summary>タブに出す名前。</summary>
    public string Title => Path.GetFileName(_contents.FilePath);

    /// <summary>書庫ファイルのパス。同じ書庫を二重に開かないための照合に使う。</summary>
    public string FilePath => _contents.FilePath;

    /// <summary>閉じるボタンの説明。見出しの中にあるため、束縛で言語を切り替える (#23)。</summary>
    public string CloseTooltip => Strings.CloseTabTooltip;

    /// <summary>言語が変わったことを見出しに伝える (#23)。</summary>
    public void NotifyLanguageChanged() => Notify(nameof(CloseTooltip));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
