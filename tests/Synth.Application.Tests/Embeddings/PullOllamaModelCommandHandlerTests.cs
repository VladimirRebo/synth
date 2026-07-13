using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Synth.Application.Embeddings;

namespace Synth.Application.Tests.Embeddings;

// Proves SYNTH-70: the Ollama model-pull "validate, reserve the single slot, dispatch fire-and-forget"
// flow, plus the /api/pull progress-line parsing, now live in PullOllamaModelCommandHandler — unchanged in
// behavior from the old OllamaModelEndpoints POST handler. Runs offline: a deterministic stand-in
// IHttpClientFactory feeds a canned newline-delimited-JSON pull stream (no live Ollama), a fake resolver
// supplies the endpoint, and a real InMemoryOllamaPullTracker records progress.
public class PullOllamaModelCommandHandlerTests
{
    private const string OllamaEndpoint = "http://ollama.test:11434";

    private readonly InMemoryOllamaPullTracker _tracker = new();

    private PullOllamaModelCommandHandler CreateHandler(
        FakeHttpClientFactory httpFactory, string? endpoint = OllamaEndpoint) =>
        new(httpFactory, new FakeEndpointResolver(endpoint), _tracker, NullLoggerFactory.Instance);

    [Fact]
    public async Task Rejects_a_blank_model_with_a_validation_error()
    {
        var handler = CreateHandler(FakeHttpClientFactory.Responding((_, _) => Ndjson("""{ "status": "success" }""")));

        var result = await handler.HandleAsync(new PullOllamaModelCommand("   "));

        Assert.Equal(PullOllamaModelResult.Kind.ValidationError, result.Status);
        // Nothing was reserved.
        Assert.Equal(OllamaPullState.Idle, _tracker.Current.State);
    }

    [Fact]
    public async Task Rejects_a_missing_endpoint_with_a_validation_error()
    {
        var handler = CreateHandler(
            FakeHttpClientFactory.Responding((_, _) => Ndjson("""{ "status": "success" }""")), endpoint: null);

        var result = await handler.HandleAsync(new PullOllamaModelCommand("nomic-embed-text"));

        Assert.Equal(PullOllamaModelResult.Kind.ValidationError, result.Status);
        Assert.Contains("endpoint", result.Error);
        Assert.Equal(OllamaPullState.Idle, _tracker.Current.State);
    }

    [Fact]
    public async Task Returns_AlreadyRunning_when_a_pull_is_already_in_progress()
    {
        // Reserve the single slot up front so the handler's TryStart-guarded conflict path is hit.
        Assert.True(_tracker.TryStart("already-running"));
        var handler = CreateHandler(FakeHttpClientFactory.Responding((_, _) => Ndjson("""{ "status": "success" }""")));

        var result = await handler.HandleAsync(new PullOllamaModelCommand("another"));

        Assert.Equal(PullOllamaModelResult.Kind.AlreadyRunning, result.Status);
    }

    [Fact]
    public async Task Starts_immediately_and_the_background_pull_reaches_Done_with_progress()
    {
        // A canned /api/pull stream: manifest -> a download line with byte counts -> success.
        var httpFactory = FakeHttpClientFactory.Responding((request, _) =>
        {
            Assert.Equal($"{OllamaEndpoint}/api/pull", request.RequestUri?.ToString());
            return Ndjson(
                """{ "status": "pulling manifest" }""",
                """{ "status": "downloading", "completed": 50, "total": 100 }""",
                """{ "status": "success" }""");
        });
        var handler = CreateHandler(httpFactory);

        var result = await handler.HandleAsync(new PullOllamaModelCommand("nomic-embed-text"));

        // Fire-and-forget: Started with the model before the background run has necessarily finished.
        Assert.Equal(PullOllamaModelResult.Kind.Started, result.Status);
        Assert.Equal("nomic-embed-text", result.Model);

        await WaitForTerminalAsync();

        Assert.Equal(OllamaPullState.Done, _tracker.Current.State);
        Assert.Equal("nomic-embed-text", _tracker.Current.Model);
        Assert.Null(_tracker.Current.Error);
    }

    [Fact]
    public async Task Trims_the_model_before_reserving_and_dispatching()
    {
        var httpFactory = FakeHttpClientFactory.Responding((_, _) => Ndjson("""{ "status": "success" }"""));
        var handler = CreateHandler(httpFactory);

        var result = await handler.HandleAsync(new PullOllamaModelCommand("  nomic-embed-text  "));

        Assert.Equal(PullOllamaModelResult.Kind.Started, result.Status);
        Assert.Equal("nomic-embed-text", result.Model);

        await WaitForTerminalAsync();
        Assert.Equal("nomic-embed-text", _tracker.Current.Model);
    }

