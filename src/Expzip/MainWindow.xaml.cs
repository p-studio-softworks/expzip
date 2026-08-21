using System.Windows;
using System.Windows.Controls;

namespace Expzip;

/// <summary>
/// メインウィンドウ。
/// エクスプローラーライクな2ペイン構成 (docs/SPEC.md 5.1節) の土台であり、
/// 書庫の読み込みと操作はフェーズ1の個別チケットで実装する。
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>ウィンドウタイトルに使うアプリケーション名。</summary>
    private const string AppName = "Expzip";

    public MainWindow()
    {
        InitializeComponent();
        UpdateTitle(null);
    }

    /// <summary>
    /// 開いている書庫に応じてタイトルバーを切り替える。
    /// フェーズ3でタブ表示に対応した際は、アクティブなタブの書庫名を渡す
    /// (docs/SPEC.md 5.2節)。
    /// </summary>
    /// <param name="archiveFileName">書庫のファイル名。開いていない場合は null。</param>
    private void UpdateTitle(string? archiveFileName)
    {
        Title = string.IsNullOrEmpty(archiveFileName)
            ? AppName
            : $"{archiveFileName} - {AppName}";
    }

    /// <summary>
    /// ツールバー右端のオーバーフロー用矢印を消す。
    /// WPF の ToolBar は入りきらない項目を畳むための領域を常に確保するため、
    /// 何も畳まれていなくても矢印が residual に表示されてしまう。
    /// エクスプローラー風の見た目にするうえで邪魔なので、テンプレート内の
    /// 該当要素を直接隠している。
    /// </summary>
    private void ToolBar_Loaded(object sender, RoutedEventArgs e)
    {
        var toolBar = (ToolBar)sender;

        if (toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement overflowGrid)
        {
            overflowGrid.Visibility = Visibility.Collapsed;
        }

        // オーバーフロー領域のぶん右に空いていた余白を詰める
        if (toolBar.Template.FindName("MainPanelBorder", toolBar) is FrameworkElement mainPanelBorder)
        {
            mainPanelBorder.Margin = new Thickness(0);
        }
    }
}
