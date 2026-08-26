using System.Runtime.InteropServices;

namespace Expzip.Inspection;

/// <summary>
/// Windows の AMSI (Antimalware Scan Interface) に中身を渡して判定させる (#56)。
/// </summary>
/// <remarks>
/// <para>
/// ディスクに書かず、メモリ上のバイト列のまま渡す。検体を取り出さずに済むうえ、
/// 取り出した先で対策ソフトが先に反応して騒ぎになることもない。
/// </para>
/// <para>
/// 外部の対策ソフトのコマンドを呼ぶ方式は採らない。設定が要り、設定しなければ
/// 何も起きないため。
/// </para>
/// <para>
/// 実測 (Windows 11 + Defender): 初期化2〜3ms、64MBのバッファで93ms
/// (およそ700MB/秒)。EICARテスト検体は検出、ふつうのテキストは未検出。
/// </para>
/// </remarks>
internal sealed class AmsiScanner : IDisposable
{
    /// <summary>
    /// 一度に渡せる大きさの上限。
    /// </summary>
    /// <remarks>
    /// <see cref="AmsiScanBuffer"/> はバッファ全体を一度に渡す必要がある。
    /// これを超えるものは「検査できず」として報告し、<b>黙って分割しない</b>。
    /// 分割すると署名が境目で切れて見落とすため。
    /// </remarks>
    public const long SizeLimit = 256L * 1024 * 1024;

    /// <summary>この値以上が「検出」(AMSI_RESULT_DETECTED)。</summary>
    private const int DetectedThreshold = 32768;

    private readonly IntPtr _context;

    private readonly IntPtr _session;

    private bool _closed;

    private AmsiScanner(IntPtr context, IntPtr session)
    {
        _context = context;
        _session = session;
    }

    /// <summary>
    /// 検査の窓口を開く。
    /// </summary>
    /// <returns>
    /// AMSI を提供する対策ソフトが居ない環境では <see langword="null"/>。
    /// その場合はマルウェア検査だけを「利用できません」とし、他の検査は行う。
    /// </returns>
    public static AmsiScanner? TryCreate()
    {
        try
        {
            if (AmsiInitialize("Expzip", out var context) != 0 || context == IntPtr.Zero)
            {
                return null;
            }

            // 同じ書庫の中を続けて調べるため、対策ソフト側に一続きだと伝える。
            // 開けなくても検査そのものは行えるので、その場合はセッション無しで進む
            if (AmsiOpenSession(context, out var session) != 0)
            {
                session = IntPtr.Zero;
            }

            return new AmsiScanner(context, session);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // amsi.dll を持たない Windows。ここに来ることは想定していないが、
            // 検査が使えないだけで済ませる
            return null;
        }
    }

    /// <summary>
    /// バイト列を判定に掛ける。
    /// </summary>
    /// <param name="buffer">中身。<paramref name="length"/> より長くてもよい。</param>
    /// <param name="length">実際に見てもらう長さ。</param>
    /// <param name="name">対策ソフト側の記録に残る名前。書庫内のパスを渡す。</param>
    /// <returns>検出されたとき <see langword="true"/>。</returns>
    public bool Scan(byte[] buffer, int length, string name)
    {
        if (_closed)
        {
            return false;
        }

        // 空のファイルは渡さない。判定するものが無い
        if (length <= 0)
        {
            return false;
        }

        return AmsiScanBuffer(_context, buffer, (uint)length, name, _session, out var result) == 0
               && result >= DetectedThreshold;
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        if (_session != IntPtr.Zero)
        {
            AmsiCloseSession(_context, _session);
        }

        AmsiUninitialize(_context);
    }

    [DllImport("amsi.dll", CharSet = CharSet.Unicode)]
    private static extern int AmsiInitialize(string appName, out IntPtr context);

    [DllImport("amsi.dll")]
    private static extern void AmsiUninitialize(IntPtr context);

    [DllImport("amsi.dll")]
    private static extern int AmsiOpenSession(IntPtr context, out IntPtr session);

    [DllImport("amsi.dll")]
    private static extern void AmsiCloseSession(IntPtr context, IntPtr session);

    [DllImport("amsi.dll", CharSet = CharSet.Unicode)]
    private static extern int AmsiScanBuffer(
        IntPtr context, byte[] buffer, uint length, string contentName, IntPtr session,
        out int result);
}
