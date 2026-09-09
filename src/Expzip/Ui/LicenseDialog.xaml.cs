using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// 配るものに入っているソフトウェアの、著作権表示と許諾条文を出す (#105)。
/// </summary>
/// <remarks>
/// <para>
/// <b>MIT は「上記の著作権表示とこの許諾条文を、複製物に含めること」を条件にしている。</b>
/// Expzip は単一の実行ファイルとして配るので、添付ファイルを並べる形は採れない。
/// 実行ファイルの中に入れ、ここから読めるようにすることで、どの複製にも必ず付いてくる。
/// </para>
/// <para>
/// 入っているのは SharpCompress・SharpZipLib・.NET ランタイムの3つ。
/// .NET は自己完結で発行しているためランタイムそのものが実行ファイルに入っており、
/// さらにランタイムが含む第三者のぶんも Microsoft の表示をそのまま載せてある。
/// </para>
/// </remarks>
public partial class LicenseDialog : Window
{
    /// <summary>埋め込んだ表示の名前。<c>Expzip.csproj</c> の LogicalName と揃える。</summary>
    private const string ResourceName = "Expzip.Resources.THIRD-PARTY-NOTICES.txt";

    internal LicenseDialog(Window owner)
    {
        InitializeComponent();
        Owner = owner;

        Title = Strings.LicenseTitle;
        CloseButton.Content = Strings.AboutClose;
        NoticeText.Text = Read();
    }

    /// <summary>埋め込んだ表示を取り出す。</summary>
    /// <remarks>
    /// 取り出せないのは組み立てを間違えたときだけなので、黙って空にはしない。
    /// 何が起きたのかを画面に出す。
    /// </remarks>
    private static string Read()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream(ResourceName);

        if (stream is null)
        {
            return Strings.LicenseMissing(ResourceName);
        }

        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
