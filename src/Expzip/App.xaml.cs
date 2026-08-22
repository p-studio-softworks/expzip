using System.Text;
using System.Windows;

namespace Expzip;

/// <summary>アプリケーションのエントリポイント。</summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // CP932 は .NET Core 以降、既定では登録されていない。
        // 古い日本語書庫のファイル名を正しく読むために必要 (docs/SPEC.md 7章, #13)。
        // .NET 10 では NuGet パッケージの明示的な参照は不要。
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        // 拾いきれなかった失敗でアプリごと消えないようにする。
        // 書庫の更新はいずれも作業用ファイル上で行い、最後に差し替える作りなので、
        // 途中で失敗しても元の書庫は無事。ここで止めるより、何が起きたかを見せて
        // 操作を続けられるようにするほうが実害が少ない。
        DispatcherUnhandledException += OnDispatcherUnhandledException;

        base.OnStartup(e);
    }

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
            $"処理中に問題が起きました。{Environment.NewLine}"
            + $"書庫は変更していません。{Environment.NewLine}{Environment.NewLine}"
            + $"{e.Exception.GetType().Name}{Environment.NewLine}{e.Exception.Message}",
            "Expzip", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
