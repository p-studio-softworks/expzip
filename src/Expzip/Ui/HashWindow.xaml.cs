using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// ファイルのハッシュ値を出すウィンドウ (#259)。配布元が公開している値と照らし合わせる。
/// </summary>
/// <remarks>
/// <para>
/// 開くとすぐに計算を始める。大きなファイルでは時間がかかるので、進み具合を出し、
/// 閉じれば中断する。
/// </para>
/// <para>
/// 閉じるまで本体を触れないようにしてある (ShowDialog)。計算しているファイルは本体が
/// 取り出した一時ファイルか書庫そのもので、計算の途中で書庫を書き換えられると、
/// どちらの中身の値なのか分からなくなるため。
/// </para>
/// </remarks>
// WPF が作る相方の宣言に合わせて public にしてある。受け渡す型は internal のままにしたいので、
// それらを受け渡す所だけ internal にする
public partial class HashWindow : Window
{
    private readonly string _title;
    private readonly IReadOnlyList<HashTarget> _targets;
    private readonly ObservableCollection<HashRow> _rows = [];
    private readonly List<(HashTarget Target, FileHashes Hashes)> _done = [];
    private readonly CancellationTokenSource _cancellation = new();

    private bool _running;

    /// <param name="owner">本体のウィンドウ。</param>
    /// <param name="title">タイトルに出す名前。書庫の名前。</param>
    /// <param name="targets">計算するファイル。</param>
    internal HashWindow(Window owner, string title, IReadOnlyList<HashTarget> targets)
    {
        InitializeComponent();

        Owner = owner;
        _title = title;
        _targets = targets;

        HashList.ItemsSource = _rows;
        HashList.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection()));

        ApplyLanguage();

        Loaded += async (_, _) => await ComputeAsync();
        Closing += (_, _) => _cancellation.Cancel();
    }

    /// <summary>文字をいまの言語で入れ直す (#23)。</summary>
    internal void ApplyLanguage()
    {
        Title = Strings.HashTitle(_title);
        NameColumn.Header = Strings.HashColumnName;
        KindColumn.Header = Strings.HashColumnKind;
        ValueColumn.Header = Strings.HashColumnValue;
        CompareLabel.Text = Strings.HashCompareLabel;
        CompareBox.ToolTip = Strings.HashCompareTooltip;
        AutomationProperties.SetName(CompareBox, Strings.HashCompareLabel.TrimEnd(':', '：'));
        CopyButton.Content = Strings.HashCopy;
        CopyButton.ToolTip = Strings.HashCopyTooltip;
        CloseButton.Content = Strings.HashClose;
    }

    /// <summary>1 個ずつ読んで計算し、できたものから一覧に足す。</summary>
    private async Task ComputeAsync()
    {
        _running = true;

        var total = Math.Max(1, _targets.Sum(static t => LengthOf(t.Path)));
        long read = 0;
        var token = _cancellation.Token;

        // 読んだ量は計算の側から届く。画面へは Progress で渡す
        var progress = new Progress<long>(bytes =>
        {
            read += bytes;
            ComputeProgress.Value = Math.Min(1, (double)read / total);
        });

        try
        {
            for (var i = 0; i < _targets.Count; i++)
            {
                Headline.Text = Strings.HashComputing(i, _targets.Count);

                var target = _targets[i];
                IProgress<long> report = progress;

                try
                {
                    var hashes = await Task.Run(() =>
                    {
                        using var stream = new FileStream(
                            target.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        return FileHashes.Compute(stream, report.Report, token);
                    }, token);

                    _done.Add((target, hashes));

                    foreach (var kind in FileHashes.Kinds)
                    {
                        _rows.Add(new HashRow(target.Name, Strings.HashKindName(kind), hashes.Values[kind]));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _rows.Add(new HashRow(target.Name, "-", Strings.HashReadFailed(Strings.Reason(ex))));
                }

                UpdateMatch();
            }

            Headline.Text = Strings.HashDone(_targets.Count);
        }
        catch (OperationCanceledException)
        {
            // 閉じたときにも来る。閉じた後の画面は触らない
            if (IsLoaded)
            {
                Headline.Text = Strings.HashCancelled;
            }
        }
        finally
        {
            _running = false;
        }

        if (IsLoaded)
        {
            // 終わったら進み具合は要らない。満ちた帯が残ると、まだ何かしているように見える
            ComputeProgress.Visibility = Visibility.Collapsed;
            UpdateMatch();
        }
    }

    private static long LengthOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private void CompareBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateMatch();

    /// <summary>照らし合わせる欄の値と一致するものを探し、あれば選ぶ。</summary>
    private void UpdateMatch()
    {
        var text = CompareBox.Text;

        if (string.IsNullOrWhiteSpace(text))
        {
            ShowResult(string.Empty, string.Empty, null);
            return;
        }

        if (FileHashes.Normalize(text) is not { } value)
        {
            ShowResult("", Strings.HashNotHex, "CautionBrush");
            return;
        }

        foreach (var (target, hashes) in _done)
        {
            if (hashes.Match(value) is not { } kind)
            {
                continue;
            }

            var name = Strings.HashKindName(kind);
            var row = _rows.FirstOrDefault(r => r.Name == target.Name && r.Kind == name);
            if (row is not null)
            {
                HashList.SelectedItem = row;
                HashList.ScrollIntoView(row);
            }

            ShowResult("", Strings.HashMatched(target.Name, name), "EncryptedBrush");
            return;
        }

        // 一致しないことは、色だけでなく印と言葉でも出す (#119)
        ShowResult(
            "",
            _running ? Strings.HashNoMatchYet : Strings.HashNoMatch,
            _running ? null : "WarningBrush");
    }

    /// <summary>
    /// 照らし合わせの結果を出す。色は**名前で参照させる** (#122)。ハイコントラストでは付けない (#121)。
    /// </summary>
    private void ShowResult(string glyph, string message, string? accentKey)
    {
        CompareGlyph.Text = glyph;
        CompareResult.Text = message;

        foreach (var text in new[] { CompareGlyph, CompareResult })
        {
            if (accentKey is not null && Theme.Instance.UseAccentColors)
            {
                text.SetResourceReference(ForegroundProperty, accentKey);
            }
            else
            {
                text.ClearValue(ForegroundProperty);
            }
        }
    }

    private void HashList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => CopyButton.IsEnabled = HashList.SelectedItems.Count > 0;

    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopySelection();

    /// <summary>
    /// 選んだ行の値を写す。1 行なら値だけ、何行もなら「種類 値 名前」を 1 行ずつ。
    /// </summary>
    /// <remarks>1 行のときに値だけにするのは、そのまま検索欄や照らし合わせに貼れるようにするため。</remarks>
    private void CopySelection()
    {
        var rows = HashList.SelectedItems.OfType<HashRow>().OrderBy(_rows.IndexOf).ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var text = rows.Count == 1
            ? rows[0].Value
            : string.Join(Environment.NewLine, rows.Select(static r => $"{r.Kind}  {r.Value}  {r.Name}"));

        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            // ほかのアプリがクリップボードを掴んでいる。もう一度押せば済む
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}

/// <summary>ハッシュ値を計算するファイル (#259)。</summary>
/// <param name="Name">一覧に出す名前。書庫の中のパス、または書庫の名前。</param>
/// <param name="Path">読むファイル。</param>
internal sealed record HashTarget(string Name, string Path);

/// <summary>ハッシュ値の一覧に出す 1 行 (#259)。</summary>
internal sealed class HashRow(string name, string kind, string value)
{
    public string Name { get; } = name;

    public string Kind { get; } = kind;

    public string Value { get; } = value;

    /// <summary>支援技術が読むこの行の名前 (#109)。</summary>
    public string RowName => Strings.HashRowName(Name, Kind, Value);
}
