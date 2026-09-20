namespace Expzip.Archives;

/// <summary>
/// 書庫の中の書庫を、専用のタブで開いている間の状態 (#30)。
/// </summary>
/// <remarks>
/// <para>
/// 仕組みは「書庫内の1ファイルを外のアプリで開く」(#16) とまったく同じ。中の書庫を
/// 一時ファイルへ取り出し、それが書き換わったら親書庫へ戻す。開く相手が外のアプリ
/// ではなく Expzip 自身であるだけの違いなので、<see cref="EditSession"/> を土台にする。
/// </para>
/// <para>
/// それでも別の型にしてあるのは、尋ねる時機が違うため。外のアプリで開いたファイルは
/// 保存されるたびに尋ねてよいが、ネスト書庫で同じことをすると、1件足すたびに
/// 尋ねることになる。タブを閉じたときと、明示的な保存操作のときに尋ねる (仕様書 4.3節)。
/// </para>
/// </remarks>
internal sealed class NestSession(
    string parentPath, ArchiveEntry entry, string tempPath, string destinationFolder)
    : EditSession(parentPath, entry, tempPath, destinationFolder)
{
    /// <summary>親書庫のタブが先に閉じられたか (#30)。</summary>
    /// <remarks>
    /// こうなると親書庫への上書き保存はできない。開いているタブが無いと、
    /// パスワードが要るかどうかも分からないまま書き込むことになるため。
    /// 以降は「名前を付けて保存」だけを出し、タブを閉じるときも終了するときも
    /// 親への反映は尋ねない (仕様書 4.3節)。
    /// </remarks>
    public bool Orphaned { get; private set; }

    /// <summary>親書庫のタブが閉じられたことを記録する。</summary>
    public void Orphan() => Orphaned = true;
}
