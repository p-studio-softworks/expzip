using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using Expzip.Localization;
using Expzip.Speed;
using Microsoft.Win32;

namespace Expzip.Ui;

/// <summary>
/// このパソコンで圧縮・展開にかかる時間を見積もるウィンドウ (#269)。
/// </summary>
/// <remarks>
/// <para>
/// 押したときだけ測る。開いた時点では前回の結果を出す。パソコンを何台か持っているとき、
/// それぞれで測った結果をコピーして並べれば、大きな作業をどれで行うとよいかを決められる。
/// 比べるための集計は作らない (Expzip どうしで通信しない)。
/// </para>
/// <para>
/// 閉じるまで本体を触れないようにしてある (ShowDialog)。測っている間にほかの作業をすると、
/// その分だけ遅く測ってしまう。
/// </para>
/// </remarks>
// WPF が作る相方の宣言に合わせて public にしてある。受け渡す型は internal のままにしたいので、
// それらを受け渡す所だけ internal にする
public partial class SpeedWindow : Window
{
    /// <summary>見積もりを出す中身の大きさ。100 MB から 1 TB まで。</summary>
    private static readonly long[] Sizes =
        [100L << 20, 1L << 30, 10L << 30, 100L << 30, 1L << 40];

    private static readonly SpeedKind[] Kinds = [SpeedKind.Documents, SpeedKind.Media, SpeedKind.SmallFiles];

    private readonly string _tempFolder;
    private readonly Action<SpeedResult> _save;

    private SpeedResult? _result;
    private string? _folder;
    private CancellationTokenSource? _cancellation;

    /// <summary>見出しに出す、いまの状態。言語を切り替えても出し直せるように持つ。</summary>
    private Func<string> _headline;

    /// <param name="owner">本体のウィンドウ。</param>
    /// <param name="tempFolder">一時フォルダーで測るときに使う場所。</param>
    /// <param name="previous">前回の結果。測っていなければ <see langword="null"/>。</param>
    /// <param name="save">測り終えた結果を残す。</param>
    internal SpeedWindow(Window owner, string tempFolder, SpeedResult? previous, Action<SpeedResult> save)
    {
        InitializeComponent();

        Owner = owner;
        _tempFolder = tempFolder;
        _save = save;
        _result = previous;
        _headline = previous is null ? () => Strings.SpeedNotYet : () => Strings.SpeedPrevious;

        // 前回、選んだフォルダーで測っていれば、同じ場所で測り直せるようにしておく
        if (previous is { Location.Length: > 0 } && Directory.Exists(previous.Location))
        {
            _folder = previous.Location;
            FolderChoice.IsChecked = true;
        }

        ApplyLanguage();

        Closing += (_, _) => _cancellation?.Cancel();
    }

    /// <summary>文字をいまの言語で入れ直す (#23)。</summary>
    internal void ApplyLanguage()
    {
        Title = Strings.SpeedTitle;
        Intro.Text = Strings.SpeedIntro;
        PlaceLabel.Text = Strings.SpeedPlaceLabel;
        TempChoice.Content = $"{Strings.SpeedPlaceTemp} ({Path.TrimEndingDirectorySeparator(Path.GetTempPath())})";
        FolderChoice.Content = $"{Strings.SpeedPlaceFolder} ({_folder ?? Strings.SpeedPlaceNone})";
        PlaceHint.Text = Strings.SpeedPlaceHint;
        BrowseButton.Content = Strings.SpeedBrowse;
        AutomationProperties.SetName(BrowseButton, Strings.SpeedBrowseTitle);
        SizeColumn.Header = Strings.SpeedColumnSize;
        DocumentsColumn.Header = Strings.SpeedKindName(SpeedKind.Documents);
        MediaColumn.Header = Strings.SpeedKindName(SpeedKind.Media);
        SmallFilesColumn.Header = Strings.SpeedKindName(SpeedKind.SmallFiles);
        RatingColumn.Header = Strings.SpeedColumnRating;
        Note.Text = Strings.SpeedNote;
        ConditionsLabel.Text = Strings.SpeedConditionsLabel;
        CopyButton.Content = Strings.SpeedCopy;
        CopyButton.ToolTip = Strings.SpeedCopyTooltip;
        CloseButton.Content = Strings.SpeedClose;
        Headline.Text = _headline();

        ShowResult();
        UpdateButtons();
    }

