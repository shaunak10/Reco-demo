using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Ai;
using ReconAI.Core.Importing;
using ReconAI.Core.Security;
using ReconAI.Data;
using ReconAI.Tests.Support;
using static ReconAI.Tests.Support.Scenarios;

namespace ReconAI.Tests.Ai;

/// <summary>
/// Step 4 through the real pipeline: AI suggestions are only suggestions. They are validated, logged without values,
/// never saved or imported by themselves, never block the manual path, and the operator's confirmation is what counts.
/// </summary>
public sealed class AiMappingFlowTests : IDisposable
{
    private const string Scenario = "scenario1_main";
    private readonly FakeAiMappingClient _ai = new();
    private readonly ReconWebFactory _factory;

    public AiMappingFlowTests()
    {
        _ai.Respond = FakeAiMappingClient.GroundTruth(Scenario);
        _factory = new ReconWebFactory(_ai);
    }

    public void Dispose() => _factory.Dispose();

    private async Task<HttpClient> OperatorAsync()
    {
        var client = _factory.NewClient();
        var creds = _factory.Seeded(UserRole.Operator);
        Assert.Equal(HttpStatusCode.Redirect, (await client.LoginAsync(creds.Email, creds.Password)).StatusCode);
        return client;
    }

    private static async Task<int> UploadAsync(HttpClient client, SourceKind kind, string scenario = Scenario) =>
        (await client.UploadAsync(kind, FileOf(scenario, kind), Bytes(scenario, kind))).FileIdFrom();

    private Task<int> Count<T>() where T : class => _factory.WithDbAsync(db => db.Set<T>().IgnoreQueryFilters().CountAsync());

    // ------------------------------------------------------------------ suggestions are only suggestions
    [Fact]
    public async Task Suggest_returns_validated_suggestions_with_confidence_and_reasons()
    {
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Ledger);

        using var json = await client.SuggestAsync(id);
        var root = json.RootElement;
        Assert.True(root.GetProperty("available").GetBoolean());
        Assert.Equal("reconai-column-ranker", root.GetProperty("model").GetProperty("name").GetString());

