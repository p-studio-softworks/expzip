using System.ComponentModel;
using Expzip.Localization;

namespace Expzip.Archives;

/// <summary>
/// 書庫内のフォルダを表すツリーのノード。
/// ZIPはフォルダ構造を明示的に持たないこともあるため、エントリのパスから組み立てる。
/// </summary>
internal sealed class ArchiveFolder : INotifyPropertyChanged
{
    private bool _breaksRules;

    private string? _ruleTooltip;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// このフォルダ自身か、**配下のどこか**にルールに合っていない項目があるとき true (#87)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="HasEncryptedContent"/> と同じく、中身から決めて親へ伝える。
    /// **伝えないと、深いところの違反に気付くには開いて回るしかない。**
    /// 121ファイル38フォルダの書庫で、それは現実的ではない。
    /// </para>
    /// <para>
    /// 組み立て時ではなく**検査のたびに入れ替わる**ので、変わったことを画面へ知らせる。
    /// </para>
    /// </remarks>
    public bool BreaksRules
    {
        get => _breaksRules;
        set
        {
            if (_breaksRules == value)
            {
                return;
            }

            _breaksRules = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BreaksRules)));
        }
    }

    /// <summary>
    /// 印に添える説明 (#88)。**何が合っていないのか**を、項目ごとに並べる。
    /// </summary>
    /// <remarks>
    /// 印だけでは、直すときに何をすればよいか分からない。フォルダ自身ではなく
    /// **配下**の違反で印が付いていることもあるので、どこの何かまで書く。
    /// 印と同じく検査のたびに入れ替わるため、変わったことを画面へ知らせる。
    /// </remarks>
    public string? RuleTooltip
    {
        get => _ruleTooltip;
        set
        {
            if (_ruleTooltip == value)
            {
                return;
            }

            _ruleTooltip = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RuleTooltip)));
        }
    }

    /// <summary>フォルダ名。ルートの場合は書庫のファイル名を入れる。</summary>
    public required string Name { get; init; }

    /// <summary>
    /// 支援技術が読むこの節の名前 (#119)。
    /// </summary>
    /// <remarks>
    /// 印は色と絵でしか出していないため、名前のうしろに言葉でも並べる。
    /// 印が入れ替わるたびに読み直させる必要があるので、
    /// <see cref="NotifyRowName"/> を呼ぶこと。
    /// </remarks>
    public string RowName => Strings.EntryRowName(
        Name,
        IsPathSuspicious ? Strings.MarkSuspiciousPath : string.Empty,
        BreaksRules ? Strings.MarkRuleBreak : string.Empty,
        HasEncryptedContent ? Strings.MarkEncrypted : string.Empty);

    /// <summary>
    /// 名前を読み直させる。印を付け替えたときと、言語を切り替えたときに呼ぶ。
    /// </summary>
    /// <remarks>
    /// 名前に関わるものをまとめて読み直させるため、空の名前で知らせる
    /// (<see cref="INotifyPropertyChanged"/> で「すべて」を意味する)。
    /// </remarks>
    public void NotifyRowName()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    /// <summary>書庫内のパス。ルートは空文字。区切りは <c>/</c>。</summary>
    public required string FullPath { get; init; }

    /// <summary>親フォルダ。ルートの場合は <see langword="null"/>。</summary>
    public ArchiveFolder? Parent { get; init; }

    /// <summary>直下のフォルダ。</summary>
    public List<ArchiveFolder> Folders { get; } = [];

    /// <summary>直下のファイル。</summary>
    public List<ArchiveEntry> Files { get; } = [];

    /// <summary>ツリーの初期表示で開いておくかどうか。ルートのみ true にする。</summary>
    public bool IsExpanded { get; set; }

    /// <summary>
    /// パスに <c>..</c> やドライブ指定が含まれ、通常の書庫ではあり得ない形のとき true。
    /// ツリーと一覧で警告を出すために使う (#36)。
    /// </summary>
    public bool IsPathSuspicious { get; init; }

    /// <summary>
    /// 配下にパスワードで保護されたファイルがあるとき true (#20)。
    /// フォルダ自体は暗号化されないため、中身から決める。
    /// 組み立ての最後に <see cref="ArchiveTreeBuilder"/> がまとめて印を付ける。
    /// </summary>
    public bool HasEncryptedContent { get; set; }
}