    [Fact]
    public async Task Surfaces_a_download_progress_line_as_a_percentage()
    {
        // Hold the stream open on the download line so the tracker can be observed mid-pull.
        var release = new TaskCompletionSource();
        var httpFactory = FakeHttpClientFactory.Responding((_, _) => Ndjson(
            """{ "status": "downloading", "completed": 42, "total": 100 }""",
            release.Task));
        var handler = CreateHandler(httpFactory);

        await handler.HandleAsync(new PullOllamaModelCommand("nomic-embed-text"));

        await WaitForAsync(() => _tracker.Current.Status.Contains("42%"));
        Assert.Equal(OllamaPullState.Running, _tracker.Current.State);
        Assert.Equal("downloading (42%)", _tracker.Current.Status);

        release.SetResult();
        await WaitForTerminalAsync();
        Assert.Equal(OllamaPullState.Done, _tracker.Current.State);
    }

    [Fact]
    public async Task Records_a_stream_error_line_as_Failed()
    {
        var httpFactory = FakeHttpClientFactory.Responding((_, _) =>
            Ndjson("""{ "error": "pull model manifest: file does not exist" }"""));
        var handler = CreateHandler(httpFactory);

        var result = await handler.HandleAsync(new PullOllamaModelCommand("does-not-exist"));
        Assert.Equal(PullOllamaModelResult.Kind.Started, result.Status);

        await WaitForTerminalAsync();
        Assert.Equal(OllamaPullState.Failed, _tracker.Current.State);
        Assert.Contains("file does not exist", _tracker.Current.Error);
    }

    [Fact]
    public async Task Records_a_non_success_http_response_as_Failed()
    {
        var httpFactory = FakeHttpClientFactory.Responding((_, _) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var handler = CreateHandler(httpFactory);

        await handler.HandleAsync(new PullOllamaModelCommand("nomic-embed-text"));

        await WaitForTerminalAsync();
        Assert.Equal(OllamaPullState.Failed, _tracker.Current.State);
    }

    private Task WaitForTerminalAsync(TimeSpan? timeout = null) =>
        WaitForAsync(() => _tracker.Current.State is OllamaPullState.Done or OllamaPullState.Failed, timeout);

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Condition was not met in time.");
            await Task.Delay(25);
        }
    }

    private static HttpResponseMessage Ndjson(params string[] lines) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                string.Join('\n', lines.Select(JsonMinify)), Encoding.UTF8, "application/x-ndjson"),
        };

    // An ndjson response whose body streams the given lines, then blocks until `release` completes before
    // ending the stream — lets a test observe the tracker's mid-pull progress.
    private static HttpResponseMessage Ndjson(string line, Task release) =>
        new(HttpStatusCode.OK) { Content = new StreamContent(new BlockingLineStream(JsonMinify(line), release)) };

    // Collapse the multi-line raw string literals above into single physical lines so the handler's
    // line-by-line reader sees exactly one JSON object per line.
    private static string JsonMinify(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(doc.RootElement);
    }

    // A read-only stream that yields "<line>\n" then blocks reads until `release` completes, at which point
    // it reports end-of-stream. Enough to keep the pull's reader parked on one progress line.
    private sealed class BlockingLineStream(string line, Task release) : Stream
    {
        private readonly byte[] _payload = Encoding.UTF8.GetBytes(line + "\n");
        private int _position;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _payload.Length)
            {
                var count = Math.Min(buffer.Length, _payload.Length - _position);
                _payload.AsMemory(_position, count).CopyTo(buffer);
                _position += count;
                return count;
            }

            await release.WaitAsync(cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class FakeEndpointResolver(string? endpoint) : IOllamaEndpointResolver
    {
        public string? Resolve() => endpoint;
    }

    // A deterministic stand-in for IHttpClientFactory: every CreateClient() returns an HttpClient over a
    // stub handler that either invokes a supplied responder or throws. No live Ollama.
    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly StubHandler _handler;

        private FakeHttpClientFactory(StubHandler handler) => _handler = handler;

        public static FakeHttpClientFactory Responding(
            Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) =>
            new(new StubHandler(responder));

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);

        private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder)
            : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(responder(request, cancellationToken));
        }
    }
}
