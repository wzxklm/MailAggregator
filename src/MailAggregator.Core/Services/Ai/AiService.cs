using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MailAggregator.Core.Models;
using Serilog;

namespace MailAggregator.Core.Services.Ai;

public class AiService : IAiService
{
    private readonly HttpClient _httpClient;
    private readonly IAiSettingsService _settingsService;
    private readonly ILogger _logger;

    public AiService(HttpClient httpClient, IAiSettingsService settingsService, ILogger logger)
    {
        _httpClient = httpClient;
        _settingsService = settingsService;
        _logger = logger;
    }

    public Task<string> TranslateAsync(EmailMessage message, string? languageOverride = null, CancellationToken cancellationToken = default)
        => InvokeAsync(message, isTranslate: true, languageOverride, cancellationToken);

    public Task<string> SummarizeAsync(EmailMessage message, string? languageOverride = null, CancellationToken cancellationToken = default)
        => InvokeAsync(message, isTranslate: false, languageOverride, cancellationToken);

    private async Task<string> InvokeAsync(EmailMessage message, bool isTranslate, string? languageOverride, CancellationToken cancellationToken)
    {
        var settings = await _settingsService.GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
            throw new InvalidOperationException("AI base URL is not configured. Open the AI menu to configure it.");
        if (string.IsNullOrWhiteSpace(settings.Model))
            throw new InvalidOperationException("AI model is not configured. Open the AI menu to configure it.");

        var apiKey = _settingsService.GetDecryptedApiKey(settings);
        var language = string.IsNullOrWhiteSpace(languageOverride) ? settings.DefaultLanguage : languageOverride;
        var promptTemplate = isTranslate ? settings.TranslatePrompt : settings.SummarizePrompt;
        if (string.IsNullOrWhiteSpace(promptTemplate))
        {
            promptTemplate = isTranslate
                ? _settingsService.GetDefaultTranslatePrompt()
                : _settingsService.GetDefaultSummarizePrompt();
        }

        var systemPrompt = promptTemplate.Replace("{language}", language, StringComparison.OrdinalIgnoreCase);
        var userContent = BuildEmailContent(message);

        var request = new ChatRequest
        {
            Model = settings.Model,
            Messages = new[]
            {
                new ChatMessage { Role = "system", Content = systemPrompt },
                new ChatMessage { Role = "user", Content = userContent }
            }
        };

        var endpoint = BuildEndpoint(settings.BaseUrl);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(request, options: SerializerOptions)
        };
        if (!string.IsNullOrEmpty(apiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }

        _logger.Debug("AI request: {Endpoint} model={Model} translate={IsTranslate}", endpoint, settings.Model, isTranslate);

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"AI request failed ({(int)response.StatusCode} {response.StatusCode}): {Truncate(responseBody, 500)}");
        }

        var parsed = JsonSerializer.Deserialize<ChatResponse>(responseBody, SerializerOptions);
        var content = parsed?.Choices?.FirstOrDefault()?.Message?.Content;
        if (string.IsNullOrWhiteSpace(content))
            throw new InvalidOperationException("AI returned an empty response.");
        return content;
    }

    private static string BuildEmailContent(EmailMessage message)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(message.Subject))
            sb.Append("Subject: ").AppendLine(message.Subject);
        if (!string.IsNullOrWhiteSpace(message.FromName) || !string.IsNullOrWhiteSpace(message.FromAddress))
            sb.Append("From: ").Append(message.FromName).Append(" <").Append(message.FromAddress).AppendLine(">");
        if (!string.IsNullOrWhiteSpace(message.ToAddresses))
            sb.Append("To: ").AppendLine(message.ToAddresses);
        sb.AppendLine();

        var body = !string.IsNullOrWhiteSpace(message.BodyText)
            ? message.BodyText
            : StripHtml(message.BodyHtml);
        sb.Append(body);
        return sb.ToString();
    }

    private static string StripHtml(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        var withoutScripts = Regex.Replace(html, @"<(script|style)[^>]*?>.*?</\1>", " ",
            RegexOptions.Singleline | RegexOptions.IgnoreCase);
        var withoutTags = Regex.Replace(withoutScripts, "<[^>]+>", " ");
        var decoded = System.Net.WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string BuildEndpoint(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        // If user already included /chat/completions or /v1/chat/completions, leave it
        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        return trimmed + "/chat/completions";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max) + "...";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; set; } = string.Empty;
        [JsonPropertyName("messages")] public ChatMessage[] Messages { get; set; } = Array.Empty<ChatMessage>();
        [JsonPropertyName("stream")] public bool Stream => false;
    }

    private class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; set; } = string.Empty;
        [JsonPropertyName("content")] public string Content { get; set; } = string.Empty;
    }

    private class ChatResponse
    {
        [JsonPropertyName("choices")] public ChatChoice[]? Choices { get; set; }
    }

    private class ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; set; }
    }
}
