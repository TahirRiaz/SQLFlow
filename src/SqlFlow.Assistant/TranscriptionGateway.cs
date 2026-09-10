using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using Azure.Core;
using Microsoft.Extensions.Logging;
using SqlFlow.Azure;

namespace SqlFlow.Assistant;

/// <summary>
/// Turns a recorded voice message into text for the chat composer, using the same provider account
/// the assistant answers from: an audio-transcription deployment in the Foundry account (managed
/// identity), or an OpenAI transcription model (API key). Both speak the same
/// <c>/audio/transcriptions</c> multipart wire format. The feature is opt-in per provider (a
/// configured deployment/model name); <see cref="Enabled"/> is false otherwise, and surfaces hide
/// their microphone when it is. Anthropic has no transcription API, so the feature is unavailable
/// under that provider.
/// </summary>
public sealed class TranscriptionGateway : IDisposable
{
    private const string AzureApiVersion = "2025-04-01-preview";
    private static readonly string[] AzureScopes = ["https://cognitiveservices.azure.com/.default"];

    /// <summary>The largest accepted recording; the providers cap uploads at 25 MB themselves, so a
    /// bigger body would only travel to be rejected.</summary>
    public const long MaxAudioBytes = 25_000_000;

    private readonly HttpClient _http;
    private readonly TokenCredential? _credential;
    private readonly string? _apiKey;
    private readonly Uri? _endpoint;
    private readonly string _model;
    private readonly ILogger<TranscriptionGateway> _logger;

    public TranscriptionGateway(
        AssistantSettings settings,
        IAzureCredentialFactory credentialFactory,
        ILogger<TranscriptionGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(credentialFactory);
        _logger = logger;
        switch (settings.Provider)
        {
            case AssistantProvider.AzureFoundry when settings.Foundry.TranscriptionDeploymentName.Length > 0:
                _credential = credentialFactory.Create();
                _apiKey = null;
                _model = settings.Foundry.TranscriptionDeploymentName;
                _endpoint = BuildAzureTranscriptionEndpoint(settings.Foundry.ProjectEndpoint, _model);
                break;
            case AssistantProvider.OpenAI when settings.OpenAI.TranscriptionModel.Length > 0:
                _credential = null;
                _apiKey = settings.OpenAI.ApiKey;
                _model = settings.OpenAI.TranscriptionModel;
                _endpoint = new Uri(settings.OpenAI.BaseUrl.TrimEnd('/') + "/v1/audio/transcriptions");
                break;
            default:
                // No transcription capability under this configuration; the gateway stays constructible
                // so hosts can register it unconditionally and expose Enabled to their capability surface.
                _credential = null;
                _apiKey = null;
                _model = "";
                _endpoint = null;
                break;
        }
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>Whether the configuration provides a transcription model at all.</summary>
    public bool Enabled => _endpoint is not null;

    /// <summary>
    /// Transcribes one recorded audio message and returns its text. The audio arrives as the
    /// browser recorded it (webm/ogg/mp4/wav); the file name's extension tells the provider the
    /// container format.
    /// </summary>
    public async Task<string> TranscribeAsync(
        Stream audio, string fileName, string contentType, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        if (_endpoint is null)
        {
            throw new InvalidOperationException(
                "Transcription is not configured: set Foundry:TranscriptionDeploymentName (AzureFoundry) or OpenAI:TranscriptionModel (OpenAI).");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, _endpoint);
        if (_credential is not null)
        {
            var token = await _credential.GetTokenAsync(new TokenRequestContext(AzureScopes), ct).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
        }

        using var form = new MultipartFormDataContent();
        using var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(audioContent, "file", fileName);
        // The deployment already fixes the model on Azure, but the field is accepted there and
        // required by OpenAI, so it is always sent.
        form.Add(new StringContent(_model), "model");
        form.Add(new StringContent("json"), "response_format");
        request.Content = form;

        using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
        var payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Transcription request failed with {Status}: {Payload}",
                (int)response.StatusCode, payload.Length <= 600 ? payload : payload[..600]);
            throw new InvalidOperationException(
                $"The transcription service returned {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        var text = JsonNode.Parse(payload)?["text"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException("The transcription service returned no text (was the recording silent?).");
        }
        return text.Trim();
    }

    /// <summary>The Azure transcription endpoint for a deployment, derived from the Foundry project
    /// endpoint exactly as the Responses endpoint is (first host label = the account).</summary>
    private static Uri BuildAzureTranscriptionEndpoint(string projectEndpoint, string deployment)
    {
        if (!Uri.TryCreate(projectEndpoint, UriKind.Absolute, out var parsed))
        {
            throw new InvalidOperationException(
                $"Foundry ProjectEndpoint '{projectEndpoint}' is not an absolute URL.");
        }
        var account = parsed.Host.Split('.', 2)[0];
        if (account.Length == 0)
        {
            throw new InvalidOperationException(
                $"Could not read the Foundry account name from ProjectEndpoint '{projectEndpoint}'.");
        }
        return new Uri(
            $"https://{account}.openai.azure.com/openai/deployments/{Uri.EscapeDataString(deployment)}/audio/transcriptions?api-version={AzureApiVersion}");
    }

    public void Dispose() => _http.Dispose();
}
