using System.ComponentModel;
using Expzip.Ai;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>直し方の一覧に並べる1行 (#28)。</summary>
/// <remarks>
/// **付け直す名前は、打つそばから確かめる。**決まりの形に合っているか、
/// 別の決まりを破らないか、同じ名前のものが既にないかを、その場で見せる
/// (#26 と同じ考え方)。適用してから間違いに気付くのでは遅い。
/// </remarks>
internal sealed class RuleFixRow : INotifyPropertyChanged
{
    private readonly RuleAudit _audit;
    private readonly ArchiveContents _contents;

    public RuleFixRow(RuleFix fix, RuleAudit audit, ArchiveContents contents)
    {
        Fix = fix;
        _audit = audit;
        _contents = contents;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>この行の直し方。</summary>
    public RuleFix Fix { get; }

    /// <summary>この直しを行うか。</summary>
    public bool Chosen
    {
        get => Fix.Chosen;
        set
        {
            Fix.Chosen = value && Applicable;
            Notify(nameof(Chosen));
        }
    }

    /// <summary>付け直す名前。</summary>
    public string NewName
    {
        get => Fix.NewName;
        set
        {
            Fix.NewName = value;

            // 名前が変われば、直るかどうかも変わる
            Notify(nameof(NewName));
            Notify(nameof(Verdict));
            Notify(nameof(Applicable));
            Notify(nameof(CanChoose));

            if (!Applicable && Fix.Chosen)
            {
                Chosen = false;
            }
        }
    }

    public string KindText => Fix.KindText;

    public string Target => Fix.Path.Length == 0 ? Fix.Rules[0].Value : Fix.Path;

    public string Reason => Fix.Reason;

    /// <summary>名前を打ち込める行かどうか。</summary>
    public bool CanRename => Fix.Kind == RuleFixKind.Rename;

    /// <summary>いま選べる行かどうか。</summary>
    public bool CanChoose => Applicable;

    /// <summary>この直しを実際に行えるか。</summary>
    public bool Applicable => Fix.Kind switch
    {
        RuleFixKind.Remove => true,
        RuleFixKind.Rename => RuleFixer.WhyNot(Fix, Fix.NewName, _audit, _contents) is null,
        _ => false,
    };

    /// <summary>直すとどうなるか。打つそばから引き直す。</summary>
    public string Verdict => Fix.Kind switch
    {
        RuleFixKind.Remove => Strings.RuleFixWillRemove,
        RuleFixKind.Manual => Strings.RuleFixByHand,
        _ => RuleFixer.WhyNot(Fix, Fix.NewName, _audit, _contents) ?? Strings.RuleFixWillFix,
    };

    /// <summary>言語が変わったことを行に伝える (#23)。</summary>
    public void NotifyLanguageChanged()
    {
        foreach (var name in (string[])[nameof(KindText), nameof(Reason), nameof(Verdict)])
        {
            Notify(name);
        }
    }

    private void Notify(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
