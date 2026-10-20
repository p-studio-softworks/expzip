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
/// 選んだファイルのほかに、書庫ファイルそのものの値も出す (#283)。選んだファイルと混ぜず、一覧の下の別の欄に出す。
/// 混ぜると、選んだより多くのファイルを計算したように見える。計算は最後にする。
/// 書庫ファイルの値は書庫を全部読まないと出ず、大きな書庫では時間がかかる。欲しい値が出たら閉じれば止まる。
/// </para>
/// <para>
/// 閉じるまで本体を触れないようにしてある (ShowDialog)。計算しているのは書庫の中身か
/// 書庫そのもので、計算の途中で書庫を書き換えられると、どちらの中身の値なのか分からなくなるため。
/// </para>
/// </remarks>
// WPF が作る相方の宣言に合わせて public にしてある。受け渡す型は internal のままにしたいので、
// それらを受け渡す所だけ internal にする
public partial class HashWindow : Window
{
    private readonly string _title;
    private readonly string _archivePath;
    private readonly HashTarget _archive;
    private readonly IReadOnlyList<HashTarget> _targets;
    private readonly HashSource? _source;
    private readonly ObservableCollection<HashRow> _rows = [];
    private readonly ObservableCollection<HashRow> _archiveRows = [];
    private readonly List<(HashTarget Target, FileHashes Hashes)> _done = [];
    private readonly CancellationTokenSource _cancellation = new();

    private bool _running;

