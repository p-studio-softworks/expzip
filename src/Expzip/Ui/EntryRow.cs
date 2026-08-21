using Expzip.Archives;

namespace Expzip.Ui;

/// <summary>リストビューの行の種別。</summary>
internal enum EntryRowKind
{
    /// <summary>親フォルダへ戻るための行。</summary>
    Parent,

    /// <summary>フォルダ。</summary>
    Folder,

    /// <summary>ファイル。</summary>
    File,
}

/// <summary>
/// リストビューの1行。親へ戻る行、フォルダ、ファイルを同じ型で扱う。
/// 表示用の文字列とソート用の生値の両方を持たせ、桁揃えとソートを両立させる。
/// </summary>
internal sealed class EntryRow
{
    /// <summary>フォルダに上矢印を重ねた「一つ上へ」のアイコン。</summary>
    private const string GlyphParent = "";

    /// <summary>
    /// 塗りつぶしのフォルダ。輪郭線だけのフォルダ (E8B7) は
    /// 書類アイコンと形が似ており、一覧の中で見分けがつかない。
    /// </summary>
    private const string GlyphFolder = "";

    /// <summary>書類。</summary>
    private const string GlyphFile = "";

    public required string Name { get; init; }

    public required EntryRowKind Kind { get; init; }

    /// <summary>フォルダ行および親行の移動先。ファイル行では <see langword="null"/>。</summary>
    public ArchiveFolder? Folder { get; init; }

    /// <summary>ファイル行の実体。それ以外では <see langword="null"/>。</summary>
    public ArchiveEntry? Entry { get; init; }

    /// <summary>Segoe MDL2 Assets のアイコン。</summary>
    public string Glyph => Kind switch
    {
        EntryRowKind.Parent => GlyphParent,
        EntryRowKind.Folder => GlyphFolder,
        _ => GlyphFile,
    };

    public string SizeText => Entry is null ? string.Empty : Entry.Length.ToString("N0");

    public string CompressedText => Entry is null ? string.Empty : Entry.CompressedLength.ToString("N0");

    public string RatioText => Entry is null ? string.Empty : $"{Entry.CompressionRatio:F0}%";

    public string DateText => Entry is null || Entry.LastWriteTime == default
        ? string.Empty
        : Entry.LastWriteTime.ToString("yyyy/MM/dd HH:mm");

    // --- 以下はソート用。表示文字列で並べるとサイズが桁数順になってしまうため分けている ---

    public long SortLength => Entry?.Length ?? 0;

    public long SortCompressedLength => Entry?.CompressedLength ?? 0;

    public double SortRatio => Entry?.CompressionRatio ?? 0;

    public DateTime SortDate => Entry?.LastWriteTime ?? default;
}
