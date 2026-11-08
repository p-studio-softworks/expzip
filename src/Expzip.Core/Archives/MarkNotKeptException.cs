namespace Expzip.Archives;

/// <summary>
/// 置き換える前のファイルに付ける MotW を付けられなかったので、置き換えなかった (#350)。
/// </summary>
/// <remarks>
/// MotW を保存できる場所で MotW が消えると、そのあと取り出したファイルを開いても Windows の警告が出ない。
/// 元のファイルは中身も MotW もそのまま残る。
/// </remarks>
internal sealed class MarkNotKeptException(string path)
    : IOException($"The Mark of the Web could not be kept on '{path}'.");
