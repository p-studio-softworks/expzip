using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Expzip.Archives;
using Expzip.Ui;
using Microsoft.Win32;

namespace Expzip;

/// <summary>
/// メインウィンドウ。
/// エクスプローラーライクな2ペイン構成 (docs/SPEC.md 5.1節)。
/// </summary>
public partial class MainWindow : Window
{
    private const string AppName = "Expzip";

    /// <summary>開いている書庫。未読み込みの場合は <see langword="null"/>。</summary>
    private ArchiveContents? _contents;

    /// <summary>リストビューに表示している現在のフォルダ。</summary>
    private ArchiveFolder? _currentFolder;

    /// <summary>現在の並び順。ヘッダークリックのたびに更新する。</summary>
    private string _sortColumn = "名前";
    private bool _sortDescending;

    /// <summary>
    /// ツリーの選択変更に反応してリストを差し替える処理を、
    /// こちらから選択を動かしたときに走らせないための抑止フラグ。
    /// </summary>
    private bool _suppressTreeSelection;

    public MainWindow()
    {
        InitializeComponent();
        UpdateTitle(null);

        // 引数で書庫を渡された場合はそれを開く。
        // ウィンドウが出来上がってからでないとエラー表示の親にできないため Loaded で行う。
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            var path = args[1];
            Loaded += (_, _) => OpenArchive(path);
        }
    }

    // ------------------------------------------------------------------ 操作

    private void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "書庫を開く",
            Filter = "ZIP書庫 (*.zip)|*.zip|すべてのファイル (*.*)|*.*",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            OpenArchive(dialog.FileName);
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (_contents is not null)
        {
            // 開き直したあとも同じ場所を表示できるよう、現在位置を覚えておく
            OpenArchive(_contents.FilePath, _currentFolder?.FullPath);
        }
    }

    /// <summary>書庫を読み込んで画面に反映する。</summary>
    /// <param name="path">書庫ファイルのパス。</param>
    /// <param name="restorePath">読み込み後に表示したい書庫内フォルダのパス。</param>
    private void OpenArchive(string path, string? restorePath = null)
    {
        ArchiveContents contents;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            contents = ZipArchiveReader.Open(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                $"書庫を開けませんでした。{Environment.NewLine}{Environment.NewLine}{path}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                AppName,
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        _contents = contents;
        FolderTree.ItemsSource = new[] { contents.Root };
        RefreshButton.IsEnabled = true;
        UpdateTitle(Path.GetFileName(path));

        var target = restorePath is null ? contents.Root : FindFolder(contents.Root, restorePath) ?? contents.Root;
        SelectInTree(target);
        Navigate(target);

        StatusMessage.Text = $"{contents.FileCount:N0} 個のファイル";
        TotalSizeInfo.Text = $"合計 {contents.TotalLength:N0} バイト "
                           + $"(圧縮後 {contents.TotalCompressedLength:N0} バイト)";
    }

    /// <summary>指定フォルダの内容をリストビューに表示する。</summary>
    private void Navigate(ArchiveFolder folder)
    {
        _currentFolder = folder;

        var rows = new List<EntryRow>(folder.Folders.Count + folder.Files.Count + 1);

        // ルート以外では先頭に親へ戻る行を置く
        if (folder.Parent is not null)
        {
            rows.Add(new EntryRow { Name = "..", Kind = EntryRowKind.Parent, Folder = folder.Parent });
        }

        foreach (var child in folder.Folders)
        {
            rows.Add(new EntryRow { Name = child.Name, Kind = EntryRowKind.Folder, Folder = child });
        }

        foreach (var file in folder.Files)
        {
            rows.Add(new EntryRow { Name = file.Name, Kind = EntryRowKind.File, Entry = file });
        }

        EntryList.ItemsSource = ApplySort(rows);
        EmptyStateMessage.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateMessage.Text = _contents is null ? "書庫が開かれていません" : "このフォルダは空です";

        AddressBar.Text = _contents is null
            ? string.Empty
            : Path.GetFileName(_contents.FilePath)
              + (folder.FullPath.Length == 0 ? string.Empty : "/" + folder.FullPath);

        UpdateSelectionInfo();
    }

    // ------------------------------------------------------------------ 並び替え

    /// <summary>
    /// 現在の並び順を適用する。
    /// エクスプローラーと同じく、親行を先頭に、続いてフォルダ、最後にファイルを置く。
    /// 列の値による並び替えはその各グループの中で行う。
    /// </summary>
    private List<EntryRow> ApplySort(List<EntryRow> rows)
    {
        var sorted = rows
            .OrderBy(static r => r.Kind switch
            {
                EntryRowKind.Parent => 0,
                EntryRowKind.Folder => 1,
                _ => 2,
            })
            .ThenBy(r => r, Comparer<EntryRow>.Create(CompareByCurrentColumn))
            .ToList();

        return sorted;
    }

    private int CompareByCurrentColumn(EntryRow a, EntryRow b)
    {
        var result = _sortColumn switch
        {
            "サイズ" => a.SortLength.CompareTo(b.SortLength),
            "圧縮後" => a.SortCompressedLength.CompareTo(b.SortCompressedLength),
            "圧縮率" => a.SortRatio.CompareTo(b.SortRatio),
            "更新日時" => a.SortDate.CompareTo(b.SortDate),
            _ => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase),
        };

        // 値が同じときは名前で決めて、並びが毎回変わらないようにする
        if (result == 0)
        {
            result = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            return result;
        }

        return _sortDescending ? -result : result;
    }

    private void EntryList_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header
            || header.Column?.Header is not string column)
        {
            return;
        }

        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        if (EntryList.ItemsSource is List<EntryRow> current)
        {
            EntryList.ItemsSource = ApplySort(current);
        }
    }

    // ------------------------------------------------------------------ 選択と移動

    private void FolderTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (_suppressTreeSelection || e.NewValue is not ArchiveFolder folder)
        {
            return;
        }

        Navigate(folder);
    }

    private void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (EntryList.SelectedItem is not EntryRow row || row.Folder is null)
        {
            // ファイルのダブルクリックで既定のアプリを開く動作は #12 で実装する
            return;
        }

        SelectInTree(row.Folder);
        Navigate(row.Folder);
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectionInfo();

    private void UpdateSelectionInfo()
    {
        var selected = EntryList.SelectedItems.OfType<EntryRow>().ToList();
        var totalBytes = selected.Sum(static r => r.SortLength);

        SelectionInfo.Text = selected.Count == 0
            ? "選択 0 個"
            : $"選択 {selected.Count:N0} 個 ({totalBytes:N0} バイト)";
    }

    /// <summary>ツリー上の該当ノードを選択状態にする。祖先は順に展開する。</summary>
    private void SelectInTree(ArchiveFolder folder)
    {
        // 祖先を展開しないと TreeViewItem が生成されず、選択状態にできない
        for (var ancestor = folder.Parent; ancestor is not null; ancestor = ancestor.Parent)
        {
            ancestor.IsExpanded = true;
        }

        _suppressTreeSelection = true;
        try
        {
            FolderTree.UpdateLayout();
            var container = FindTreeViewItem(FolderTree, folder);
            if (container is not null)
            {
                container.IsSelected = true;
                container.BringIntoView();
            }
        }
        finally
        {
            _suppressTreeSelection = false;
        }
    }

    /// <summary>データに対応する <see cref="TreeViewItem"/> を辿って探す。</summary>
    private static TreeViewItem? FindTreeViewItem(ItemsControl parent, ArchiveFolder target)
    {
        for (var i = 0; i < parent.Items.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromIndex(i) is not TreeViewItem item)
            {
                continue;
            }

            if (ReferenceEquals(item.DataContext, target))
            {
                return item;
            }

            item.UpdateLayout();
            var found = FindTreeViewItem(item, target);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    /// <summary>書庫内パスからフォルダを探す。「更新」で同じ場所に戻るために使う。</summary>
    private static ArchiveFolder? FindFolder(ArchiveFolder folder, string fullPath)
    {
        if (folder.FullPath == fullPath)
        {
            return folder;
        }

        foreach (var child in folder.Folders)
        {
            var found = FindFolder(child, fullPath);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    // ------------------------------------------------------------------ 見た目の調整

    /// <summary>
    /// 開いている書庫に応じてタイトルバーを切り替える。
    /// フェーズ3でタブ表示に対応した際は、アクティブなタブの書庫名を渡す
    /// (docs/SPEC.md 5.2節)。
    /// </summary>
    private void UpdateTitle(string? archiveFileName)
    {
        Title = string.IsNullOrEmpty(archiveFileName)
            ? AppName
            : $"{archiveFileName} - {AppName}";
    }

    /// <summary>
    /// ツールバー右端のオーバーフロー用矢印を消す。
    /// WPF の ToolBar は入りきらない項目を畳むための領域を常に確保するため、
    /// 何も畳まれていなくても矢印が表示されてしまう。
    /// </summary>
    private void ToolBar_Loaded(object sender, RoutedEventArgs e)
    {
        var toolBar = (ToolBar)sender;

        if (toolBar.Template.FindName("OverflowGrid", toolBar) is FrameworkElement overflowGrid)
        {
            overflowGrid.Visibility = Visibility.Collapsed;
        }

        if (toolBar.Template.FindName("MainPanelBorder", toolBar) is FrameworkElement mainPanelBorder)
        {
            mainPanelBorder.Margin = new Thickness(0);
        }
    }
}
