using System.ComponentModel;
using System.Windows.Media;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>リストビューの行の種別。</summary>
internal enum EntryRowKind
{
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

    /// <summary>フォルダ行の移動先。ファイル行では <see langword="null"/>。</summary>
    public ArchiveFolder? Folder { get; init; }

    /// <summary>ファイル行の実体。それ以外では <see langword="null"/>。</summary>
    public ArchiveEntry? Entry { get; init; }

    /// <summary>
    /// エクスプローラーと同じ絵 (#158)。文書・画像・実行ファイルなどで絵が違うので、
    /// 見分けやすい。以前は Segoe MDL2 Assets の字で、ファイルは種類によらず全部同じだった。
    /// </summary>
    public ImageSource? Icon => Kind == EntryRowKind.Folder
        ? ShellIcons.Folder
        : ShellIcons.ForFile(Name);

    /// <summary>
    /// パスが通常ではない項目かどうか (#36)。
    /// 一覧から隠すのではなく警告を添えて見せる。隠すと書庫に何が入っているかを
    /// 確認できなくなり、かえって危険なため。
    /// </summary>
    public bool IsPathSuspicious =>
        Entry?.IsPathSuspicious ?? Folder?.IsPathSuspicious ?? false;

    /// <summary>
    /// パスワードで保護されているかどうか (#20)。
    /// フォルダの行は、配下に保護されたファイルがあれば印を付ける。
    /// </summary>
    public bool IsEncrypted => Entry?.IsEncrypted ?? Folder?.HasEncryptedContent ?? false;

    public string? WarningTooltip => IsPathSuspicious ? Strings.SuspiciousPathTooltip : null;

    /// <summary>
    /// 保存した決まりに合っていない項目かどうか (#27)。
    /// </summary>
    /// <remarks>
    /// 決まりは書庫の外にあり、項目自身は自分が合っているかを知らない。
    /// 一覧を組み立てるときに当てた結果 (<see cref="RuleAudit"/>) を渡す。
    /// </remarks>
    public bool BreaksRules { get; init; }

    /// <summary>どの決まりに合っていないか。行に添える説明。</summary>
    public string? RuleTooltip { get; init; }

    /// <summary>
    /// 支援技術が読むこの行の名前 (#119)。
    /// </summary>
    /// <remarks>
    /// 印は色と絵でしか出していないため、名前のうしろに言葉でも並べる。
    /// 印が無い行は名前だけになる。
    /// </remarks>
    public string RowName => Strings.EntryRowName(
        Name,
        IsPathSuspicious ? Strings.MarkSuspiciousPath : string.Empty,
        BreaksRules ? Strings.MarkRuleBreak : string.Empty,
        IsEncrypted ? Strings.MarkEncrypted : string.Empty);

    /// <summary>言語が変わったことを行に伝える (#23)。</summary>
    public void NotifyLanguageChanged()
    {
        Notify(nameof(WarningTooltip));
        Notify(nameof(RowName));
    }

    // NSIS のように展開後の大きさを持たない形式がある (#68)。
    // 0 と出すと「空のファイル」に見えるので、分からないことを示す
    public string SizeText => Entry is null ? string.Empty
        : Entry.LengthKnown ? Entry.Length.ToString("N0") : "-";

    // 7z のように、まとめて圧縮していてエントリごとの内訳を持たない形式がある (#19)。
    // 0 と出すと「まったく場所を取っていない」ように見えるので、分からないことを示す
    public string CompressedText => Entry is null ? string.Empty
        : Entry.CompressedLengthKnown ? Entry.CompressedLength.ToString("N0") : "-";

    public string RatioText => Entry is null ? string.Empty
        : Entry.CompressedLengthKnown ? $"{Entry.CompressionRatio:F0}%" : "-";

    public string DateText => Entry is null || Entry.LastWriteTime == default
        ? string.Empty
        : Entry.LastWriteTime.ToString("yyyy/MM/dd HH:mm");

    // --- 以下はソート用。表示文字列で並べるとサイズが桁数順になってしまうため分けている ---

    public long SortLength => Entry?.Length ?? 0;

    public long SortCompressedLength => Entry?.CompressedLength ?? 0;

    public double SortRatio => Entry?.CompressionRatio ?? 0;

    public DateTime SortDate => Entry?.LastWriteTime ?? default;
}
