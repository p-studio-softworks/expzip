using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Expzip.Inspection;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 書庫検査の結果を1枚にまとめて出すウィンドウ (#57)。
/// </summary>
/// <remarks>
/// <para>
/// 別ウィンドウにして、開いたまま一覧を触れるようにしてある。行を選ぶと本体側でその項目に
/// 飛ぶため、閉じないと先へ進めないダイアログでは用を成さない。
/// </para>
/// <para>
/// <b>問題が無かったことも明示する</b>。何も出ないと、検査が働いたのか、それとも
/// 何も見つからなかったのかが区別できない。一覧にも「問題は見つかりませんでした」の
/// 1行を置く。ただし「何をどれだけ調べたか」は畳んでおく。変わるのは数だけで、
/// 利用者が知りたいのは結末のほうのため。
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

    /// <param name="owner">本体のウィンドウ。閉じると一緒に閉じる。</param>
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

    /// <summary>新しい検査の結果に差し替える。ウィンドウは開いたままにする。</summary>
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
        Headline.Text = Strings.InspectionHeadline(_report.DangerCount, _report.WarningCount);
        ShowHeadlineAccent(worst);

        CancelledLine.Text = Strings.InspectionCancelledLine;
        CancelledLine.Visibility = _report.Cancelled ? Visibility.Visible : Visibility.Collapsed;

        var scanned = _report.Malware == MalwareStatus.Ran;

        // 使えなかったことは黙って省かない (#56)。ここだけは畳まずに出す
        MalwareWarningLine.Text = Strings.InspectionMalwareUnavailable;
        MalwareWarningLine.Visibility = scanned ? Visibility.Collapsed : Visibility.Visible;

        DetailsExpander.Header = Strings.InspectionDetails;

        ChecksLine.Text = scanned
            ? Strings.InspectionChecksLine
            : Strings.InspectionChecksLineWithoutMalware;

        ContentsLine.Text = Strings.InspectionContentsLine(
            _report.ContentsChecked, _report.FileCount);

        MalwareLine.Text = Strings.InspectionMalwareLine(_report.MalwareScanned);
        MalwareLine.Visibility = scanned ? Visibility.Visible : Visibility.Collapsed;

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

    /// <summary>
    /// 要約の色を付ける。**名前で参照させる** (#122)。ブラシを代入すると、
    /// テーマが切り替わったときにここだけ前の色で残る。
    /// ハイコントラストでは付けない (#121、一覧の行と同じ理由)。
    /// </summary>
    private void ShowHeadlineAccent(InspectionSeverity worst)
    {
        foreach (var text in new[] { HeadlineGlyph, Headline })
        {
            if (Theme.Instance.UseAccentColors)
            {
                text.SetResourceReference(ForegroundProperty, AccentKeyOf(worst));
            }
            else
            {
                text.ClearValue(ForegroundProperty);
            }
        }
    }

    /// <summary>重さを表す色の名前。一覧の警告色と揃えてある (App.xaml)。</summary>
    internal static string AccentKeyOf(InspectionSeverity severity) => severity switch
    {
        InspectionSeverity.Danger => "WarningBrush",
        InspectionSeverity.Warning => "CautionBrush",
        _ => "EncryptedBrush",
    };

    /// <summary>重さを表す印 (Segoe MDL2 Assets)。</summary>
    internal static string GlyphOf(InspectionSeverity severity) => severity switch
    {
        InspectionSeverity.Danger => "\uE7BA",   // 三角の警告
        InspectionSeverity.Warning => "\uE814",  // 感嘆符。一覧の警告と同じ印
        _ => "\uE73E",                           // チェック
    };

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

    /// <summary>
    /// 支援技術が読むこの行の名前 (#109)。入れておかないと型の名前が読まれる。
    /// </summary>
    public string RowName => Strings.FindingRowName(SeverityText, Target, Message);

    public string Glyph => InspectionWindow.GlyphOf(Severity);

    /// <summary>
    /// 重さを表す色の名前 (#122)。**ブラシそのものではなく名前で持つ。**
    /// <see cref="ResourceDictionary"/> に入れたブラシは凍結されるため、テーマが
    /// 切り替わるときはブラシごと差し替わる。持ったままにすると前の色で残る。
    /// </summary>
    public string AccentKey => InspectionWindow.AccentKeyOf(Severity);
}
