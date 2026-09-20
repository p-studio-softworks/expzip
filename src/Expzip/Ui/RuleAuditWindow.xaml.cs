using System.IO;
using System.Windows;
using System.Windows.Media;
using Expzip.Ai;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 保存した決まりを書庫に当てた結果を1枚にまとめて出す窓 (#27、仕様書 10.3節の4)。
/// </summary>
/// <remarks>
/// <para>
/// 一覧には旗を立てるが、**旗は見ているフォルダの中しか出ない**。深いところに
/// あるものは、そこへ行くまで気付けない。ここに全部を並べ、行を選ぶと本体側で
/// その項目へ飛ぶ。
/// </para>
/// <para>
/// **「必ずある」はずのものが無い場合は、指させる項目が無い。**一覧には印を
/// 付けられないため、ここでだけ出す。印が付かないことを「問題なし」と
/// 読ませないための場所でもある。
/// </para>
/// <para>
/// 検査結果の窓 (#57) と同じ作りにしてある。別窓にして、開いたまま一覧を
/// 触れるようにする。閉じないと先へ進めないダイアログでは用を成さない。
/// </para>
/// </remarks>
// WPF が作る相方の宣言に合わせて public にしてある。決まりの型は internal の
// ままにしたいので、それらを受け渡す口だけ internal にする
public partial class RuleAuditWindow : Window
{
    /// <summary>旗。一覧に立てるものと同じ形にする。</summary>
    private const string GlyphFlag = "\uE7C1";

    /// <summary>丸に。「必ずある」はずのものが無いことを表す。</summary>
    private const string GlyphMissing = "\uE814";

    /// <summary>チェック。合っていないものが無かったことを表す。</summary>
    private const string GlyphClean = "\uE73E";

    /// <summary>
    /// 一度に並べる上限。
    /// </summary>
    /// <remarks>
    /// 拡張子ひとつの決まりが数千件に当たることがある。全部並べても読めないうえ、
    /// 窓が固まる。切ったことは要約に書く。黙って切ると、直したのに減らない、
    /// という読み違いを生む。
    /// </remarks>
    private const int MaxRows = 500;

    private readonly Action<string> _jump;

    /// <summary>「完了」で呼ぶ先。書庫を読み直して当て直す (#87)。</summary>
    private readonly Action _recheck;

    /// <summary>自分で対処すると印を付けたもの。行の並べ直しをまたいで覚える。</summary>
    private readonly HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「完了」を押して、当て直しの結果を待っているか。</summary>
    private bool _waiting;

    private RuleAudit _audit;

    /// <summary>一覧を作り直している最中か。作り直しの拍子に飛ばないための印。</summary>
    private bool _rebuilding;

    /// <param name="owner">本体の窓。閉じると一緒に閉じる。</param>
    /// <param name="audit">出す結果。</param>
    /// <param name="archivePath">当てた書庫。飛び先のタブを決めるのに使う。</param>
    /// <param name="jump">行が選ばれたときに、書庫内のパスを渡す先。</param>
    /// <param name="recheck">「完了」が押されたときに呼ぶ先 (#87)。</param>
    internal RuleAuditWindow(
        Window owner, RuleAudit audit, string archivePath, Action<string> jump,
        Action recheck)
    {
        InitializeComponent();

        _audit = audit;
        _jump = jump;
        _recheck = recheck;
        ArchivePath = archivePath;
        Owner = owner;

        ApplyLanguage();
    }

    /// <summary>いま出している結果の書庫。飛び先のタブを決めるのに使う。</summary>
    internal string ArchivePath { get; private set; }

    /// <summary>
    /// 自分で対処すると印を付けたもの (#88)。
    /// </summary>
    /// <remarks>
    /// 閉じたあとに、**ツリーの印をこれだけに絞る**のに使う。全部に印が出たままでは、
    /// どれを引き受けたのかが見えない。
    /// </remarks>
    internal IReadOnlyCollection<string> Handled => _handled;

    /// <summary>新しい結果に差し替える。窓は開いたままにする。</summary>
    internal void ShowAudit(RuleAudit audit, string archivePath)
    {
        _audit = audit;
        ArchivePath = archivePath;
        ApplyLanguage();
        Activate();
    }

    /// <summary>文字をいまの言語で入れ直す (#23)。</summary>
    internal void ApplyLanguage()
    {
        Title = Strings.RuleAuditTitle(Path.GetFileName(ArchivePath));
        CloseButton.Content = Strings.InspectionClose;
        DoneButton.Content = Strings.RuleDone;

        // 「更新」だけでは何をするか分からない (#88)
        DoneButton.ToolTip = Strings.RuleDoneHint;
        HandleColumn.Header = Strings.RuleColumnHandle;

        // 合っていないものが何も無ければ、押しても言うことがない
        DoneButton.IsEnabled = !_audit.Clean;
        KindColumn.Header = Strings.RuleColumnKind;
        TargetColumn.Header = Strings.RuleAuditColumnTarget;
        MessageColumn.Header = Strings.RuleAuditColumnRule;

        BuildSummary();
        BuildRows();
    }

    private void BuildSummary()
    {
        var clean = _audit.Clean;

        HeadlineGlyph.Text = clean ? GlyphClean : GlyphFlag;
        HeadlineGlyph.Foreground = clean
            ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))
            : (Brush)FindResource("RuleBreakBrush");

        Headline.Text = clean
            ? Strings.RuleAuditClean
            : Strings.RuleAuditFound(_audit.BrokenCount, _audit.Unmet.Count);

        SourceLine.Text = Strings.RuleAuditSource(
            _audit.RuleCount, _audit.LearnedFrom, _audit.LearnedAt);
    }

    private void BuildRows()
    {
        _rebuilding = true;

        var rows = new List<Row>();

        // 「必ずある」はずのものが無いほうを先に出す。指させる項目が無く、
        // 一覧の旗では気付けないため
        foreach (var rule in _audit.Unmet)
        {
            rows.Add(new Row
            {
                Marks = _handled,
                Glyph = GlyphMissing,
                Accent = (Brush)FindResource("CautionBrush"),
                KindText = Strings.RuleMissing,
                Target = rule.Value,
                Message = Describe(rule),
            });
        }

        foreach (var (path, rules) in _audit.Broken.OrderBy(
            static p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (rows.Count >= MaxRows)
            {
                break;
            }

            rows.Add(new Row
            {
                Marks = _handled,
                Glyph = GlyphFlag,
                Accent = (Brush)FindResource("RuleBreakBrush"),
                KindText = rules[0].KindText,
                Target = path,
                Message = string.Join(" / ", rules.Select(Describe)),
                Path = path,
            });
        }

        var trimmed = _audit.Unmet.Count + _audit.Broken.Count - rows.Count;
        TrimmedLine.Text = Strings.RuleAuditTrimmed(trimmed);
        TrimmedLine.Visibility = trimmed > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (rows.Count == 0)
        {
            rows.Add(new Row
            {
                Marks = _handled,
                Glyph = GlyphClean,
                Accent = new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10)),
                KindText = string.Empty,
                Target = string.Empty,
                Message = Strings.RuleAuditClean,
            });
        }

        // 並べ直しても、付けた印は残す (#87)
        foreach (var row in rows)
        {
            row.Handled = _handled.Contains(row.Target);
        }

        FindingList.ItemsSource = rows;
        _rebuilding = false;

        // 当て直しの結果を待っていたなら、ここで言う
        TellWhatHappened();
    }

    /// <summary>決まりを一言で書く。AI が説明を書いていなければ、種類と値で書く。</summary>
    private static string Describe(ArchiveRule rule)
        => rule.Description.Length > 0
            ? rule.Description
            : $"{rule.KindText}: {rule.Value}";

    private void FindingList_SelectionChanged(
        object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_rebuilding || FindingList.SelectedItem is not Row { Path: { } path })
        {
            return;
        }

        _jump(path);
    }

    /// <summary>
    /// 直し終えたので、当て直す (#87)。
    /// </summary>
    /// <remarks>
    /// **確かめずに印を消さない。**消してしまうと、直したつもりで直せていない
    /// ことに気付けない。書庫を読み直して当て直し、その結果で言う。
    /// </remarks>
    private void DoneButton_Click(object sender, RoutedEventArgs e)
    {
        _waiting = true;
        _recheck();
    }

    /// <summary>当て直したあとに、印を付けたものがどうなったかを言う。</summary>
    private void TellWhatHappened()
    {
        if (!_waiting)
        {
            return;
        }

        _waiting = false;

        if (_handled.Count == 0)
        {
            return;
        }

        // まだ残っているもの。印を付けたのに直っていない
        var left = _handled.Count(Still);

        // 直ったものは、もう覚えておく必要がない
        _handled.RemoveWhere(target => !Still(target));

        MessageBox.Show(
            this,
            left == 0 ? Strings.RuleDoneAll : Strings.RuleDoneLeft(left),
            "Expzip", MessageBoxButton.OK,
            left == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    /// <summary>その項目が、いまも合っていないままか。</summary>
    private bool Still(string target)
        => FindingList.ItemsSource is IEnumerable<Row> rows
            && rows.Any(row => string.Equals(
                row.Target, target, StringComparison.OrdinalIgnoreCase));

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>一覧の1行。</summary>
    private sealed class Row
    {
        private bool _handled;

        /// <summary>この行の印を覚えておく先。窓が持っている入れ物。</summary>
        public required HashSet<string> Marks { get; init; }

        /// <summary>自分で対処すると印を付けたか (#87)。</summary>
        public bool Handled
        {
            get => _handled;
            set
            {
                _handled = value;

                if (value)
                {
                    Marks.Add(Target);
                }
                else
                {
                    Marks.Remove(Target);
                }
            }
        }

        public required string Glyph { get; init; }

        public required Brush Accent { get; init; }

        public required string KindText { get; init; }

        public required string Target { get; init; }

        public required string Message { get; init; }

        /// <summary>飛び先の書庫内パス。飛べない行では <see langword="null"/>。</summary>
        public string? Path { get; init; }

        /// <summary>
        /// 支援技術が読むこの行の名前 (#109)。問題が無かった行は内容だけになる。
        /// </summary>
        public string RowName => Strings.FindingRowName(KindText, Target, Message);
    }
}
