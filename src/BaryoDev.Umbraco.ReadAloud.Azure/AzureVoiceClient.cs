using System.Text.Json;

namespace BaryoDev.Umbraco.ReadAloud.Azure;

internal enum VoiceFetchStatus
{
    Ok,
    Invalid,
    Rejected,
    Network,
    Other,
}

/// <summary>What one call to the voice list endpoint came to. Never carries the key.</summary>
internal sealed record VoiceFetchResult(VoiceFetchStatus Status, IReadOnlyList<string> Voices, int? Code = null);

/// <summary>Fetches the voice list of a region with a key. A seam so tests need no network.</summary>
internal interface IAzureVoiceClient
{
    Task<VoiceFetchResult> FetchAsync(string region, string key, CancellationToken ct);
}

/// <summary>
/// Calls <c>GET https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list</c>.
/// </summary>
/// <remarks>
/// The region is checked to be lowercase letters and digits before it goes into the host name, so
/// it cannot point the call anywhere else. The key only goes into a request header. No exception
/// or response text leaves this class, because both can echo what was sent.
/// </remarks>
internal sealed class AzureVoiceClient : IAzureVoiceClient
{
    internal const string HttpClientName = "BaryoDev.ReadAloud.AzureVoices";

    private readonly IHttpClientFactory _factory;
    private readonly TimeSpan _timeout;

    public AzureVoiceClient(IHttpClientFactory factory)
        : this(factory, TimeSpan.FromSeconds(10))
    {
    }

    internal AzureVoiceClient(IHttpClientFactory factory, TimeSpan timeout)
    {
        _factory = factory;
        _timeout = timeout;
    }

    internal static bool IsHostSafe(string region) => AzureSpeechProviderSettings.IsRegionName(region);

    public async Task<VoiceFetchResult> FetchAsync(string region, string key, CancellationToken ct)
    {
        if (!IsHostSafe(region) || key.Length == 0)
        {
            return new VoiceFetchResult(VoiceFetchStatus.Invalid, []);
        }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get, $"https://{region}.tts.speech.microsoft.com/cognitiveservices/voices/list");
            request.Headers.Add("Ocp-Apim-Subscription-Key", key);

            using var response = await _factory.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, limit.Token);

            var code = (int)response.StatusCode;

            if (code is 401 or 403) return new VoiceFetchResult(VoiceFetchStatus.Rejected, [], code);
            if (!response.IsSuccessStatusCode) return new VoiceFetchResult(VoiceFetchStatus.Other, [], code);

            var json = await response.Content.ReadAsStringAsync(limit.Token);
            var voices = Parse(json);

            return voices is null
                ? new VoiceFetchResult(VoiceFetchStatus.Other, [], code)
                : new VoiceFetchResult(VoiceFetchStatus.Ok, voices, code);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (FormatException)
        {
            // A key that cannot be a header value.
            return new VoiceFetchResult(VoiceFetchStatus.Invalid, []);
        }
        catch (Exception)
        {
            // Timeout, DNS, TLS, reset: all read as "could not reach Azure". The exception is
            // dropped on purpose.
            return new VoiceFetchResult(VoiceFetchStatus.Network, []);
        }
    }

    private static List<string>? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            var names = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Object
                    && item.TryGetProperty("ShortName", out var name)
                    && name.ValueKind == JsonValueKind.String
                    && name.GetString() is { Length: > 0 } value)
                {
                    names.Add(value);
                }
            }

            return [.. names];
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
