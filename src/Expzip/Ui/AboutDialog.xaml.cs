using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// バージョン情報 (#104)。
/// </summary>
/// <remarks>
/// <b>版とリビジョンの両方を出す。</b> 版だけでは、同じ 0.1.0 のどれを渡したのかが
/// 分からない。不具合の報告と手元の木を突き合わせられるよう、建てたときの
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

    /// <summary>建てたときのコミット。git の無いところで建てたなら <see langword="null"/>。</summary>
    internal static string? Revision => Meta("Revision");

    /// <summary>そのコミットから手を入れた木で建てたか。</summary>
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
            RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription);
        CloseButton.Content = Strings.AboutClose;

        // 空の枠が何なのか分からないままにしない (#104)
        IconSlot.ToolTip = Strings.AboutIconLater;
        AutomationProperties.SetName(IconPlaceholder, Strings.AboutIconLater);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
