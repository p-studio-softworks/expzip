using System.IO;
using System.Windows;
using System.Windows.Media;
using Expzip.Ai;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 保存した決まりを書庫に当てた結果を1枚にまとめて出すウィンドウ (#27、仕様書 10.3節の4)。
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
/// 検査結果のウィンドウ (#57) と同じ作りにしてある。別ウィンドウにして、開いたまま一覧を
/// 触れるようにする。閉じないと先へ進めないダイアログでは用を成さない。
/// </para>
/// </remarks>
// WPF が作る相方の宣言に合わせて public にしてある。決まりの型は internal の
// ままにしたいので、それらを受け渡す所だけ internal にする
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
    /// ウィンドウが固まる。切ったことは要約に書く。黙って切ると、直したのに減らない、
    /// という読み違いを生む。
    /// </remarks>
    private const int MaxRows = 500;

    private readonly Action<string> _jump;

    /// <summary>
    /// 「修正する」の印を本体のツリーと一覧に反映する先 (#88、#166)。
    /// 渡した項目だけに印を絞る。書庫のパスも渡す。
    /// </summary>
    private readonly Action<string, IReadOnlyCollection<string>> _applyMarks;

    /// <summary>書庫を読み直して当て直す先 (#87)。「適用する」で呼ぶ。</summary>
    private readonly Action _recheck;

    /// <summary>自分で対処すると印を付けたもの。行の並べ直しをまたいで覚える。</summary>
    private HashSet<string> _handled = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 最後に反映した印 (#166)。これと違えば、閉じる前に尋ねる。
    /// 触ったかどうかではなく中身で比べる。付けて外しただけなら尋ねない。
    /// </summary>
    private HashSet<string> _applied = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>「適用する」を押して、当て直しの結果を待っているか。</summary>
    private bool _waiting;

    /// <summary>尋ねずに閉じるか。本体ごと閉じるときに立てる。</summary>
    private bool _discarding;

    private RuleAudit _audit;

    /// <summary>一覧を作り直している最中か。作り直しの拍子に飛ばないための印。</summary>
    private bool _rebuilding;

    /// <param name="owner">本体のウィンドウ。閉じると一緒に閉じる。</param>
    /// <param name="audit">出す結果。</param>
    /// <param name="archivePath">当てた書庫。飛び先のタブを決めるのに使う。</param>
    /// <param name="marks">いま本体で印を絞っている項目。絞っていなければ <see langword="null"/>。</param>
    /// <param name="jump">行が選ばれたときに、書庫内のパスを渡す先。</param>
    /// <param name="applyMarks">「修正する」の印を本体に反映する先 (#166)。</param>
    /// <param name="recheck">書庫を読み直して当て直す先 (#87)。</param>
    internal RuleAuditWindow(
        Window owner, RuleAudit audit, string archivePath, IEnumerable<string>? marks,
        Action<string> jump, Action<string, IReadOnlyCollection<string>> applyMarks,
        Action recheck)
    {
        InitializeComponent();

        _audit = audit;
        _jump = jump;
        _applyMarks = applyMarks;
        _recheck = recheck;
        ArchivePath = archivePath;
        Owner = owner;
        Closing += Window_Closing;

        TakeMarks(marks);
        ApplyLanguage();
    }

    /// <summary>いま出している結果の書庫。飛び先のタブを決めるのに使う。</summary>
    internal string ArchivePath { get; private set; }

    /// <summary>新しい結果に差し替える。ウィンドウは開いたままにする。</summary>
    /// <param name="marks">
    /// 開き直したときに、本体で印を絞っている項目。当て直しの結果を出すときは省き、
    /// いまのチェックを残す。
    /// </param>
    internal void ShowAudit(RuleAudit audit, string archivePath, IEnumerable<string>? marks = null)
    {
        if (marks is not null || !string.Equals(archivePath, ArchivePath, StringComparison.OrdinalIgnoreCase))
        {
            TakeMarks(marks);
        }

        _audit = audit;
        ArchivePath = archivePath;
        ApplyLanguage();
        Activate();
    }

    /// <summary>本体ごと閉じるときに呼ぶ。印は本体と一緒に消えるので、尋ねない。</summary>
    internal void CloseWithoutAsking()
    {
        _discarding = true;
        Close();
    }

    /// <summary>本体で絞っている印を、チェックの初めの状態にする (#166)。</summary>
    /// <remarks>
    /// 「閉じる」は何も反映しないので、開いたときのチェックは本体の印と揃えておく。
    /// 揃っていないと、開いて閉じただけで印が変わったように見える。
    /// </remarks>
    private void TakeMarks(IEnumerable<string>? marks)
    {
        _handled = new HashSet<string>(marks ?? [], StringComparer.OrdinalIgnoreCase);
        _applied = new HashSet<string>(_handled, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>文字をいまの言語で入れ直す (#23)。</summary>
    internal void ApplyLanguage()
    {
        Title = Strings.RuleAuditTitle(Path.GetFileName(ArchivePath));
        CloseButton.Content = Strings.InspectionClose;
        ApplyButton.Content = Strings.RuleApply;

        // 名前だけでは、書庫を読み直すことまでは分からない (#88)
        ApplyButton.ToolTip = Strings.RuleAuditApplyHint;
        HandleColumn.Header = Strings.RuleColumnHandle;

        // 前の結果の知らせは残さない。当て直した結果なら、一覧を作り直した後で言い直す
        ResultText.Text = string.Empty;

        // 合っていないものが何も無ければ、印を付ける相手も、確かめ直すものも無い
        ApplyButton.IsEnabled = !_audit.Clean;
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
            ? (Brush)FindResource("EncryptedBrush")
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
                AccentKey = "CautionBrush",
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
                AccentKey = "RuleBreakBrush",
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
                AccentKey = "EncryptedBrush",
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
    /// 「修正する」の印を確定し、書庫を読み直して当て直す (#87、#166)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 印を付けたときと、書庫を直したあとの確かめ直しの、どちらもこのボタンで行う。
    /// 本体の F5 に任せると、このウィンドウの中で結果が変わらず、直ったかが見えない。
    /// </para>
    /// <para>
    /// **確かめずに印を消さない。**消してしまうと、直したつもりで直せていない
    /// ことに気付けない。書庫を読み直して当て直し、その結果で言う。
    /// </para>
    /// </remarks>
    private void ApplyButton_Click(object sender, RoutedEventArgs e) => Apply();

    private void Apply()
    {
        _applyMarks(ArchivePath, _handled);
        _applied = new HashSet<string>(_handled, StringComparer.OrdinalIgnoreCase);
        _waiting = true;
        _recheck();
    }

    /// <summary>
    /// 当て直したあとに、印を付けたものがどうなったかを言う。
    /// </summary>
    /// <remarks>
    /// ダイアログではなくウィンドウの中に書く (#165)。押すたびに閉じさせる知らせは、
    /// 印を付けただけのときには邪魔になる。
    /// </remarks>
    private void TellWhatHappened()
    {
        if (!_waiting)
        {
            return;
        }

        _waiting = false;

        if (_handled.Count == 0)
        {
            ResultText.Text = Strings.RuleAuditAppliedNone;
            return;
        }

        var marked = _handled.Count;

        // まだ残っているもの。印を付けたのに直っていない
        var left = _handled.Count(Still);

        // 直ったものは、もう覚えておく必要がない
        _handled.RemoveWhere(target => !Still(target));
        _applied = new HashSet<string>(_handled, StringComparer.OrdinalIgnoreCase);

        ResultText.Text = left == marked
            ? Strings.RuleAuditAppliedMarks(marked)
            : left == 0 ? Strings.RuleDoneAll : Strings.RuleDoneLeft(left);
    }

    /// <summary>適用していない印があれば、閉じる前に尋ねる (#166)。</summary>
    /// <remarks>
    /// 「閉じる」でも × でも Esc でもここを通る。ルールの推定のウィンドウ (#145) と同じ尋ね方。
    /// 閉じるときは書庫を読み直さない。印を本体に反映するだけにする。
    /// </remarks>
    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_discarding && !_handled.SetEquals(_applied))
        {
            var answer = MessageBox.Show(
                this, Strings.RuleConfirmClose, "Expzip",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Yes);

            if (answer == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }

            if (answer == MessageBoxResult.Yes)
            {
                _applyMarks(ArchivePath, _handled);
            }
        }

        // 閉じる前に本体を前に出す (#164)。このウィンドウからダイアログを出したあとに閉じると、
        // Windows は本体ではなく、その下にある別のアプリのウィンドウを前に出していた
        if (!_discarding)
        {
            Owner?.Activate();
        }
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

        /// <summary>この行の印を覚えておく先。ウィンドウが持っている入れ物。</summary>
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

        /// <summary>
        /// 種類を表す色の名前 (#122)。**ブラシそのものではなく名前で持つ。**
        /// `ResourceDictionary` に入れたブラシは凍結されるため、テーマが切り替わるときはブラシごと
        /// 差し替わる。持ったままにすると前の色で残る
        /// </summary>
        public required string AccentKey { get; init; }

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
