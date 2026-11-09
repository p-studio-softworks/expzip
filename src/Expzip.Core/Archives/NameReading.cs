using System.Text;

namespace Expzip.Archives;

/// <summary>
/// 書庫の中の名前の読み方 (#335)。いつもの読み方 (<see cref="Default"/>) か、アプリを使う人が選び直した文字コード。
/// </summary>
/// <remarks>
/// <para>
/// 読み方は、その書庫を読む処理に、引数で毎回渡す。共通の値を切り替える作りにすると、
/// 切り替えている間に裏で動く書き換えが、選び直した読み方で書庫の名前を書き直したり、
/// 切り替え忘れの操作が一覧と違う名前で展開したりする (#335 の設計の確認)。
/// 書庫を書き換える処理は、いつもの読み方しか使わない。選び直した書庫は書き換えない (読み取り専用)。
/// </para>
/// <para>
/// ZIP で UTF-8 の印 (EFS フラグ) が立っている名前は、どちらの読み方でも UTF-8 で読む
/// (<see cref="System.IO.Compression.ZipArchive"/> は印の立たない名前にだけ、渡した読み方を使う)。
/// </para>
/// </remarks>
internal sealed class NameReading
{
    private const int LegacyJapaneseCodePage = 932;

    private NameReading(Encoding encoding, int? codePage)
    {
        Encoding = encoding;
        CodePage = codePage;
    }

    /// <summary>
    /// いつもの読み方。UTF-8 として正しければ UTF-8、そうでなければ CP932 (#13)。
    /// CP932 を使えない環境では UTF-8 だけ。
    /// </summary>
    /// <remarks>CP932 は起動時に登録してから使えるので、初めて使うときに作る。</remarks>
    public static NameReading Default => LazyDefault.Value;

    private static readonly Lazy<NameReading> LazyDefault = new(static () => new(new ArchiveEntryNameEncoding(Legacy()), null));

    /// <summary>名前を読む <see cref="System.Text.Encoding"/>。</summary>
    public Encoding Encoding { get; }

    /// <summary>選び直した文字コードの番号。いつもの読み方なら <see langword="null"/>。</summary>
    public int? CodePage { get; }

    /// <summary>いつもの読み方か。</summary>
    public bool IsDefault => CodePage is null;

    /// <summary>選び直した文字コードで読む。読めないバイトは置き換え文字 (U+FFFD) にする。</summary>
    public static NameReading Chosen(int codePage)
        => new(Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, DecoderFallback.ReplacementFallback), codePage);

    private static Encoding Legacy()
    {
        try
        {
            return Encoding.GetEncoding(LegacyJapaneseCodePage);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            // プロバイダーが登録されていない環境ではUTF-8のみで動作させる。
            // 古い書庫のファイル名は化けるが、書庫自体は開ける。
            return Encoding.UTF8;
        }
    }
}
