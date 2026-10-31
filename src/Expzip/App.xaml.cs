using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Expzip.CommandLine;
using Expzip.Configuration;
using Expzip.Localization;

namespace Expzip;

/// <summary>アプリケーションのエントリポイント。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // CP932 は .NET Core 以降、デフォルトでは登録されていない。
        // 古い日本語書庫のファイル名を正しく読むために必要 (docs/SPEC.md 6章, #13)。
        // .NET 10 では NuGet パッケージの明示的な参照は不要。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // コマンドラインからの圧縮 (#316) はウィンドウを作らない。
        // 下の失敗の受け皿はダイアログを出すので、それを付ける前に分ける
        if (CommandRunner.IsCommand(e.Args))
        {
            RunCommand(e.Args);
            return;
        }

        // 拾いきれなかった失敗でアプリごと消えないようにする。
        // 書庫の更新はいずれも作業用ファイル上で行い、最後に差し替える作りなので、
        // 途中で失敗しても元の書庫は無事。ここで止めるより、何が起きたかを見せて
        // 操作を続けられるようにするほうが実害が少ない。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 意味を表す色を、背景色の明るさに合う組にする (#121、#122)。
        // **ウィンドウを作る前に。**最初のウィンドウが読み込まれる時点でキーが揃っている必要がある
        Theme.Start(Resources);

        base.OnStartup(e);

        new MainWindow().Show();
    }

    /// <summary>コマンドラインの指定どおりに動かして、そのまま終わる (#316)。</summary>
    private static void RunCommand(string[] args)
    {
        ConnectConsole();

        int exitCode;
        try
        {
            Strings.Language = Strings.Resolve(SettingsStore.Load().Language);
            exitCode = CommandRunner.Run(args, Console.Out, Console.Error);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(Strings.UnhandledError(ex.GetType().Name, Strings.Reason(ex)));
            exitCode = CommandRunner.Failed;
        }

        Environment.Exit(exitCode);
    }

    /// <summary>
    /// WinExe には自分のコンソールが無いので、呼び出し元のコンソールへ書けるようにつなぐ。
    /// </summary>
    /// <remarks>
    /// <b>つなぐと、ファイルやパイプ、<c>nul</c> へのリダイレクトが外れる</b>
    /// (実測。<c>&gt; out.txt</c> に何も入らず、<c>&gt; nul</c> でも画面に出た)。
    /// 先に控えておいて戻す。呼び出し元にコンソールが無いときはつなげないが、
    /// 出力先が無いだけなので続ける。
    /// </remarks>
    private static void ConnectConsole()
    {
        var output = GetStdHandle(StandardOutput);
        var error = GetStdHandle(StandardError);
        var keepOutput = IsRedirected(output);
        var keepError = IsRedirected(error);

        AttachConsole(AttachParentProcess);

        if (keepOutput)
        {
            SetStdHandle(StandardOutput, output);
        }

        if (keepError)
        {
            SetStdHandle(StandardError, error);
        }
    }

    /// <summary>コンソール以外へ向いているか。<c>nul</c> はコンソールと同じ種類なので、種類では分けられない。</summary>
    private static bool IsRedirected(IntPtr handle)
        => handle != IntPtr.Zero && handle != new IntPtr(-1)
           && GetFileType(handle) != FileTypeUnknown && !GetConsoleMode(handle, out _);

    private const int AttachParentProcess = -1;
    private const int StandardOutput = -11;
    private const int StandardError = -12;
    private const uint FileTypeUnknown = 0;

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int kind);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int kind, IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetConsoleMode(IntPtr handle, out uint mode);

    private void OnDispatcherUnhandledException(
        object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;

        if (base.MainWindow is MainWindow window)
        {
            window.RecoverFromUnhandledError();
        }

        MessageBox.Show(
            base.MainWindow,
            Strings.UnhandledError(e.Exception.GetType().Name, Strings.Reason(e.Exception)),
            "Expzip", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
