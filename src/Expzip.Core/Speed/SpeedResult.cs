using System.Text.Json.Serialization;

namespace Expzip.Speed;

/// <summary>試すデータの種類 (#269)。時間は大きさだけでは決まらないので、種類ごとに測る。</summary>
/// <remarks>設定ファイルには名前で書く。開いた人が何を指しているか分かるように。</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<SpeedKind>))]
internal enum SpeedKind
{
    /// <summary>文書のように縮むもの。圧縮に CPU を使う。</summary>
    Documents,

    /// <summary>写真や動画のように既に圧縮されたもの。格納するので CPU はほとんど使わない (#38)。</summary>
    Media,

    /// <summary>小さなファイルがたくさん。1 個ごとの手間が効く (#200)。</summary>
    SmallFiles,
}

/// <summary>保存先のディスクの種類 (#269)。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<StorageKind>))]
internal enum StorageKind
{
    Unknown,

    Ssd,

    Hdd,

    Network,
}

/// <summary>見積もった時間の評価 (#269)。</summary>
internal enum SpeedRating
{
    /// <summary>1 分未満。</summary>
    Comfortable,

    /// <summary>1 分以上 10 分未満。</summary>
    SomewhatSlow,

    /// <summary>10 分以上。</summary>
    Slow,
}

/// <summary>見積もった時間を評価する (#269)。</summary>
internal static class SpeedRatings
{
    /// <summary>これより短ければ快適。</summary>
    public static readonly TimeSpan ComfortableLimit = TimeSpan.FromMinutes(1);

    /// <summary>これより短ければやや遅い。これ以上は遅い。</summary>
    public static readonly TimeSpan SlowLimit = TimeSpan.FromMinutes(10);

    /// <remarks>
    /// 表示と同じく秒に丸めてから比べる。丸めないと 59.6 秒が「1 分」と出ながら快適になる。
    /// </remarks>
    public static SpeedRating Of(TimeSpan time)
    {
        var seconds = Math.Round(time.TotalSeconds);
        return seconds < ComfortableLimit.TotalSeconds ? SpeedRating.Comfortable
            : seconds < SlowLimit.TotalSeconds ? SpeedRating.SomewhatSlow
            : SpeedRating.Slow;
    }
}

/// <summary>総合スコアの基準 (#272)。</summary>
/// <remarks>
/// <para>
/// 基準のパソコンは Core i5-9500T (6 コア、2019 年の事務用の小型パソコン)、メモリー 16 GB、
/// SSD (読み込み 2,396 MB/秒、書き込み 1,202 MB/秒)、AC 電源。2026-10-17 に一時フォルダーで測った。
/// ふつうのパソコンが 1,000 前後になるように、速すぎないものを選んだ。
/// </para>
/// <para>
/// <b>一度決めたら変えない。</b>変えると、前に出した点と比べられなくなる。
/// </para>
/// </remarks>
internal static class SpeedScore
{
    /// <summary>基準のパソコンの点。</summary>
    public const double BaselineScore = 1000;

    /// <summary>比べる大きさ (圧縮する前)。</summary>
    public const long Bytes = 10L << 30;

    /// <summary>基準のパソコンで <see cref="Bytes"/> を圧縮・展開する時間の見積もり (秒)。</summary>
    public static readonly (SpeedKind Kind, double Compress, double Extract)[] Baseline =
    [
        (SpeedKind.Documents, 46.6, 19.4),
        (SpeedKind.Media, 18.4, 12.8),
        (SpeedKind.SmallFiles, 414, 972),
    ];
}

/// <summary>1 種類のデータを試した結果。</summary>
/// <remarks>設定ファイルに残すので、読み書きできる形にしてある。</remarks>
internal sealed class SpeedKindResult
{
    public SpeedKind Kind { get; set; }

    /// <summary>試したデータの大きさ (圧縮する前)。</summary>
    public long ContentBytes { get; set; }

    /// <summary>試したデータのファイルの数。</summary>
    public int FileCount { get; set; }

    /// <summary>できた書庫の大きさ。</summary>
    public long ArchiveBytes { get; set; }

    /// <summary>圧縮にかかった秒数。</summary>
    public double CompressSeconds { get; set; }

    /// <summary>展開にかかった秒数。</summary>
    public double ExtractSeconds { get; set; }
}

/// <summary>
/// このパソコンで測った結果 (#269)。ここから書庫の大きさごとの時間を見積もる。
/// </summary>
/// <remarks>
/// 見積もりではなく測った値を残す。見積もる大きさや言葉を変えても、測り直さずに済む。
/// </remarks>
internal sealed class SpeedResult
{
    public DateTime MeasuredAt { get; set; }

