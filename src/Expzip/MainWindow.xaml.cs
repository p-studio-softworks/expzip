using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Expzip.Archives;
using Expzip.Configuration;
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

    /// <summary>実行中の処理の中断要求。処理中でなければ null (#37)。</summary>
    private CancellationTokenSource? _cancellation;

    /// <summary>処理中にウィンドウを閉じられた。処理が終わり次第閉じる (#37)。</summary>
    private bool _closeWhenIdle;

    /// <summary>exe と同じフォルダに保存する設定 (#2)。</summary>
    private readonly AppSettings _settings;

    public MainWindow()
    {
        InitializeComponent();
        UpdateTitle(null);

        _settings = SettingsStore.Load();
        ApplySettings();

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

    private void NewButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "新しい書庫を作成",
            Filter = "ZIP書庫 (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = "新しい書庫.zip",
            // 上書きの確認はダイアログ側に任せる。既存の書庫を選ぶと中身が消えるため
            OverwritePrompt = true,
        };

        // 書庫を開いているなら、その隣に作るのが自然
        if (_contents is not null)
        {
            var directory = Path.GetDirectoryName(_contents.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                dialog.InitialDirectory = directory;
            }
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            ZipArchiveWriter.CreateEmpty(dialog.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException)
        {
            MessageBox.Show(
                this,
                $"書庫を作成できませんでした。{Environment.NewLine}{Environment.NewLine}"
                + $"{dialog.FileName}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 作ったらそのまま開く。中身は空なので、ここからファイルを追加していく
        OpenArchive(dialog.FileName);
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
        ExtractButton.IsEnabled = true;
        AddButton.IsEnabled = true;
        UpdateTitle(Path.GetFileName(path));

        var target = restorePath is null ? contents.Root : FindFolder(contents.Root, restorePath) ?? contents.Root;
        SelectInTree(target);
        Navigate(target);

        StatusMessage.Text = $"{contents.FileCount:N0} 個のファイル";

        // パスが通常ではない項目を含む書庫は、開いた時点で気付けるようにする (#36)。
        // 一覧から隠すのではなく警告を添える。隠すと書庫に何が入っているかを
        // 確認できなくなり、かえって危険なため。
        SuspiciousWarningItem.Visibility = contents.SuspiciousCount > 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        SuspiciousWarningText.Text =
            $"パスが通常ではない項目が {contents.SuspiciousCount:N0} 件あります";
        TotalSizeInfo.Text = $"合計 {contents.TotalLength:N0} バイト "
                           + $"(圧縮後 {contents.TotalCompressedLength:N0} バイト)";
    }

    // ------------------------------------------------------------------ 削除

    private void EntryList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        DeleteMenuItem.IsEnabled = _contents is not null
                                   && _cancellation is null
                                   && SelectedRowsForEdit().Count > 0;
    }

    private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        => await DeleteSelectedAsync();

    private async void EntryList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete)
        {
            return;
        }

        e.Handled = true;
        await DeleteSelectedAsync();
    }

    /// <summary>操作の対象にできる選択行。親へ戻る行は除く。</summary>
    private List<EntryRow> SelectedRowsForEdit()
        => EntryList.SelectedItems.OfType<EntryRow>()
            .Where(static r => r.Kind != EntryRowKind.Parent)
            .ToList();

    private async Task DeleteSelectedAsync()
    {
        if (_contents is null || _cancellation is not null)
        {
            return;
        }

        var rows = SelectedRowsForEdit();
        if (rows.Count == 0)
        {
            return;
        }

        var files = new HashSet<string>(StringComparer.Ordinal);
        var folders = new List<string>();
        var affected = 0;

        foreach (var row in rows)
        {
            if (row.Entry is not null)
            {
                files.Add(row.Entry.SourceName);
                affected++;
            }
            else if (row.Folder is not null)
            {
                folders.Add(row.Folder.FullPath);
                affected += CountFilesUnder(row.Folder);
            }
        }

        // 取り消せない操作なので、何がいくつ消えるかを示してから確認する
        var preview = string.Join(Environment.NewLine, rows.Take(5).Select(static r => "  " + r.Name));
        var more = rows.Count > 5 ? $"{Environment.NewLine}  ほか {rows.Count - 5:N0} 件" : string.Empty;
        var detail = folders.Count > 0
            ? $"{Environment.NewLine}{Environment.NewLine}フォルダの中身を含めて {affected:N0} 個のファイルが削除されます。"
            : string.Empty;

        var answer = MessageBox.Show(
            this,
            $"選択した {rows.Count:N0} 個の項目を書庫から削除します。"
            + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}{detail}"
            + $"{Environment.NewLine}{Environment.NewLine}この操作は取り消せません。削除しますか?",
            AppName,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        await RunDeleteAsync(files, folders);
    }

    private async Task RunDeleteAsync(IReadOnlySet<string> files, IReadOnlyList<string> folders)
    {
        if (_contents is null)
        {
            return;
        }

        var archivePath = _contents.FilePath;
        var destinationFolder = _currentFolder?.FullPath ?? string.Empty;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        // 削除は書庫全体の書き直しが1回走るだけなので、進捗を刻めない
        ProgressIndicator.IsIndeterminate = true;
        StatusMessage.Text = "削除しています…";

        try
        {
            var result = await Task.Run(() => ZipArchiveWriter.Delete(
                archivePath, files, folders, cancellation.Token));

            if (result.Cancelled)
            {
                StatusMessage.Text = "削除を中断しました";
                MessageBox.Show(
                    this,
                    $"削除を中断しました。{Environment.NewLine}{Environment.NewLine}書庫は変更していません。",
                    AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                StatusMessage.Text = $"{result.Deleted:N0} 個の項目を削除しました";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"削除できませんでした。{Environment.NewLine}{Environment.NewLine}{ex.Message}"
                + $"{Environment.NewLine}{Environment.NewLine}元の書庫は変更していません。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ProgressIndicator.IsIndeterminate = false;
            _cancellation = null;
            SetBusy(false);

            if (_closeWhenIdle)
            {
                Close();
            }
            else
            {
                // 消したフォルダを表示中だった場合に備え、無ければルートに戻る
                OpenArchive(archivePath, destinationFolder);
            }
        }
    }

    private static int CountFilesUnder(ArchiveFolder folder)
        => folder.Files.Count + folder.Folders.Sum(CountFilesUnder);

    // ------------------------------------------------------------------ 追加

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (_contents is null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "書庫に追加するファイルを選択",
            Filter = "すべてのファイル (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await AddToArchiveAsync(dialog.FileNames);
        }
    }

    private void EntryList_DragOver(object sender, DragEventArgs e)
    {
        // 書庫を開いていないと追加先が無い
        var acceptable = _contents is not null
                         && _cancellation is null
                         && e.Data.GetDataPresent(DataFormats.FileDrop);

        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void EntryList_Drop(object sender, DragEventArgs e)
    {
        if (_contents is null || _cancellation is not null)
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0)
        {
            return;
        }

        e.Handled = true;
        await AddToArchiveAsync(paths);
    }

    /// <summary>ディスク上のファイルやフォルダを、いま表示しているフォルダに追加する。</summary>
    private async Task AddToArchiveAsync(IReadOnlyList<string> sourcePaths)
    {
        if (_contents is null)
        {
            return;
        }

        var archivePath = _contents.FilePath;
        var destinationFolder = _currentFolder?.FullPath ?? string.Empty;

        // 同名のエントリがある場合だけ確認を出す。無用な確認は挟まない
        var replaceExisting = true;
        var conflicts = FindConflicts(sourcePaths, destinationFolder);
        if (conflicts.Count > 0)
        {
            var preview = string.Join(Environment.NewLine, conflicts.Take(5).Select(static c => "  " + c));
            var more = conflicts.Count > 5 ? $"{Environment.NewLine}  ほか {conflicts.Count - 5:N0} 件" : string.Empty;

            var answer = MessageBox.Show(
                this,
                $"同じ名前の項目が書庫内に {conflicts.Count:N0} 件あります。置き換えますか?"
                + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "「いいえ」を選ぶと、それらは書庫内のまま残します。",
                AppName, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            replaceExisting = answer == MessageBoxResult.Yes;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        var progress = new Progress<AddProgress>(p =>
        {
            ProgressIndicator.Value = p.Percent;
            StatusMessage.Text = $"追加中: {p.CurrentName}";
        });

        try
        {
            var result = await Task.Run(() => ZipArchiveWriter.Add(
                archivePath, sourcePaths, destinationFolder, replaceExisting, progress, cancellation.Token));

            ShowAddResult(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"書庫に追加できませんでした。{Environment.NewLine}{Environment.NewLine}{ex.Message}"
                + $"{Environment.NewLine}{Environment.NewLine}元の書庫は変更していません。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancellation = null;
            SetBusy(false);

            if (_closeWhenIdle)
            {
                Close();
            }
            else
            {
                // 書庫の中身が変わったので読み直す。表示位置は保つ
                OpenArchive(archivePath, destinationFolder);
            }
        }
    }

    private void ShowAddResult(AddResult result)
    {
        if (result.Cancelled)
        {
            StatusMessage.Text = "追加を中断しました";
            MessageBox.Show(
                this,
                $"追加を中断しました。{Environment.NewLine}{Environment.NewLine}"
                + "書庫は変更していません。作業用の複製に対して処理していたため、"
                + $"{Environment.NewLine}中断しても元の書庫はそのまま残ります。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var message = new System.Text.StringBuilder();
        message.AppendLine($"追加したファイル: {result.Added:N0} 個");

        if (result.Replaced > 0)
        {
            message.AppendLine($"置き換えたファイル: {result.Replaced:N0} 個");
        }

        if (result.Skipped > 0)
        {
            message.AppendLine($"置き換えず残したファイル: {result.Skipped:N0} 個");
        }

        var icon = MessageBoxImage.Information;
        if (result.Failed.Count > 0)
        {
            icon = MessageBoxImage.Warning;
            message.AppendLine();
            message.AppendLine($"追加できなかったファイル: {result.Failed.Count:N0} 個");
            foreach (var (name, reason) in result.Failed.Take(5))
            {
                message.AppendLine($"  {name} … {reason}");
            }
        }

        StatusMessage.Text = $"{result.Added + result.Replaced:N0} 個のファイルを追加しました";
        MessageBox.Show(this, message.ToString().TrimEnd(), AppName, MessageBoxButton.OK, icon);
    }

    /// <summary>追加しようとしている名前のうち、書庫内に既にあるものを返す。</summary>
    private List<string> FindConflicts(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        if (_contents is null)
        {
            return [];
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPaths(_contents.Root, existing);

        return ZipArchiveWriter.PlanEntryNames(sourcePaths, destinationFolder)
            .Where(name => existing.Contains(name.TrimEnd('/')))
            .ToList();
    }

    private static void CollectPaths(ArchiveFolder folder, HashSet<string> into)
    {
        foreach (var file in folder.Files)
        {
            into.Add(file.FullPath);
        }

        foreach (var child in folder.Folders)
        {
            into.Add(child.FullPath);
            CollectPaths(child, into);
        }
    }

    // ------------------------------------------------------------------ 展開

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_contents is null)
        {
            return;
        }

        // 選択が無ければ書庫全体を展開する
        var selection = CollectSelectedSourceNames();

        var picker = new OpenFolderDialog
        {
            Title = selection is null ? "書庫全体の展開先を選択" : "選択した項目の展開先を選択",
        };

        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var destination = picker.FolderName;

        // 展開先が空なら衝突しようがないので、無用な確認を出さない
        var overwrite = true;
        if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any())
        {
            var answer = MessageBox.Show(
                this,
                $"展開先に既にファイルがあります。{Environment.NewLine}"
                + $"同名のファイルを上書きしますか?{Environment.NewLine}{Environment.NewLine}"
                + "「いいえ」を選ぶと、同名のファイルは展開せずに残します。",
                AppName,
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            overwrite = answer == MessageBoxResult.Yes;
        }

        await RunExtractionAsync(_contents.FilePath, selection, destination, overwrite);
    }

    private async Task RunExtractionAsync(
        string archivePath, IReadOnlySet<string>? selection, string destination, bool overwrite)
    {
        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        var progress = new Progress<ExtractProgress>(p =>
        {
            ProgressIndicator.Value = p.Percent;
            StatusMessage.Text = $"展開中: {p.CurrentName}";
        });

        try
        {
            var result = await Task.Run(() => ArchiveExtractor.Extract(
                archivePath, selection, destination, overwrite, progress, cancellation.Token));

            ShowExtractResult(result, destination);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"展開に失敗しました。{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancellation = null;
            SetBusy(false);

            // 処理中に閉じられていた場合は、後始末が済んだこの時点で閉じる
            if (_closeWhenIdle)
            {
                Close();
            }
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is null)
        {
            return;
        }

        // 二度押しを防ぎ、要求が伝わったことを見せる。
        // 実際に止まるのは処理側が次に中断を確認した時点。
        CancelButton.IsEnabled = false;
        StatusMessage.Text = "中断しています…";
        _cancellation.Cancel();
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_cancellation is null)
        {
            // 保存できなくてもアプリを止めない。書き込めない場所に置かれている
            // 場合は設定が残らないだけで、動作そのものには影響しない (#2)
            CaptureSettings();
            SettingsStore.TrySave(_settings);
            return;
        }

        // 処理中に閉じると書きかけのファイルが残りうる。
        // いったん閉じるのを止め、中断を要求して後始末を待つ (#37)。
        e.Cancel = true;
        _closeWhenIdle = true;

        if (!_cancellation.IsCancellationRequested)
        {
            CancelButton.IsEnabled = false;
            StatusMessage.Text = "中断しています…";
            _cancellation.Cancel();
        }
    }

    private void ShowExtractResult(ExtractResult result, string destination)
    {
        StatusMessage.Text = result.Cancelled
            ? $"展開を中断しました({result.Extracted:N0} 個展開済み)"
            : $"{result.Extracted:N0} 個のファイルを展開しました";

        var message = new System.Text.StringBuilder();

        if (result.Cancelled)
        {
            // 中断は失敗ではないので、警告ではなく事実だけを伝える
            message.AppendLine("展開を中断しました。");
            message.AppendLine("中断までに展開したファイルはそのまま残してあります。");
            message.AppendLine("書きかけだったファイルは削除しました。");
            message.AppendLine();
        }

        message.AppendLine($"展開先: {destination}");
        message.AppendLine();
        message.AppendLine($"展開したファイル: {result.Extracted:N0} 個");

        if (result.Skipped > 0)
        {
            message.AppendLine($"上書きせず残したファイル: {result.Skipped:N0} 個");
        }

        var icon = MessageBoxImage.Information;

        if (result.Rejected.Count > 0)
        {
            // 展開先の外へ書き出そうとするエントリ。書庫が細工されている可能性がある
            icon = MessageBoxImage.Warning;
            message.AppendLine();
            message.AppendLine($"安全でないパスのため展開しなかったファイル: {result.Rejected.Count:N0} 個");
            message.AppendLine("展開先の外に書き出そうとするエントリが含まれていました。");
            foreach (var name in result.Rejected.Take(5))
            {
                message.AppendLine($"  {name}");
            }
        }

        if (result.Failed.Count > 0)
        {
            icon = MessageBoxImage.Warning;
            message.AppendLine();
            message.AppendLine($"書き出せなかったファイル: {result.Failed.Count:N0} 個");
            foreach (var (name, reason) in result.Failed.Take(5))
            {
                message.AppendLine($"  {name} … {reason}");
            }
        }

        MessageBox.Show(this, message.ToString().TrimEnd(), AppName, MessageBoxButton.OK, icon);
    }

    /// <summary>
    /// 選択されている項目のエントリ名を集める。
    /// フォルダが選ばれている場合はその配下をすべて含める。
    /// </summary>
    /// <returns>選択が無ければ <see langword="null"/> (書庫全体が対象)。</returns>
    private IReadOnlySet<string>? CollectSelectedSourceNames()
    {
        var rows = EntryList.SelectedItems.OfType<EntryRow>()
            .Where(static r => r.Kind != EntryRowKind.Parent)
            .ToList();

        if (rows.Count == 0)
        {
            return null;
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.Entry is not null)
            {
                names.Add(row.Entry.SourceName);
            }
            else if (row.Folder is not null)
            {
                AddFilesRecursively(row.Folder, names);
            }
        }

        return names;
    }

    private static void AddFilesRecursively(ArchiveFolder folder, HashSet<string> names)
    {
        foreach (var file in folder.Files)
        {
            names.Add(file.SourceName);
        }

        foreach (var child in folder.Folders)
        {
            AddFilesRecursively(child, names);
        }
    }

    /// <summary>時間のかかる処理の間、操作を止めて進捗を表示する。</summary>
    private void SetBusy(bool busy)
    {
        OpenButton.IsEnabled = !busy;
        NewButton.IsEnabled = !busy;
        ExtractButton.IsEnabled = !busy && _contents is not null;
        AddButton.IsEnabled = !busy && _contents is not null;
        RefreshButton.IsEnabled = !busy && _contents is not null;
        EntryList.IsEnabled = !busy;
        FolderTree.IsEnabled = !busy;

        ProgressIndicator.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressIndicator.Value = 0;

        CancelButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CancelButton.IsEnabled = busy;

        Mouse.OverrideCursor = busy ? Cursors.AppStarting : null;
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

    // ------------------------------------------------------------------ 設定の反映と保存

    /// <summary>保存されている設定をウィンドウに反映する。</summary>
    private void ApplySettings()
    {
        if (_settings.WindowLeft is { } left
            && _settings.WindowTop is { } top
            && _settings.WindowWidth is { } width && width > 0
            && _settings.WindowHeight is { } height && height > 0
            && IsReachableOnScreen(left, top, width, height))
        {
            // 画面構成が変わって前回の位置が画面外になっている場合は既定に任せる
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
            Width = width;
            Height = height;
        }

        if (_settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        if (_settings.TreePaneWidth is { } paneWidth && paneWidth > 0)
        {
            TreeColumn.Width = new GridLength(paneWidth);
        }

        // 列を増減した場合に古い設定が残っていることがあるので、数が合うときだけ使う
        if (_settings.ColumnWidths is { } columnWidths
            && EntryList.View is GridView gridView
            && columnWidths.Length == gridView.Columns.Count)
        {
            for (var i = 0; i < columnWidths.Length; i++)
            {
                if (columnWidths[i] > 0)
                {
                    gridView.Columns[i].Width = columnWidths[i];
                }
            }
        }
    }

    /// <summary>
    /// その位置にウィンドウを出しても操作できるか。
    /// 前回終了時から画面構成が変わり、保存された位置が画面外になっていることがある。
    /// タイトルバーをつかめる程度に画面と重なっていることを条件にする。
    /// </summary>
    private static bool IsReachableOnScreen(double left, double top, double width, double height)
    {
        const double margin = 80;

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        return left + width > screenLeft + margin
               && left < screenRight - margin
               && top + margin < screenBottom
               && top + height > screenTop;
    }

    /// <summary>いまのウィンドウの状態を設定に取り込む。</summary>
    private void CaptureSettings()
    {
        // 最大化中の Left/Top/Width/Height は最大化後の値なので、
        // 次回に元の大きさで開けるよう復元用の値を使う
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width > 0 && bounds.Height > 0)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.TreePaneWidth = TreeColumn.ActualWidth > 0 ? TreeColumn.ActualWidth : null;

        if (EntryList.View is GridView gridView)
        {
            _settings.ColumnWidths = gridView.Columns.Select(static c => c.ActualWidth).ToArray();
        }
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
