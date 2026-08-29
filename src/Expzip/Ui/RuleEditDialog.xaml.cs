using System.Windows;
using System.Windows.Media;
using Expzip.Ai;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>決まりを1つ入れる・直すダイアログ (#26)。</summary>
/// <remarks>
/// <para>
/// **AIの読み違いを人が正せることを必須とする** (仕様書 11.3節の3)。挙がった
/// 決まりをそのまま受け取るしかないなら、提案として出す意味がない。
/// </para>
/// <para>
/// **直した結果をその場に出す。**いま開いている書庫に当てるとどうなるかを、
/// 打つそばから引き直して見せる。当ててみるまで分からないままだと、
/// 直したつもりで壊すことになる。
/// </para>
/// </remarks>
public partial class RuleEditDialog : Window
{
    private static readonly RuleKind[] Kinds =
    [
        RuleKind.RequiredEntry, RuleKind.RequiredFolder, RuleKind.ForbiddenExtension,
        RuleKind.ForbiddenName, RuleKind.NamePattern,
    ];

    private static readonly RuleScope[] Scopes =
    [
        RuleScope.Root, RuleScope.Folders, RuleScope.Files, RuleScope.All,
    ];

    private readonly ArchiveContents _sample;
    private readonly string _evidence;

    private bool _ready;

    /// <param name="owner">親の窓。</param>
    /// <param name="sample">いま開いている書庫。当てた結果を出すのに使う。</param>
    /// <param name="rule">直す決まり。新しく入れるときは <see langword="null"/>。</param>
    internal RuleEditDialog(Window owner, ArchiveContents sample, ArchiveRule? rule)
    {
        InitializeComponent();
        Owner = owner;

        _sample = sample;

        // AI が挙げた決まりを直しても、AI が何を根拠にしたかは残す
        _evidence = rule?.Evidence ?? string.Empty;

        KindCombo.ItemsSource = Kinds.Select(KindName).ToArray();
        ScopeCombo.ItemsSource = Scopes.Select(ScopeName).ToArray();

        KindCombo.SelectedIndex = rule is null ? 0 : Array.IndexOf(Kinds, rule.Kind);
        ScopeCombo.SelectedIndex = rule is null
            ? Array.IndexOf(Scopes, RuleScope.All)
            : Array.IndexOf(Scopes, rule.Scope);
        ValueBox.Text = rule?.Value ?? string.Empty;
        DescriptionBox.Text = rule?.Description ?? string.Empty;

        ApplyLanguage();
        _ready = true;
        ShowVerdict();

        Loaded += (_, _) => ValueBox.Focus();
    }

    /// <summary>入れた決まり。作れていなければ <see langword="null"/>。</summary>
    internal ArchiveRule? Rule { get; private set; }

    private static string KindName(RuleKind kind) => kind switch
    {
        RuleKind.RequiredEntry => Strings.RuleKindRequiredEntry,
        RuleKind.RequiredFolder => Strings.RuleKindRequiredFolder,
        RuleKind.ForbiddenExtension => Strings.RuleKindForbiddenExtension,
        RuleKind.ForbiddenName => Strings.RuleKindForbiddenName,
        _ => Strings.RuleKindNamePattern,
    };

    private static string ScopeName(RuleScope scope) => scope switch
    {
        RuleScope.Root => Strings.RuleScopeRoot,
        RuleScope.Folders => Strings.RuleScopeFolders,
        RuleScope.Files => Strings.RuleScopeFiles,
        _ => Strings.RuleScopeAll,
    };

    private void ApplyLanguage()
    {
        Title = Strings.RuleEditTitle;
        IntroText.Text = Strings.RuleEditIntro;
        KindLabel.Text = Strings.RuleColumnKind;
        ScopeLabel.Text = Strings.RuleColumnScope;
        ValueLabel.Text = Strings.RuleColumnValue;
        DescriptionLabel.Text = Strings.RuleColumnDescription;
        OkButton.Content = Strings.RuleEditOk;
        CancelButton.Content = Strings.AiCancel;
    }

    /// <summary>いまの入力から決まりを作る。作れなければ <see langword="null"/>。</summary>
    /// <remarks>
    /// 人が入れた・直したものは、出どころを「自分」にする。AI が挙げたままの
    /// ものと見分けが付かなくなると、提案であることを明示できない。
    /// </remarks>
    private ArchiveRule? Compose()
    {
        var kind = Kinds[Math.Max(0, KindCombo.SelectedIndex)];
        var scope = Scopes[Math.Max(0, ScopeCombo.SelectedIndex)];

        return ArchiveRule.TryCreate(
            kind, scope, ValueBox.Text, DescriptionBox.Text, _evidence, RuleSource.Hand);
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (_ready)
        {
            ShowVerdict();
        }
    }

    /// <summary>値の意味と、いま開いている書庫に当てた結果を出す。</summary>
    private void ShowVerdict()
    {
        var kind = Kinds[Math.Max(0, KindCombo.SelectedIndex)];
        HintText.Text = Strings.RuleValueHint(kind);

        // 当てる先が効かない種類では、選ばせない
        ScopeCombo.IsEnabled = kind is RuleKind.RequiredEntry or RuleKind.RequiredFolder
            or RuleKind.NamePattern;

        var rule = Compose();
        OkButton.IsEnabled = rule is not null;

        if (rule is null)
        {
            VerdictText.Text = ValueBox.Text.Trim().Length == 0
                ? Strings.RuleNeedsValue
                : Strings.RuleBadPattern;
            VerdictText.Foreground = SystemColors.GrayTextBrush;
            return;
        }

        var verdict = RuleRow.Judge(rule, _sample);
        VerdictText.Text = Strings.RuleHereIs(verdict);
        VerdictText.Foreground = verdict == Strings.RuleHolds
            ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))
            : SystemColors.GrayTextBrush;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (Compose() is not { } rule)
        {
            return;
        }

        Rule = rule;
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
