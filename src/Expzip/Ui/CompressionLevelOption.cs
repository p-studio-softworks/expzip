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

    /// <summary>
    /// 選べる圧縮方式。
    /// </summary>
    /// <remarks>
    /// <b>2つだけにしてある</b> (#38)。書き換えに使う SharpZipLib は deflate の段階を
    /// 渡せず、そもそも段階を上げてもほとんど縮まないため。8MB の検体での実測は
    /// 「高速」146KB / 「標準」72.0KB / 「最大圧縮」72.2KB で、**最大圧縮は標準と
    /// ほぼ同じ**だった。使い分けられない選択肢を並べても迷わせるだけになる。
    /// </remarks>
    public static IReadOnlyList<CompressionLevelOption> All { get; } =
    [
        new(CompressionLevel.NoCompression),
        new(CompressionLevel.Optimal),
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
