namespace Expzip.Inspection;

/// <summary>
/// 索引 (中央ディレクトリ) と項目のヘッダー (ローカルヘッダー) の食い違いの種類 (#268)。
/// </summary>
/// <remarks>
/// <see cref="InspectionIssue.HeaderMismatch"/> の補足として、名前の文字列で
/// <see cref="InspectionFinding.Detail"/> に入れる。表示するときに言語を選ぶので、
/// 検査のあとで言語を切り替えても括弧の中が入れ替わる (#23)。
/// </remarks>
internal enum HeaderMismatchKind
{
    /// <summary>索引が指す位置に項目のヘッダーが無い。</summary>
    Missing,

    /// <summary>名前が違う。</summary>
    Name,

    /// <summary>圧縮前か圧縮後の大きさが違う。</summary>
    Size,

    /// <summary>CRC が違う。</summary>
    Crc,

    /// <summary>圧縮方式が違う。</summary>
    Method,

    /// <summary>暗号化などの設定 (汎用フラグ) が違う。</summary>
    Flags,

    /// <summary>ヘッダーの値が規格に合わない。</summary>
    Invalid,

    /// <summary>名前がフォルダーなのに中身がある。</summary>
    FolderWithData,
}

/// <summary>SharpZipLib の照合が返す英語の文を、食い違いの種類に振り分ける (#268)。</summary>
/// <remarks>
/// 文はそのまま画面に出さない。SharpZipLib を上げて文が変わったときは、
/// 振り分けられずに括弧が消えるだけで、英語は出ない。
/// 文は SharpZipLib 1.4.2 の <c>ZipFile.TestLocalHeader</c> から拾った。
/// </remarks>
internal static class HeaderMismatchReasons
{
    private static readonly (string Prefix, HeaderMismatchKind Kind)[] Table =
    [
        ("Wrong local header signature", HeaderMismatchKind.Missing),

        ("Central header and local header file name mismatch", HeaderMismatchKind.Name),
        ("File name length mismatch", HeaderMismatchKind.Name),

        ("Size mismatch between", HeaderMismatchKind.Size),
        ("Compressed size mismatch", HeaderMismatchKind.Size),
        ("Size invalid for descriptor", HeaderMismatchKind.Size),
        ("Compressed size invalid for descriptor", HeaderMismatchKind.Size),
        ("Required Zip64 extended information missing", HeaderMismatchKind.Size),

        ("Central header/local header crc mismatch", HeaderMismatchKind.Crc),
        ("Invalid CRC for empty entry", HeaderMismatchKind.Crc),

        ("Central header/local header compression method mismatch", HeaderMismatchKind.Method),

        ("Central header/local header flags mismatch", HeaderMismatchKind.Flags),

        ("Version required to extract this entry is invalid", HeaderMismatchKind.Invalid),
        ("Version required to extract this entry is too low", HeaderMismatchKind.Invalid),
        ("Reserved bit flags cannot be set", HeaderMismatchKind.Invalid),
        ("Strong encryption flag set but", HeaderMismatchKind.Invalid),
        ("Patched data requires higher version", HeaderMismatchKind.Invalid),
        ("Extract version mismatch", HeaderMismatchKind.Invalid),
        ("Header masked set but", HeaderMismatchKind.Invalid),

        ("Directory cannot have size", HeaderMismatchKind.FolderWithData),
        ("Directory compressed size invalid", HeaderMismatchKind.FolderWithData),
    ];

    /// <summary>
    /// 文を振り分ける。<c>TestArchive</c> が包む <c>Exception during test - '…'</c> は外して見る。
    /// 振り分けられないときは null。
    /// </summary>
    public static HeaderMismatchKind? Classify(string message)
    {
        const string Wrapper = "Exception during test - '";

        if (message.StartsWith(Wrapper, StringComparison.Ordinal) && message.EndsWith('\''))
        {
            message = message[Wrapper.Length..^1];
        }

        foreach (var (prefix, kind) in Table)
        {
            if (message.StartsWith(prefix, StringComparison.Ordinal))
            {
                return kind;
            }
        }

        return null;
    }
}
