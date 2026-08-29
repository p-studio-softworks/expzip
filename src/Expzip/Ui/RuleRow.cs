using System.ComponentModel;
using Expzip.Ai;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 決まりの一覧に並べる1行 (#26)。
/// </summary>
/// <remarks>
/// <para>
/// 決まりそのもの (<see cref="ArchiveRule"/>) は動かさない。使うかどうかと、
/// **いま開いている書庫に当てるとどうなるか**を、この行が持つ。
/// </para>
/// <para>
/// **当てた結果をその場に出すのが、この画面の要**になる。人が値を直したときに、
/// その直しでお手本の何件が外れるようになったかがすぐ分かる。分からないまま
/// 直させると、直したつもりで壊すことになる。
/// </para>
/// </remarks>
internal sealed class RuleRow : INotifyPropertyChanged
{
    private readonly ArchiveContents _sample;

    private bool _enabled;

    public RuleRow(ArchiveRule rule, bool enabled, ArchiveContents sample)
    {
        Rule = rule;
        _enabled = enabled;
        _sample = sample;
        Verdict = Judge(rule, sample);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>この行の決まり。</summary>
    public ArchiveRule Rule { get; private set; }

    /// <summary>この決まりを使うか。外したものは保存しても当てない。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Notify(nameof(Enabled));
        }
    }

    /// <summary>いま開いている書庫に当てた結果。</summary>
    public string Verdict { get; private set; }

    public string KindText => Rule.KindText;

    public string ScopeText => Rule.ScopeText;

    public string Value => Rule.Value;

    public string Description => Rule.Description;

    public string Evidence => Rule.Evidence;

    public string SourceText => Rule.SourceText;

    /// <summary>決まりを差し替える。当てた結果も引き直す。</summary>
    public void Replace(ArchiveRule rule)
    {
        Rule = rule;
        Verdict = Judge(rule, _sample);

        foreach (var name in (string[])
            [nameof(KindText), nameof(ScopeText), nameof(Value), nameof(Description),
                nameof(Evidence), nameof(SourceText), nameof(Verdict)])
        {
            Notify(name);
        }
    }

    /// <summary>この書庫に当てるとどうなるかを、一言で書く。</summary>
    public static string Judge(ArchiveRule rule, ArchiveContents contents)
    {
        var result = RuleChecker.Check(rule, contents);

        if (result.Applied == 0)
        {
            return Strings.RuleNothingToCheck;
        }

        if (result.Satisfied)
        {
            return Strings.RuleHolds;
        }

        return result.Violations.Count == 0
            ? Strings.RuleMissing
            : Strings.RuleBreaks(result.Violations.Count);
    }

    private void Notify(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
