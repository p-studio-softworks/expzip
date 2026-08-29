using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Expzip.Localization;

namespace Expzip.Ai;

/// <summary>AI への繋ぎ口 (#24)。</summary>
/// <remarks>
/// <para>
/// **OpenAI 互換の <c>/chat/completions</c> だけを話す** (#35で決定)。主要な提供元は
/// どこもこの形の入口を持っていて、ローカルで動かす道具もほぼ全部が同じ形のため、
/// 1つ実装すればどこへでも繋がる。どこを使うかは利用者が URL と鍵で決める。
/// </para>
/// <para>
/// 送るのはファイル名とフォルダ構成だけで、ファイルの中身は一切送らない
/// (仕様書 11.4節、確定方針)。ここでは繋がるかどうかを確かめるところまでを持つ。
/// </para>
/// </remarks>
internal static class AiClient
{
    /// <summary>OpenAI 互換の入口の、最後の部分。</summary>
    private const string Path = "chat/completions";

    /// <summary>繋がるか確かめるときの待ち時間。</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>送り出す形。日本語をそのまま載せる。</summary>
    /// <remarks>
    /// 既定では ASCII 以外が <c>\uXXXX</c> に開かれる。JSON としては正しいが、
    /// 1文字が 3バイトから 6バイトに膨らむ。送るのは書庫の中の名前で、日本語の
    /// ものが並ぶことになるため (#25)、そのまま載せる。
    /// 「Unsafe」と付くのは HTML に埋め込む場合の話で、JSON の本文では通例の選び。
    /// </remarks>
    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>入力された場所を、実際に叩く場所に整える。</summary>
    /// <remarks>
    /// 提供元の案内は <c>.../v1</c> までだったり <c>.../v1/</c> だったりする。
    /// 利用者が <c>/chat/completions</c> まで貼ることもある。どれで入れても
    /// 同じところへ届くようにする。
    /// </remarks>
    public static string? ToRequestUri(string? endpoint)
    {
        var text = endpoint?.Trim();

        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return null;
        }

        var trimmed = text.TrimEnd('/');

        return trimmed.EndsWith(Path, StringComparison.OrdinalIgnoreCase)
            ? trimmed
            : trimmed + "/" + Path;
    }

    /// <summary>繋がるかどうかを確かめる。</summary>
    /// <remarks>
    /// いちばん短いやり取りを1回だけ投げる。答えの中身は見ない。繋がること、
    /// 鍵が通ること、その名前の模型があることの3つが分かればよい。
    /// </remarks>
    public static async Task<AiTestResult> TestAsync(
        AiOptions options, CancellationToken cancellationToken = default)
    {
        if (ToRequestUri(options.Endpoint) is not { } uri)
        {
            return new AiTestResult(false, Strings.AiBadEndpoint);
        }

        if (string.IsNullOrWhiteSpace(options.Model))
        {
            return new AiTestResult(false, Strings.AiNoModel);
        }

        var payload = JsonSerializer.Serialize(
            new
            {
                model = options.Model,
                messages = new[] { new { role = "user", content = "ping" } },
                max_tokens = 1,
            },
            Wire);

        using var client = new HttpClient { Timeout = Patience };
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (!string.IsNullOrEmpty(options.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        }

        try
        {
            using var response = await client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            return response.IsSuccessStatusCode
                ? new AiTestResult(true, Strings.AiReachable(options.Model))
                : new AiTestResult(false, Strings.AiRefused((int)response.StatusCode, Explain(body)));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new AiTestResult(false, Strings.AiTimedOut);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                   or UriFormatException)
        {
            return new AiTestResult(false, ex.Message);
        }
    }

    /// <summary>返ってきた本文から、人に見せる一文を取り出す。</summary>
    /// <remarks>
    /// 提供元によって形が違うが、たいてい <c>error.message</c> に入っている。
    /// 見つからなければ本文の頭を切って出す。黙って「失敗しました」とだけ言うより、
    /// 相手の言い分を見せるほうが直しようがある。
    /// </remarks>
    private static string Explain(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);

            if (document.RootElement.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? string.Empty;
                }

                if (error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? string.Empty;
                }
            }
        }
        catch (JsonException)
        {
        }

        var text = body.Trim();
        return text.Length <= 300 ? text : text[..300] + "…";
    }
}

/// <summary>AI への繋ぎ先 (#24)。</summary>
/// <param name="Endpoint">OpenAI 互換の入口。</param>
/// <param name="Model">使う模型の名前。</param>
/// <param name="ApiKey">APIキー。ローカルの道具では要らないことがある。</param>
internal sealed record AiOptions(string Endpoint, string Model, string ApiKey)
{
    /// <summary>繋ぎ先が揃っているか。揃っていなければ AI の機能を出さない。</summary>
    /// <remarks>
    /// 鍵は要らないことがある (ローカルで動かす道具)。場所と模型の名前だけを見る。
    /// </remarks>
    public bool IsConfigured
        => AiClient.ToRequestUri(Endpoint) is not null && !string.IsNullOrWhiteSpace(Model);
}

/// <summary>繋がるか確かめた結果 (#24)。</summary>
/// <param name="Reachable">繋がったか。</param>
/// <param name="Message">人に見せる一文。</param>
internal readonly record struct AiTestResult(bool Reachable, string Message);
