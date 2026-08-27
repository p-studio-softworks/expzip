using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Expzip.Localization;
using Expzip.Splitting;
using Microsoft.Win32;

namespace Expzip.Ui;

/// <summary>分割サイズの単位。</summary>
internal enum SplitUnit
{
    Kilobytes,
    Megabytes,
    Gigabytes,
}

/// <summary>
/// ファイルの分割を尋ねるダイアログ (#59)。
/// </summary>
/// <remarks>
/// <b>設定は3つだけにする。</b> 対象・置き場・大きさ。連結方法の
/// 選択肢は出さない。作るものは常に「断片 + 自己連結プログラム (CRC付き)」に
/// 決めてあるため、選ばせても迷わせるだけになる。
/// </remarks>
public partial class SplitDialog : Window
{
    private bool _ready;

    internal SplitDialog(Window owner, string? sourcePath, long chunkSize)
    {
        InitializeComponent();
        Owner = owner;

        SourceBox.Text = sourcePath ?? string.Empty;
        DestinationBox.Text = sourcePath is null
            ? string.Empty
            : Path.GetDirectoryName(sourcePath) ?? string.Empty;

        UnitCombo.ItemsSource = new[]
        {
            Strings.SplitUnitKilobytes, Strings.SplitUnitMegabytes, Strings.SplitUnitGigabytes,
        };

        ShowSize(chunkSize);
        ApplyLanguage();

        _ready = true;
        UpdatePreview();

        Loaded += (_, _) => SourceBox.Focus();
    }

    /// <summary>分ける対象。</summary>
    internal string SourcePath => SourceBox.Text.Trim();

    /// <summary>断片と連結プログラムの置き場。</summary>
    internal string DestinationDirectory => DestinationBox.Text.Trim();

    /// <summary>1つあたりの大きさ。読めない場合は 0。</summary>
    internal long ChunkSize
    {
        get
        {
            if (!double.TryParse(
                    SizeBox.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture,
                    out var value) || value <= 0)
            {
                return 0;
            }

            var scale = (SplitUnit)Math.Max(0, UnitCombo.SelectedIndex) switch
            {
                SplitUnit.Gigabytes => 1024L * 1024 * 1024,
                SplitUnit.Megabytes => 1024L * 1024,
                _ => 1024L,
            };

            var bytes = value * scale;
            return bytes >= long.MaxValue ? 0 : (long)bytes;
        }
    }

    internal void ApplyLanguage()
    {
        Title = Strings.SplitTitle;
        SourceLabel.Text = Strings.SplitSourceLabel;
        DestinationLabel.Text = Strings.SplitDestinationLabel;
        SizeLabel.Text = Strings.SplitSizeLabel;
        BrowseSourceButton.Content = Strings.SplitBrowse;
        BrowseDestinationButton.Content = Strings.SplitBrowse;
        SplitButton.Content = Strings.SplitStart;
        CancelButton.Content = Strings.SplitCancel;
    }

    /// <summary>大きさを、割り切れる一番大きな単位で入れる。</summary>
    private void ShowSize(long bytes)
    {
        var unit = SplitUnit.Kilobytes;
        var value = Math.Max(FileSplitter.MinimumChunk, bytes) / 1024.0;

        if (value >= 1024 && value % 1024 == 0)
        {
            value /= 1024;
            unit = SplitUnit.Megabytes;

            if (value >= 1024 && value % 1024 == 0)
            {
                value /= 1024;
                unit = SplitUnit.Gigabytes;
            }
        }

        SizeBox.Text = value.ToString("0.###", CultureInfo.CurrentCulture);
        UnitCombo.SelectedIndex = (int)unit;
    }

    private void Input_Changed(object sender, TextChangedEventArgs e) => UpdatePreview();

    private void Unit_Changed(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    /// <summary>
    /// いまの設定でどうなるかを出す。
    /// </summary>
    /// <remarks>
    /// 押してから断られるより、押す前に分かるほうがよい。分割の必要が無い場合も
    /// ここで伝える。
    /// </remarks>
    private void UpdatePreview()
    {
        if (!_ready)
        {
            return;
        }

        SplitButton.IsEnabled = false;

        if (SourcePath.Length == 0 || !File.Exists(SourcePath))
        {
            PreviewText.Text = Strings.SplitSourceMissing;
            return;
        }

        if (DestinationDirectory.Length == 0)
        {
            PreviewText.Text = Strings.SplitDestinationMissing;
            return;
        }

        var chunk = ChunkSize;
        if (chunk < FileSplitter.MinimumChunk)
        {
            PreviewText.Text = Strings.SplitTooSmall(FileSplitter.MinimumChunk);
            return;
        }

        long length;
        try
        {
            length = new FileInfo(SourcePath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            PreviewText.Text = Strings.SplitSourceMissing;
            return;
        }

        if (chunk >= length)
        {
            PreviewText.Text = Strings.SplitNotNeeded;
            return;
        }

        SplitButton.IsEnabled = true;
        PreviewText.Text = Strings.SplitPreview(FileSplitter.CountParts(length, chunk));
    }

    private void BrowseSourceButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = Strings.SplitSourceTitle,
            Filter = Strings.SplitAnyFile,
            FileName = SourcePath,
        };

        if (picker.ShowDialog(this) == true)
        {
            SourceBox.Text = picker.FileName;

            // 置き場をまだ触っていなければ、選んだファイルの隣に合わせる
            if (DestinationDirectory.Length == 0)
            {
                DestinationBox.Text = Path.GetDirectoryName(picker.FileName) ?? string.Empty;
            }
        }
    }

    private void BrowseDestinationButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog
        {
            Title = Strings.SplitDestinationTitle,
            InitialDirectory = Directory.Exists(DestinationDirectory)
                ? DestinationDirectory
                : string.Empty,
        };

        if (picker.ShowDialog(this) == true)
        {
            DestinationBox.Text = picker.FolderName;
        }
    }

    private void SplitButton_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void CancelButton_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
