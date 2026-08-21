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

        base.OnStartup(e);
    }
}
