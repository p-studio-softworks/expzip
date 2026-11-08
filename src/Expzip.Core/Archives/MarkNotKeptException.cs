namespace Expzip.Archives;

/// <summary>
/// 置き換える前のファイルに付ける出所の印 (Mark of the Web) を付けられなかったので、置き換えなかった (#350)。
/// </summary>
/// <remarks>
/// 印を保存できる場所で印が消えると、そのあと取り出したファイルを開いても Windows の警告が出ない。
/// 元のファイルは中身も印もそのまま残る。
/// </remarks>
internal sealed class MarkNotKeptException(string path)
    : IOException($"The Mark of the Web could not be kept on '{path}'.");
