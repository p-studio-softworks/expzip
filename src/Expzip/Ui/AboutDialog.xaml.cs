using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using Expzip.Localization;
using Microsoft.Win32;

namespace Expzip.Ui;

/// <summary>
/// バージョン情報 (#104)。
/// </summary>
/// <remarks>
/// <b>版とリビジョンの両方を出す。</b> 版だけでは、同じ 0.1.0 のどれを渡したのかが
/// 分からない。不具合の報告と手元の木を突き合わせられるよう、ビルドしたときの
/// コミットを組み込んである (Expzip.csproj の StampRevision)。
/// </remarks>
public partial class AboutDialog : Window
{
    internal AboutDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;
        ApplyLanguage();
    }

    /// <summary>ビルドしたときのコミット。git の無いところでビルドしたなら <see langword="null"/>。</summary>
    internal static string? Revision => Meta("Revision");

    /// <summary>そのコミットから手を入れた木でビルドしたか。</summary>
    internal static bool Modified => Meta("RevisionModified") == "true";

    /// <summary>アプリの名前。</summary>
    internal static string ProductName =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyProductAttribute>()?.Product
            ?? "Expzip";

    /// <summary>版。ビルド番号ではなく、csproj に書いた版そのもの。</summary>
    internal static string Version
    {
        get
        {
            var text = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (string.IsNullOrEmpty(text))
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "?";
            }

            // 「0.1.0+abc1234」の形で入ることがある。前半だけを版として出す
            var plus = text.IndexOf('+');
            return plus < 0 ? text : text[..plus];
        }
    }

    /// <summary>著作権表示。</summary>
    internal static string Copyright =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyCopyrightAttribute>()?.Copyright
            ?? string.Empty;

    /// <summary>
    /// Windows の設定と同じ形の文字列 ("Windows 11 Pro バージョン 25H2 (OS ビルド 26200.9457)")。
    /// 読み取れなければ <see cref="RuntimeInformation.OSDescription"/> にする (#149)。
    /// </summary>
    /// <remarks>
    /// Windows 11 でも、レジストリの ProductName は昔のまま「Windows 10 ...」を返す。
    /// 文字列でこの判定をしている古いアプリを壊さないための Windows 側の仕様。
    /// ビルド番号(22000 以降が Windows 11)で判定して直す
    /// </remarks>
    private static string WindowsDescription
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

                if (key?.GetValue("ProductName") is not string productName
                    || key.GetValue("DisplayVersion") is not string displayVersion
                    || key.GetValue("CurrentBuildNumber") is not string buildText
                    || !int.TryParse(buildText, out var build)
                    || key.GetValue("UBR") is not int ubr)
                {
                    return RuntimeInformation.OSDescription;
                }

                if (build >= 22000 && productName.StartsWith("Windows 10", StringComparison.Ordinal))
                {
                    productName = "Windows 11" + productName["Windows 10".Length..];
                }

                return Strings.AboutWindows(productName, displayVersion, $"{build}.{ubr}");
            }
            catch (Exception)
            {
                // レジストリが読めない環境(制限されたポリシーなど)でも、バージョン情報の窓自体は開けるようにする
                return RuntimeInformation.OSDescription;
            }
        }
    }

    private static string? Meta(string key) =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(one => one.Key == key)
            ?.Value is { Length: > 0 } value
            ? value
            : null;

    private void ApplyLanguage()
    {
        Title = Strings.AboutTitle;
        NameText.Text = ProductName;
        VersionText.Text = Strings.AboutVersion(Version, RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant());
        RevisionText.Text = Revision is { } revision
            ? Modified ? Strings.AboutRevisionModified(revision) : Strings.AboutRevision(revision)
            : Strings.AboutRevisionUnknown;
        CopyrightText.Text = Copyright;
        PlatformText.Text = Strings.AboutPlatform(
            WindowsDescription, RuntimeInformation.FrameworkDescription);
        CloseButton.Content = Strings.AboutClose;
        LicenseButton.Content = Strings.AboutLicense;
    }

    private void LicenseButton_Click(object sender, RoutedEventArgs e)
        => new LicenseDialog(this).ShowDialog();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
