using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Expzip.Ai;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 決まりに合わせて直す (#28、仕様書 11.3節の5)。
/// </summary>
/// <remarks>
/// <para>
/// **既定ではどれも選ばれていない。**取り消せない操作なので、選ぶのは人の仕事に
/// する。押したら全部消えた、が起きないようにする (仕様書 11.4節、無断上書きはしない)。
/// </para>
/// <para>
/// **付け直す名前は、打つそばから確かめる。**決まりの形に合っているか、別の決まりを
/// 破らないか、同じ名前のものが既にないか。適用してから気付くのでは遅い。
/// </para>
/// <para>
/// **名前の案は AI に出させるが、そのまま入れない** (<see cref="RuleNamer"/>)。
/// 確かめて通ったものだけを入れ、通らなかった数はその場に出す。
/// </para>
/// </remarks>
public partial class RuleFixDialog : Window
{
    private readonly AiOptions _options;
    private readonly ArchiveContents _contents;
    private readonly RuleAudit _audit;
    private readonly ObservableCollection<RuleFixRow> _rows = [];

    private CancellationTokenSource? _asking;

    internal RuleFixDialog(
        Window owner, AiOptions options, RuleAudit audit, ArchiveContents contents)
    {
        InitializeComponent();
        Owner = owner;

        _options = options;
        _audit = audit;
        _contents = contents;

        foreach (var fix in RuleFixer.Propose(audit, contents))
        {
            var row = new RuleFixRow(fix, audit, contents);
            row.PropertyChanged += Row_PropertyChanged;
            _rows.Add(row);
        }

        FixList.ItemsSource = _rows;

        ApplyLanguage();
        ShowCount();

        Closing += (_, _) => _asking?.Cancel();
    }

    /// <summary>行うことにした直し。</summary>
    internal IReadOnlyList<RuleFix> Accepted
        => [.. _rows.Where(static row => row.Chosen && row.Applicable)
            .Select(static row => row.Fix)];

    private void ApplyLanguage()
    {
        Title = Strings.RuleFixTitle(Path.GetFileName(_contents.FilePath));
        IntroText.Text = Strings.RuleFixIntro;
        ReadOnlyText.Text = Strings.RuleFixReadOnly(Strings.FormatName(_contents.Format));
        ReadOnlyText.Visibility = _contents.IsEditable
            ? Visibility.Collapsed
            : Visibility.Visible;
        AllButton.Content = Strings.RuleFixAll;
        NoneButton.Content = Strings.RuleFixNone;
        NameButton.Content = Strings.RuleFixAskNames;
        ApplyButton.Content = Strings.RuleFixApply;
        CloseButton.Content = Strings.RuleClose;
        UseColumn.Header = Strings.RuleColumnUse;
        KindColumn.Header = Strings.RuleFixColumnKind;
        TargetColumn.Header = Strings.RuleAuditColumnTarget;
        NameColumn.Header = Strings.RuleFixColumnNewName;
        ReasonColumn.Header = Strings.RuleAuditColumnRule;
        VerdictColumn.Header = Strings.RuleFixColumnVerdict;

        // AI は名前の案にしか使わない。繋いでいなくても、取り除くほうは直せる
        NameButton.IsEnabled = _options.IsConfigured
            && _rows.Any(static row => row.CanRename);
    }

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e) => ShowCount();

    /// <summary>いくつ直すことになっているかを出す。</summary>
    private void ShowCount()
    {
        var chosen = Accepted;
        var removing = chosen.Count(static fix => fix.Kind == RuleFixKind.Remove);
        var renaming = chosen.Count - removing;

        CountText.Text = chosen.Count == 0
            ? Strings.RuleFixNothingChosen
            : Strings.RuleFixChosen(removing, renaming);

        ApplyButton.IsEnabled = chosen.Count > 0 && _contents.IsEditable;
    }

    private void AllButton_Click(object sender, RoutedEventArgs e) => ChooseAll(true);

    private void NoneButton_Click(object sender, RoutedEventArgs e) => ChooseAll(false);

    private void ChooseAll(bool chosen)
    {
        foreach (var row in _rows)
        {
            row.Chosen = chosen;
        }

        ShowCount();
    }

    /// <summary>付け直す名前の案を AI に出してもらう。</summary>
    private async void NameButton_Click(object sender, RoutedEventArgs e)
    {
        if (_asking is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _asking = cancellation;
        NameButton.IsEnabled = false;
        NameResult.Text = Strings.RuleSending;

        NamingResult result;

        try
        {
            result = await RuleNamer.SuggestAsync(
                _options, [.. _rows.Select(static row => row.Fix)], _audit, _contents,
                cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            _asking = null;
            NameButton.IsEnabled = _options.IsConfigured
                && _rows.Any(static row => row.CanRename);
        }

        NameResult.Text = result.Ok
            ? Strings.RuleFixNamed(result.Filled, result.Refused)
            : result.Message;

        // 入った名前を一覧に映す
        foreach (var row in _rows)
        {
            row.NewName = row.Fix.NewName;
        }

        ShowCount();
    }

    /// <summary>直す前に、何をするのかを並べて確かめる。</summary>
    private void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var chosen = Accepted;

        if (chosen.Count == 0)
        {
            return;
        }

        var removing = chosen.Where(static fix => fix.Kind == RuleFixKind.Remove).ToList();
        var renaming = chosen.Where(static fix => fix.Kind == RuleFixKind.Rename).ToList();

        var lines = new List<string>();
        lines.AddRange(removing.Take(5).Select(static fix => "  - " + fix.Path));
        lines.AddRange(renaming.Take(5).Select(
            static fix => $"  - {fix.Path} → {fix.NewName}"));

        var more = chosen.Count > lines.Count ? Strings.More(chosen.Count - lines.Count) : string.Empty;

        var answer = MessageBox.Show(
            this,
            Strings.RuleFixConfirm(
                removing.Count, renaming.Count,
                string.Join(Environment.NewLine, lines), more),
            "Expzip",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        DialogResult = true;
        Close();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
