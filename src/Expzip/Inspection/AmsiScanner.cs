using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

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
    /// 検査のウィンドウ口を開く。
    /// </summary>
    /// <remarks>
    /// <para>
    /// AMSI は Windows の口であって、特定の対策ソフトのものではない。Defender の
    /// 代わりに別の対策ソフトを入れている環境でも、その製品が提供者として登録して
    /// いれば同じように働く。こちら側は製品名を知らないし、知る必要もない。
    /// </para>
    /// <para>
    /// ただし<b>初期化に成功しただけでは、判定が働いているとは言えない</b>。応じる
    /// 提供者が居なければ、問い合わせはいつも「問題なし」で返ってくる。それを
    /// 「調べた」として報告すると、何も見ていないのに安全だと伝えることになる。
    /// そこで先に <see cref="HasProvider"/> で登録の有無を見る。
    /// </para>
    /// </remarks>
    /// <returns>
    /// 判定に応じる対策ソフトが居ない環境では <see langword="null"/>。
    /// その場合はマルウェア検査だけを「利用できません」とし、他の検査は行う。
    /// </returns>
    public static AmsiScanner? TryCreate()
    {
        try
        {
            // 初期化に成功しただけでは、判定が働いているとは言えない。
            // 応じる相手が居なければ、問い合わせはいつも「問題なし」で返ってくる
            if (!HasProvider())
            {
                return null;
            }

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
    /// 判定に応じる対策ソフトが登録されているかどうか。
    /// </summary>
    /// <remarks>
    /// <para>
    /// AMSI に応じる製品は、自分の識別子をここに登録する。1つも無ければ、
    /// 問い合わせても答える相手が居ない。
    /// </para>
    /// <para>
    /// 試験用の検体 (EICAR) を1つ通してみるほうが確実だが、その方法は採らない。
    /// 対策ソフトの検出履歴に「Expzip.exe で脅威を検出」として毎回残るためで、
    /// 利用者から見れば Expzip 自身がマルウェアのように見えてしまう。
    /// </para>
    /// <para>
    /// 登録があっても、その製品が実際に答えるとは限らない (別の対策ソフトを
    /// 入れると Defender は待機側に回る)。そこまでは見分けられないため、
    /// 報告では「安全です」とは言わず「判定に掛けました」と書く。
    /// </para>
    /// </remarks>
    private static bool HasProvider()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(ProviderKey);
            return key is not null && key.SubKeyCount > 0;
        }
        catch (Exception ex) when (ex is SecurityException or UnauthorizedAccessException
                                   or IOException)
        {
            // 読めないだけなら、居ないとは限らない。使える前提で進む
            return true;
        }
    }

    /// <summary>AMSI に応じる製品が自分を登録する場所。</summary>
    private const string ProviderKey = @"SOFTWARE\Microsoft\AMSI\Providers";

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