    public string MachineName { get; set; } = string.Empty;

    /// <summary>CPU の名前。分からなければ空。</summary>
    public string Cpu { get; set; } = string.Empty;

    /// <summary>論理コアの数。圧縮と展開はこの数だけ並べる (#195)。</summary>
    public int Cores { get; set; }

    /// <summary>メモリーの大きさ。分からなければ 0。</summary>
    public long MemoryBytes { get; set; }

    /// <summary>測った場所。一時フォルダーなら空。</summary>
    public string Location { get; set; } = string.Empty;

    public StorageKind Storage { get; set; }

    /// <summary>バッテリーで動いていたか。分からなければ <see langword="null"/>。</summary>
    public bool? OnBattery { get; set; }

    /// <summary>保存先に読み書きする速さ (バイト / 秒)。</summary>
    public double DiskReadBytesPerSecond { get; set; }

    public double DiskWriteBytesPerSecond { get; set; }

    public List<SpeedKindResult> Kinds { get; set; } = [];

    /// <summary>
    /// 中身が <paramref name="contentBytes"/> の書庫を圧縮・展開する時間を見積もる。
    /// 測っていない種類なら <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 試しの量は小さいので、書き込みは Windows がメモリーに溜めたまま終わることがある。
    /// 大きな書庫ではディスクに書き終えるのを待つことになるため、
    /// <b>試した速さと、ディスクを読み書きするだけの時間の、遅いほう</b>を見積もりにする。
    /// </para>
    /// <para>
    /// 圧縮は中身を読んで書庫を書き、展開は書庫を読んで中身を書く。
    /// 元のファイルと書庫は同じディスクにあるとみなす。
    /// </para>
    /// </remarks>
    public (TimeSpan Compress, TimeSpan Extract)? Estimate(SpeedKind kind, long contentBytes)
    {
        var measured = Kinds.FirstOrDefault(k => k.Kind == kind);
        if (measured is null || measured.ContentBytes <= 0)
        {
            return null;
        }

        var scale = (double)contentBytes / measured.ContentBytes;
        var archiveBytes = contentBytes * ((double)measured.ArchiveBytes / measured.ContentBytes);

        var compress = Math.Max(
            measured.CompressSeconds * scale,
            DiskSeconds(read: contentBytes, written: archiveBytes));
        var extract = Math.Max(
            measured.ExtractSeconds * scale,
            DiskSeconds(read: archiveBytes, written: contentBytes));

        return (TimeSpan.FromSeconds(compress), TimeSpan.FromSeconds(extract));
    }

    /// <summary>
    /// 総合スコア (#272)。基準のパソコンを 1,000 とし、その何倍速いかを表す。
    /// 測っていない種類があれば <see langword="null"/>。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 6 つの見積もり (3 種類 × 圧縮・展開) それぞれで「基準の時間 ÷ この時間」を求め、
    /// その<b>幾何平均</b>に 1,000 を掛ける。単純な平均だと、1 つの項目だけ速いパソコンが高い点になる。
    /// </para>
    /// <para>
    /// 見積もりを使うので、ディスクの速さも入る。見積もりは大きさに比例するので、どの大きさで比べても同じ。
    /// 測るたびに 1 割ほど揺れるので、10 の位で丸める。
    /// </para>
    /// </remarks>
    public int? Score()
    {
        var logSum = 0.0;
        foreach (var (kind, compress, extract) in SpeedScore.Baseline)
        {
            if (Estimate(kind, SpeedScore.Bytes) is not { } time
                || time.Compress <= TimeSpan.Zero || time.Extract <= TimeSpan.Zero)
            {
                return null;
            }

            logSum += Math.Log(compress / time.Compress.TotalSeconds)
                      + Math.Log(extract / time.Extract.TotalSeconds);
        }

        var score = SpeedScore.BaselineScore * Math.Exp(logSum / (SpeedScore.Baseline.Length * 2));
        return Math.Max(10, (int)Math.Round(score / 10, MidpointRounding.AwayFromZero) * 10);
    }

    /// <summary>ディスクを読み書きするだけにかかる秒数。速さを測れていなければ 0。</summary>
    private double DiskSeconds(double read, double written)
        => (DiskReadBytesPerSecond > 0 ? read / DiskReadBytesPerSecond : 0)
           + (DiskWriteBytesPerSecond > 0 ? written / DiskWriteBytesPerSecond : 0);
}
