using System.Text;

namespace Expzip.Archives;

/// <summary>
/// NSIS の文字列表から名前を取り出す (#68)。
/// </summary>
/// <remarks>
/// <para>
/// 名前は文字列表への位置で持たれている。<b>Unicode 版では位置が文字数</b>なので、
/// バイト位置に直してから読む。
/// </para>
/// <para>
/// 文字列の中には <c>$INSTDIR</c> のような変数が符号で埋め込まれている。
/// Unicode 版では「符号 1〜4」+「番号を表す1語」の2語、ANSI 版では
/// <c>0xFC〜0xFF</c> +「番号2バイト」の3バイトで入る。そのまま出すと文字化けに
/// 見えるため、名前に直す。
/// </para>
/// <para>
/// 実物 (NSIS-3 Unicode) で確かめた並びはこうなっていた。
/// <c>0003 809A 005C 0053 0079 ...</c> が <c>$PLUGINSDIR\System.dll</c> にあたる。
/// </para>
/// </remarks>
internal sealed class NsisStrings
{
    /// <summary>
    /// 変数を表す符号 (Unicode 版)。実物で確かめた値。
    /// </summary>
    /// <remarks>
    /// NSIS の並びは 飛ばし / 変数 / 決まったフォルダ / 言語 の順。
    /// 実物では <c>0003</c> のあとに番号が続き、番号を解くと変数表と一致した。
    /// </remarks>
    private const ushort UnicodeSkipCode = 1;
    private const ushort UnicodeVarCode = 2;
    private const ushort UnicodeShellCode = 3;
    private const ushort UnicodeLangCode = 4;

    /// <summary>変数を表す符号 (ANSI 版)。</summary>
    private const byte AnsiSkipCode = 0xFC;
    private const byte AnsiVarCode = 0xFD;
    private const byte AnsiShellCode = 0xFE;
    private const byte AnsiLangCode = 0xFF;

    /// <summary>
    /// 番号で決まっている変数の名前。NSIS の <c>exehead/vars.h</c> の並び。
    /// </summary>
    /// <remarks>
    /// ここに挙げるのは<b>実物で確かめられた分だけ</b>。この先にも内部用の変数が
    /// あるが、名前を確かめられていないので番号のまま出す。間違った名前を出すより
    /// 「何番の変数か」が分かるほうがよい。7-Zip も同じ番号を番号のまま出す。
    /// </remarks>
    private static readonly string[] Variables =
    [
        "$0", "$1", "$2", "$3", "$4", "$5", "$6", "$7", "$8", "$9",
        "$R0", "$R1", "$R2", "$R3", "$R4", "$R5", "$R6", "$R7", "$R8", "$R9",
        "$CMDLINE", "$INSTDIR", "$OUTDIR", "$EXEDIR", "$LANGUAGE",
        "$TEMP", "$PLUGINSDIR", "$EXEPATH", "$EXEFILE", "$HWNDPARENT",
    ];

    private readonly byte[] _pool;
    private readonly bool _unicode;

    public NsisStrings(byte[] pool, bool unicode)
    {
        _pool = pool;
        _unicode = unicode;
    }

    /// <summary>文字列表が空かどうか。</summary>
    public bool IsEmpty => _pool.Length == 0;

    /// <summary>指定の位置にある名前を読む。読めない場合は空文字。</summary>
    /// <param name="offset">命令に入っていた位置。Unicode 版では文字数。</param>
    public string Read(uint offset)
        => _unicode ? ReadUnicode(offset) : ReadAnsi(offset);

    private string ReadUnicode(uint offset)
    {
        var at = checked((long)offset * 2);

        if (at < 0 || at >= _pool.Length)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        while (at + 1 < _pool.Length)
        {
            var unit = (ushort)(_pool[at] | (_pool[at + 1] << 8));
            at += 2;

            if (unit == 0)
            {
                break;
            }

            // 変数の符号なら、続く1語が番号
            if (unit is UnicodeShellCode or UnicodeVarCode or UnicodeSkipCode or UnicodeLangCode)
            {
                if (at + 1 >= _pool.Length)
                {
                    break;
                }

                var parameter = (ushort)(_pool[at] | (_pool[at + 1] << 8));
                at += 2;
                text.Append(Describe(unit, parameter));
                continue;
            }

            text.Append((char)unit);
        }

        return text.ToString();
    }

    private string ReadAnsi(uint offset)
    {
        var at = (long)offset;

        if (at < 0 || at >= _pool.Length)
        {
            return string.Empty;
        }

        var text = new StringBuilder();

        while (at < _pool.Length)
        {
            var b = _pool[at++];

            if (b == 0)
            {
                break;
            }

            if (b is AnsiShellCode or AnsiVarCode or AnsiSkipCode or AnsiLangCode)
            {
                if (at + 1 >= _pool.Length)
                {
                    break;
                }

                var parameter = (ushort)(_pool[at] | (_pool[at + 1] << 8));
                at += 2;

                text.Append(Describe(
                    b switch
                    {
                        AnsiShellCode => UnicodeShellCode,
                        AnsiVarCode => UnicodeVarCode,
                        AnsiSkipCode => UnicodeSkipCode,
                        _ => UnicodeLangCode,
                    },
                    parameter));

                continue;
            }

            text.Append((char)b);
        }

        return text.ToString();
    }

    /// <summary>変数の符号を名前に直す。</summary>
    /// <remarks>
    /// <para>
    /// 番号は<b>7ビットずつ2組</b>で入っている。実物で確かめた値では
    /// <c>0x809A</c> が 26 (<c>$PLUGINSDIR</c>)、<c>0x8095</c> が 21
    /// (<c>$INSTDIR</c>) になり、どちらも 7-Zip が出す名前と一致した。
    /// </para>
    /// <para>
    /// 分からない番号は <c>$_12</c> のように番号のまま出す。文字化けに見えるより、
    /// 何かの変数だと分かるほうがよい。
    /// </para>
    /// </remarks>
    private static string Describe(ushort code, ushort parameter)
    {
        if (code is UnicodeVarCode or UnicodeShellCode)
        {
            var index = (parameter & 0x7F) | (((parameter >> 8) & 0x7F) << 7);

            return index < Variables.Length ? Variables[index] : $"$_{index}";
        }

        return code == UnicodeLangCode ? $"$(LangString{parameter})" : string.Empty;
    }
}
