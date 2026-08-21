using System.Windows;
using System.Windows.Controls;

namespace Expzip.Ui;

/// <summary>
/// 名前の変更を受け付ける小さなダイアログ (#15)。
/// </summary>
/// <remarks>
/// 一覧の上で直接書き換える方式(インプレース編集)はエクスプローラーに近いが、
/// 仮想化した <see cref="ListView"/> で行の中に編集欄を出すと、スクロールで
/// 行が使い回された際の取り扱いが面倒になる。まずは確実に動くダイアログにする。
/// XAMLを持たないのは、この程度の内容なら組み立てを1か所で読めるほうが早いため。
/// </remarks>
internal sealed class RenameDialog : Window
{
    private readonly TextBox _input;

    public RenameDialog(Window owner, string currentName, bool isFolder)
    {
        Owner = owner;
        Title = isFolder ? "フォルダ名の変更" : "ファイル名の変更";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 420;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Background = SystemColors.ControlBrush;

        _input = new TextBox
        {
            Text = currentName,
            Padding = new Thickness(4, 3, 4, 3),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var ok = new Button
        {
            Content = "OK",
            IsDefault = true,
            Width = 88,
            Height = 26,
            Margin = new Thickness(0, 0, 8, 0),
        };
        ok.Click += (_, _) => Commit();

        var cancel = new Button
        {
            Content = "キャンセル",
            IsCancel = true,
            Width = 88,
            Height = 26,
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var layout = new StackPanel { Margin = new Thickness(14) };
        layout.Children.Add(new TextBlock
        {
            Text = "新しい名前",
            Margin = new Thickness(0, 0, 0, 6),
        });
        layout.Children.Add(_input);
        layout.Children.Add(buttons);

        Content = layout;

        // エクスプローラーと同じく、拡張子を除いた部分だけを選んだ状態で開く。
        // 拡張子はそのまま使うことがほとんどで、毎回打ち直すのは煩わしい。
        Loaded += (_, _) =>
        {
            _input.Focus();

            var stem = isFolder ? -1 : currentName.LastIndexOf('.');
            if (stem > 0)
            {
                _input.Select(0, stem);
            }
            else
            {
                _input.SelectAll();
            }
        };
    }

    /// <summary>入力された名前。前後の空白は落とす。</summary>
    public string NewName => _input.Text.Trim();

    private void Commit()
    {
        // 空のままOKを押されても閉じない。閉じてから叱るより、その場で気付ける
        if (NewName.Length == 0)
        {
            System.Media.SystemSounds.Beep.Play();
            _input.Focus();
            return;
        }

        DialogResult = true;
    }
}
