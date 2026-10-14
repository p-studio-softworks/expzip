using System.Windows;
using System.Windows.Automation;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// ZIP の書庫全体のコメントを見る・直すウィンドウ (#258)。
/// </summary>
/// <remarks>
/// 書き換えられない書庫 (自己解凍書庫、分割された書庫、MSIX など) では見るだけにする。
/// 保存は呼んだ側が行う。ここでは直した文字を返すだけ。
/// </remarks>
// WPF が作る相方の宣言に合わせて public にしてある
public partial class CommentDialog : Window
{
    private readonly string _archiveName;
    private readonly string _original;
    private readonly bool _editable;

    /// <param name="owner">本体のウィンドウ。</param>
    /// <param name="archiveName">タイトルに出す書庫の名前。</param>
    /// <param name="comment">いまのコメント。そのままの文字。</param>
    /// <param name="editable">保存できるか。</param>
    internal CommentDialog(Window owner, string archiveName, string comment, bool editable)
    {
        InitializeComponent();

        Owner = owner;
        _archiveName = archiveName;
        _editable = editable;

        // 制御文字は見える記号にして出す。直さずに閉じたかどうかは、この形どうしで比べる
        _original = ZipComment.ForDisplay(comment);
        CommentBox.Text = _original;
        CommentBox.IsReadOnly = !editable;

        // Enter は改行に使うので、保存は既定のボタンにしない
        SaveButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
        ReadOnlyNote.Visibility = editable ? Visibility.Collapsed : Visibility.Visible;

        ApplyLanguage();

        Loaded += (_, _) =>
        {
            CommentBox.Focus();
            CommentBox.CaretIndex = 0;
        };
    }

    /// <summary>直して保存を押したときの文字。直していなければ <see langword="null"/>。</summary>
    internal string? Edited { get; private set; }

    private void ApplyLanguage()
    {
        Title = Strings.CommentTitle(_archiveName);
        AutomationProperties.SetName(CommentBox, Strings.MenuComment);
        ReadOnlyNote.Text = Strings.CommentReadOnlyNote;
        SaveButton.Content = Strings.CommentSave;
        CancelButton.Content = _editable ? Strings.CommentCancel : Strings.CommentClose;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var text = CommentBox.Text;

        if (string.Equals(text, _original, StringComparison.Ordinal))
        {
            DialogResult = false;
            return;
        }

        // 終端レコードの長さの欄は 2 バイト。超える分は書けない
        if (!ZipComment.Fits(text))
        {
            MessageBox.Show(this, Strings.CommentTooLong, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Edited = text;
        DialogResult = true;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
