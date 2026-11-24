using System.Text.Json;
using System.Text.Json.Serialization;
using Expzip.Localization;
using Expzip.Speed;

namespace Expzip.Configuration;

/// <summary>
/// 設定ファイルに保存する内容。
/// ポータブル運用のため exe と同じフォルダに置く (docs/SPEC.md 7章)。
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
    /// <summary>
    /// 画面の言語 (#23)。<c>auto</c> / <c>ja</c> / <c>en</c>。
    /// デフォルトの <c>auto</c> は Windows の表示言語に合わせる。
    /// </summary>
    public string Language { get; set; } = Strings.AutoPreference;

    /// <summary>
    /// 前回の分割サイズ (バイト、#59)。
    /// </summary>
    /// <remarks>
    /// 同じ人は同じ大きさで分けることが多い。デフォルトは 100MB とする。
    /// </remarks>
    public long SplitChunkSize { get; set; } = 100L * 1024 * 1024;

    /// <summary>分割で結合用のバッチファイルも作るか (#343)。デフォルトは作らない。</summary>
    public bool SplitMakeBatch { get; set; }

    /// <summary>AI の API (#24)。OpenAI 互換の場所を入れる (#35)。</summary>
    public string AiEndpoint { get; set; } = string.Empty;

    /// <summary>使うモデルの名前 (#24)。</summary>
    public string AiModel { get; set; } = string.Empty;

    /// <summary>
    /// APIキー。**そのままではなく、その利用者だけが戻せる形で持つ** (#24)。
    /// </summary>
    /// <remarks>
    /// この設定ファイルは exe と同じフォルダに、人が読める形で置いてある。
    /// 鍵をそのまま書くと、ファイルを手に入れた人が誰でも使えてしまう。
    /// 別の PC や別の利用者では戻せない。読めなかったときは入れ直してもらう。
    /// </remarks>
    public string AiApiKeyProtected { get; set; } = string.Empty;

    /// <summary>
    /// 前回、処理時間の見積もりのために測った結果 (#269)。測っていなければ null。
    /// </summary>
    /// <remarks>次に開いたときにも前の結果を見られるように残す。</remarks>
    public SpeedResult? SpeedResult { get; set; }

    /// <summary>
    /// このバージョンが知らない項目 (#409)。新しいバージョンの Expzip が足したものなど。無ければ null。
    /// </summary>
    /// <remarks>
    /// 読んだまま持っておき、保存のときにそのまま書き戻す。持たないと、古いバージョンで保存したときに消える。
    /// 中身は使わない。書き戻せないものと、知っている項目と大文字・小文字だけ違う名前のものは、
    /// 読んだときに捨てる (<see cref="SettingsStore"/> の <c>TidyUnknown</c>)。
    /// </remarks>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }

    /// <summary>
    /// 設定ファイルがあったのに読めず、既定の値で作ったもの (#399)。読めていれば null。設定ファイルには書かない。
    /// </summary>
    /// <remarks>
    /// これが付いた設定は保存しない (<see cref="SettingsStore.TrySave(AppSettings)"/>)。
    /// 保存すると、読めなかった設定ファイルが既定の値で上書きされる。
    /// <see cref="LoadProblem"/> を読んだ設定そのものに持たせ、別の場所の状態に頼らない。
    /// </remarks>
    [JsonIgnore]
    internal SettingsLoadProblem? LoadProblem { get; set; }
}

/// <summary>設定ファイルを読めなかったこと (#399)。</summary>
/// <param name="FilePath">読めなかった設定ファイル。</param>
/// <param name="KeptPath">中身を写して残したファイル。残せなかった、または中身を読めなかったなら null。</param>
/// <param name="Failure">読めなかった理由。知らせる文を分ける。</param>
internal sealed record SettingsLoadProblem(string FilePath, string? KeptPath, SettingsLoadFailure Failure);

/// <summary>設定ファイルを読めなかった理由 (#399)。</summary>
/// <remarks>
/// 中身を開けなかったときは、中身は無事かもしれない。「削除すると」と勧めないよう、壊れているときと分ける。
/// </remarks>
internal enum SettingsLoadFailure
{
    /// <summary>中身が設定として読めない (壊れている、大きすぎる)。</summary>
    Damaged,

    /// <summary>ほかのプログラムが開いていて読めない。閉じれば次の起動で読める。</summary>
    InUse,

    /// <summary>アクセス権が無い、同じ名前のフォルダーがある、など。起動し直しても読めない。</summary>
    Inaccessible,
}
