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
/// (仕様書 10.4節、確定方針)。何を送るかを決めるのはここではなく、
/// <see cref="ArchiveDigest"/> と <see cref="RuleEstimator"/> (#25)。
/// </para>
/// </remarks>
internal static class AiClient
{
    /// <summary>OpenAI 互換の入口の、最後の部分。</summary>
    private const string Path = "chat/completions";

    /// <summary>繋がるか確かめるときの待ち時間。</summary>
    /// <remarks>
    /// **考えてから答えるモデルは、短いやり取りでも時間がかかる** (#76)。
    /// 30秒では `gemini-3.6-flash` が日によって間に合わず、繋がるのに
    /// 繋がらないと言う状態になっていた。
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 繋がるか確かめるときに許す答えの長さ。
    /// </summary>
    /// <remarks>
    /// **1 にしてはいけない** (#76)。考えてから答えるモデルは、考えた分も
    /// この予算から引く。1 では考える前に尽き、答えが返らない。
    /// 中身は見ないので短くてよいが、考える余地は残す。
    /// </remarks>
    private const int PingLimit = 16;

    /// <summary>尋ねるときの待ち時間。読んで考える分だけ長くとる (#25)。</summary>
    /// <remarks>2分では考えるモデルに足りなかった (#76)。</remarks>
    private static readonly TimeSpan Thinking = TimeSpan.FromMinutes(5);

    /// <summary>送り出す形。日本語をそのまま載せる。</summary>
    /// <remarks>
    /// デフォルトでは ASCII 以外が <c>\uXXXX</c> に開かれる。JSON としては正しいが、
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
        if (Refuse(options) is { } refusal)
        {
            return new AiTestResult(false, refusal);
        }

        var sent = await SendAsync(options, Body(options, "ping", null, PingLimit), Patience,
            cancellationToken);

        return sent.Ok
            ? new AiTestResult(true, Strings.AiReachable(options.Model))
            : new AiTestResult(false, sent.Message);
    }

    /// <summary>尋ねて、返ってきた文章を受け取る (#25)。</summary>
    /// <remarks>
    /// <para>
    /// **答えの形は指定しない。**OpenAI 互換の入口には答えを JSON に縛る指定
    /// (<c>response_format</c>) があるが、**受け付ける先と受け付けない先がある**。
    /// 繋ぎ先を利用者が選ぶ作りなので、一部でしか通らない指定を送ると、
    /// 選んだ先によっては断られる。頼み方で JSON を書かせ、返ってきたものを
    /// こちら側で緩く読む (<see cref="RuleEstimator"/>)。
    /// </para>
    /// <para>
    /// 待ち時間は繋がるか試すときより長くとる。名前を並べて渡すため、
    /// 読んで考える分だけ掛かる。
    /// </para>
    /// </remarks>
    public static async Task<AiAnswer> AskAsync(
        AiOptions options, string instruction, string question, int limit,
        CancellationToken cancellationToken = default)
    {
        if (Refuse(options) is { } refusal)
        {
            return new AiAnswer(false, string.Empty, refusal);
        }

        var sent = await SendAsync(options, Body(options, question, instruction, limit),
            Thinking, cancellationToken);

        if (!sent.Ok)
        {
            return new AiAnswer(false, string.Empty, sent.Message);
        }

        var said = Said(sent.Body);

        return said.Length == 0
            ? new AiAnswer(false, string.Empty, Strings.AiEmptyAnswer)
            : new AiAnswer(true, said, string.Empty);
    }

    /// <summary>繋ぎ先が揃っていない理由。揃っていれば <see langword="null"/>。</summary>
    private static string? Refuse(AiOptions options)
    {
        if (ToRequestUri(options.Endpoint) is null)
        {
            return Strings.AiBadEndpoint;
        }

        return string.IsNullOrWhiteSpace(options.Model) ? Strings.AiNoModel : null;
    }

    /// <summary>送り出す中身を組み立てる。</summary>
    private static string Body(AiOptions options, string question, string? instruction, int limit)
    {
        var messages = new List<object>();

        if (instruction is not null)
        {
            messages.Add(new { role = "system", content = instruction });
        }

        messages.Add(new { role = "user", content = question });

        return JsonSerializer.Serialize(
            new { model = options.Model, messages, max_tokens = limit }, Wire);
    }

    /// <summary>実際に送る。繋ぎ先とのやり取りは、ここ1箇所だけで行う。</summary>
    private static async Task<(bool Ok, string Body, string Message)> SendAsync(
        AiOptions options, string payload, TimeSpan patience,
        CancellationToken cancellationToken)
    {
        using var client = new HttpClient { Timeout = patience };
        using var request = new HttpRequestMessage(
            HttpMethod.Post, ToRequestUri(options.Endpoint))
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
                ? (true, body, string.Empty)
                : (false, body,
                    Strings.AiRefused((int)response.StatusCode, Explain(body)));
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 待ち時間切れ。**繋がらなかったのとは違う** (#76)。
            // 相手には届いており、答えが返る前に上限に達しただけ
            return (false, string.Empty, Strings.AiTimedOut((int)patience.TotalSeconds));
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException
                                   or UriFormatException)
        {
            return (false, string.Empty, Strings.Reason(ex));
        }
    }

    /// <summary>
    /// 答えの根を取り出す。
    /// </summary>
    /// <remarks>
    /// **配列で包んで返す提供元がある** (#69)。Google は断るときに
    /// <c>[{"error": {...}}]</c> の形で返すことがある。根がそのまま配列だと、
    /// 中の名前を引こうとした時点で例外になる。包まれていれば中の最初のものを見る。
    /// </remarks>
    private static JsonElement Root(JsonDocument document)
    {
        var root = document.RootElement;

        return root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0
            ? root[0]
            : root;
    }

    /// <summary>返ってきた本文から、AI が言ったことを取り出す。</summary>
    private static string Said(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = Root(document);

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("choices", out var choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].ValueKind == JsonValueKind.Object
                && choices[0].TryGetProperty("message", out var message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString()?.Trim() ?? string.Empty;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
        }

        return string.Empty;
    }

    /// <summary>返ってきた本文から、人に見せる一文を取り出す。</summary>
    /// <remarks>
    /// 提供元によって形が違うが、たいてい <c>error.message</c> に入っている。
    /// 配列で包まれていることもある (#69)。
    /// 見つからなければ本文の頭を切って出す。黙って「失敗しました」とだけ言うより、
    /// 相手の言い分を見せるほうが直しようがある。
    /// </remarks>
    private static string Explain(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = Root(document);

            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("error", out var error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return error.GetString() ?? string.Empty;
                }

                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return message.GetString() ?? string.Empty;
                }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
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

/// <summary>尋ねた答え (#25)。</summary>
/// <param name="Ok">答えが返ってきたか。</param>
/// <param name="Text">AI が言ったこと。</param>
/// <param name="Message">返ってこなかったときに、人に見せる一文。</param>
internal readonly record struct AiAnswer(bool Ok, string Text, string Message);
