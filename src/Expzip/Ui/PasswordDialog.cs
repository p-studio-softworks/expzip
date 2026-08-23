using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Expzip.Ui;

/// <summary>
/// パスワード付き書庫の合言葉を尋ねる小さなダイアログ (#20)。
/// </summary>
/// <remarks>
/// 名前の変更 (#15) と違い、ここはダイアログにする。伏せ字での入力が要るうえ、
/// 一覧の行に紐づく操作ではないため。
/// </remarks>
internal sealed class PasswordDialog : Window
{
    private readonly PasswordBox _input;

    public PasswordDialog(Window owner, string archiveName, bool retry)
    {
        Owner = owner;
        Title = "パスワード";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        _input = new PasswordBox { Margin = new Thickness(0, 8, 0, 0), Padding = new Thickness(4, 3, 4, 3) };
        _input.KeyDown += OnInputKeyDown;

        var message = new TextBlock
        {
            Text = retry
                ? $"パスワードが違います。{Environment.NewLine}「{archiveName}」のパスワードを入力してください。"
                : $"「{archiveName}」はパスワードで保護されています。",
            TextWrapping = TextWrapping.Wrap,
        };

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 88,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
        };

        ok.Click += (_, _) => Close(true);

        var cancel = new Button
        {
            Content = "キャンセル",
            IsCancel = true,
            Width = 88,
            Height = 26,
        };

        cancel.Click += (_, _) => Close(false);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var layout = new StackPanel { Margin = new Thickness(16) };
        layout.Children.Add(message);
        layout.Children.Add(_input);
        layout.Children.Add(buttons);
        Content = layout;

        // 開いた時点で入力できる状態にする。ここで打ち始められないと二度手間になる
        Loaded += (_, _) => _input.Focus();
    }

    /// <summary>入力された合言葉。取り消された場合は <see langword="null"/>。</summary>
    public string? Password { get; private set; }

    private void OnInputKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        Close(true);
    }

    private void Close(bool accepted)
    {
        Password = accepted && _input.Password.Length > 0 ? _input.Password : null;
        DialogResult = accepted && Password is not null;
        Close();
    }
}
