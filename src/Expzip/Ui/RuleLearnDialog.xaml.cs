using System.IO;
using System.Windows;
using Expzip.Ai;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// お手本書庫から作り方の決まりを読み取るダイアログ (#25、仕様書 11.3節)。
/// </summary>
/// <remarks>
/// <para>
/// **送る前に、送るものをそのまま見せる。**このアプリで唯一、書庫の中の名前が
/// 外へ出る操作になる。要約した説明ではなく、実際に送られる文字列そのものを
/// 置いて、押すかどうかを利用者に決めてもらう。開いただけでは何も送らない。
/// </para>
/// <para>
/// 読み取れた決まりはここでは直せない。**あくまで提案**であることを断り、
/// 確認と手直しは #26 で入れる (仕様書 11.5節)。
/// </para>
/// </remarks>
public partial class RuleLearnDialog : Window
{
    private readonly AiOptions _options;
    private readonly ArchiveContents _sample;
    private readonly ArchiveDigest _digest;

    private CancellationTokenSource? _asking;

    internal RuleLearnDialog(Window owner, AiOptions options, ArchiveContents sample)
    {
        InitializeComponent();
        Owner = owner;

        _options = options;
        _sample = sample;
        _digest = ArchiveDigest.Build(sample);

        PayloadBox.Text = _digest.Text;

        ApplyLanguage();

        Closing += (_, _) => _asking?.Cancel();
        Loaded += (_, _) => SendButton.Focus();
    }

    /// <summary>読み取れた決まり。まだ読み取っていなければ空。</summary>
    internal IReadOnlyList<ArchiveRule> Rules { get; private set; } = [];

    private void ApplyLanguage()
    {
        Title = Strings.RuleDialogTitle;
        IntroText.Text = Strings.RuleIntro(Path.GetFileName(_sample.FilePath));
        SendLabel.Text = Strings.RuleSendLabel;
        PayloadText.Text = Strings.RulePrivacyShort + Environment.NewLine
            + Strings.RulePayload(_digest.Bytes, _digest.Omitted);
        NoticeText.Text = Strings.RuleProposalNotice;
        KindColumn.Header = Strings.RuleColumnKind;
        ScopeColumn.Header = Strings.RuleColumnScope;
        ValueColumn.Header = Strings.RuleColumnValue;
        DescriptionColumn.Header = Strings.RuleColumnDescription;
        EvidenceColumn.Header = Strings.RuleColumnEvidence;
        SendButton.Content = Strings.RuleSend;
        CloseButton.Content = Strings.RuleClose;
    }

    private async void SendButton_Click(object sender, RoutedEventArgs e)
    {
        if (_asking is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _asking = cancellation;
        SendButton.IsEnabled = false;
        RuleList.Visibility = Visibility.Collapsed;
        NoticeText.Visibility = Visibility.Collapsed;
        ResultText.Text = Strings.RuleSending;

        RuleEstimate estimate;

        try
        {
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
    private void Show(RuleEstimate estimate)
    {
        if (!estimate.Ok)
        {
            ResultText.Text = estimate.Message;
            return;
        }

        Rules = estimate.Rules;
        var dropped = Strings.RuleDropped(estimate.Rejected, estimate.Unusable);

        if (estimate.Rules.Count == 0)
        {
            ResultText.Text = dropped.Length == 0
                ? Strings.RuleNoneFound
                : Strings.RuleNoneFound + Environment.NewLine + dropped;

            return;
        }

        ResultText.Text = dropped.Length == 0
            ? Strings.RuleFound(estimate.Rules.Count)
            : Strings.RuleFound(estimate.Rules.Count) + Environment.NewLine + dropped;

        RuleList.ItemsSource = estimate.Rules;
        RuleList.Visibility = Visibility.Visible;
        NoticeText.Visibility = Visibility.Visible;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
