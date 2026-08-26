using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Expzip.Inspection;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 書庫検査の結果を1枚にまとめて出す窓 (#57)。
/// </summary>
/// <remarks>
/// <para>
/// 別窓にして、開いたまま一覧を触れるようにしてある。行を選ぶと本体側でその項目に
/// 飛ぶため、閉じないと先へ進めないダイアログでは用を成さない。
/// </para>
/// <para>
/// <b>問題が無かったことも明示する</b>。何も出ないと、検査が働いたのか、それとも
/// 何も見つからなかったのかが区別できない。行った検査の種類と件数を必ず出し、
/// 一覧にも「問題は見つかりませんでした」の1行を置く。
/// </para>
/// </remarks>
// WPF が作る相方の宣言に合わせて public にしてある。書庫検査の型は internal の
// ままにしたいので、それらを受け渡す口だけ internal にする
public partial class InspectionWindow : Window
{
    private readonly Action<string> _jump;

    private InspectionReport _report;

    /// <summary>一覧を作り直している最中か。作り直しの拍子に飛ばないための印。</summary>
    private bool _rebuilding;

    /// <param name="owner">本体の窓。閉じると一緒に閉じる。</param>
    /// <param name="report">出す結果。</param>
    /// <param name="jump">行が選ばれたときに、書庫内のパスを渡す先。</param>
    internal InspectionWindow(Window owner, InspectionReport report, Action<string> jump)
    {
        InitializeComponent();

        _report = report;
        _jump = jump;
        Owner = owner;

        ApplyLanguage();
    }

    /// <summary>いま出している結果の書庫。飛び先のタブを決めるのに使う。</summary>
    internal string ArchivePath => _report.ArchivePath;

    /// <summary>新しい検査の結果に差し替える。窓は開いたままにする。</summary>
    internal void ShowReport(InspectionReport report)
    {
        _report = report;
        ApplyLanguage();
        Activate();
    }

    /// <summary>
    /// 文字をいまの言語で入れ直す (#23)。
    /// </summary>
    /// <remarks>
    /// 結果は文言ではなく事柄の種類で持たせてあるため、検査が済んだあとに言語を
    /// 切り替えても、報告の中身までそのまま入れ替わる。
    /// </remarks>
    internal void ApplyLanguage()
    {
        Title = Strings.InspectionTitle(Path.GetFileName(_report.ArchivePath));
        CloseButton.Content = Strings.InspectionClose;

        SeverityColumn.Header = Strings.InspectionColumnSeverity;
        TargetColumn.Header = Strings.InspectionColumnTarget;
        MessageColumn.Header = Strings.InspectionColumnDetail;

        BuildSummary();
        BuildRows();
    }

    /// <summary>いちばん上の要約。何を調べ、何が見つかったかを書く。</summary>
    private void BuildSummary()
    {
        var worst = _report.Worst;

        HeadlineGlyph.Text = GlyphOf(worst);
        HeadlineGlyph.Foreground = AccentOf(worst);
        Headline.Text = Strings.InspectionHeadline(_report.DangerCount, _report.WarningCount);
        Headline.Foreground = AccentOf(worst);

        CancelledLine.Text = Strings.InspectionCancelledLine;
        CancelledLine.Visibility = _report.Cancelled ? Visibility.Visible : Visibility.Collapsed;

        var scanned = _report.Malware == MalwareStatus.Ran;

        ChecksLine.Text = scanned
            ? Strings.InspectionChecksLine
            : Strings.InspectionChecksLineWithoutMalware;

        ContentsLine.Text = Strings.InspectionContentsLine(
            _report.ContentsChecked, _report.FileCount);

        // 使えなかったことは黙って省かず、色を変えてはっきり出す (#56)
        MalwareLine.Text = scanned
            ? Strings.InspectionMalwareLine(_report.MalwareScanned)
            : Strings.InspectionMalwareUnavailable;

        MalwareLine.Foreground = scanned
            ? SystemColors.GrayTextBrush
            : AccentOf(InspectionSeverity.Warning);

        ElapsedLine.Text = Strings.InspectionElapsedLine(_report.Elapsed);
    }

    private void BuildRows()
    {
        _rebuilding = true;

        try
        {
            FindingList.ItemsSource = _report.Findings.Count == 0
                ? [Clean()]
                : _report.Findings.Select(ToRow).ToList();

            FindingList.SelectedItem = null;
        }
        finally
        {
            _rebuilding = false;
        }
    }

    /// <summary>何も見つからなかったことを表す1行。</summary>
    private InspectionRow Clean() => new()
    {
        Severity = InspectionSeverity.Ok,
        Target = Path.GetFileName(_report.ArchivePath),
        EntryPath = string.Empty,
        Message = Strings.InspectionNoProblems,
    };

    private static InspectionRow ToRow(InspectionFinding finding) => new()
    {
        Severity = finding.Severity,
        Target = finding.Target.Length == 0 ? Strings.InspectionArchiveItself : finding.Target,

        // まとめの行は特定の項目を指していないため、飛び先を持たせない
        EntryPath = finding.Extra > 0 ? string.Empty : finding.Target,
        Message = finding.Extra > 0
            ? Strings.InspectionMoreLine(finding.Extra)
            : Strings.InspectionMessage(finding.Issue, finding.Detail),
    };

    private void FindingList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_rebuilding || FindingList.SelectedItem is not InspectionRow { EntryPath.Length: > 0 } row)
        {
            return;
        }

        _jump(row.EntryPath);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>重さを表す色。一覧の警告色と揃えてある (App.xaml)。</summary>
    internal static Brush AccentOf(InspectionSeverity severity) => severity switch
    {
        InspectionSeverity.Danger => Resource("WarningBrush"),
        InspectionSeverity.Warning => Resource("CautionBrush"),
        _ => Resource("EncryptedBrush"),
    };

    /// <summary>重さを表す印 (Segoe MDL2 Assets)。</summary>
    internal static string GlyphOf(InspectionSeverity severity) => severity switch
    {
        InspectionSeverity.Danger => "\uE7BA",   // 三角の警告
        InspectionSeverity.Warning => "\uE814",  // 感嘆符。一覧の警告と同じ印
        _ => "\uE73E",                           // チェック
    };

    private static Brush Resource(string key)
        => Application.Current.Resources[key] as Brush ?? SystemColors.ControlTextBrush;
}

/// <summary>検査結果の一覧に出す1行 (#57)。</summary>
internal sealed class InspectionRow
{
    public required InspectionSeverity Severity { get; init; }

    /// <summary>対象の欄に出す文字。</summary>
    public required string Target { get; init; }

    /// <summary>飛び先の書庫内パス。飛べない行では空。</summary>
    public required string EntryPath { get; init; }

    /// <summary>内容の欄に出す文字。</summary>
    public required string Message { get; init; }

    public string SeverityText => Strings.SeverityName(Severity);

    public string Glyph => InspectionWindow.GlyphOf(Severity);

    public Brush Accent => InspectionWindow.AccentOf(Severity);
}
