using System.IO.Compression;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>圧縮方式の選択肢 (#11)。</summary>
/// <param name="Level">実際に使う圧縮の強さ。</param>
internal sealed record CompressionLevelOption(CompressionLevel Level)
{
    /// <summary>
    /// 画面に出す名前。
    /// 言語を切り替えたら選択肢を作り直す前提で、その時点の文言を返す (#23)。
    /// </summary>
    public string Label => Strings.CompressionLevelLabel(Level);

    /// <summary>選べる圧縮方式。強さの弱い順に並べる。</summary>
    public static IReadOnlyList<CompressionLevelOption> All { get; } =
    [
        new(CompressionLevel.NoCompression),
        new(CompressionLevel.Fastest),
        new(CompressionLevel.Optimal),
        new(CompressionLevel.SmallestSize),
    ];

    /// <summary>既定の圧縮方式。</summary>
    public static CompressionLevel Default => CompressionLevel.Optimal;

    /// <summary>
    /// 設定ファイルの文字列から圧縮方式を求める。
    /// 設定ファイルは利用者が直接編集できるので、知らない値が入っていても
    /// 既定に落として動かす。ここで例外にすると設定全体が使えなくなる。
    /// </summary>
    public static CompressionLevel Parse(string? value)
        => Enum.TryParse<CompressionLevel>(value, ignoreCase: true, out var level)
           && All.Any(option => option.Level == level)
            ? level
            : Default;

    /// <summary>設定ファイルに書き出す文字列。</summary>
    public static string ToSettingValue(CompressionLevel level) => level.ToString();
}
