using System.ComponentModel;
using System.IO;
using Expzip.Ai;
using Expzip.Archives;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 1つの書庫を開いているタブ (#22)。
/// </summary>
/// <remarks>
/// ツリーと一覧の部品はタブごとに作らず、1組を使い回して中身を差し替える。
/// タブごとに持つのは「どの書庫の、どこを、どの順で見ているか」だけ。
/// 部品を複製すると、書庫の数だけ仮想化されていない要素が積み上がる。
/// </remarks>
internal sealed class ArchiveTab(ArchiveContents contents) : INotifyPropertyChanged
{
    private ArchiveContents _contents = contents;

    /// <summary>このタブが開いている書庫。</summary>
    public ArchiveContents Contents
    {
        get => _contents;
        set
        {
            _contents = value;

            // 中身が変われば、当てた結果はもう当てにならない (#27)
            Audit = null;
            AuditDone = false;

            Notify(nameof(Contents));
            Notify(nameof(Title));
            Notify(nameof(FilePath));
        }
    }

    /// <summary>
    /// 保存した決まりをこの書庫に当てた結果 (#27)。決まりが無ければ
    /// <see langword="null"/>。
    /// </summary>
    public RuleAudit? Audit { get; set; }

    /// <summary>もう当ててあるか。決まりが無くて <c>null</c> の場合と区別する。</summary>
    public bool AuditDone { get; set; }

    /// <summary>
    /// 利用者が、このタブで当てることを求めたか (#91)。
    /// </summary>
    /// <remarks>
    /// **開いただけでは当てない。**書庫を開く理由は中を見ることで、いつも
    /// ルールを気にしているわけではない。求められてから当てる。
    /// <para>
    /// 一度求められたら、そのタブでは覚えておく。読み直しのたびに求め直させると、
    /// 直したかどうかを確かめる流れ (#87) が途切れる。
    /// </para>
    /// </remarks>
    public bool AuditWanted { get; set; }

    /// <summary>
    /// ツリーに印を出す項目 (#88)。<see langword="null"/> なら合っていないもの全部。
    /// </summary>
    /// <remarks>
    /// 結果のウィンドウで「直す」に印を付けて閉じると、**その項目だけ**が残る。
    /// 全部に印が出たままでは、どれを自分が引き受けたのかが見えない。
    /// </remarks>
    public HashSet<string>? RuleMarks { get; set; }

    /// <summary>一覧に出している書庫内フォルダ。</summary>
    public ArchiveFolder CurrentFolder { get; set; } = contents.Root;

    /// <summary>並び順。タブごとに覚える (仕様書 4.2)。</summary>
    public EntryColumn SortColumn { get; set; } = EntryColumn.Name;

    /// <summary>並び順が降順かどうか。</summary>
    public bool SortDescending { get; set; }

    /// <summary>
    /// 一覧で選んでいた項目の名前。タブを離れるときに控え、戻ったら選び直す (仕様書 4.2)。
    /// </summary>
    public IReadOnlyList<string> SelectedNames { get; set; } = [];

    /// <summary>タブに出す名前。</summary>
    public string Title => Path.GetFileName(_contents.FilePath);

    /// <summary>書庫ファイルのパス。同じ書庫を二重に開かないための照合に使う。</summary>
    public string FilePath => _contents.FilePath;

    /// <summary>
    /// 親書庫の中の書庫を開いているタブなら、その繋がり (#30)。
    /// ふつうに開いたタブでは <see langword="null"/>。
    /// </summary>
    public NestSession? Nest { get; init; }

    // -------------------------------------------------------------- 外での書き換えを見つける (#64)

    /// <summary>読み込んだ時点の書庫ファイルの状態。</summary>
    private FileStamp _read;

    /// <summary>前回の巡回で見たファイルの状態。書き込みの途中かどうかの判断に使う。</summary>
    private FileStamp _seen;

    /// <summary>
    /// 外で書き換わったが、まだ読み直していないかどうか。
    /// 見ていないタブの書庫が変わった場合に立て、そのタブへ戻ったときに読み直す。
    /// </summary>
    public bool NeedsReload { get; set; }

    /// <summary>
    /// 読み直しの理由が「中の書庫を書き戻したから」かどうか (#30)。
    /// 外で書き換えられた場合と案内を分けるために覚える。自分で書いたものを
    /// 「外で書き換えられました」と知らせるのは嘘になる。
    /// </summary>
    public bool ReloadFromNest { get; set; }

    /// <summary>いま画面に出している中身が、どの状態のファイルから来たかを控える。</summary>
    /// <param name="stamp">読み込みを始める直前に読んだファイルの状態。</param>
    public void MarkRead(FileStamp stamp)
    {
        _read = stamp;
        _seen = stamp;
        NeedsReload = false;
    }

    /// <summary>
    /// 見つけた書き換えに対して読み直しを試みたことを記録する。
    /// 読み直しに失敗しても、同じ書き換えで何度も試さないようにするため。
    /// </summary>
    public void MarkAttempted() => _read = _seen;

    /// <summary>書庫ファイルが外で書き換えられ、書き込みが落ち着いたなら true。</summary>
    /// <param name="current">いまのファイルの状態。読めなかった場合はデフォルト値。</param>
    /// <remarks>
    /// <para>
    /// 変化を見つけてすぐに読み直すと、書き込みの途中の書庫を読んでしまう。
    /// 大きな書庫の書き換えは数秒かかり、その間は壊れた書庫にしか見えない。
    /// 2回続けて同じ状態が見えたときだけ「落ち着いた」とみなす。
    /// </para>
    /// <para>
    /// ファイルを読めない場合 (消えている、掴めない) は何もしない。今出している
    /// 中身をそのまま残すほうが、空の画面に切り替えるより手掛かりが多い。
    /// </para>
    /// <para>
    /// ファイルを見に行くのは呼ぶ側の仕事にしてある。ネットワーク上の書庫では
    /// 状態を1つ読むだけでも待たされることがあり、画面を動かす筋で読むと
    /// そのたびにウィンドウが固まるため (#39, #64)。
    /// </para>
    /// </remarks>
    public bool DetectExternalChange(FileStamp current)
    {
        if (current == default || current == _read)
        {
            _seen = current;
            return false;
        }

        if (current != _seen)
        {
            _seen = current;
            return false;
        }

        return true;
    }

    /// <summary>閉じるボタンの説明。見出しの中にあるため、束縛で言語を切り替える (#23)。</summary>
    public string CloseTooltip => Strings.CloseTabTooltip;

    /// <summary>言語が変わったことを見出しに伝える (#23)。</summary>
    public void NotifyLanguageChanged() => Notify(nameof(CloseTooltip));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify(string property)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
