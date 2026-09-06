using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Expzip.Ai;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// お手本書庫から作り方の決まりを読み取り、人が確かめて直す (#25、#26、仕様書 11.3節)。
/// </summary>
/// <remarks>
/// <para>
/// **送る前に、送るものをそのまま見せる。**このアプリで唯一、書庫の中の名前が
/// 外へ出る操作になる。要約した説明ではなく、実際に送られる文字列そのものを
/// 置いて、押すかどうかを利用者に決めてもらう。開いただけでは何も送らない。
/// </para>
/// <para>
/// **読み取った決まりは、そのまま確定しない** (#26)。使うかどうかを1件ずつ選べる。
/// **AIの読み違いを人が正せることを必須**とする (仕様書 11.3節の3)。ここでは
/// 「採らない」という形で正す。
/// </para>
/// <para>
/// 自分の決まりを足す・直す・消す口も一度は置いたが、**入力の仕方が分かりにくく、
/// 誤った決まりを作らせてしまう**ため、いったん外した (#72)。作り直しは #71。
/// ファイルを手で書けば自分の決まりは持てるので、その道は塞いでいない。
/// </para>
/// <para>
/// **読み取っても、保存するまでは何も残らない。**すでに保存された決まりがあれば
/// 開いた時点で読み込み、読み取った決まりは**足す**。人が直したものを、
/// もう一度読み取っただけで消さない。
/// </para>
/// </remarks>
public partial class RuleLearnDialog : Window
{
    private readonly AiOptions _options;
    private readonly ArchiveContents _sample;
    private readonly ArchiveDigest _digest;
    private readonly ObservableCollection<RuleRow> _rows = [];

    private CancellationTokenSource? _asking;

    internal RuleLearnDialog(Window owner, AiOptions options, ArchiveContents sample)
    {
        InitializeComponent();
        Owner = owner;

        _options = options;
        _sample = sample;
        _digest = ArchiveDigest.Build(sample);

        PayloadBox.Text = _digest.Text;
        RuleList.ItemsSource = _rows;

        ApplyLanguage();
        LoadSaved();
        ShowRows();

        Closing += (_, _) => _asking?.Cancel();
        Loaded += (_, _) => SendButton.Focus();
    }

    /// <summary>保存した決まりの数。保存していなければ 0。</summary>
    internal int SavedCount { get; private set; }

    /// <summary>使うことにした決まり。</summary>
    private List<RuleRow> InUse => [.. _rows.Where(static row => row.Enabled)];

    private void ApplyLanguage()
    {
        Title = Strings.RuleDialogTitle;
        IntroText.Text = Strings.RuleIntro(Path.GetFileName(_sample.FilePath));
        SendLabel.Text = Strings.RuleSendLabel;
        PayloadText.Text = Strings.RulePrivacyShort + Environment.NewLine
            + Strings.RulePayload(
                _digest.Bytes, _digest.Omitted, ArchiveDigest.PerFolderLimit);
        NoticeText.Text = Strings.RuleProposalNotice;
        // 押せる見出しだと分かる形が他に無いので、説明を添える (#94)
        UseColumn.Header = new TextBlock
        {
            Text = Strings.RuleColumnUse,
            ToolTip = Strings.RuleUseAll,
        };
        KindColumn.Header = Strings.RuleColumnKind;
        ScopeColumn.Header = Strings.RuleColumnScope;
        PlaceColumn.Header = Strings.RuleColumnPlace;
        ValueColumn.Header = Strings.RuleColumnValue;
        DescriptionColumn.Header = Strings.RuleColumnDescription;
        EvidenceColumn.Header = Strings.RuleColumnEvidence;
        SourceColumn.Header = Strings.RuleColumnSource;
        VerdictColumn.Header = Strings.RuleColumnVerdict;
        SendButton.Content = Strings.RuleSend;
        SaveButton.Content = Strings.RuleSave;
        CloseButton.Content = Strings.RuleClose;
    }

    /// <summary>すでに保存されている決まりがあれば読み込む。</summary>
    private void LoadSaved()
    {
        if (RuleStore.Load() is not { } book || book.Rules.Count == 0)
        {
            return;
        }

        foreach (var entry in book.Rules)
        {
            _rows.Add(new RuleRow(entry.Rule, entry.Enabled, _sample));
        }

        ResultText.Text = Strings.RuleLoaded(book.Rules.Count, book.LearnedFrom);
    }

    // ------------------------------------------------------------------ 尋ねる (#25)

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_asking is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _asking = cancellation;
        SendButton.IsEnabled = false;

        RuleEstimate estimate;

        try
        {
            // 待つ間、秒を進める。考えるモデルは分単位かかる (#76)
            using var ticker = new WaitTicker(Strings.RuleSending, t => ResultText.Text = t);

            estimate = await RuleEstimator.EstimateAsync(
                _options, _sample, _digest, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _asking = null;
            SendButton.IsEnabled = true;
        }

        Show(estimate);
    }

