using Expzip.Localization;

namespace Expzip.Configuration;

/// <summary>
/// 設定ファイルに保存する内容。
/// ポータブル運用のため exe と同じフォルダに置く (docs/SPEC.md 8章)。
/// 利用者が直接開いて編集することも想定し、人が読める形で書き出す。
/// </summary>
internal sealed class AppSettings
{
    /// <summary>ウィンドウの位置と大きさ。未保存なら null。</summary>
    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public double? WindowWidth { get; set; }

    public double? WindowHeight { get; set; }

    /// <summary>最大化された状態で終了したか。</summary>
    public bool WindowMaximized { get; set; }

    /// <summary>左のツリーペインの幅。</summary>
    public double? TreePaneWidth { get; set; }

    /// <summary>一覧の各列の幅。列を増減した場合に備え、数が合わなければ無視する。</summary>
    public double[]? ColumnWidths { get; set; }

    /// <summary>最近開いた書庫のパス。新しいものが先頭 (#10)。</summary>
    public List<string> RecentArchives { get; set; } = [];

    /// <summary>
    /// ファイルを追加するときの圧縮方式 (#11)。
    /// NoCompression / Fastest / Optimal / SmallestSize のいずれか。
    /// 数値ではなく名前で持つのは、利用者が設定ファイルを開いたときに
    /// 何を指しているか分かるようにするため。
    /// </summary>
    public string CompressionLevel { get; set; } = "Optimal";

    /// <summary>
    /// 画面の言語 (#23)。<c>auto</c> / <c>ja</c> / <c>en</c>。
    /// 既定の <c>auto</c> は Windows の表示言語に合わせる。
    /// </summary>
    public string Language { get; set; } = Strings.AutoPreference;
}
