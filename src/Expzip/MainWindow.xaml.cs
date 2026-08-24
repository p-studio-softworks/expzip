using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Threading;
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

    /// <summary>開いているタブ (#22)。上限は設けない。</summary>
    private readonly ObservableCollection<ArchiveTab> _tabs = [];

    /// <summary>いま選ばれているタブ。1つも開いていなければ <see langword="null"/>。</summary>
    private ArchiveTab? Tab => ArchiveTabs.SelectedItem as ArchiveTab;

    /// <summary>いま見ている書庫。開いていなければ <see langword="null"/>。</summary>
    private ArchiveContents? Contents => Tab?.Contents;

    /// <summary>リストビューに表示している現在のフォルダ。</summary>
    private ArchiveFolder? CurrentFolder
    {
        get => Tab?.CurrentFolder;
        set
        {
            if (Tab is { } tab && value is not null)
            {
                tab.CurrentFolder = value;
            }
        }
    }

    /// <summary>現在の並び順。タブごとに覚える (仕様書 5.2)。</summary>
    private string SortColumn
    {
        get => Tab?.SortColumn ?? "名前";
        set
        {
            if (Tab is { } tab)
            {
                tab.SortColumn = value;
            }
        }
    }

    private bool SortDescending
    {
        get => Tab?.SortDescending ?? false;
        set
        {
            if (Tab is { } tab)
            {
                tab.SortDescending = value;
            }
        }
    }

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

    /// <summary>
    /// 既定のアプリで開くために取り出したファイルの置き場 (#12)。
    /// 実際に取り出すまで作らない。一時フォルダに書けない環境でも、
    /// 書庫を見るだけなら支障なく使えるようにするため。
    /// </summary>
    private TempWorkspace? _temp;

    /// <summary>一時フォルダを用意できなかった。同じ知らせを繰り返さないための記録。</summary>
    private bool _tempUnavailableReported;

    /// <summary>外部のアプリで編集中のファイル (#16)。</summary>
    private readonly List<EditSession> _edits = [];

    /// <summary>編集中のファイルが書き換わっていないか見に行く巡回。</summary>
    private DispatcherTimer? _editWatch;

    /// <summary>反映するかどうかを尋ねている最中。巡回が重ならないようにする。</summary>
    private bool _askingAboutEdit;

    /// <summary>終了前の書き戻しを済ませてから閉じる途中。</summary>
    private bool _closingAfterSave;

    /// <summary>ドラッグアウトの起点 (#17)。押した位置から一定以上動いたら開始する。</summary>
    private Point _dragOrigin;
    private bool _dragCandidate;

    /// <summary>自分が始めたドラッグの最中。自分の一覧に落とし直されるのを防ぐ。</summary>
    private bool _draggingOut;

    /// <summary>ツリーで押されたフォルダ。動かされたらドラッグアウトを始める (#17)。</summary>
    private ArchiveFolder? _treeDragFolder;

    /// <summary>
    /// 選択済みの項目をもう一度クリックしたときに始める、名前の変更の待ち合わせ (#44)。
    /// ダブルクリックと区別するため、少し待ってから始める。
    /// </summary>
    private DispatcherTimer? _renameClickTimer;
    private EntryRow? _pendingRenameRow;

    /// <summary>いま名前を書き換えている行。ウィンドウのどこかを押したら確定させる (#45)。</summary>
    private EntryRow? _editingRow;

    /// <summary>タブを足したり閉じたりしている最中。選択の変更に二重に反応しないための印 (#22)。</summary>
    private bool _switchingTab;

    public MainWindow()
    {
        InitializeComponent();
        UpdateTitle(null);

        ArchiveTabs.ItemsSource = _tabs;

        _settings = SettingsStore.Load();
        ApplySettings();

        // 異常終了で消し残した一時ファイルを片付ける (#12)。
        // 起動を待たせたくないので裏で行い、結果も見ない。動いている別の
        // インスタンスの置き場は錠ファイルで守られているため巻き込まない。
        Task.Run(static () => TempWorkspace.CleanUpAbandoned(null));

        // 引数で書庫を渡された場合はそれを開く。
        // ウィンドウが出来上がってからでないとエラー表示の親にできないため Loaded で行う。
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && File.Exists(args[1]))
        {
            var path = args[1];
            Loaded += async (_, _) => await OpenArchiveAsync(path);
        }
    }

    // ------------------------------------------------------------------ 操作

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "書庫を開く",
            Filter = ArchiveFormats.OpenFilter,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) == true)
        {
            await OpenInTabAsync(dialog.FileName);
        }
    }

    private async void NewButton_Click(object sender, RoutedEventArgs e)
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
        if (Contents is not null)
        {
            var directory = Path.GetDirectoryName(Contents.FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                dialog.InitialDirectory = directory;
            }
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // パスワードを付けるかどうかをここで決める。中身を入れた後では、
        // 既に暗号化されていないエントリが残ってしまう (#20)
        var password = AskNewArchivePassword();

        try
        {
            ZipArchiveWriter.CreateEmpty(dialog.FileName);

            if (password is not null)
            {
                // 空の書庫には暗号化するものが無い。合言葉を覚えておき、
                // 最初に何かを入れるときから暗号化する
                _passwords[dialog.FileName] = password;
            }
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
        await OpenInTabAsync(dialog.FileName);
    }

    private async void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        if (Contents is not null)
        {
            // 開き直したあとも同じ場所を表示できるよう、現在位置を覚えておく
            await OpenArchiveAsync(Contents.FilePath, CurrentFolder?.FullPath);
        }
    }

    /// <summary>書庫を読み込んで画面に反映する。</summary>
    /// <param name="path">書庫ファイルのパス。</param>
    /// <param name="restorePath">読み込み後に表示したい書庫内フォルダのパス。</param>
    /// <param name="inNewTab">別のタブとして開くかどうか (#22)。</param>
    /// <remarks>
    /// 読み込みは別スレッドで行う。同期で読むと、大きな書庫やネットワーク上の
    /// 書庫でウィンドウが応答しなくなり、中断もできない (#39)。
    /// 読み込み中は今開いている書庫の表示をそのまま残し、成功した時点で差し替える。
    /// 中断や失敗のたびに画面が空になるのは、開き直しの操作で不便なため。
    /// </remarks>
    private async Task OpenArchiveAsync(string path, string? restorePath = null, bool inNewTab = false)
    {
        // 他の処理の最中は受け付けない。ツールバーは SetBusy で止めているが、
        // 最近使った書庫のメニューやコマンドライン起動など別の入口もある。
        if (_cancellation is not null)
        {
            return;
        }

        // 同じ書庫を読み直すときは、ツリーで開いていたフォルダを覚えておく。
        // ノードは読み込みのたびに作り直すため、控えておかないと表示先の祖先しか
        // 開かれず、追加や名前の変更のたびにツリーが畳まれてしまう (#49)。
        var expanded = !inNewTab && Contents is not null
                       && string.Equals(Contents.FilePath, path, StringComparison.OrdinalIgnoreCase)
            ? CollectExpanded(Contents.Root)
            : null;

        ArchiveContents contents;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        var fileName = Path.GetFileName(path);
        var progress = new Progress<OpenProgress>(p =>
        {
            ProgressIndicator.Value = p.Percent;
            StatusMessage.Text = $"{fileName} を読み込んでいます… "
                                 + $"({p.DoneEntries:N0} / {p.TotalEntries:N0} 件)";
        });

        try
        {
            contents = await Task.Run(
                () => ArchiveReader.Open(path, progress, cancellation.Token), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage.Text = "読み込みを中断しました";
            return;
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
            _cancellation = null;
            SetBusy(false);

            // 読み込み中に閉じられていた場合は、ここで閉じる
            if (_closeWhenIdle)
            {
                Close();
            }
        }

        // 閉じる途中なら画面を作り直さない
        if (_closeWhenIdle)
        {
            return;
        }

        RememberRecent(path);

        if (expanded is not null)
        {
            ApplyExpanded(contents.Root, expanded);
        }

        // 新しいタブで開くか、いまのタブを差し替えるか (#22)
        if (inNewTab || Tab is null)
        {
            AddTab(new ArchiveTab(contents));
        }
        else
        {
            Tab.Contents = contents;
        }

        var target = restorePath is null ? contents.Root : FindFolder(contents.Root, restorePath) ?? contents.Root;
        CurrentFolder = target;

        ShowActiveTab();

        StatusMessage.Text = $"{contents.FileCount:N0} 個のファイル{DescribeLimits(contents)}";

        // 中身を取り出せないものが混じっている場合は、開いた時点で知らせる (#19)。
        // ZIP はパスワードを入れれば取り出せるため、ここでは黙っている (#20)
        if (contents.HasEncryptedEntries && !contents.RequiresPassword)
        {
            MessageBox.Show(
                this,
                $"この書庫には暗号化されたファイルが含まれています。{Environment.NewLine}{Environment.NewLine}"
                + "一覧は読めますが、中身の取り出しには対応していません。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
        }

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

    // ------------------------------------------------------------------ 圧縮方式

    /// <summary>いま選ばれている圧縮の強さ (#11)。</summary>
    private CompressionLevel SelectedCompressionLevel
        => CompressionCombo.SelectedItem is CompressionLevelOption option
            ? option.Level
            : CompressionLevelOption.Default;

    private void CompressionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 起動時の初期化でも呼ばれるので、設定を読み終える前は何もしない
        if (_settings is null || CompressionCombo.SelectedItem is not CompressionLevelOption option)
        {
            return;
        }

        var value = CompressionLevelOption.ToSettingValue(option.Level);
        if (_settings.CompressionLevel == value)
        {
            return;
        }

        _settings.CompressionLevel = value;
        SettingsStore.TrySave(_settings);
    }

    // ------------------------------------------------------------------ 最近使った書庫

    /// <summary>履歴に残す件数。</summary>
    private const int RecentLimit = 10;

    private void RecentButton_Click(object sender, RoutedEventArgs e)
    {
        if (RecentButton.ContextMenu is not { } menu)
        {
            return;
        }

        BuildRecentMenu(menu);
        menu.PlacementTarget = RecentButton;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void BuildRecentMenu(ContextMenu menu)
    {
        menu.Items.Clear();

        if (_settings.RecentArchives.Count == 0)
        {
            menu.Items.Add(new MenuItem { Header = "(履歴はありません)", IsEnabled = false });
            return;
        }

        var index = 1;
        foreach (var path in _settings.RecentArchives)
        {
            // ファイル名の _ がアクセスキー扱いにならないよう二重にする
            var label = Path.GetFileName(path).Replace("_", "__");

            var item = new MenuItem
            {
                Header = $"_{index % 10} {label}",
                ToolTip = path,
                Tag = path,
                IsEnabled = _cancellation is null,
            };

            item.Click += RecentItem_Click;
            menu.Items.Add(item);
            index++;
        }

        menu.Items.Add(new Separator());

        var clear = new MenuItem { Header = "履歴を消去(_C)" };
        clear.Click += (_, _) =>
        {
            _settings.RecentArchives.Clear();
            SettingsStore.TrySave(_settings);
        };
        menu.Items.Add(clear);
    }

    private async void RecentItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string path })
        {
            return;
        }

        // 履歴に載せたあとで移動や削除をされていることがある
        if (!File.Exists(path))
        {
            MessageBox.Show(
                this,
                $"ファイルが見つかりませんでした。{Environment.NewLine}{Environment.NewLine}{path}"
                + $"{Environment.NewLine}{Environment.NewLine}履歴から削除します。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);

            _settings.RecentArchives.RemoveAll(
                p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            SettingsStore.TrySave(_settings);
            return;
        }

        await OpenInTabAsync(path);
    }

    /// <summary>開いた書庫を履歴の先頭に移す。</summary>
    private void RememberRecent(string path)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        // 「更新」や追加・削除のあとの読み直しでも呼ばれる。
        // 既に先頭なら中身は変わらないので、設定ファイルへの書き込みも省く
        if (_settings.RecentArchives.Count > 0
            && string.Equals(_settings.RecentArchives[0], full, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.RecentArchives.RemoveAll(
            p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase));
        _settings.RecentArchives.Insert(0, full);

        if (_settings.RecentArchives.Count > RecentLimit)
        {
            _settings.RecentArchives.RemoveRange(
                RecentLimit, _settings.RecentArchives.Count - RecentLimit);
        }

        // 終了時だけでなくこの時点で保存する。異常終了しても履歴が残るように
        SettingsStore.TrySave(_settings);
    }

    // ------------------------------------------------------------------ 削除

    private void EntryList_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var editable = SelectedRowsForEdit();

        DeleteMenuItem.IsEnabled = CanEdit && editable.Count > 0;

        // 「開く」は1件だけを対象にする。複数選んだまま開くと、
        // 選んだ数だけアプリが立ち上がって収拾がつかない (#12)
        OpenMenuItem.IsEnabled = Contents is not null
                                 && _cancellation is null
                                 && EntryList.SelectedItems.Count == 1
                                 && editable.Count == 1;

        // 名前の変更も1件ずつ (#15)
        RenameMenuItem.IsEnabled = OpenMenuItem.IsEnabled && CanEdit;

        // フォルダの作成は選択と関係なく、何もない場所を押したときも使える (#50)
        NewFolderMenuItem.IsEnabled = CanEdit;
    }

    private async void NewFolderMenuItem_Click(object sender, RoutedEventArgs e)
        => await CreateFolderAsync();

    /// <summary>
    /// ツリーを右クリックしたら、押された節へ移ってからメニューを出す (#50)。
    /// 選ばれている場所と違う節を押したのに、別の場所にフォルダができるのを防ぐ。
    /// </summary>
    private void FolderTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FolderUnder(e.OriginalSource as DependencyObject) is not { } folder
            || ReferenceEquals(folder, CurrentFolder))
        {
            return;
        }

        SelectInTree(folder);
        Navigate(folder);
    }

    private void FolderTree_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        => TreeNewFolderMenuItem.IsEnabled = CanEdit;

    private async void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        => await DeleteSelectedAsync();

    private async void RenameMenuItem_Click(object sender, RoutedEventArgs e)
        => await RenameSelectedAsync();

    /// <summary>
    /// 名前を書き換えている最中に、入力欄の外を押したら確定する (#45)。
    /// </summary>
    /// <remarks>
    /// 入力欄から離れたことは <see cref="RenameBox_LostKeyboardFocus"/> でも拾えるが、
    /// 一覧の余白やツリーの空き部分を押しても入力欄はフォーカスを手放さない。
    /// そこで終わるようにする。
    /// </remarks>
    private async void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var row = _editingRow;
        if (row is null || !row.IsEditing)
        {
            return;
        }

        // 入力欄そのものの上なら、文字を選ぶための操作なので触らない
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<TextBox>(source) is { DataContext: EntryRow clicked }
            && ReferenceEquals(clicked, row))
        {
            return;
        }

        await CommitRenameAsync(row, row.EditName);
    }

    // ------------------------------------------------------------------ 名前の変更 (#15)

    /// <summary>
    /// 選択している1件の名前を、一覧の上でその場で書き換え始める。
    /// </summary>
    /// <remarks>
    /// エクスプローラー と同じく、ダイアログは出さない。
    /// </remarks>
    private Task RenameSelectedAsync()
    {
        if (!CanEdit || CurrentFolder is null)
        {
            return Task.CompletedTask;
        }

        var rows = SelectedRowsForEdit();
        if (rows.Count != 1)
        {
            return Task.CompletedTask;
        }

        var row = rows[0];
        BeginEditing(row);
        return Task.CompletedTask;
    }

    /// <summary>入力欄が現れたら、そこに入力できる状態にする。</summary>
    private static void PrepareRenameBox(TextBox box)
    {
        if (box.DataContext is not EntryRow row || !row.IsEditing)
        {
            return;
        }

        box.Focus();
        Keyboard.Focus(box);

        // エクスプローラーと同じく拡張子を除いた部分だけを選ぶ。
        // 拡張子はそのまま使うことがほとんどで、毎回打ち直すのは煩わしい
        var stem = row.Folder is not null ? -1 : row.Name.LastIndexOf('.');
        if (stem > 0)
        {
            box.Select(0, stem);
        }
        else
        {
            box.SelectAll();
        }
    }

    private void RenameBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box)
        {
            PrepareRenameBox(box);
        }
    }

    private void RenameBox_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // 行は使い回されるため、表示されたタイミングでも整える必要がある
        if (sender is TextBox box && e.NewValue is true)
        {
            box.Dispatcher.BeginInvoke(new Action(() => PrepareRenameBox(box)),
                DispatcherPriority.Input);
        }
    }

    private async void RenameBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox box || box.DataContext is not EntryRow row)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            row.IsEditing = false;
            _editingRow = null;
            EntryList.Focus();
            return;
        }

        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await CommitRenameAsync(row, box.Text);
        }
    }

    private async void RenameBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        // エクスプローラーと同じく、他所をクリックした場合も確定させる
        if (sender is TextBox box && box.DataContext is EntryRow row && row.IsEditing)
        {
            await CommitRenameAsync(row, box.Text);
        }
    }

    /// <summary>書き換えた名前を確定する。</summary>
    private async Task CommitRenameAsync(EntryRow row, string text)
    {
        if (!row.IsEditing)
        {
            return;
        }

        // 二重に走らせない。確定の途中で入力欄が消え、再び通知が来ることがある
        row.IsEditing = false;
        _editingRow = null;

        if (Contents is null || CurrentFolder is null || _cancellation is not null)
        {
            return;
        }

        var newName = text.Trim();
        if (newName.Length == 0 || string.Equals(newName, row.Name, StringComparison.Ordinal))
        {
            return;
        }

        if (!ValidateNewName(newName, row))
        {
            return;
        }

        var isFolder = row.Folder is not null;
        var oldPath = isFolder ? row.Folder!.FullPath : row.Entry!.FullPath;
        var parent = CurrentFolder.FullPath;
        var newPath = parent.Length == 0 ? newName : parent + "/" + newName;

        await RunRenameAsync(Contents.FilePath, oldPath, newPath, isFolder, CurrentFolder.FullPath);
    }

    /// <summary>入力された名前が書庫内で使えるかを確かめ、駄目な理由を伝える。</summary>
    private bool ValidateNewName(string newName, EntryRow row)
    {
        // 区切り文字を許すと、名前の変更のつもりが移動になってしまう
        if (newName.IndexOfAny(['/', '\\']) >= 0)
        {
            ShowRenameProblem("名前に \\ と / は使えません。フォルダの移動は名前の変更では行えません。");
            return false;
        }

        if (newName is "." or "..")
        {
            ShowRenameProblem("その名前は使えません。");
            return false;
        }

        // 書庫に入れられても、展開した先で作れない名前にはしない
        var invalid = newName.IndexOfAny(Path.GetInvalidFileNameChars());
        if (invalid >= 0)
        {
            ShowRenameProblem(
                $"名前に使えない文字が含まれています ({newName[invalid]})。"
                + $"{Environment.NewLine}展開したときにファイルを作れなくなります。");
            return false;
        }

        // 同じフォルダに同じ名前があると、展開時にどちらかが失われる
        var duplicated = CurrentFolder!.Folders.Any(
                             f => !ReferenceEquals(f, row.Folder)
                                  && string.Equals(f.Name, newName, StringComparison.OrdinalIgnoreCase))
                         || CurrentFolder.Files.Any(
                             f => !ReferenceEquals(f, row.Entry)
                                  && string.Equals(f.Name, newName, StringComparison.OrdinalIgnoreCase));

        if (duplicated)
        {
            ShowRenameProblem($"このフォルダには既に「{newName}」があります。");
            return false;
        }

        return true;
    }

    private void ShowRenameProblem(string message)
        => MessageBox.Show(this, message, AppName, MessageBoxButton.OK, MessageBoxImage.Warning);

    private async Task RunRenameAsync(
        string archivePath, string oldPath, string newPath, bool isFolder, string restorePath)
    {
        if (!TryGetPassword(out var password))
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        var level = SelectedCompressionLevel;
        var progress = new Progress<int>(done =>
        {
            StatusMessage.Text = $"名前を変更しています… ({done:N0} 件)";
        });

        RenameResult? result = null;
        try
        {
            result = await Task.Run(() => password is null
                ? ZipArchiveWriter.Rename(
                    archivePath, oldPath, newPath, isFolder, level, progress, cancellation.Token)
                : ZipEncryptedWriter.Move(
                    archivePath, [new PathChange(oldPath, newPath, isFolder)], password,
                    level, progress, cancellation.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"名前を変更できませんでした。{Environment.NewLine}{Environment.NewLine}{ex.Message}",
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
        }

        if (_closeWhenIdle || result is null)
        {
            return;
        }

        if (result.Cancelled)
        {
            StatusMessage.Text = "名前の変更を中断しました";
            return;
        }

        // 書庫が変わったので開き直す。フォルダ名を変えた場合は元の場所が
        // 無くなっているため、その親を表示する
        var restore = isFolder && restorePath.Length == 0 ? null : restorePath;
        await OpenArchiveAsync(archivePath, restore);
        StatusMessage.Text = $"{result.Renamed:N0} 件の名前を変更しました";
    }

    private async void EntryList_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter でも開けるようにする。エクスプローラーと同じ操作感にするため (#12)
        if (e.Key == Key.Enter)
        {
            if (EntryList.SelectedItem is EntryRow row)
            {
                e.Handled = true;
                await ActivateAsync(row);
            }

            return;
        }

        // 一つ上のフォルダへ。`..` の行を置かない代わりの手段 (#46)
        if (e.Key == Key.Back)
        {
            e.Handled = true;

            if (CurrentFolder?.Parent is { } parent)
            {
                SelectInTree(parent);
                Navigate(parent);
            }

            return;
        }

        // F2 で名前の変更。エクスプローラーと同じ操作 (#15)
        if (e.Key == Key.F2)
        {
            e.Handled = true;
            await RenameSelectedAsync();
            return;
        }

        // Ctrl+Shift+N で新しいフォルダ。これもエクスプローラーに合わせる (#50)
        if (e.Key == Key.N
            && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            await CreateFolderAsync();
            return;
        }

        if (e.Key != Key.Delete)
        {
            return;
        }

        e.Handled = true;
        await DeleteSelectedAsync();
    }

    /// <summary>
    /// いま開いているフォルダの中に、空のフォルダを作る (#50)。
    /// </summary>
    /// <remarks>
    /// エクスプローラーと同じく名前を尋ねるダイアログは出さず、仮の名前で作って
    /// その場で書き換えられる状態にする。名前の変更の仕組みをそのまま使えるため、
    /// 入力の検証や重複の扱いも一箇所で済む。
    /// </remarks>
    private async Task CreateFolderAsync()
    {
        if (!CanEdit || Contents is null || CurrentFolder is null
            || !TryGetPassword(out var password))
        {
            return;
        }

        var archivePath = Contents.FilePath;
        var parent = CurrentFolder.FullPath;
        var name = UniqueFolderName(CurrentFolder);
        var path = parent.Length == 0 ? name : parent + "/" + name;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);
        StatusMessage.Text = "フォルダを作っています…";

        var created = false;
        try
        {
            created = await Task.Run(() => password is null
                ? ZipArchiveWriter.CreateFolder(archivePath, path)
                : ZipEncryptedWriter.CreateFolder(archivePath, path, password));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"フォルダを作れませんでした。{Environment.NewLine}{Environment.NewLine}{ex.Message}",
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
        }

        if (_closeWhenIdle || !created)
        {
            return;
        }

        // 書庫が変わったので開き直す。同じ場所に戻る
        await OpenArchiveAsync(archivePath, parent);
        StatusMessage.Text = $"「{name}」を作りました";

        // 作った直後は名前を打ち替えたいことがほとんど
        var row = EntryList.Items.OfType<EntryRow>()
            .FirstOrDefault(r => r.Folder is { } folder
                                 && string.Equals(folder.FullPath, path, StringComparison.Ordinal));

        if (row is null)
        {
            return;
        }

        EntryList.SelectedItem = row;
        EntryList.ScrollIntoView(row);
        BeginEditing(row);
    }

    /// <summary>
    /// 新しいフォルダに付ける、そのフォルダの中で重複しない名前。
    /// エクスプローラーと同じく、既にあれば番号を付けて避ける。
    /// </summary>
    private static string UniqueFolderName(ArchiveFolder folder)
    {
        const string BaseName = "新しいフォルダー";

        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in folder.Folders)
        {
            taken.Add(child.Name);
        }

        foreach (var file in folder.Files)
        {
            taken.Add(file.Name);
        }

        if (!taken.Contains(BaseName))
        {
            return BaseName;
        }

        for (var number = 2; ; number++)
        {
            var candidate = $"{BaseName} ({number})";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// 新しく作る書庫にパスワードを付けるか尋ねる (#20)。
    /// </summary>
    /// <returns>付ける場合は合言葉。付けない場合は <see langword="null"/>。</returns>
    private string? AskNewArchivePassword()
    {
        var answer = MessageBox.Show(
            this,
            $"この書庫にパスワードを付けますか?{Environment.NewLine}{Environment.NewLine}"
            + $"付ける場合、入れたファイルは AES-256 で暗号化されます。{Environment.NewLine}"
            + "パスワードを忘れると中身は取り出せません。",
            AppName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return null;
        }

        var dialog = new PasswordDialog(this, "新しい書庫", retry: false);
        return dialog.ShowDialog() == true ? dialog.Password : null;
    }

    // ------------------------------------------------------------------ タブ (#22)

    /// <summary>
    /// 別の書庫を開く。開いていない書庫は新しいタブに出し、既に開いていれば
    /// そのタブへ移る (#22)。
    /// </summary>
    private async Task OpenInTabAsync(string path)
    {
        if (TrySwitchToOpenArchive(path))
        {
            return;
        }

        await OpenArchiveAsync(path, inNewTab: true);
    }

    /// <summary>
    /// タブの切り替えと開け閉め (#22)。ブラウザやエクスプローラーに合わせる。
    /// </summary>
    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // 名前を書き換えている最中は、そちらの操作を優先する
        if (_editingRow is not null)
        {
            return;
        }

        if (e.Key == Key.W && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;

            if (Tab is { } tab)
            {
                CloseTab(tab);
            }

            return;
        }

        // Ctrl+Tab で次のタブ、Ctrl+Shift+Tab で前のタブ
        if (e.Key is Key.Tab && (Keyboard.Modifiers & ModifierKeys.Control) != 0 && _tabs.Count > 1)
        {
            e.Handled = true;

            var index = Tab is { } current ? _tabs.IndexOf(current) : -1;
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : 1;
            ArchiveTabs.SelectedItem = _tabs[((index + step) % _tabs.Count + _tabs.Count) % _tabs.Count];
        }
    }

    /// <summary>タブを足して、それを選ぶ。</summary>
    private void AddTab(ArchiveTab tab)
    {
        _tabs.Add(tab);

        // 1つでも開いていればタブの帯を出す。閉じるボタンがそこにあるため
        ArchiveTabs.Visibility = Visibility.Visible;

        _switchingTab = true;
        try
        {
            ArchiveTabs.SelectedItem = tab;
        }
        finally
        {
            _switchingTab = false;
        }
    }

    /// <summary>いま選ばれているタブの内容を画面に出す。</summary>
    private void ShowActiveTab()
    {
        if (Tab is not { } tab)
        {
            ClearView();
            return;
        }

        var contents = tab.Contents;

        FolderTree.ItemsSource = new[] { contents.Root };
        RefreshButton.IsEnabled = true;
        ExtractButton.IsEnabled = true;

        // 7z と tar は読み取りのみ。書き換える操作は出さない (#19)
        AddButton.IsEnabled = contents.IsEditable;
        UpdateTitle(tab.Title);

        SelectInTree(tab.CurrentFolder);
        Navigate(tab.CurrentFolder);
        RestoreSelection(tab);

        StatusMessage.Text = $"{contents.FileCount:N0} 個のファイル{DescribeLimits(contents)}";
    }

    /// <summary>タブに戻ったときに、前に選んでいた項目を選び直す。</summary>
    private void RestoreSelection(ArchiveTab tab)
    {
        if (tab.SelectedNames.Count == 0)
        {
            return;
        }

        var wanted = tab.SelectedNames.ToHashSet(StringComparer.Ordinal);

        foreach (var row in EntryList.Items.OfType<EntryRow>())
        {
            if (wanted.Contains(row.Name))
            {
                EntryList.SelectedItems.Add(row);
            }
        }

        if (EntryList.SelectedItem is EntryRow first)
        {
            EntryList.ScrollIntoView(first);
        }
    }

    /// <summary>書庫を1つも開いていない状態に戻す。</summary>
    private void ClearView()
    {
        FolderTree.ItemsSource = null;
        EntryList.ItemsSource = null;
        AddressBar.Text = string.Empty;
        AddressBar.ToolTip = null;
        SuspiciousWarningItem.Visibility = Visibility.Collapsed;
        TotalSizeInfo.Text = string.Empty;
        SelectionInfo.Text = "選択 0 個";
        StatusMessage.Text = "書庫が開かれていません";
        EmptyStateMessage.Visibility = Visibility.Visible;
        EmptyStateMessage.Text = "書庫が開かれていません";

        RefreshButton.IsEnabled = false;
        ExtractButton.IsEnabled = false;
        AddButton.IsEnabled = false;
        UpdateTitle(null);

        ArchiveTabs.Visibility = _tabs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ArchiveTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // タブを足した直後は、開く処理の側で画面を作る
        if (_switchingTab || !ReferenceEquals(e.OriginalSource, ArchiveTabs))
        {
            return;
        }

        // 離れるタブの選択を控えておく。戻ってきたときに選び直すため (仕様書 5.2)
        if (e.RemovedItems.Count > 0 && e.RemovedItems[0] is ArchiveTab leaving)
        {
            leaving.SelectedNames = EntryList.SelectedItems.OfType<EntryRow>()
                .Select(static r => r.Name)
                .ToList();
        }

        ShowActiveTab();
    }

    /// <summary>タブの帯を中ボタンで押したら、そのタブを閉じる (ブラウザと同じ)。</summary>
    private void ArchiveTabs_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle)
        {
            return;
        }

        if (FindAncestor<TabItem>(e.OriginalSource as DependencyObject)?.DataContext is ArchiveTab tab)
        {
            e.Handled = true;
            CloseTab(tab);
        }
    }

    private void CloseTabButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ArchiveTab tab)
        {
            CloseTab(tab);
        }
    }

    /// <summary>タブを閉じる。最後の1つを閉じたら、書庫を開いていない状態に戻す。</summary>
    private void CloseTab(ArchiveTab tab)
    {
        // 何かの処理中は閉じない。読み込みの結果を差し込む先が消えてしまう
        if (_cancellation is not null)
        {
            return;
        }

        var index = _tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        CancelPendingRename();

        _switchingTab = true;
        try
        {
            _tabs.RemoveAt(index);

            // 閉じた位置の次を選ぶ。無ければ手前 (ブラウザと同じ)
            ArchiveTabs.SelectedItem = _tabs.Count == 0
                ? null
                : _tabs[Math.Min(index, _tabs.Count - 1)];
        }
        finally
        {
            _switchingTab = false;
        }

        ShowActiveTab();
    }

    /// <summary>同じ書庫を開いているタブがあれば、それを選ぶ。</summary>
    /// <returns>切り替えた場合は true。</returns>
    private bool TrySwitchToOpenArchive(string path)
    {
        var found = _tabs.FirstOrDefault(
            t => string.Equals(t.FilePath, path, StringComparison.OrdinalIgnoreCase));

        if (found is null)
        {
            return false;
        }

        if (!ReferenceEquals(found, Tab))
        {
            ArchiveTabs.SelectedItem = found;
        }

        return true;
    }

    /// <summary>書庫にできることの断り書き。件数の後ろに添える。</summary>
    private static string DescribeLimits(ArchiveContents contents)
    {
        if (contents.RequiresPassword)
        {
            return contents.UsesAes
                ? " (パスワード付き / AES)"
                : " (パスワード付き)";
        }

        return contents.IsEditable
            ? string.Empty
            : $" ({ArchiveFormats.DisplayName(contents.Format)} は読み取りのみに対応)";
    }

    /// <summary>
    /// この起動の間だけ覚えておく、書庫ごとのパスワード (#20)。
    /// 設定ファイルには書かない。持ち出されると書庫を守る意味が無くなるため。
    /// </summary>
    private readonly Dictionary<string, string> _passwords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// いま開いている書庫のパスワードを用意する (#20)。
    /// 一度入れたものは、この起動の間は覚えておく。
    /// </summary>
    /// <returns>合っているパスワード。取り消された場合は <see langword="null"/>。</returns>
    private string? EnsurePassword(string archivePath)
    {
        if (_passwords.TryGetValue(archivePath, out var known))
        {
            return known;
        }

        var name = Path.GetFileName(archivePath);

        for (var retry = false; ; retry = true)
        {
            var dialog = new PasswordDialog(this, name, retry);
            if (dialog.ShowDialog() != true || dialog.Password is not { } password)
            {
                return null;
            }

            // 合っていないパスワードで展開を始めると、中身が壊れたファイルが
            // 書き出される。始める前に確かめる
            if (ZipEncryption.TestPassword(archivePath, password))
            {
                _passwords[archivePath] = password;
                return password;
            }
        }
    }

    /// <summary>取り出しに使うパスワード。要らない書庫では null。</summary>
    /// <returns>取り消された場合は false。</returns>
    private bool TryGetPassword(out string? password)
    {
        password = null;

        if (Contents is null)
        {
            return true;
        }

        // 一度入れたもの、または「新規作成」で決めたものがあればそれを使う
        if (_passwords.TryGetValue(Contents.FilePath, out var known))
        {
            password = known;
            return true;
        }

        if (!Contents.RequiresPassword)
        {
            return true;
        }

        password = EnsurePassword(Contents.FilePath);
        return password is not null;
    }

    /// <summary>
    /// いま書き換えの操作を受け付けられるか (#19)。
    /// 7z と tar は読み取りのみなので、追加・削除・名前の変更・移動は行わせない。
    /// </summary>
    private bool CanEdit => Contents is { IsEditable: true } && _cancellation is null;

    /// <summary>操作の対象にできる選択行。</summary>
    private List<EntryRow> SelectedRowsForEdit()
        => EntryList.SelectedItems.OfType<EntryRow>().ToList();

    private async Task DeleteSelectedAsync()
    {
        if (!CanEdit || Contents is null)
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
        if (Contents is null || !TryGetPassword(out var password))
        {
            return;
        }

        var archivePath = Contents.FilePath;
        var destinationFolder = CurrentFolder?.FullPath ?? string.Empty;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        // 削除は書庫全体の書き直しが1回走るだけなので、進捗を刻めない
        ProgressIndicator.IsIndeterminate = true;
        StatusMessage.Text = "削除しています…";

        try
        {
            var result = await Task.Run(() => password is null
                ? ZipArchiveWriter.Delete(archivePath, files, folders, cancellation.Token)
                : ZipEncryptedWriter.Delete(archivePath, files, folders, password, cancellation.Token));

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
                await OpenArchiveAsync(archivePath, destinationFolder);
            }
        }
    }

    private static int CountFilesUnder(ArchiveFolder folder)
        => folder.Files.Count + folder.Folders.Sum(CountFilesUnder);

    // ------------------------------------------------------------------ 追加

    private async void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (Contents is null)
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

    private void EntryList_DragOver(object sender, DragEventArgs e) => HandleDragOver(e);

    private async void EntryList_Drop(object sender, DragEventArgs e) => await HandleDropAsync(e);

    // 一覧の外(ツリーやステータスバーの上)に落とされても受け取る。
    // 「ウィンドウに書庫を落とすと開く」ようにするため (#41)
    private void Window_DragOver(object sender, DragEventArgs e) => HandleDragOver(e);

    private async void Window_Drop(object sender, DragEventArgs e) => await HandleDropAsync(e);

    /// <summary>落とされたファイルのパス。ファイル以外が落とされた場合は空。</summary>
    private static string[] DroppedPaths(DragEventArgs e)
        => e.Data.GetData(DataFormats.FileDrop) as string[] ?? [];

    /// <summary>開ける書庫として扱う拡張子か (#19)。</summary>
    private static bool IsArchiveFile(string path)
        => File.Exists(path) && ArchiveFormats.IsArchive(path);

    private void HandleDragOver(DragEventArgs e)
    {
        e.Handled = true;

        // 書庫の中での移動 (#43)。自分が始めたドラッグを自分の中に落とした場合
        if (e.Data.GetData(InternalMoveFormat) is InternalMove move)
        {
            e.Effects = ResolveMoveTarget(e, move) is null
                ? DragDropEffects.None
                : DragDropEffects.Move;
            return;
        }

        var paths = DroppedPaths(e);

        // 自分が書庫から出したファイルを自分に落とし直すのは無意味なので受け取らない (#17)。
        // 書庫を開いていなくても、書庫そのものが落とされたなら開ける
        var acceptable = _cancellation is null
                         && !_draggingOut
                         && paths.Length > 0
                         && (Contents is not null || (paths.Length == 1 && IsArchiveFile(paths[0])));

        e.Effects = acceptable ? DragDropEffects.Copy : DragDropEffects.None;
    }

    /// <summary>
    /// 落とそうとしている先のフォルダ。移動できない組み合わせなら <see langword="null"/>。
    /// </summary>
    private ArchiveFolder? ResolveMoveTarget(DragEventArgs e, InternalMove move)
    {
        if (!CanEdit || Contents is null
            || !string.Equals(move.ArchivePath, Contents.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var target = FolderUnder(e.OriginalSource as DependencyObject) ?? CurrentFolder;
        if (target is null)
        {
            return null;
        }

        foreach (var item in move.Items)
        {
            // 元と同じ場所へは動かせない
            var parent = ParentFolderOf(item.Path);
            if (string.Equals(parent, target.FullPath, StringComparison.Ordinal))
            {
                return null;
            }

            // 自分自身や自分の配下には入れられない
            if (item.IsFolder
                && (string.Equals(target.FullPath, item.Path, StringComparison.Ordinal)
                    || target.FullPath.StartsWith(item.Path + "/", StringComparison.Ordinal)))
            {
                return null;
            }
        }

        return target;
    }

    /// <summary>カーソルの下にあるフォルダ。ツリーの節、一覧のフォルダ行、親へ戻る行を見る。</summary>
    private ArchiveFolder? FolderUnder(DependencyObject? source)
    {
        if (source is null)
        {
            return null;
        }

        if (FindAncestor<TreeViewItem>(source)?.DataContext is ArchiveFolder folder)
        {
            return folder;
        }

        if (FindAncestor<ListViewItem>(source)?.Content is EntryRow row)
        {
            return row.Folder;
        }

        return null;
    }

    /// <summary>
    /// 落とされたものを受け取る。書庫を1つ落とされた場合は開き、それ以外は追加する (#41)。
    /// </summary>
    /// <remarks>
    /// 「書庫を落としたら開く」と「書庫を落としたら中に入れる」は両立しない。
    /// 開くほうを既定にする。書庫の中に書庫を入れたい場合は
    /// Shift を押しながら落とす。使う頻度は開くほうが高いという判断。
    /// </remarks>
    private async Task HandleDropAsync(DragEventArgs e)
    {
        // 書庫の中での移動 (#43)。自分が始めたドラッグなので、
        // 外への持ち出しを拒む門 (_draggingOut) より前に見る
        if (e.Data.GetData(InternalMoveFormat) is InternalMove move)
        {
            e.Handled = true;

            if (ResolveMoveTarget(e, move) is { } target)
            {
                await MoveInArchiveAsync(move, target);
            }

            return;
        }

        if (_cancellation is not null || _draggingOut)
        {
            return;
        }

        var paths = DroppedPaths(e);
        if (paths.Length == 0)
        {
            return;
        }

        e.Handled = true;

        var addInstead = (e.KeyStates & DragDropKeyStates.ShiftKey) != 0;

        if (!addInstead && paths.Length == 1 && IsArchiveFile(paths[0]))
        {
            // 開いた結果 (件数) をそのまま出す。落とし方の説明は添えない (#51)
            await OpenInTabAsync(paths[0]);
            return;
        }

        if (Contents is null)
        {
            MessageBox.Show(
                this,
                $"追加先の書庫がありません。{Environment.NewLine}{Environment.NewLine}"
                + "先に書庫を開くか、「新規作成」で作ってください。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 7z と tar は読み取りのみ。落とされたものを黙って捨てない (#19)
        if (!Contents.IsEditable)
        {
            MessageBox.Show(
                this,
                $"{ArchiveFormats.DisplayName(Contents.Format)} 書庫にはファイルを追加できません。"
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "この形式は読み取りのみに対応しています。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await AddToArchiveAsync(paths);
    }

    /// <summary>ディスク上のファイルやフォルダを、いま表示しているフォルダに追加する。</summary>
    private async Task AddToArchiveAsync(IReadOnlyList<string> sourcePaths)
    {
        if (!CanEdit || Contents is null)
        {
            return;
        }

        // パスワード付きの書庫では、足すものも同じ合言葉で暗号化する (#20)
        if (!TryGetPassword(out var password))
        {
            return;
        }

        var archivePath = Contents.FilePath;
        var destinationFolder = CurrentFolder?.FullPath ?? string.Empty;

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

        // 別スレッドから画面の部品には触れないため、ここで値にしておく
        var level = SelectedCompressionLevel;

        var progress = new Progress<AddProgress>(p =>
        {
            ProgressIndicator.Value = p.Percent;
            StatusMessage.Text = $"追加中: {p.CurrentName}";
        });

        // パスワード付きの書庫は、残すエントリも暗号化し直すため書庫全体を作り直す。
        // 2,000件の書庫に1件足すのに3.5秒かかる。何が起きているかを出しておく (#20)
        if (password is not null)
        {
            ProgressIndicator.IsIndeterminate = true;
            StatusMessage.Text = "パスワード付きの書庫を作り直しています…";
        }

        try
        {
            var result = await Task.Run(() => password is null
                ? ZipArchiveWriter.Add(
                    archivePath, sourcePaths, destinationFolder, replaceExisting,
                    level, progress, cancellation.Token)
                : ZipEncryptedWriter.Add(
                    archivePath, sourcePaths, destinationFolder, replaceExisting, password,
                    level, progress, cancellation.Token));

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
                await OpenArchiveAsync(archivePath, destinationFolder);
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

        // うまくいった場合はステータスバーだけにする。ファイルを放り込むたびに
        // ダイアログを閉じさせるのは邪魔でしかない。伝えることがある場合だけ出す
        if (result.Failed.Count == 0 && result.Skipped == 0)
        {
            return;
        }

        MessageBox.Show(this, message.ToString().TrimEnd(), AppName, MessageBoxButton.OK, icon);
    }

    /// <summary>追加しようとしている名前のうち、書庫内に既にあるものを返す。</summary>
    private List<string> FindConflicts(IReadOnlyList<string> sourcePaths, string destinationFolder)
    {
        if (Contents is null)
        {
            return [];
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectPaths(Contents.Root, existing);

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

    // ------------------------------------------------------------------ 保存されたら書き戻す (#16)

    /// <summary>取り出したファイルを見張り始める。保存されたら書庫へ反映するか尋ねる。</summary>
    private void StartEditing(ArchiveEntry entry, string target, string directory)
    {
        // 書き戻せない形式では見張らない。尋ねても応えられない (#19)
        if (Contents is not { IsEditable: true })
        {
            StatusMessage.Text = $"{entry.Name} を開きました "
                                 + $"({ArchiveFormats.DisplayName(Contents!.Format)} は読み取りのみのため、"
                                 + "書き換えても書庫には戻りません)";
            return;
        }

        _edits.Add(new EditSession(Contents.FilePath, entry, target, ParentFolderOf(entry.FullPath)));

        // 巡回はファイルを編集し始めてから動かす。書庫を見ているだけの間は要らない
        if (_editWatch is null)
        {
            _editWatch = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(1),
            };
            _editWatch.Tick += EditWatch_Tick;
        }

        _editWatch.Start();
        StatusMessage.Text = $"{entry.Name} を開きました。保存すると書庫へ反映するか尋ねます";
    }

    /// <summary>書庫内のパスから、その親フォルダのパスを取り出す。</summary>
    private static string ParentFolderOf(string entryPath)
    {
        var separator = entryPath.LastIndexOf('/');
        return separator < 0 ? string.Empty : entryPath[..separator];
    }

    private EditSession? FindEdit(string tempPath)
        => _edits.FirstOrDefault(
            e => string.Equals(e.TempPath, tempPath, StringComparison.OrdinalIgnoreCase));

    private async void EditWatch_Tick(object? sender, EventArgs e)
    {
        // 他の処理の最中や、既に尋ねている最中は見送る。次の巡回で拾える
        if (_askingAboutEdit || _cancellation is not null || _closeWhenIdle)
        {
            return;
        }

        var changed = _edits.FirstOrDefault(static s => s.DetectChange());
        if (changed is null)
        {
            return;
        }

        _askingAboutEdit = true;
        try
        {
            var answer = MessageBox.Show(
                this,
                $"{changed.EntryPath}{Environment.NewLine}{Environment.NewLine}"
                + $"編集されました。書庫に反映しますか?{Environment.NewLine}{Environment.NewLine}"
                + "「いいえ」を選んでも編集内容は残ります。アプリを終了するときに改めて尋ねます。",
                AppName, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer == MessageBoxResult.Yes)
            {
                await ApplyEditAsync(changed);
            }
        }
        finally
        {
            _askingAboutEdit = false;
        }
    }

    /// <summary>編集した一時ファイルを書庫へ書き戻す。</summary>
    /// <returns>書き戻せた場合は true。</returns>
    private async Task<bool> ApplyEditAsync(EditSession session)
    {
        if (_cancellation is not null || !TryGetPassword(out var password))
        {
            return false;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        var level = SelectedCompressionLevel;
        var progress = new Progress<AddProgress>(p =>
        {
            ProgressIndicator.Value = p.Percent;
            StatusMessage.Text = $"{session.Name} を書庫に反映しています…";
        });

        AddResult? result = null;
        try
        {
            // 追加と同じ経路を通す。取り出したときのパスをそのまま使っているので、
            // 追加先フォルダを指定すれば元のエントリを置き換える形になる。
            result = await Task.Run(() => password is null
                ? ZipArchiveWriter.Add(
                    session.ArchivePath, [session.TempPath], session.DestinationFolder,
                    replaceExisting: true, level, progress, cancellation.Token)
                : ZipEncryptedWriter.Add(
                    session.ArchivePath, [session.TempPath], session.DestinationFolder,
                    replaceExisting: true, password, level, progress, cancellation.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"書庫に反映できませんでした。{Environment.NewLine}{Environment.NewLine}"
                + $"{session.EntryPath}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _cancellation = null;
            SetBusy(false);
        }

        if (result is null || result.Cancelled)
        {
            StatusMessage.Text = "反映を中断しました";
            return false;
        }

        if (result.Failed.Count > 0)
        {
            MessageBox.Show(
                this,
                $"書庫に反映できませんでした。{Environment.NewLine}{Environment.NewLine}"
                + $"{session.EntryPath}{Environment.NewLine}{Environment.NewLine}{result.Failed[0].Reason}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // 書き戻したエントリ名は区切りを `/` に正規化した形になる。DOS時代の
        // ツールが書いた `\` 区切りの書庫では元のエントリが別物として残るため、
        // その場合だけ消しておく。放っておくと同じファイルが二重に見える。
        var written = session.DestinationFolder.Length == 0
            ? session.Name
            : session.DestinationFolder + "/" + session.Name;

        if (!string.Equals(written, session.SourceName, StringComparison.Ordinal))
        {
            try
            {
                var stale = session.SourceName;
                await Task.Run(() => ZipArchiveWriter.Delete(
                    session.ArchivePath, new HashSet<string>(StringComparer.Ordinal) { stale },
                    [], CancellationToken.None));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                       or InvalidDataException)
            {
                // 消せなくても書き戻し自体は済んでいる。二重に見えるだけで中身は無事
            }
        }

        session.MarkApplied();

        // 反映後は大きさや圧縮率が変わっているので、開いている書庫なら表示を更新する
        if (Contents is not null
            && string.Equals(Contents.FilePath, session.ArchivePath, StringComparison.OrdinalIgnoreCase))
        {
            await OpenArchiveAsync(session.ArchivePath, CurrentFolder?.FullPath);
        }

        StatusMessage.Text = $"{session.Name} を書庫に反映しました";
        return true;
    }

    /// <summary>
    /// まだ書庫に反映していない編集があれば、終了前に尋ねる (#16)。
    /// </summary>
    /// <returns>そのまま閉じてよい場合は true。</returns>
    private bool ConfirmPendingEdits()
    {
        var pending = _edits.Where(static s => s.HasPendingChanges).ToList();
        if (pending.Count == 0)
        {
            return true;
        }

        var names = string.Join(Environment.NewLine, pending.Select(static s => "・" + s.EntryPath));
        var answer = MessageBox.Show(
            this,
            $"書庫に反映していない編集があります。{Environment.NewLine}{Environment.NewLine}{names}"
            + $"{Environment.NewLine}{Environment.NewLine}反映してから終了しますか?{Environment.NewLine}"
            + "「いいえ」を選ぶと編集内容は失われます。",
            AppName, MessageBoxButton.YesNoCancel, MessageBoxImage.Warning, MessageBoxResult.Yes);

        if (answer == MessageBoxResult.Cancel)
        {
            return false;
        }

        if (answer == MessageBoxResult.No)
        {
            // 破棄して閉じる。以降は聞き直さない
            foreach (var session in pending)
            {
                session.MarkApplied();
            }

            return true;
        }

        // 反映してから閉じる。書き戻しは非同期なので、いったん閉じるのを止める
        _closingAfterSave = true;
        _ = ApplyPendingThenCloseAsync(pending);
        return false;
    }

    private async Task ApplyPendingThenCloseAsync(IReadOnlyList<EditSession> pending)
    {
        foreach (var session in pending)
        {
            if (!await ApplyEditAsync(session))
            {
                // 反映できなかったものは残す。閉じるのは取りやめ、状況を見せる
                _closingAfterSave = false;
                return;
            }
        }

        _closingAfterSave = false;
        Close();
    }

    // ------------------------------------------------------------------ ドラッグアウト (#17)

    /// <summary>
    /// 一度に取り出せる件数の上限。
    /// ドラッグの開始は同期処理のため、展開にかかる時間がそのまま無応答時間になる。
    /// ファイル1件あたり約0.28msの固定費があり、この件数で0.85秒ほど (#18)。
    /// </summary>
    private const int DragOutFileLimit = 3000;

    /// <summary>一度に取り出せる合計サイズの上限。展開の速度はおよそ300MB/秒 (#18)。</summary>
    private const long DragOutByteLimit = 512L * 1024 * 1024;

    private void EntryList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        CancelPendingRename();

        // 行の上で押された場合だけドラッグの起点にする。
        // 列見出しや余白から始まる範囲選択を邪魔しないため。
        var item = e.OriginalSource is DependencyObject source
            ? ItemsControl.ContainerFromElement(EntryList, source) as ListViewItem
            : null;

        _dragCandidate = item is not null;
        _dragOrigin = e.GetPosition(null);

        // エクスプローラーと同じく、選択済みの項目をもう一度クリックすると
        // 名前の変更を始める。押した時点で選ばれていたかどうかで見分ける (#44)
        _pendingRenameRow = item is { Content: EntryRow row }
                            && item.IsSelected
                            && EntryList.SelectedItems.Count == 1
                            && !row.IsEditing
                            && IsInNameColumn(e.GetPosition(item))
            ? row
            : null;
    }

    /// <summary>行の中で、名前の列の上を指しているか。</summary>
    private bool IsInNameColumn(Point positionInRow)
        => EntryList.View is GridView { Columns.Count: > 0 } grid
           && positionInRow.X >= 0
           && positionInRow.X < grid.Columns[0].ActualWidth;

    private void EntryList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // ドラッグに移った場合や、対象でない場合は何もしない
        if (_pendingRenameRow is null || !_dragCandidate || _cancellation is not null)
        {
            _pendingRenameRow = null;
            return;
        }

        var row = _pendingRenameRow;

        _renameClickTimer ??= new DispatcherTimer(DispatcherPriority.Input);
        _renameClickTimer.Stop();

        // ダブルクリックの2回目が来るかもしれないので、その分だけ待つ。
        // 待たずに始めると、開くつもりのダブルクリックで入力欄が出てしまう
        _renameClickTimer.Interval = TimeSpan.FromMilliseconds(GetDoubleClickTime() + 50);
        _renameClickTimer.Tick -= RenameClickTimer_Tick;
        _renameClickTimer.Tick += RenameClickTimer_Tick;
        _pendingRenameRow = row;
        _renameClickTimer.Start();
    }

    private void RenameClickTimer_Tick(object? sender, EventArgs e)
    {
        _renameClickTimer?.Stop();

        var row = _pendingRenameRow;
        _pendingRenameRow = null;

        // 待っている間に選択が変わっていたら始めない
        if (row is null || _cancellation is not null
            || EntryList.SelectedItems.Count != 1
            || !ReferenceEquals(EntryList.SelectedItem, row))
        {
            return;
        }

        BeginEditing(row);
    }

    /// <summary>一覧の上で名前を書き換え始める。</summary>
    private void BeginEditing(EntryRow row)
    {
        _editingRow = row;
        row.EditName = row.Name;
        row.IsEditing = true;
    }

    /// <summary>待ち合わせ中の名前の変更を取りやめる。</summary>
    private void CancelPendingRename()
    {
        _renameClickTimer?.Stop();
        _pendingRenameRow = null;
    }

    /// <summary>ダブルクリックとみなされる間隔 (ミリ秒)。利用者の設定に従う。</summary>
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private void EntryList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || _draggingOut || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        // 押しただけの微妙な揺れでドラッグを始めない。判定はWindowsの設定に合わせる
        var moved = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _dragCandidate = false;
        DragOut(SelectedRowsForEdit(), EntryList);
    }

    private void FolderTree_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _treeDragFolder = null;
        _dragOrigin = e.GetPosition(null);

        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // 開閉の三角を押しただけならドラッグにしない
        if (FindAncestor<ToggleButton>(source) is not null)
        {
            return;
        }

        _treeDragFolder = (FindAncestor<TreeViewItem>(source)?.DataContext) as ArchiveFolder;
    }

    private void FolderTree_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_treeDragFolder is null || _draggingOut || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(null) - _dragOrigin;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var folder = _treeDragFolder;
        _treeDragFolder = null;

        // ルートは書庫そのもの。フォルダとしては取り出せないので、直下の項目をまとめて渡す
        var rows = folder.Parent is null
            ? folder.Folders
                .Select(f => new EntryRow { Name = f.Name, Kind = EntryRowKind.Folder, Folder = f })
                .Concat(folder.Files
                    .Select(f => new EntryRow { Name = f.Name, Kind = EntryRowKind.File, Entry = f }))
                .ToList()
            : [new EntryRow { Name = folder.Name, Kind = EntryRowKind.Folder, Folder = folder }];

        DragOut(rows, FolderTree);
    }

    /// <summary>視覚ツリーを遡って目的の型の親を探す。</summary>
    private static T? FindAncestor<T>(DependencyObject start) where T : DependencyObject
    {
        for (var current = start; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    /// <summary>
    /// 選択した項目を一時フォルダへ取り出し、ファイルとしてドラッグを始める (#17)。
    /// </summary>
    /// <remarks>
    /// 事前展開 + 標準ファイルドロップ方式 (#18で選定)。ドラッグの開始は同期処理で、
    /// 途中で待たせる手段が無い。そのため取り出しもここで待ち合わせる。
    /// 件数と大きさに上限を設けているのはこのため。
    /// </remarks>
    private void DragOut(List<EntryRow> rows, UIElement dragSource)
    {
        if (Contents is null || _cancellation is not null || rows.Count == 0)
        {
            return;
        }

        var names = CollectSourceNames(rows);
        if (names.Count == 0)
        {
            StatusMessage.Text = "取り出せるファイルがありません";
            return;
        }

        // ドラッグの前に合言葉を用意する。掴んだ後では尋ねられない (#20)
        if (!TryGetPassword(out var dragPassword))
        {
            StatusMessage.Text = "取り出しを取りやめました";
            return;
        }

        if (!ConfirmDragOutSize(names))
        {
            return;
        }

        var workspace = EnsureWorkspace();
        if (workspace is null)
        {
            return;
        }

        string[] paths;
        try
        {
            var directory = workspace.DirectoryFor(Contents.FilePath);
            var zone = MarkOfTheWeb.TryRead(Contents.FilePath);

            Mouse.OverrideCursor = Cursors.Wait;
            StatusMessage.Text = $"{names.Count:N0} 件を取り出しています…";

            // 既に取り出してあるものは触らない。開いたままのアプリに掴まれていて
            // 上書きできない場合でも、ドラッグ自体は成り立つようにする
            ArchiveExtractor.Extract(
                Contents.FilePath, Contents.Format, names, directory,
                overwrite: false, progress: null, cancellationToken: CancellationToken.None,
                zoneIdentifier: zone, password: dragPassword);

            // ドラッグの対象は選んだ項目そのもの。フォルダを選んだ場合は
            // 配下のファイルではなくフォルダを渡す
            paths = rows
                .Select(r => r.Folder is not null ? r.Folder.FullPath : r.Entry!.FullPath)
                .Select(ArchivePath.ToSafeRelativePath)
                .Where(static relative => relative is not null)
                .Select(relative => Path.Combine(directory, relative!))
                .Where(path => EnsureDragPath(path, rows, directory))
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or InvalidDataException or NotSupportedException
                                   or PathTooLongException)
        {
            MessageBox.Show(
                this,
                $"取り出しに失敗しました。{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        if (paths.Length == 0)
        {
            StatusMessage.Text = "取り出せませんでした。ドラッグを開始できません";
            return;
        }

        _draggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths);

            // 自分の中に落とされた場合は移動として扱いたいので、書庫内の情報も載せる。
            // Explorer はこの形式を知らないので無視し、FileDrop のほうを使う (#43)
            data.SetData(InternalMoveFormat, new InternalMove(Contents.FilePath, rows
                .Select(r => new MoveItem(
                    r.Folder is not null ? r.Folder.FullPath : r.Entry!.FullPath,
                    r.Name,
                    r.Folder is not null))
                .ToList()));

            var effect = DragDrop.DoDragDrop(
                dragSource, data, DragDropEffects.Copy | DragDropEffects.Move);

            // 書庫の中へ落とされた場合は移動の側で知らせる
            if (effect == DragDropEffects.Copy)
            {
                StatusMessage.Text = $"{paths.Length:N0} 件を取り出しました";
            }
        }
        catch (COMException)
        {
            // ドロップ先のアプリが応答しないなどで失敗することがある。
            // 取り出したファイルは置き場に残り、終了時に片付く
            StatusMessage.Text = "ドラッグを完了できませんでした";
        }
        finally
        {
            _draggingOut = false;
        }
    }

    /// <summary>
    /// ドラッグで渡すパスが実体を持っているかを確かめる。
    /// 中身の無いフォルダは取り出しでは作られないため、ここで用意する。
    /// </summary>
    private static bool EnsureDragPath(string path, List<EntryRow> rows, string directory)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            return true;
        }

        // 空のフォルダを選んだ場合。フォルダとして渡せるように作っておく
        var isFolder = rows.Any(r => r.Folder is not null
                                     && ArchivePath.ToSafeRelativePath(r.Folder.FullPath) is { } relative
                                     && string.Equals(Path.Combine(directory, relative), path,
                                                      StringComparison.OrdinalIgnoreCase));
        if (!isFolder)
        {
            return false;
        }

        try
        {
            Directory.CreateDirectory(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// 取り出す量が多すぎないかを確かめる。多い場合は「展開」を勧める。
    /// </summary>
    private bool ConfirmDragOutSize(IReadOnlySet<string> names)
    {
        long totalBytes = 0;
        void Measure(ArchiveFolder folder)
        {
            foreach (var file in folder.Files)
            {
                if (names.Contains(file.SourceName))
                {
                    totalBytes += file.Length;
                }
            }

            foreach (var child in folder.Folders)
            {
                Measure(child);
            }
        }

        Measure(Contents!.Root);

        if (names.Count <= DragOutFileLimit && totalBytes <= DragOutByteLimit)
        {
            return true;
        }

        MessageBox.Show(
            this,
            $"ドラッグで取り出せるのは {DragOutFileLimit:N0} 件 / "
            + $"{DragOutByteLimit / 1024 / 1024:N0}MB までです。"
            + $"{Environment.NewLine}選択されているのは {names.Count:N0} 件 / "
            + $"{totalBytes / 1024 / 1024:N0}MB です。"
            + $"{Environment.NewLine}{Environment.NewLine}"
            + "ドラッグでは取り出しが終わるまで操作を受け付けられないため、"
            + $"{Environment.NewLine}この量では「展開」を使ってください。中断もできます。",
            AppName, MessageBoxButton.OK, MessageBoxImage.Information);

        return false;
    }

    // ------------------------------------------------------------------ 書庫内の移動 (#43)

    /// <summary>
    /// ドラッグに載せる、書庫の中での移動の情報。
    /// Explorer はこの形式を知らないため、外へ落とした場合は無視される。
    /// </summary>
    private const string InternalMoveFormat = "Expzip.InternalMove";

    private sealed record MoveItem(string Path, string Name, bool IsFolder);

    private sealed record InternalMove(string ArchivePath, IReadOnlyList<MoveItem> Items);

    /// <summary>掴んだ項目を書庫内の別のフォルダへ移す。</summary>
    private async Task MoveInArchiveAsync(InternalMove move, ArchiveFolder target)
    {
        if (!CanEdit || Contents is null)
        {
            return;
        }

        if (!TryGetPassword(out var password))
        {
            return;
        }

        // 移した先に同じ名前があると、どちらかが失われる。先に断る
        var conflicts = move.Items
            .Where(item => target.Folders.Any(
                       f => string.Equals(f.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                   || target.Files.Any(
                       f => string.Equals(f.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(static item => item.Name)
            .ToList();

        if (conflicts.Count > 0)
        {
            MessageBox.Show(
                this,
                $"移動先に同じ名前の項目があります。{Environment.NewLine}{Environment.NewLine}"
                + string.Join(Environment.NewLine, conflicts.Take(5).Select(static c => "  " + c))
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "名前を変えてから移動してください。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var changes = move.Items
            .Select(item => new PathChange(
                item.Path,
                target.FullPath.Length == 0 ? item.Name : target.FullPath + "/" + item.Name,
                item.IsFolder))
            .ToList();

        var archivePath = Contents.FilePath;
        var level = SelectedCompressionLevel;

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        var progress = new Progress<int>(done =>
        {
            StatusMessage.Text = $"移動しています… ({done:N0} 件)";
        });

        RenameResult? result = null;
        try
        {
            result = await Task.Run(() => password is null
                ? ZipArchiveWriter.Move(archivePath, changes, level, progress, cancellation.Token)
                : ZipEncryptedWriter.Move(archivePath, changes, password, level, progress, cancellation.Token));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"移動できませんでした。{Environment.NewLine}{Environment.NewLine}{ex.Message}"
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
        }

        if (_closeWhenIdle || result is null)
        {
            return;
        }

        if (result.Cancelled)
        {
            StatusMessage.Text = "移動を中断しました";
            return;
        }

        // 書庫が変わったので開き直す。移動先を見せたほうが結果が分かりやすい
        await OpenArchiveAsync(archivePath, target.FullPath);
        StatusMessage.Text = $"{move.Items.Count:N0} 件を移動しました";
    }

    // ------------------------------------------------------------------ 展開

    // ------------------------------------------------------------------ 既定のアプリで開く (#12)

    private async void OpenMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (EntryList.SelectedItem is EntryRow row)
        {
            await ActivateAsync(row);
        }
    }

    /// <summary>
    /// 書庫内のファイルを一時フォルダへ取り出し、既定のアプリで開く (#12)。
    /// 取り出したファイルはアプリ終了時に消える。
    /// </summary>
    /// <remarks>
    /// 開いた先で保存されたら、書庫へ反映するか尋ねる (#16)。以前は「開く」と
    /// 「編集」を分けていたが、どちらも既定のアプリに渡すだけで見た目が同じで、
    /// 違いが伝わらなかった。開く手段は一つにする (#52)。
    /// </remarks>
    private async Task OpenWithDefaultAppAsync(ArchiveEntry entry)
    {
        if (Contents is null || _cancellation is not null)
        {
            return;
        }

        // 取り出し先は書庫内のパスから決める。展開と同じ判定を使い、
        // 置き場の外を指すエントリは開かない (Zip Slip 対策)。
        var relative = ArchivePath.ToSafeRelativePath(entry.SourceName);
        if (relative is null)
        {
            MessageBox.Show(
                this,
                $"このファイルは開けません。{Environment.NewLine}{Environment.NewLine}"
                + $"{entry.SourceName}{Environment.NewLine}{Environment.NewLine}"
                + "書庫の外を指すパスが指定されています。",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (RiskyFileTypes.IsExecutable(entry.Name) && !ConfirmExecutable(entry.Name))
        {
            return;
        }

        var workspace = EnsureWorkspace();
        if (workspace is null)
        {
            return;
        }

        string directory;
        string target;
        try
        {
            directory = workspace.DirectoryFor(Contents.FilePath);
            target = Path.GetFullPath(Path.Combine(directory, relative));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or PathTooLongException
                                   or NotSupportedException)
        {
            ReportTempFailure(ex);
            return;
        }

        // 既に開いているファイルをもう一度開こうとした場合は、取り出し直さずにそのまま渡す。
        // 上書きしてしまうと、まだ書庫に反映していない編集内容が消える (#16)。
        var editing = FindEdit(target);
        if (editing is not null)
        {
            LaunchDefaultApp(target);
            return;
        }

        // 同じファイルを開き直したときは取り出し直さない。開いたままのアプリに
        // 掴まれていると上書きできないうえ、大きなファイルでは待ち時間も無駄になる。
        var reusable = TryGetLength(target) == entry.Length;

        if (!reusable && !await ExtractForViewingAsync(entry, directory, target))
        {
            return;
        }

        StartEditing(entry, target, directory);
        LaunchDefaultApp(target);
    }

    /// <summary>
    /// 実行されうるファイルを開く前の確認。
    /// </summary>
    /// <remarks>
    /// 「はい / いいえ」ではなく「OK / キャンセル」にしている。前者では Esc も
    /// タイトルバーの×も効かず、必ずボタンを押させることになる。危ないほうを
    /// 既定にしない確認では、何もせず閉じられることのほうが大事なため。
    /// </remarks>
    private bool ConfirmExecutable(string fileName)
        => MessageBox.Show(
            this,
            $"{fileName}{Environment.NewLine}{Environment.NewLine}"
            + $"このファイルは開くと実行されます。{Environment.NewLine}"
            + $"出所の分からない書庫の場合は開かないでください。{Environment.NewLine}{Environment.NewLine}"
            + "続けますか?",
            AppName,
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;

    /// <summary>一時ファイルの置き場を用意する。用意できなければ <see langword="null"/>。</summary>
    private TempWorkspace? EnsureWorkspace()
    {
        if (_temp is not null)
        {
            return _temp;
        }

        try
        {
            _temp = TempWorkspace.Create();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ReportTempFailure(ex);
        }

        return _temp;
    }

    /// <summary>
    /// 一時フォルダを使えなかったことを知らせる。
    /// ダイアログは一度だけにする。開くたびに同じ知らせが出ても操作の邪魔にしかならない。
    /// </summary>
    private void ReportTempFailure(Exception ex)
    {
        StatusMessage.Text = "一時フォルダを使えないため、ファイルを開けません";

        if (_tempUnavailableReported)
        {
            return;
        }

        _tempUnavailableReported = true;
        MessageBox.Show(
            this,
            $"ファイルを開くための一時フォルダを用意できませんでした。{Environment.NewLine}"
            + $"「展開」で場所を指定すれば取り出せます。{Environment.NewLine}{Environment.NewLine}"
            + $"{TempWorkspace.Root}{Environment.NewLine}{Environment.NewLine}{ex.Message}",
            AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>ファイルの大きさ。無い場合や読めない場合は -1。</summary>
    private static long TryGetLength(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? info.Length : -1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return -1;
        }
    }

    /// <summary>1ファイルだけを一時フォルダへ取り出す。</summary>
    /// <returns>取り出せて、開いてよい状態になった場合は true。</returns>
    private async Task<bool> ExtractForViewingAsync(ArchiveEntry entry, string directory, string target)
    {
        var archivePath = Contents!.FilePath;
        var format = Contents.Format;

        if (!TryGetPassword(out var password))
        {
            return false;
        }

        using var cancellation = new CancellationTokenSource();
        _cancellation = cancellation;
        SetBusy(true);

        var progress = new Progress<ExtractProgress>(p =>
        {
            ProgressIndicator.Value = p.Percent;
            StatusMessage.Text = $"{entry.Name} を取り出しています…";
        });

        try
        {
            // 書庫に付いている出所の印は、取り出したファイルにも引き継ぐ (#12)
            var zone = MarkOfTheWeb.TryRead(archivePath);

            var result = await Task.Run(() =>
            {
                // 何かの拍子に読み取り専用のまま残っていると上書きできない
                TempWorkspace.ClearReadOnly(target);

                return ArchiveExtractor.Extract(
                    archivePath, format,
                    new HashSet<string>(StringComparer.Ordinal) { entry.SourceName },
                    directory, overwrite: true, progress: progress,
                    cancellationToken: cancellation.Token, zoneIdentifier: zone, password: password);
            });

            if (result.Cancelled)
            {
                StatusMessage.Text = "取り出しを中断しました";
                return false;
            }

            if (result.Extracted != 1)
            {
                var reason = result.Failed.Count > 0
                    ? result.Failed[0].Reason
                    : "書庫から取り出せませんでした。";

                MessageBox.Show(
                    this,
                    $"{entry.Name} を開けませんでした。{Environment.NewLine}{Environment.NewLine}{reason}",
                    AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            MessageBox.Show(
                this,
                $"{entry.Name} を開けませんでした。{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        finally
        {
            _cancellation = null;
            SetBusy(false);

            // 取り出している間に閉じられていた場合は、ここで閉じる。
            // 戻り値は finally より先に決まるため、閉じる場合は開かずに終わる。
            if (_closeWhenIdle)
            {
                Close();
            }
        }
    }

    /// <summary>取り出したファイルを既定のアプリに渡す。</summary>
    private void LaunchDefaultApp(string path)
    {
        // 関連付けが無い / 利用者が「アプリを選ぶ」を取り消した場合の Win32 のエラー番号
        const int NoAssociation = 1155;
        const int Cancelled = 1223;

        try
        {
            // UseShellExecute を有効にしないと関連付けが使われず、実行ファイル以外を開けない
            using (Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }))
            {
            }

            StatusMessage.Text = $"{Path.GetFileName(path)} を既定のアプリで開きました";
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == NoAssociation)
        {
            // 関連付けが無いときは Windows の「プログラムから開く」を出す。
            // ここで諦めると、拡張子の無いファイルなどを覗く手立てが無くなる。
            ShowOpenWithDialog(path);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == Cancelled)
        {
            StatusMessage.Text = "開くのを取り消しました";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                   or FileNotFoundException or ObjectDisposedException)
        {
            MessageBox.Show(
                this,
                $"既定のアプリで開けませんでした。{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Windows の「プログラムから開く」を表示する。</summary>
    private void ShowOpenWithDialog(string path)
    {
        try
        {
            using (Process.Start(new ProcessStartInfo("rundll32.exe")
            {
                Arguments = $"shell32.dll,OpenAs_RunDLL \"{path}\"",
                UseShellExecute = true,
            }))
            {
            }

            StatusMessage.Text = $"{Path.GetFileName(path)} を開くアプリを選んでください";
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException
                                   or FileNotFoundException)
        {
            MessageBox.Show(
                this,
                $"このファイルを開けるアプリが見つかりませんでした。{Environment.NewLine}{Environment.NewLine}{ex.Message}",
                AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ------------------------------------------------------------------ 展開

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (Contents is null)
        {
            return;
        }

        var selection = CollectSelectedSourceNames();
        var title = "選択した項目の展開先を選択";

        // 選んだものを展開先の最上位に置く。書庫のルートからの階層は作らない (#48)。
        // 一覧で選んだ場合はいま開いているフォルダまで、ツリーで選んだ場合は
        // その親までを取り除く
        var basePath = CurrentFolder?.FullPath;

        if (selection is null)
        {
            // 一覧で何も選んでいない場合は、いま開いているフォルダが対象。
            // ツリーでフォルダを選んだ状態はこれに当たる。ルートなら書庫全体 (#47)
            if (CurrentFolder is { Parent: not null } current)
            {
                var names = new HashSet<string>(StringComparer.Ordinal);
                AddFilesRecursively(current, names);

                if (names.Count > 0)
                {
                    selection = names;
                    basePath = current.Parent.FullPath;
                    title = $"「{current.Name}」の展開先を選択";
                }
            }

            if (selection is null)
            {
                basePath = null;
                title = "書庫全体の展開先を選択";
            }
        }

        var picker = new OpenFolderDialog { Title = title };

        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        var destination = picker.FolderName;

        // 本当に同じ名前があるときだけ確認する。展開先に何か入っているだけで
        // 尋ねるのは、関係のないファイルを見て驚かせるだけになる (#48)
        List<string> conflicts;
        try
        {
            Mouse.OverrideCursor = Cursors.Wait;
            conflicts = await Task.Run(() => FindExtractConflicts(selection, basePath, destination));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            conflicts = [];
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }

        var overwrite = true;
        if (conflicts.Count > 0)
        {
            var preview = string.Join(Environment.NewLine, conflicts.Take(5).Select(static c => "  " + c));
            var more = conflicts.Count > 5
                ? $"{Environment.NewLine}  ほか {conflicts.Count - 5:N0} 件"
                : string.Empty;

            var answer = MessageBox.Show(
                this,
                $"展開先に同じ名前のファイルが {conflicts.Count:N0} 件あります。上書きしますか?"
                + $"{Environment.NewLine}{Environment.NewLine}{preview}{more}"
                + $"{Environment.NewLine}{Environment.NewLine}"
                + "「いいえ」を選ぶと、それらは展開せずに残します。",
                AppName,
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (answer == MessageBoxResult.Cancel)
            {
                return;
            }

            overwrite = answer == MessageBoxResult.Yes;
        }

        await RunExtractionAsync(
            Contents.FilePath, Contents.Format, selection, destination, overwrite, basePath);
    }

    /// <summary>
    /// 展開先に同じ名前のファイルが既にあるかを調べる。
    /// </summary>
    /// <remarks>
    /// 展開先を一度なめて名前の一覧を作り、そこと突き合わせる。1件ずつ存在を
    /// 確かめると件数が多い書庫で待たされるため。展開先が空なら即座に終わる。
    /// </remarks>
    private List<string> FindExtractConflicts(
        IReadOnlySet<string>? selection, string? basePath, string destination)
    {
        var conflicts = new List<string>();

        if (!Directory.Exists(destination))
        {
            return conflicts;
        }

        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFiles(destination, "*", SearchOption.AllDirectories))
        {
            existing.Add(Path.GetRelativePath(destination, path));
        }

        if (existing.Count == 0)
        {
            return conflicts;
        }

        void Walk(ArchiveFolder folder)
        {
            foreach (var file in folder.Files)
            {
                if (selection is not null && !selection.Contains(file.SourceName))
                {
                    continue;
                }

                var relative = ArchivePath.ToSafeRelativePath(
                    ArchiveExtractor.StripBase(file.SourceName, basePath));

                if (relative is not null && existing.Contains(relative))
                {
                    conflicts.Add(relative);
                }
            }

            foreach (var child in folder.Folders)
            {
                Walk(child);
            }
        }

        Walk(Contents!.Root);
        return conflicts;
    }

    private async Task RunExtractionAsync(
        string archivePath, ArchiveFormat format, IReadOnlySet<string>? selection, string destination,
        bool overwrite, string? basePath = null)
    {
        // パスワード付きの書庫では、始める前に合言葉を用意する (#20)
        if (!TryGetPassword(out var password))
        {
            StatusMessage.Text = "展開を取りやめました";
            return;
        }

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
            // 書庫に付いている出所の印は、書き出したファイルにも引き継ぐ (#12)
            var zone = MarkOfTheWeb.TryRead(archivePath);

            var result = await Task.Run(() => ArchiveExtractor.Extract(
                archivePath, format, selection, destination, overwrite, progress, cancellation.Token,
                zone, basePath, password));

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
            // 反映していない編集が残っていれば先に尋ねる (#16)。
            // 反映してから閉じる場合は、書き戻しが済んだ時点で閉じ直す。
            if (!_closingAfterSave && !ConfirmPendingEdits())
            {
                e.Cancel = true;
                return;
            }

            _editWatch?.Stop();

            // 保存できなくてもアプリを止めない。書き込めない場所に置かれている
            // 場合は設定が残らないだけで、動作そのものには影響しない (#2)
            CaptureSettings();
            SettingsStore.TrySave(_settings);

            // 既定のアプリで開くために取り出したファイルを消す (#12)。
            // 開いたままのアプリに掴まれている分は消せないが、それは次回起動時に
            // 片付ける。ここで待たされてアプリが終われないほうが困る。
            _temp?.Dispose();
            _temp = null;
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
        var rows = SelectedRowsForEdit();
        return rows.Count == 0 ? null : CollectSourceNames(rows);
    }

    /// <summary>指定した行に含まれるファイルのエントリ名を集める。フォルダは配下ごと。</summary>
    private static IReadOnlySet<string> CollectSourceNames(IReadOnlyList<EntryRow> rows)
    {
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
    /// <summary>
    /// 拾いきれなかった失敗のあとに、操作を続けられる状態へ戻す。
    /// 処理中のまま固まると、書庫を開くことも閉じることもできなくなるため。
    /// </summary>
    internal void RecoverFromUnhandledError()
    {
        _cancellation = null;
        _draggingOut = false;
        _askingAboutEdit = false;
        SetBusy(false);
        StatusMessage.Text = "処理を中断しました";
    }

    private void SetBusy(bool busy)
    {
        OpenButton.IsEnabled = !busy;
        NewButton.IsEnabled = !busy;
        ExtractButton.IsEnabled = !busy && Contents is not null;
        AddButton.IsEnabled = !busy && Contents is { IsEditable: true };
        RefreshButton.IsEnabled = !busy && Contents is not null;
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
        CurrentFolder = folder;

        // 親へ戻る `..` の行は置かない。エクスプローラーにも無い。
        // 一つ上へはツリーか BackSpace で移動する (#46)
        var rows = new List<EntryRow>(folder.Folders.Count + folder.Files.Count);

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
        EmptyStateMessage.Text = Contents is null ? "書庫が開かれていません" : "このフォルダは空です";

        // 書庫のあるフォルダから続けて書庫の中の位置まで、ひと続きの場所として出す。
        // 書庫名だけでは同じ名前の別の書庫と区別が付かず、「場所」の名に合わない (#58)。
        // エクスプローラーが書庫を開いたときの表示に合わせ、区切りは `\` にする
        AddressBar.Text = Contents is null
            ? string.Empty
            : folder.FullPath.Length == 0
                ? Contents.FilePath
                : Contents.FilePath + "\\" + folder.FullPath.Replace('/', '\\');

        // 長い場所は欄からはみ出すため、全体を見られるようにしておく
        AddressBar.ToolTip = AddressBar.Text.Length == 0 ? null : AddressBar.Text;

        UpdateSelectionInfo();
    }

    // ------------------------------------------------------------------ 並び替え

    /// <summary>
    /// 現在の並び順を適用する。
    /// エクスプローラーと同じく、フォルダを先に、続いてファイルを置く。
    /// 列の値による並び替えはその各グループの中で行う。
    /// </summary>
    private List<EntryRow> ApplySort(List<EntryRow> rows)
    {
        var sorted = rows
            .OrderBy(static r => r.Kind == EntryRowKind.Folder ? 0 : 1)
            .ThenBy(r => r, Comparer<EntryRow>.Create(CompareByCurrentColumn))
            .ToList();

        return sorted;
    }

    private int CompareByCurrentColumn(EntryRow a, EntryRow b)
    {
        var result = SortColumn switch
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

        return SortDescending ? -result : result;
    }

    private void EntryList_ColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header
            || header.Column?.Header is not string column)
        {
            return;
        }

        if (SortColumn == column)
        {
            SortDescending = !SortDescending;
        }
        else
        {
            SortColumn = column;
            SortDescending = false;
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

    private async void EntryList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 開くつもりのダブルクリックで名前の変更が始まらないようにする (#44)
        CancelPendingRename();

        // 列見出しや余白のダブルクリックでは何もしない。
        // 行の上で押されたかを確かめないと、見出しをダブルクリックしただけで
        // 選択中のファイルが開いてしまう。
        if (e.OriginalSource is not DependencyObject source
            || ItemsControl.ContainerFromElement(EntryList, source) is not ListViewItem item
            || item.Content is not EntryRow row)
        {
            return;
        }

        await ActivateAsync(row);
    }

    /// <summary>
    /// 行を「開く」。フォルダなら移動し、ファイルなら既定のアプリで開く (#12)。
    /// </summary>
    private async Task ActivateAsync(EntryRow row)
    {
        if (row.Folder is not null)
        {
            SelectInTree(row.Folder);
            Navigate(row.Folder);
            return;
        }

        if (row.Entry is not null)
        {
            await OpenWithDefaultAppAsync(row.Entry);
        }
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 選択が別のものに移ったなら、もう一度クリックされたわけではない (#44)。
        // 選択済みの項目をクリックした場合も、他を外す形で通知が来ることがあるため、
        // 「その行だけが選ばれている」なら待ち合わせを続ける
        if (_pendingRenameRow is not null
            && !(EntryList.SelectedItems.Count == 1
                 && ReferenceEquals(EntryList.SelectedItem, _pendingRenameRow)))
        {
            CancelPendingRename();
        }

        UpdateSelectionInfo();
    }

    private void UpdateSelectionInfo()
    {
        // 全選択のたびに選択分のリストを作り直すと、30万件で0.25秒かかっていた。
        // 数えるだけなので一度なめれば足りる (#39)。
        var count = 0;
        long totalBytes = 0;

        foreach (var row in EntryList.SelectedItems.OfType<EntryRow>())
        {
            count++;
            totalBytes += row.SortLength;
        }

        SelectionInfo.Text = count == 0
            ? "選択 0 個"
            : $"選択 {count:N0} 個 ({totalBytes:N0} バイト)";
    }

    /// <summary>ツリーで開かれているフォルダのパスを集める。</summary>
    private static HashSet<string> CollectExpanded(ArchiveFolder root)
    {
        var paths = new HashSet<string>(StringComparer.Ordinal);
        Walk(root);
        return paths;

        void Walk(ArchiveFolder folder)
        {
            if (folder.IsExpanded)
            {
                paths.Add(folder.FullPath);
            }

            foreach (var child in folder.Folders)
            {
                Walk(child);
            }
        }
    }

    /// <summary>
    /// 読み込み直す前に開いていたフォルダを開き直す。
    /// 無くなったフォルダは単に見つからないだけなので、特に何もしなくてよい。
    /// </summary>
    private static void ApplyExpanded(ArchiveFolder folder, HashSet<string> expanded)
    {
        folder.IsExpanded = expanded.Contains(folder.FullPath);

        foreach (var child in folder.Folders)
        {
            ApplyExpanded(child, expanded);
        }
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

        CompressionCombo.ItemsSource = CompressionLevelOption.All;
        var level = CompressionLevelOption.Parse(_settings.CompressionLevel);
        CompressionCombo.SelectedItem =
            CompressionLevelOption.All.First(option => option.Level == level);

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