    /// <param name="owner">本体のウィンドウ。</param>
    /// <param name="archivePath">書庫ファイル。名前をタイトルに出し、ファイルそのものの値も計算する。</param>
    /// <param name="targets">書庫の中で計算するファイル。数と大きさを進み具合に使う。</param>
    /// <param name="source">
    /// 書庫の中で計算するファイルの中身を渡すもの。<see langword="null"/> なら書庫ファイルだけを計算する。
    /// </param>
    internal HashWindow(Window owner, string archivePath, IReadOnlyList<HashTarget> targets, HashSource? source)
    {
        InitializeComponent();

        Owner = owner;
        _title = Path.GetFileName(archivePath);
        _archivePath = archivePath;
        _archive = new HashTarget(Strings.HashArchiveFile(_title), FileStamp.Read(archivePath).Length);
        _targets = targets;
        _source = source;

        HashList.ItemsSource = _rows;
        ArchiveList.ItemsSource = _archiveRows;
        foreach (var list in new[] { HashList, ArchiveList })
        {
            list.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, (_, _) => CopySelection()));
        }

        // 選んだものが無ければ、書庫ファイルの欄だけを上に出す
        if (targets.Count == 0)
        {
            HashList.Visibility = Visibility.Collapsed;
            Grid.SetRow(ArchivePanel, 0);
            ArchivePanel.VerticalAlignment = VerticalAlignment.Top;
            ArchivePanel.BorderThickness = new Thickness(0);
        }

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
        ArchiveKindColumn.Header = Strings.HashColumnKind;
        ArchiveValueColumn.Header = Strings.HashColumnValue;
        ArchiveHeading.Text = Strings.HashArchiveHeading(_title);
        CompareLabel.Text = Strings.HashCompareLabel;
        CompareBox.ToolTip = Strings.HashCompareTooltip;
        AutomationProperties.SetName(CompareBox, Strings.HashCompareLabel.TrimEnd(':', '：'));
        CopyButton.Content = Strings.HashCopy;
        CopyButton.ToolTip = Strings.HashCopyTooltip;
        CloseButton.Content = Strings.HashClose;
    }

    /// <summary>1 個ずつ読んで計算し、できたものから一覧に足す。</summary>
    /// <remarks>
    /// 読むのは裏で、書庫の読み方に任せて順に進める (#265)。結果は 1 個ずつ画面へ渡す。
    /// </remarks>
    private async Task ComputeAsync()
    {
        _running = true;

        var total = Math.Max(1, _targets.Append(_archive).Sum(static t => Math.Max(0, t.Length)));
        long read = 0;
        var token = _cancellation.Token;

        // 読んだ量は計算の側から届く。画面へは Progress で渡す
        IProgress<long> progress = new Progress<long>(bytes =>
        {
            read += bytes;
            ComputeProgress.Value = Math.Min(1, (double)read / total);
        });

        IProgress<int> started = new Progress<int>(
            done => Headline.Text = Strings.HashComputing(done, _targets.Count));

        IProgress<(HashTarget Target, FileHashes? Hashes, string? Failure)> finished =
            new Progress<(HashTarget Target, FileHashes? Hashes, string? Failure)>(result =>
            {
                // 書庫ファイルは選んだファイルと混ぜず、下の欄に出す (#283)
                var rows = ReferenceEquals(result.Target, _archive) ? _archiveRows : _rows;

                if (result.Hashes is { } hashes)
                {
                    _done.Add((result.Target, hashes));

                    foreach (var kind in FileHashes.Kinds)
                    {
                        rows.Add(new HashRow(result.Target.Name, Strings.HashKindName(kind), hashes.Values[kind]));
                    }
                }
                else
                {
                    rows.Add(new HashRow(result.Target.Name, "-", Strings.HashReadFailed(result.Failure!)));
                }

                UpdateMatch();
            });

        try
        {
            started.Report(0);
            var count = 0;

            void Visit(HashTarget target, Func<Stream> open)
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    using var stream = open();
                    finished.Report((target, FileHashes.Compute(stream, progress.Report, token), null));
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    // 1 個読めなくても、ほかは計算する
                    finished.Report((target, null, Strings.Reason(ex)));
                }

                if (!ReferenceEquals(target, _archive))
                {
                    started.Report(++count);
                }
            }

            if (_source is { } source)
            {
                try
                {
                    await Task.Run(() => source(Visit, token), token);
                }
                catch (Exception ex) when (IsReadFailure(ex))
                {
                    // 書庫そのものが途中から読めない (7z の塊の復号に失敗したなど)。どのファイルとは言えない
                    finished.Report((new HashTarget(_title, 0), null, Strings.Reason(ex)));
                }
            }

            // 書庫ファイルそのもの。ダウンロードした書庫を、配布元の値と照らし合わせる用途 (#283)
            Headline.Text = Strings.HashComputingArchive;
            await Task.Run(() => Visit(_archive,
                () => new FileStream(_archivePath, FileMode.Open, FileAccess.Read, FileShare.Read)), token);

            // 数えるのは選んだファイルだけ。書庫ファイルまで数えると、選んだより多く計算したように見える
            Headline.Text = _targets.Count > 0 ? Strings.HashDone(_targets.Count) : Strings.HashArchiveDone;
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

    /// <summary>読めなかったことによる失敗か。読み方ごとに投げる例外が違う。</summary>
    private static bool IsReadFailure(Exception ex)
        => ArchiveContentReader.IsReadFailure(ex) || ex is UnauthorizedAccessException
            or ICSharpCode.SharpZipLib.SharpZipBaseException or SharpCompress.Common.SharpCompressException
            or OutOfMemoryException;

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
            var (list, rows) = ReferenceEquals(target, _archive) ? (ArchiveList, _archiveRows) : (HashList, _rows);
            var row = rows.FirstOrDefault(r => r.Name == target.Name && r.Kind == name);
            if (row is not null)
            {
                list.SelectedItem = row;
                list.ScrollIntoView(row);
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

    /// <summary>
    /// 行を選べるのは、選んだファイルの一覧か書庫ファイルの欄のどちらか一方だけ (#283)。
    /// 両方で選べると、「コピー」でどちらを写すのかが分からない。
    /// </summary>
    private void HashList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListView { SelectedItems.Count: > 0 } list)
        {
            (ReferenceEquals(list, HashList) ? ArchiveList : HashList).UnselectAll();
        }

        CopyButton.IsEnabled = HashList.SelectedItems.Count > 0 || ArchiveList.SelectedItems.Count > 0;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e) => CopySelection();

    /// <summary>
    /// 選んだ行の値を写す。1 行なら値だけ、何行もなら「種類 値 名前」を 1 行ずつ。
    /// </summary>
    /// <remarks>1 行のときに値だけにするのは、そのまま検索欄や照らし合わせに貼れるようにするため。</remarks>
    private void CopySelection()
    {
        var (list, all) = ArchiveList.SelectedItems.Count > 0 ? (ArchiveList, _archiveRows) : (HashList, _rows);
        var rows = list.SelectedItems.OfType<HashRow>().OrderBy(all.IndexOf).ToList();
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
/// <param name="Length">大きさ。進み具合に使う。分からなければ 0。</param>
internal sealed record HashTarget(string Name, long Length);

/// <summary>
/// 計算するファイルの中身を、1 個ずつ渡す (#265)。渡す順は読み方に任せる (7z は書庫の順にしか読めない)。
/// </summary>
/// <param name="visit">
/// 1 個ごとに呼ぶ。2 番目は中身を読む流れを開く。流れは呼び出しの中で閉じる。
/// 読めないものは、開くと例外を投げる流れで渡す。
/// </param>
/// <param name="cancellationToken">中断用。</param>
internal delegate void HashSource(Action<HashTarget, Func<Stream>> visit, CancellationToken cancellationToken);

/// <summary>ハッシュ値の一覧に出す 1 行 (#259)。</summary>
internal sealed class HashRow(string name, string kind, string value)
{
    public string Name { get; } = name;

    public string Kind { get; } = kind;

    public string Value { get; } = value;

    /// <summary>支援技術が読むこの行の名前 (#109)。</summary>
    public string RowName => Strings.HashRowName(Name, Kind, Value);
}
