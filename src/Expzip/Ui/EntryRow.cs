using System.ComponentModel;
using Expzip.Archives;

namespace Expzip.Ui;

/// <summary>リストビューの行の種別。</summary>
internal enum EntryRowKind
{
    /// <summary>親フォルダへ戻るための行。</summary>
    Parent,

    /// <summary>フォルダ。</summary>
    Folder,

    /// <summary>ファイル。</summary>
    File,
}

/// <summary>
/// リストビューの1行。親へ戻る行、フォルダ、ファイルを同じ型で扱う。
/// 表示用の文字列とソート用の生値の両方を持たせ、桁揃えとソートを両立させる。
/// </summary>
internal sealed class EntryRow : INotifyPropertyChanged
{
    /// <summary>フォルダに上矢印を重ねた「一つ上へ」のアイコン。</summary>
    private const string GlyphParent = "\uE197";

    /// <summary>
    /// 塗りつぶしのフォルダ。輪郭線だけのフォルダ (E8B7) は
    /// 書類アイコンと形が似ており、一覧の中で見分けがつかない。
    /// </summary>
    private const string GlyphFolder = "\uE8D5";

    /// <summary>書類。</summary>
    private const string GlyphFile = "\uE7C3";

    /// <summary>塗りつぶしの警告三角。小さく表示しても輪郭線より目に留まる。</summary>
    private const string GlyphWarning = "\uE814";

    private bool _isEditing;
    private string _editName = string.Empty;

    public required string Name { get; init; }

    /// <summary>
    /// 一覧の上で名前を書き換えている最中かどうか (#15)。
    /// エクスプローラーと同じく、ダイアログではなくその場で書き換える。
    /// </summary>
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value)
            {
                return;
            }

            _isEditing = value;
            Notify(nameof(IsEditing));
        }
    }

    /// <summary>書き換え中の名前。確定するまで <see cref="Name"/> には反映しない。</summary>
    public string EditName
    {
        get => _editName;
        set
        {
            _editName = value;
            Notify(nameof(EditName));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

    public required EntryRowKind Kind { get; init; }

    /// <summary>フォルダ行および親行の移動先。ファイル行では <see langword="null"/>。</summary>
    public ArchiveFolder? Folder { get; init; }

    /// <summary>ファイル行の実体。それ以外では <see langword="null"/>。</summary>
    public ArchiveEntry? Entry { get; init; }

    /// <summary>Segoe MDL2 Assets のアイコン。</summary>
    public string Glyph => Kind switch
    {
        EntryRowKind.Parent => GlyphParent,
        EntryRowKind.Folder => GlyphFolder,
        _ => GlyphFile,
    };

    /// <summary>
    /// パスが通常ではない項目かどうか (#36)。
    /// 一覧から隠すのではなく警告を添えて見せる。隠すと書庫に何が入っているかを
    /// 確認できなくなり、かえって危険なため。
    /// </summary>
    public bool IsPathSuspicious =>
        Entry?.IsPathSuspicious ?? Folder?.IsPathSuspicious ?? false;

    public string WarningGlyph => GlyphWarning;

    public string? WarningTooltip => IsPathSuspicious
        ? "このパスは通常の書庫では使われない形式です。"
          + Environment.NewLine
          + "展開しても、選んだフォルダの外には書き出されません。"
        : null;

    public string SizeText => Entry is null ? string.Empty : Entry.Length.ToString("N0");

    public string CompressedText => Entry is null ? string.Empty : Entry.CompressedLength.ToString("N0");

    public string RatioText => Entry is null ? string.Empty : $"{Entry.CompressionRatio:F0}%";

    public string DateText => Entry is null || Entry.LastWriteTime == default
        ? string.Empty
        : Entry.LastWriteTime.ToString("yyyy/MM/dd HH:mm");

    // --- 以下はソート用。表示文字列で並べるとサイズが桁数順になってしまうため分けている ---

    public long SortLength => Entry?.Length ?? 0;

    public long SortCompressedLength => Entry?.CompressedLength ?? 0;

    public double SortRatio => Entry?.CompressionRatio ?? 0;

    public DateTime SortDate => Entry?.LastWriteTime ?? default;
}
