using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ReconAI.Core.Ai;
using ReconAI.Core.Importing;
using ReconAI.Web.Services;

namespace ReconAI.Tests.Ai;

/// <summary>The client must turn every failure of the AI service into a status, and never into an exception.</summary>
public class AiMappingClientTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public string? LastBody { get; private set; }
        public Uri? LastUri { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await send(request);
        }
    }

    private static readonly IReadOnlyList<AiColumnSample> Columns =
    [
        new("Order_Ref", ["RCH-1", "RCH-2"]),
        new("Amount_INR", ["599.00", "299.00"]),
    ];

    private static AiMappingClient Client(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://127.0.0.1:8765"), Timeout = TimeSpan.FromSeconds(5) }, NullLogger<AiMappingClient>.Instance);

    private static HttpResponseMessage Json(object body, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static object GoodBody() => new
    {
        model = new { name = "reconai-column-ranker", version = "abcd1234", algorithm = "lr", trainingRows = 5000 },
        latencyMs = 9,
        suggestions = new object[]
        {
            new { columnIndex = 0, header = "Order_Ref", field = "TransactionRef", confidence = 0.96, band = "high", reasons = new[] { "r" }, alternatives = Array.Empty<object>() },
            new { columnIndex = 1, header = "Amount_INR", field = "Amount", confidence = 0.91, band = "high", reasons = new[] { "r" }, alternatives = Array.Empty<object>() },
        },
    };

    [Fact]
    public async Task A_good_answer_is_returned_as_validated_suggestions()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(GoodBody())));
        var result = await Client(handler).SuggestMappingAsync(SourceKind.Ledger, Columns, CancellationToken.None);

        Assert.True(result.Ok);
        Assert.Equal([CanonicalField.TransactionRef, CanonicalField.Amount], result.Value!.Suggestions.Select(s => s.Field));
        Assert.Equal("/v1/map-columns", handler.LastUri!.AbsolutePath);
    }

    [Fact]
    public async Task The_request_carries_only_the_source_type_headings_and_samples()
    {
        var handler = new StubHandler(_ => Task.FromResult(Json(GoodBody())));
        await Client(handler).SuggestMappingAsync(SourceKind.Ledger, Columns, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(["sourceKind", "columns"], body.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal("Ledger", body.RootElement.GetProperty("sourceKind").GetString());
        var first = body.RootElement.GetProperty("columns")[0];
        Assert.Equal("Order_Ref", first.GetProperty("header").GetString());
        Assert.Equal(["RCH-1", "RCH-2"], first.GetProperty("samples").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task An_error_status_becomes_unavailable_and_not_an_exception()
    {
        var result = await Client(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError))))
            .SuggestMappingAsync(SourceKind.Ledger, Columns, CancellationToken.None);
        Assert.Equal(AiCallStatus.Unavailable, result.Status);
        Assert.Contains("manually", result.Message);
    }

    [Fact]
    public async Task A_connection_failure_becomes_unavailable()
    {
        var result = await Client(new StubHandler(_ => throw new HttpRequestException("connection refused")))
            .SuggestMappingAsync(SourceKind.Ledger, Columns, CancellationToken.None);
        Assert.Equal(AiCallStatus.Unavailable, result.Status);
        Assert.Contains("not running", result.Message);
    }

    [Fact]
    public async Task A_timeout_becomes_a_timeout_status()
    {
        var result = await Client(new StubHandler(_ => throw new TaskCanceledException("timed out", new TimeoutException())))
            .SuggestMappingAsync(SourceKind.Ledger, Columns, CancellationToken.None);
        Assert.Equal(AiCallStatus.Timeout, result.Status);
    }

    [Fact]
    public async Task Cancellation_by_the_caller_is_not_mistaken_for_a_timeout()
    {
        using var cts = new CancellationTokenSource();
        var handler = new StubHandler(_ => { cts.Cancel(); throw new TaskCanceledException(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(handler).SuggestMappingAsync(SourceKind.Ledger, Columns, cts.Token));
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"suggestions\": \"wrong type\"}")]
    [InlineData("{\"model\": {\"name\": \"x\", \"version\": \"1\"}, \"suggestions\": null}")]    // fails validation: no suggestions list
    [InlineData("{\"suggestions\": []}")]                                                          // fails validation: no model
    public async Task A_garbled_or_incomplete_answer_is_an_invalid_response(string body)
    {
        var result = await Client(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") })))
            .SuggestMappingAsync(SourceKind.Ledger, Columns, CancellationToken.None);
        Assert.Equal(AiCallStatus.InvalidResponse, result.Status);
        Assert.Null(result.Value);
    }

    [Fact]
    public async Task An_answer_with_forbidden_fields_is_validated_not_obeyed()
    {
        var body = new
        {
            model = new { name = "m", version = "1", algorithm = "a", trainingRows = 1 },
            latencyMs = 1,
            suggestions = new object[]
            {
                new { columnIndex = 0, header = "Order_Ref", field = "Fees", confidence = 0.99, band = "high", reasons = Array.Empty<string>(), alternatives = Array.Empty<object>() },
                new { columnIndex = 1, header = "Amount_INR", field = "Amount", confidence = 0.4, band = "high", reasons = Array.Empty<string>(), alternatives = Array.Empty<object>() },
            },
        };
        var result = await Client(new StubHandler(_ => Task.FromResult(Json(body)))).SuggestMappingAsync(SourceKind.Ledger, Columns, CancellationToken.None);   // Fees is not a ledger field

        Assert.True(result.Ok);
        Assert.Null(result.Value!.Suggestions[0].Field);
        Assert.Equal("low", result.Value.Suggestions[1].Band);
        Assert.Single(result.Value.Problems);
    }
}
