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
/// **繋ぎ先はアプリが決めない。**利用者が API と鍵で選ぶ (仕様書 10.4節)。話す形は
/// OpenAI 互換だけに決めてあり (#35)、主要な提供元もローカルで動かすツールも
/// 同じ形の API を持つため、この3つを埋めればどこへでも繋がる。
/// </para>
/// <para>
/// **モデル名は決め打ちで用意しない。**名前は移り変わるもので、古い名前を
/// 候補として並べておくと、動かない設定を勧めることになる。接続先だけを候補にする。
/// </para>
/// </remarks>
public partial class AiSettingsDialog : Window
{
    /// <summary>接続先の候補。場所だけ。モデル名は入れない。</summary>
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

        // 手動設定は使うことが少ないので、いちばん後ろに置く
        PresetCombo.ItemsSource = new[]
        {
            Strings.AiPresetGoogle, Strings.AiPresetOpenAi, Strings.AiPresetAnthropic,
            Strings.AiPresetLocal, Strings.AiPresetCustom,
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

    /// <summary>URL から候補の番号を引く。合うものが無ければ「手動設定」。</summary>
    private static int IndexOf(string endpoint)
    {
        var text = endpoint.Trim().TrimEnd('/');

        for (var i = 0; i < Presets.Length; i++)
        {
            if (string.Equals(Presets[i].Endpoint.TrimEnd('/'), text,
                    StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return Presets.Length;
    }

    private void ApplyLanguage()
    {
        Title = Strings.AiDialogTitle;
        IntroText.Text = Strings.AiIntro;
        PresetLabel.Text = Strings.AiPresetLabel;
        EndpointLabel.Text = Strings.AiEndpointLabel;
        ModelLabel.Text = Strings.AiModelLabel;
        KeyLabel.Text = Strings.AiKeyLabel;
        PrivacyText.Text = Strings.AiPrivacyNotice;
        TestButton.Content = Strings.AiTest;
        SaveButton.Content = Strings.Save;
        CancelButton.Content = Strings.AiCancel;
    }

    private void Preset_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_ready || PresetCombo.SelectedIndex < 0)
        {
            return;
        }

        // 手動設定は「一から入れ直す」という選び。前の中身は残さない。
        // 選んだだけでは保存されないので、間違えて選んでもキャンセルで元に戻る
        if (PresetCombo.SelectedIndex >= Presets.Length)
        {
            EndpointBox.Text = string.Empty;
            ModelBox.Text = string.Empty;
            KeyBox.Password = string.Empty;
            return;
        }

        // 決まった接続先を選んだときは URL だけを入れる。モデル名と鍵はそのまま残す
        EndpointBox.Text = Presets[PresetCombo.SelectedIndex].Endpoint;
    }

    private void Input_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        // URL を手で書き換えたら、候補の選びを合わせ直す
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

        // 入力が変われば、前の結果はもう当てにならない
        ResultText.Text = string.Empty;
        ShowReady();
    }

    /// <summary>いまの入力で接続できる形になっているかを、ボタンの押せる押せないで出す。</summary>
    /// <remarks>
    /// **ここで結果の文は触らない。**接続テストの結果を出した直後にも呼ばれるため、
    /// ここで消すと、出したばかりの結果が読む前に消える。
    /// </remarks>
    private void ShowReady()
        => TestButton.IsEnabled = Options.IsConfigured && _testing is null;

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
        ResultText.SetResourceReference(ForegroundProperty, "TextFillColorSecondaryBrush");

        AiTestResult result;

        try
        {
            // 待つ間、秒を進める。考えるモデルは短いやり取りでも時間がかかる (#76)
            using var ticker = new WaitTicker(Strings.AiTesting, t => ResultText.Text = t);

            result = await AiClient.TestAsync(Options, cancellation.Token);
        }
        finally
        {
            _testing = null;
            SaveButton.IsEnabled = true;
        }

        ResultText.Text = result.Message;

        // **資源への参照として持たせる (#122)。**色を取り出して代入すると、
        // 表示したままテーマが切り替わったときに、その文字だけ前の色で残る
        ResultText.SetResourceReference(
            ForegroundProperty,
            result.Reachable ? "EncryptedBrush" : "TextFillColorSecondaryBrush");

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
