using System.Windows;
using System.Windows.Media;
using Expzip.Ai;
using Expzip.Localization;

namespace Expzip.Ui;

/// <summary>
/// AI 連携の繋ぎ先を尋ねるダイアログ (#24)。
/// </summary>
/// <remarks>
/// <para>
/// **繋ぎ先はアプリが決めない。**利用者が入口と鍵で選ぶ (仕様書 11.4節)。話す形は
/// OpenAI 互換だけに決めてあり (#35)、主要な提供元もローカルで動かす道具も
/// 同じ形の入口を持つため、この3つを埋めればどこへでも繋がる。
/// </para>
/// <para>
/// **模型の名前は決め打ちで用意しない。**名前は移り変わるもので、古い名前を
/// 候補として並べておくと、動かない設定を勧めることになる。入口だけを候補にする。
/// </para>
/// </remarks>
public partial class AiSettingsDialog : Window
{
    /// <summary>入口の候補。名前と場所だけ。模型の名前は入れない。</summary>
    private static readonly (string Key, string Endpoint)[] Presets =
    [
        ("google", "https://generativelanguage.googleapis.com/v1beta/openai/"),
        ("openai", "https://api.openai.com/v1/"),
        ("anthropic", "https://api.anthropic.com/v1/"),
        ("local", "http://localhost:11434/v1/"),
    ];

    private bool _ready;
    private CancellationTokenSource? _testing;

    internal AiSettingsDialog(Window owner, AiOptions options)
    {
        InitializeComponent();
        Owner = owner;

        EndpointBox.Text = options.Endpoint;
        ModelBox.Text = options.Model;
        KeyBox.Password = options.ApiKey;

        PresetCombo.ItemsSource = new[]
        {
            Strings.AiPresetCustom, Strings.AiPresetGoogle, Strings.AiPresetOpenAi,
            Strings.AiPresetAnthropic, Strings.AiPresetLocal,
        };

        PresetCombo.SelectedIndex = IndexOf(options.Endpoint);

        ApplyLanguage();
        _ready = true;
        ShowReady();

        Loaded += (_, _) => EndpointBox.Focus();
    }

    /// <summary>入力された繋ぎ先。</summary>
    internal AiOptions Options => new(
        EndpointBox.Text.Trim(), ModelBox.Text.Trim(), KeyBox.Password);

    /// <summary>入口から候補の番号を引く。合うものが無ければ「自分で入れる」。</summary>
    private static int IndexOf(string endpoint)
    {
        var text = endpoint.Trim().TrimEnd('/');

        for (var i = 0; i < Presets.Length; i++)
        {
            if (string.Equals(Presets[i].Endpoint.TrimEnd('/'), text,
                    StringComparison.OrdinalIgnoreCase))
            {
                return i + 1;
            }
        }

        return 0;
    }

    private void ApplyLanguage()
    {
        Title = Strings.AiDialogTitle;
        IntroText.Text = Strings.AiIntro;
        PresetLabel.Text = Strings.AiPresetLabel;
        EndpointLabel.Text = Strings.AiEndpointLabel;
        ModelLabel.Text = Strings.AiModelLabel;
        KeyLabel.Text = Strings.AiKeyLabel;
        HintText.Text = Strings.AiModelHint;
        PrivacyText.Text = Strings.AiPrivacyNotice;
        TestButton.Content = Strings.AiTest;
        SaveButton.Content = Strings.Save;
        CancelButton.Content = Strings.AiCancel;
    }

    private void Preset_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready || PresetCombo.SelectedIndex <= 0)
        {
            return;
        }

        // 入口だけを入れる。模型の名前と鍵はそのまま残す
        EndpointBox.Text = Presets[PresetCombo.SelectedIndex - 1].Endpoint;
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        // 入口を手で書き換えたら、候補の選びを合わせ直す
        if (ReferenceEquals(sender, EndpointBox))
        {
            var index = IndexOf(EndpointBox.Text);

            if (PresetCombo.SelectedIndex != index)
            {
                _ready = false;
                PresetCombo.SelectedIndex = index;
                _ready = true;
            }
        }

        ShowReady();
    }

    /// <summary>いまの入力で繋げる形になっているかを出す。</summary>
    private void ShowReady()
    {
        var ready = Options.IsConfigured;
        TestButton.IsEnabled = ready && _testing is null;
        ResultText.Text = ready ? string.Empty : Strings.AiIncomplete;
        ResultText.Foreground = SystemColors.GrayTextBrush;
    }

    private async void TestButton_Click(object sender, RoutedEventArgs e)
    {
        if (_testing is not null)
        {
            return;
        }

        using var cancellation = new CancellationTokenSource();
        _testing = cancellation;
        TestButton.IsEnabled = false;
        SaveButton.IsEnabled = false;
        ResultText.Foreground = SystemColors.GrayTextBrush;
        ResultText.Text = Strings.AiTesting;

        AiTestResult result;

        try
        {
            result = await AiClient.TestAsync(Options, cancellation.Token);
        }
        finally
        {
            _testing = null;
            SaveButton.IsEnabled = true;
        }

        ResultText.Text = result.Message;
        ResultText.Foreground = result.Reachable
            ? new SolidColorBrush(Color.FromRgb(0x10, 0x7C, 0x10))
            : SystemColors.GrayTextBrush;

        ShowReady();
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
