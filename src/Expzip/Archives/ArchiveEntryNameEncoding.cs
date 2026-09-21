using System.Text;
using System.Text.Unicode;

namespace Expzip.Archives;

/// <summary>
/// EFSフラグ(汎用フラグ bit11)が立たないエントリ名の解釈を担う <see cref="Encoding"/>。
/// UTF-8として厳密に妥当ならそちらを採用し、そうでなければ従来の日本語コードページとみなす。
/// </summary>
/// <remarks>
/// <para>
/// ZIP仕様上、ファイル名のエンコーディングは一意に定まらない。EFSフラグが立つエントリは
/// UTF-8だが、立たないエントリは本来CP437であり、日本語圏の既存ツールはそこにCP932を
/// 書いてきた。<see cref="System.IO.Compression.ZipArchive"/> はフラグが立つエントリを
/// 常にUTF-8として扱い、立たないエントリにのみ entryNameEncoding を適用するため、
/// このクラスを渡すだけで「古い書庫だけを救う」判定を差し込める。
/// </para>
/// <para>
/// 判定はエントリ単位で行われる。同一書庫内でもフラグの有無は混在するため
/// (ASCIIのみのファイル名にはフラグが立たない)、書庫単位の判定にしてはならない。
/// </para>
/// <para>
/// CP932のバイト列が偶然UTF-8としても妥当なら誤判定するが、ひらがな・カタカナ・全角記号・
/// 常用漢字の一部(計483文字)を対象に1文字と2文字の全数および3文字のランダム200万件を
/// 検査したところ衝突は0件だった(#13)。CP932の第1バイト 0x81〜0x9F がUTF-8の先頭バイト
/// として不正であることが効いている。
/// </para>
/// <para>
/// 読み取り専用。書き込み時はデフォルト(UTF-8 + EFSフラグ)を使う。
/// </para>
/// </remarks>
internal sealed class ArchiveEntryNameEncoding(Encoding legacy) : Encoding
{
    /// <summary>
    /// 不正なバイト列を黙って置換せず例外にする。判定は <see cref="Choose"/> で済ませて
    /// あるためここで例外が出ることはないが、取りこぼしを黙って握りつぶさないために残す。
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>
    /// どちらのコードページで解釈するかを決める。
    /// </summary>
    /// <remarks>
    /// 「デコードしてみて例外が出たらCP932」という書き方もできるが、それでは
    /// **従来のCP932書庫でエントリごとに例外が飛ぶ**。5万件あたり93msかかっていたものが、
    /// <see cref="Utf8.IsValid(ReadOnlySpan{byte})"/> による判定では1msになった (#40)。
    /// 両者の判定が一致することは1〜3バイトの全数16,777,216通りを含む1,735万件の
    /// バイト列で確認済み(不一致0件)で、#13 で決めた判定方式そのものは変わらない。
    /// </remarks>
    private Encoding Choose(byte[] bytes, int index, int count)
        => Utf8.IsValid(bytes.AsSpan(index, count)) ? StrictUtf8 : legacy;

    public override string GetString(byte[] bytes, int index, int count)
        => Choose(bytes, index, count).GetString(bytes, index, count);

    public override int GetCharCount(byte[] bytes, int index, int count)
        => Choose(bytes, index, count).GetCharCount(bytes, index, count);

    public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        => Choose(bytes, byteIndex, byteCount).GetChars(bytes, byteIndex, byteCount, chars, charIndex);

    public override int GetMaxCharCount(int byteCount) => legacy.GetMaxCharCount(byteCount);

    // 以下は書き込み用。このクラスは読み取り専用の想定だが、
    // 抽象メンバーのため UTF-8 に委譲しておく。
    public override int GetByteCount(char[] chars, int index, int count)
        => StrictUtf8.GetByteCount(chars, index, count);

    public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        => StrictUtf8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);

    public override int GetMaxByteCount(int charCount) => StrictUtf8.GetMaxByteCount(charCount);
}