    /// <summary>読み取った結果を出す。採らなかった数も隠さずに出す。</summary>
    /// <remarks>
    /// **足す。入れ替えない。**人が直したものが一覧にあることがあり、
    /// もう一度読み取っただけで消えるのは困る。同じ決まりは足さない。
    /// </remarks>
    private void Show(RuleEstimate estimate)
    {
        if (!estimate.Ok)
        {
            ResultText.Text = estimate.Message;
            return;
        }

        var added = 0;

        foreach (var rule in estimate.Rules)
        {
            if (_rows.Any(row => Same(row.Rule, rule)))
            {
                continue;
            }

            _rows.Add(new RuleRow(rule, true, _sample));
            added++;
        }

        // 採らなかった候補も並べる (#80)。使えないが、**何が捨てられたのかは見せる。**
        // 数だけ知らせても、AI が何を言ったのかは分からない
        foreach (var drop in estimate.Dropped)
        {
            if (!_rows.Any(row => Same(row.Rule, drop.Rule)))
            {
                _rows.Add(new RuleRow(drop, _sample));
            }
        }

        var dropped = Strings.RuleDropped(
            estimate.Broken, estimate.Unchecked, estimate.Unusable);
        var head = estimate.Rules.Count == 0
            ? Strings.RuleNoneFound
            : Strings.RuleFound(estimate.Rules.Count)
                + (added == estimate.Rules.Count ? string.Empty : Strings.RuleAlreadyHad);

        // 補った分は、AI が挙げた数と混ぜずに別の行で言う (#84)
        var lines = new List<string> { head };

        if (estimate.Filled > 0)
        {
            lines.Add(Strings.RuleFilledCount(estimate.Filled));
        }

        if (dropped.Length > 0)
        {
            lines.Add(dropped);
        }

        ResultText.Text = string.Join(Environment.NewLine, lines);
        ShowRows();
    }

    /// <summary>同じ決まりかどうか。説明の書きぶりの違いは見ない。</summary>
    private static bool Same(ArchiveRule a, ArchiveRule b)
        => a.Kind == b.Kind && a.Scope == b.Scope
            && string.Equals(a.Value, b.Value, StringComparison.OrdinalIgnoreCase);

    /// <summary>一覧を、中身に合わせて出し入れする。</summary>
    private void ShowRows()
    {
        var any = _rows.Count > 0;
        RuleList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        NoticeText.Visibility = any ? Visibility.Visible : Visibility.Collapsed;

        // 採らなかった候補しか無いなら、保存するものは無い (#80)
        SaveButton.IsEnabled = _rows.Any(static row => row.CanUse) || RuleStore.Exists;

        // 一覧の行は Collapsed でも * のままでは場所を取り続ける。高さも入れ替える。
        // 読み取った後は一覧のほうが主役になるので、書庫詳細より広く取る (#75)
        RuleRow.Height = any ? new GridLength(2, GridUnitType.Star) : new GridLength(0);
    }

    // ------------------------------------------------------------------ 残す (#26)

    /// <summary>
    /// 決まりを exe と同じフォルダに残す。
    /// </summary>
    /// <remarks>
    /// 使わないことにしたものも、外したという印を付けて残す。消してしまうと、
    /// 一度外した決まりが次に読み取ったときにまた挙がってくる。
    /// </remarks>
    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        // 採らなかった候補は残さない (#80)。画面には出すが、確かめようのないものを
        // ファイルに書いても害にしかならない
        var usable = _rows.Where(static row => row.CanUse).ToList();

        if (usable.Count == 0)
        {
            // 1件も無い状態で保存するのは「ルールを無くす」ということ
            if (!RuleStore.TryDelete())
            {
                ResultText.Text = Strings.RuleSaveFailed;
                return;
            }

            SavedCount = 0;
            ResultText.Text = Strings.RuleCleared;
            return;
        }

        var book = new RuleBook(
            Path.GetFileName(_sample.FilePath), DateTimeOffset.Now,
            [.. usable.Select(static row => new RuleEntry(row.Rule, row.Enabled))]);

        if (!RuleStore.TrySave(book))
        {
            ResultText.Text = Strings.RuleSaveFailed;
            return;
        }

        SavedCount = InUse.Count;
        ResultText.Text = Strings.RuleSaved(usable.Count, SavedCount);
    }

    /// <summary>
    /// 「使用」の見出しを押すと、全部入れる・全部外すを切り替える (#94)。
    /// </summary>
    /// <remarks>
    /// 20件並ぶことがある (#78)。**1つだけ使いたいときに、19回外させない。**
    /// 一度全部外してから、要るものだけ入れられるようにする。
    /// <para>
    /// **1つでも外れていれば全部入れる。**全部入っているときだけ全部外す。
    /// 押すたびに行ったり来たりするより、いまの状態から素直に決まるほうが読める。
    /// </para>
    /// </remarks>
    private void Header_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: { } column }
            || !ReferenceEquals(column, UseColumn))
        {
            return;
        }

        // 採らなかった候補 (#80) は動かさない。そもそも使えない
        var usable = _rows.Where(static row => row.CanUse).ToList();

        if (usable.Count == 0)
        {
            return;
        }

        var turnOn = usable.Exists(static row => !row.Enabled);

        foreach (var row in usable)
        {
            row.Enabled = turnOn;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
