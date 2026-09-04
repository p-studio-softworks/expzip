using System.ComponentModel;

namespace Expzip.Archives;

/// <summary>
/// 書庫内のフォルダを表すツリーのノード。
/// ZIPはフォルダ構造を明示的に持たないこともあるため、エントリのパスから組み立てる。
/// </summary>
internal sealed class ArchiveFolder : INotifyPropertyChanged
{
    private bool _breaksRules;

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

    /// <summary>フォルダ名。ルートの場合は書庫のファイル名を入れる。</summary>
    public required string Name { get; init; }

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
