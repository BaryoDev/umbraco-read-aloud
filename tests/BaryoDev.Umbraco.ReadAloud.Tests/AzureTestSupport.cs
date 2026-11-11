using System.Net;
using BaryoDev.Umbraco.ReadAloud.Azure;
using Microsoft.Extensions.Logging;

namespace BaryoDev.Umbraco.ReadAloud.Tests;

internal static class AzureTestSupport
{
    /// <summary>Looks like nothing real, and is distinctive enough that finding it anywhere is a leak.</summary>
    public const string FakeKey = "test-key-do-not-leak-0123456789";

    public static string TempFolder() =>
        Path.Combine(Path.GetTempPath(), $"readaloud-keys-{Guid.NewGuid():N}");

    internal sealed class MemoryRecords : ICredentialRecords
    {
        public string? Value { get; set; }

        public string? Read() => Value;

        public void Write(string value) => Value = value;
    }

    /// <summary>Answers the voice list call from a function. Records every request, and never touches the network.</summary>
    internal sealed class FakeVoiceHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(Uri Uri, string? Key)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls.Add((request.RequestUri!, request.Headers.TryGetValues("Ocp-Apim-Subscription-Key", out var v) ? v.Single() : null));
            return Task.FromResult(respond(request));
        }

        public static FakeVoiceHandler Voices(params string[] names) =>
            new(_ => Json(names));

        public static FakeVoiceHandler Status(HttpStatusCode code) =>
            new(_ => new HttpResponseMessage(code));

        public static FakeVoiceHandler Throws() =>
            new(_ => throw new HttpRequestException("connection refused for " + FakeKey));

        public static HttpResponseMessage Json(params string[] names) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "[" + string.Join(",", names.Select(n => $"{{\"ShortName\":\"{n}\",\"Locale\":\"x\"}}")) + "]",
                    System.Text.Encoding.UTF8,
                    "application/json"),
            };
    }

    internal sealed class FakeHttpFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    internal sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Lines.Add($"{logLevel}: {formatter(state, exception)} {exception}");
        }
    }
}