    private bool Running => _cancellation is not null;

    private void UpdateButtons()
    {
        StartButton.Content = Running ? Strings.SpeedStop : Strings.SpeedStart;
        StartButton.IsEnabled = Running || TempChoice.IsChecked == true || _folder is not null;
        TempChoice.IsEnabled = !Running;
        FolderChoice.IsEnabled = !Running;
        BrowseButton.IsEnabled = !Running;
        CopyButton.IsEnabled = !Running && _result is not null;
    }

    private void Place_Checked(object sender, RoutedEventArgs e)
    {
        // 作りかけの間にも呼ばれる。まだ部品が揃っていない
        if (IsInitialized && StartButton is not null)
        {
            UpdateButtons();
        }
    }

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = Strings.SpeedBrowseTitle };
        if (_folder is not null)
        {
            picker.InitialDirectory = _folder;
        }

        if (picker.ShowDialog(this) != true)
        {
            return;
        }

        _folder = picker.FolderName;
        FolderChoice.Content = $"{Strings.SpeedPlaceFolder} ({_folder})";
        FolderChoice.IsChecked = true;
        UpdateButtons();
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cancellation is { } running)
        {
            running.Cancel();
            return;
        }

        var useFolder = FolderChoice.IsChecked == true && _folder is not null;
        var folder = useFolder ? _folder! : _tempFolder;
        var location = useFolder ? _folder! : string.Empty;

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        MeasureProgress.Value = 0;
        MeasureProgress.Visibility = Visibility.Visible;
        UpdateButtons();

        IProgress<SpeedProgress> progress = new Progress<SpeedProgress>(p =>
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            MeasureProgress.Maximum = p.Total;
            MeasureProgress.Value = p.Done;
            _headline = () => Strings.SpeedRunning(p.Step, p.Kind, p.Done, p.Total);
            Headline.Text = _headline();
        });

        try
        {
            var result = await Task.Run(() => SpeedTest.Run(folder, location, progress, token), token);
            _result = result;
            _save(result);
            _headline = () => Strings.SpeedDone;
        }
        catch (OperationCanceledException)
        {
            _headline = () => Strings.SpeedCancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or ArgumentException or NotSupportedException
                                   or InvalidDataException)
        {
            _headline = () => Strings.SpeedCancelled;
            if (IsLoaded)
            {
                MessageBox.Show(this, Strings.SpeedFailed(Strings.Reason(ex)), Strings.SpeedTitle,
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            _cancellation.Dispose();
            _cancellation = null;
        }

        // 閉じたときにも来る。閉じた後の画面は触らない
        if (!IsLoaded)
        {
            return;
        }

        MeasureProgress.Visibility = Visibility.Collapsed;
        Headline.Text = _headline();
        ShowResult();
        UpdateButtons();
    }

    /// <summary>結果の表と、測った条件を出す。</summary>
    private void ShowResult()
    {
        if (_result is not { } result)
        {
            ResultPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ResultPanel.Visibility = Visibility.Visible;
        EstimateList.ItemsSource = Sizes.Select(size => EstimateRow.Of(result, size)).ToList();
        Conditions.Text = string.Join(Environment.NewLine, ConditionLines(result));
    }

    /// <summary>測った条件。分からないものは出さない。</summary>
    private static IEnumerable<string> ConditionLines(SpeedResult result)
    {
        yield return Strings.SpeedMeasuredAt(result.MeasuredAt);
        yield return Strings.SpeedMachine(result.MachineName);
        yield return Strings.SpeedCpu(result.Cpu, result.Cores);
        yield return Strings.SpeedMemory(result.MemoryBytes);
        yield return Strings.SpeedStorage(result.Storage, result.Location);

        if (result.OnBattery is { } onBattery)
        {
            yield return Strings.SpeedPower(onBattery);
        }

        yield return Strings.SpeedDisk(result.DiskReadBytesPerSecond, result.DiskWriteBytesPerSecond);
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result is not { } result)
        {
            return;
        }

        // 表は列をタブで区切る。表計算ソフトに貼れば、そのまま列に分かれる
        var text = new StringBuilder();
        text.AppendLine(Strings.SpeedCopyHeading);
        foreach (var line in ConditionLines(result))
        {
            text.AppendLine(line);
        }

        text.AppendLine();
        text.AppendLine(string.Join('\t',
            new[] { Strings.SpeedColumnSize }
                .Concat(Kinds.Select(Strings.SpeedKindName))
                .Append(Strings.SpeedColumnRating)));
        foreach (var size in Sizes)
        {
            var row = EstimateRow.Of(result, size);
            text.AppendLine(string.Join('\t',
                row.Size, row.Documents.Plain, row.Media.Plain, row.SmallFiles.Plain, row.Rating.Text));
        }

        text.AppendLine();
        text.AppendLine(Strings.SpeedNote);

        try
        {
            Clipboard.SetText(text.ToString());
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // ほかのアプリがクリップボードを掴んでいる。もう一度押せば済む
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 見積もりの表の 1 行 (#269)。評価は、行の中でいちばん遅い時間で決める。
    /// </summary>
    private sealed record EstimateRow(
        string Size, TimeCell Documents, TimeCell Media, TimeCell SmallFiles, RatedText Rating, string RowName)
    {
        public static EstimateRow Of(SpeedResult result, long bytes)
        {
            var times = Kinds.Select(kind => result.Estimate(kind, bytes)).ToList();
            var cells = times.Select(TimeCell.Of).ToList();
            var size = Strings.SpeedSize(bytes);

            var measured = times.OfType<(TimeSpan Compress, TimeSpan Extract)>().ToList();
            var rating = new RatedText("-", null);
            if (measured.Count > 0)
            {
                var worst = SpeedRatings.Of(measured.Max(static t => t.Compress > t.Extract ? t.Compress : t.Extract));
                rating = RatedText.Of(Strings.SpeedRatingName(worst), worst);
            }

            return new EstimateRow(size, cells[0], cells[1], cells[2], rating,
                Strings.SpeedRowName(size, cells[0].Plain, cells[1].Plain, cells[2].Plain, rating.Text));
        }
    }

    /// <summary>表の 1 マス。圧縮と展開の時間に、それぞれの評価の色を付ける。</summary>
    private sealed record TimeCell(string CompressLabel, RatedText Compress, string ExtractLabel, RatedText Extract)
    {
        /// <summary>色を付けない形。コピーと、支援技術が読む行の名前に使う。</summary>
        public string Plain { get; init; } = "-";

        public static TimeCell Of((TimeSpan Compress, TimeSpan Extract)? time)
        {
            if (time is not { } t)
            {
                return new TimeCell(string.Empty, new RatedText("-", null), string.Empty, new RatedText(string.Empty, null));
            }

            return new TimeCell(
                Strings.SpeedCompressLabel,
                RatedText.Of(Strings.SpeedDuration(t.Compress), SpeedRatings.Of(t.Compress)),
                Strings.SpeedExtractLabel,
                RatedText.Of(Strings.SpeedDuration(t.Extract), SpeedRatings.Of(t.Extract)))
            {
                Plain = Strings.SpeedCell(t.Compress, t.Extract),
            };
        }
    }

    /// <summary>評価の色を付ける字。色は名前で持つ (#122)。</summary>
    private sealed record RatedText(string Text, string? AccentKey)
    {
        public static RatedText Of(string text, SpeedRating rating) => new(text, rating switch
        {
            SpeedRating.Comfortable => "EncryptedBrush",
            SpeedRating.SomewhatSlow => "CautionBrush",
            _ => "WarningBrush",
        });
    }
}