        var suggestions = root.GetProperty("suggestions").EnumerateArray().ToList();
        Assert.Equal(8, suggestions.Count);
        var orderRef = suggestions[1];
        Assert.Equal(1, orderRef.GetProperty("columnIndex").GetInt32());
        Assert.Equal("TransactionRef", orderRef.GetProperty("field").GetString());
        Assert.Equal("Transaction reference", orderRef.GetProperty("fieldLabel").GetString());
        Assert.Equal("high", orderRef.GetProperty("band").GetString());
        Assert.True(orderRef.GetProperty("reasons").GetArrayLength() > 0);
        Assert.Equal(JsonValueKind.Null, suggestions[0].GetProperty("field").ValueKind);        // Ledger_ID -> ignore
    }

    [Fact]
    public async Task Asking_the_ai_saves_nothing_and_imports_nothing()
    {
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Ledger);
        using var _ = await client.SuggestAsync(id);

        Assert.Equal(0, await Count<SourceRow>());
        Assert.Equal(0, await Count<ColumnMapping>());
        var file = await _factory.WithDbAsync(db => db.SourceFiles.IgnoreQueryFilters().SingleAsync());
        Assert.Equal(SourceFileStatus.AwaitingMapping, file.Status);      // only the operator's confirmation can change this
    }

    [Fact]
    public async Task The_ai_is_given_the_headings_and_at_most_thirty_samples_per_column()
    {
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Gateway);
        using var _ = await client.SuggestAsync(id);

        var call = Assert.Single(_ai.Calls);
        Assert.Equal(SourceKind.Gateway, call.Kind);
        var headers = TabularReader.Read(Bytes(Scenario, SourceKind.Gateway), "g.xlsx").Headers;
        Assert.Equal(headers, call.Columns.Select(c => c.Header));
        Assert.All(call.Columns, c => Assert.Equal(30, c.Samples.Count));          // 61 rows in the file, capped at 30
    }

    [Fact]
    public async Task Every_call_is_logged_with_model_and_status_but_never_with_values()
    {
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Ledger);
        using var _ = await client.SuggestAsync(id);

        var log = await _factory.WithDbAsync(db => db.AIInferenceLogs.IgnoreQueryFilters().SingleAsync());
        Assert.Equal((AiTask.ColumnMapping, AiCallStatus.Ok, "reconai-column-ranker", "test0001", id), (log.Task, log.Status, log.ModelName, log.ModelVersion, log.SourceFileId));
        Assert.Equal(_factory.Seeded(UserRole.Operator).Email, log.UserEmail);
        Assert.Contains("8 columns", log.InputSummary);

        var everything = $"{log.InputSummary} {log.OutputJson} {log.Detail}";
        Assert.DoesNotContain("RCH-26", everything);                                // no references
        Assert.DoesNotContain("599.00", everything);                                // no amounts
        Assert.Contains("\"F\":\"TransactionRef\"", log.OutputJson);                // only index, field, confidence

        var audit = await _factory.WithDbAsync(db => db.AuditEvents.IgnoreQueryFilters().Where(a => a.Action == "AiMappingSuggested").ToListAsync());
        Assert.Single(audit);
        Assert.DoesNotContain("RCH-26", audit[0].Detail);
    }

    // ------------------------------------------------------------------ the AI can fail without blocking anything
    [Theory]
    [InlineData(AiCallStatus.Unavailable)]
    [InlineData(AiCallStatus.Timeout)]
    [InlineData(AiCallStatus.InvalidResponse)]
    public async Task When_the_ai_fails_the_screen_says_so_and_manual_mapping_still_works(AiCallStatus status)
    {
        _ai.Respond = (_, _) => new(status, null, $"AI problem: {status}", 5);
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Ledger);

        using var json = await client.SuggestAsync(id);
        Assert.False(json.RootElement.GetProperty("available").GetBoolean());
        Assert.Contains(status.ToString(), json.RootElement.GetProperty("message").GetString());
        Assert.Equal(0, json.RootElement.GetProperty("suggestions").GetArrayLength());

        var log = await _factory.WithDbAsync(db => db.AIInferenceLogs.IgnoreQueryFilters().SingleAsync());
        Assert.Equal(status, log.Status);

        var confirm = await client.PostMappingAsync(id, "Confirm", Fields(Scenario, SourceKind.Ledger));    // fully manual
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Equal(61, await Count<SourceRow>());
        Assert.Equal("Manual", (await _factory.WithDbAsync(db => db.ColumnMappings.IgnoreQueryFilters().SingleAsync())).Origin);
    }

    [Fact]
    public async Task The_real_client_reports_unavailable_when_nothing_is_listening()
    {
        using var factory = new ReconWebFactory();                                   // real AiMappingClient -> closed port
        var client = factory.NewClient();
        await client.LoginAsync(factory.Seeded(UserRole.Operator).Email, factory.Seeded(UserRole.Operator).Password);
        var id = (await client.UploadAsync(SourceKind.Ledger, "l.csv", Bytes(Scenario, SourceKind.Ledger))).FileIdFrom();

        using var json = await client.SuggestAsync(id);
        Assert.False(json.RootElement.GetProperty("available").GetBoolean());
        Assert.Contains("not running", json.RootElement.GetProperty("message").GetString());
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostMappingAsync(id, "Confirm", Fields(Scenario, SourceKind.Ledger))).StatusCode);
    }

    [Fact]
    public async Task Invalid_ai_content_is_discarded_by_validation()
    {
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Bank);
        _ai.Respond = (kind, columns) =>
        {
            var headings = columns.Select(c => c.Header).ToList();
            var raw = new RawMappingResponse(new RawModel("m", "1", "a", 1), 1, columns.Select((c, i) => new RawSuggestion(i, c.Header,
                i == 0 ? "Fees" : i == 1 ? "'; DROP TABLE SourceRows;--" : null, 0.9, "high", ["x"], [])).ToList());
            return new(AiCallStatus.Ok, AiMappingValidator.Validate(kind, headings, raw), "", 3);
        };

        using var json = await client.SuggestAsync(id);
        Assert.Equal(2, json.RootElement.GetProperty("problems").GetArrayLength());
        Assert.All(json.RootElement.GetProperty("suggestions").EnumerateArray(), s => Assert.Equal(JsonValueKind.Null, s.GetProperty("field").ValueKind));
    }

    // ------------------------------------------------------------------ provenance: the operator's confirmation is what counts
    [Fact]
    public async Task Accepting_the_ai_suggestion_unchanged_is_recorded_as_such()
    {
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Ledger);
        using var json = await client.SuggestAsync(id);
        var inference = json.RootElement.GetProperty("inferenceId").GetInt64();

        var confirm = await client.ConfirmWithAiAsync(id, Fields(Scenario, SourceKind.Ledger), inference);
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);

        var mapping = await _factory.WithDbAsync(db => db.ColumnMappings.IgnoreQueryFilters().SingleAsync());
        Assert.Equal("AI-assisted (accepted as suggested)", mapping.Origin);
        Assert.Equal(inference, mapping.AiInferenceId);
        Assert.Equal((4, 4, 0), (mapping.AiSuggestedCount, mapping.AiAcceptedCount, mapping.AiChangedColumns));   // 4 fields suggested, all kept
        Assert.Equal(61, await Count<SourceRow>());

        var page = await (await client.GetAsync($"/Recon/File/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("AI-assisted", page);
    }

    [Fact]
    public async Task Changing_a_suggestion_is_recorded_as_an_operator_edit()
    {
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Ledger);
        using var json = await client.SuggestAsync(id);
        var inference = json.RootElement.GetProperty("inferenceId").GetInt64();

        var edited = Fields(Scenario, SourceKind.Ledger);
        var headers = TabularReader.Read(Bytes(Scenario, SourceKind.Ledger), "l.csv").Headers.ToList();
        edited[headers.IndexOf("Order_Status")] = "";                                   // operator drops the Status column

        Assert.Equal(HttpStatusCode.Redirect, (await client.ConfirmWithAiAsync(id, edited, inference)).StatusCode);
        var mapping = await _factory.WithDbAsync(db => db.ColumnMappings.IgnoreQueryFilters().SingleAsync());
        Assert.Equal("AI-assisted (operator changed 1 of 8 columns)", mapping.Origin);
        Assert.Equal((4, 3, 1), (mapping.AiSuggestedCount, mapping.AiAcceptedCount, mapping.AiChangedColumns));
    }

    [Fact]
    public async Task An_inference_from_another_file_or_a_failed_call_is_ignored_for_provenance()
    {
        var client = await OperatorAsync();
        var ledger = await UploadAsync(client, SourceKind.Ledger);
        var bank = await UploadAsync(client, SourceKind.Bank);
        using var forLedger = await client.SuggestAsync(ledger);
        var ledgerInference = forLedger.RootElement.GetProperty("inferenceId").GetInt64();

        // bank file confirmed while quoting the LEDGER file's AI call -> must be treated as manual
        Assert.Equal(HttpStatusCode.Redirect, (await client.ConfirmWithAiAsync(bank, Fields(Scenario, SourceKind.Bank), ledgerInference)).StatusCode);
        var bankMapping = await _factory.WithDbAsync(db => db.ColumnMappings.IgnoreQueryFilters().SingleAsync(m => m.SourceFileId == bank));
        Assert.Equal(("Manual", (long?)null), (bankMapping.Origin, bankMapping.AiInferenceId));

        // a made-up id is ignored too
        Assert.Equal(HttpStatusCode.Redirect, (await client.ConfirmWithAiAsync(ledger, Fields(Scenario, SourceKind.Ledger), 999_999)).StatusCode);
        Assert.Equal("Manual", (await _factory.WithDbAsync(db => db.ColumnMappings.IgnoreQueryFilters().SingleAsync(m => m.SourceFileId == ledger))).Origin);
    }

    [Fact]
    public async Task A_failed_ai_call_cannot_be_quoted_as_provenance()
    {
        _ai.Respond = (_, _) => new(AiCallStatus.Timeout, null, "slow", 9);
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Ledger);
        using var json = await client.SuggestAsync(id);
        var failedCall = json.RootElement.GetProperty("inferenceId").GetInt64();

        await client.ConfirmWithAiAsync(id, Fields(Scenario, SourceKind.Ledger), failedCall);
        Assert.Equal("Manual", (await _factory.WithDbAsync(db => db.ColumnMappings.IgnoreQueryFilters().SingleAsync())).Origin);
    }

    // ------------------------------------------------------------------ access and isolation
    [Fact]
    public async Task Another_company_cannot_ask_the_ai_about_my_file_or_quote_my_inference()
    {
        var mine = await OperatorAsync();
        var id = await UploadAsync(mine, SourceKind.Ledger);
        using var forMine = await mine.SuggestAsync(id);
        var inference = forMine.RootElement.GetProperty("inferenceId").GetInt64();

        await _factory.AddTenantUserAsync("Other Co (Synthetic)", "operator@other.local", "Other-Op-Passw0rd!", UserRole.Operator);
        var theirs = _factory.NewClient();
        await theirs.LoginAsync("operator@other.local", "Other-Op-Passw0rd!");
        var theirId = (await theirs.UploadAsync(SourceKind.Ledger, "mine.csv", Bytes(Scenario, SourceKind.Ledger))).FileIdFrom();

        var token = await theirs.TokenFromAsync($"/Recon/Mapping/{theirId}");
        var denied = await theirs.PostAsync($"/Recon/Mapping/{id}?handler=Suggest", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);

        await theirs.ConfirmWithAiAsync(theirId, Fields(Scenario, SourceKind.Ledger), inference);    // my inference id, their file
        var theirMapping = await _factory.WithDbAsync(db => db.ColumnMappings.IgnoreQueryFilters().SingleAsync(m => m.SourceFileId == theirId));
        Assert.Equal("Manual", theirMapping.Origin);
    }

    [Fact]
    public async Task The_suggest_endpoint_requires_sign_in_a_token_and_an_unmapped_file()
    {
        var client = await OperatorAsync();
        var id = await UploadAsync(client, SourceKind.Ledger);

        var noToken = await client.PostAsync($"/Recon/Mapping/{id}?handler=Suggest", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, noToken.StatusCode);

        var anonymous = await _factory.NewClient().PostAsync($"/Recon/Mapping/{id}?handler=Suggest", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.Redirect, anonymous.StatusCode);

        await client.PostMappingAsync(id, "Confirm", Fields(Scenario, SourceKind.Ledger));          // now imported
        var token = await client.TokenFromAsync("/Recon/New");
        var imported = await client.PostAsync($"/Recon/Mapping/{id}?handler=Suggest", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
        Assert.Equal(HttpStatusCode.NotFound, imported.StatusCode);
    }

    [Fact]
    public async Task The_ai_activity_page_is_admin_only_and_shows_what_the_ai_did()
    {
        var op = await OperatorAsync();
        var id = await UploadAsync(op, SourceKind.Ledger);
        using var json = await op.SuggestAsync(id);
        await op.ConfirmWithAiAsync(id, Fields(Scenario, SourceKind.Ledger), json.RootElement.GetProperty("inferenceId").GetInt64());

        var denied = await op.GetAsync("/Admin/AiActivity");
        Assert.Equal(HttpStatusCode.Redirect, denied.StatusCode);
        Assert.StartsWith("/Account/AccessDenied", denied.Location());

        var admin = _factory.NewClient();
        await admin.LoginAsync(_factory.Seeded(UserRole.CompanyAdmin).Email, _factory.Seeded(UserRole.CompanyAdmin).Password);
        var html = await (await admin.GetAsync("/Admin/AiActivity")).Content.ReadAsStringAsync();
        Assert.Contains("reconai-column-ranker v" + "test0001", html);
        Assert.Contains("AI calls", html);
        Assert.Contains("100%", html);                                                              // 4 of 4 suggestions accepted
        Assert.DoesNotContain("RCH-26", html);                                                      // no source values on the page
    }
}
