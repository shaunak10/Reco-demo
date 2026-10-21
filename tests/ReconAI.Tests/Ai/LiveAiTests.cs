using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Importing;
using ReconAI.Core.Security;
using ReconAI.Data;
using ReconAI.Tests.Support;
using Xunit.Abstractions;
using static ReconAI.Tests.Support.Scenarios;

namespace ReconAI.Tests.Ai;

/// <summary>Where the real Python service is expected (override with RECONAI_AI_URL). Live tests skip themselves when it is not running.</summary>
public static class LiveAi
{
    public static readonly string Url = Environment.GetEnvironmentVariable("RECONAI_AI_URL") ?? "http://127.0.0.1:8765";
    public static readonly bool Available = Probe();

    private static bool Probe()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var doc = JsonDocument.Parse(http.GetStringAsync($"{Url}/health").GetAwaiter().GetResult());
            return doc.RootElement.GetProperty("capabilities").GetProperty("column_mapping").GetBoolean();
        }
        catch (Exception)
        {
            return false;
        }
    }
}

public sealed class LiveAiTheoryAttribute : TheoryAttribute
{
    public LiveAiTheoryAttribute()
    {
        if (!LiveAi.Available) Skip = $"The AI service is not running at {LiveAi.Url}; start it to run the live end-to-end tests.";
    }
}

/// <summary>
/// Step 4 exit test, end to end with NO fakes: real upload, real Python model, real validation, real import.
/// "Evaluator can change headers in a test file and receive meaningful different suggestions; record actual correctness."
/// </summary>
public sealed class LiveAiTests(ITestOutputHelper output)
{
    [LiveAiTheory]
    [InlineData("scenario1_main", 61, 61, 7)]
    [InlineData("scenario2_clean", 24, 24, 3)]
    [InlineData("scenario3_changed_headers", 61, 61, 7)]       // same data, different headings and file formats
    public async Task The_real_model_suggests_the_ground_truth_and_its_suggestions_import_cleanly(string scenario, int ledgerRows, int gatewayRows, int bankRows)
    {
        using var factory = new ReconWebFactory(null, LiveAi.Url);          // the real AiMappingClient talking to the real service
        var client = factory.NewClient();
        var creds = factory.Seeded(UserRole.Operator);
        Assert.Equal(HttpStatusCode.Redirect, (await client.LoginAsync(creds.Email, creds.Password)).StatusCode);

        int correct = 0, total = 0;
        foreach (var (kind, rows) in new[] { (SourceKind.Ledger, ledgerRows), (SourceKind.Gateway, gatewayRows), (SourceKind.Bank, bankRows) })
        {
            var id = (await client.UploadAsync(kind, FileOf(scenario, kind), Bytes(scenario, kind))).FileIdFrom();
            using var json = await client.SuggestAsync(id);
            var root = json.RootElement;
            Assert.True(root.GetProperty("available").GetBoolean(), root.GetProperty("message").GetString());
            Assert.Equal("reconai-column-ranker", root.GetProperty("model").GetProperty("name").GetString());

            var truth = Fields(scenario, kind);
            var suggestedFields = new List<string>();
            foreach (var s in root.GetProperty("suggestions").EnumerateArray())
            {
                var i = s.GetProperty("columnIndex").GetInt32();
                var field = s.GetProperty("field").ValueKind == JsonValueKind.Null ? "" : s.GetProperty("field").GetString()!;
                suggestedFields.Add(field);
                total++;
                if (field == truth[i]) correct++;
                else output.WriteLine($"MISS {scenario}/{kind} column {i}: suggested '{field}', expected '{truth[i]}' ({s.GetProperty("confidence").GetDouble():P0})");
            }
            output.WriteLine($"{scenario}/{kind}: {root.GetProperty("latencyMs").GetInt32()} ms in the service");

            // the operator accepts the AI's suggestion exactly as given -> it must import cleanly
            var confirm = await client.ConfirmWithAiAsync(id, suggestedFields, root.GetProperty("inferenceId").GetInt64());
            Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
            var file = await factory.WithDbAsync(db => db.SourceFiles.IgnoreQueryFilters().SingleAsync(f => f.Id == id));
            Assert.Equal((rows, 0), (file.ValidRowCount, file.RejectedRowCount));
            var mapping = await factory.WithDbAsync(db => db.ColumnMappings.IgnoreQueryFilters().SingleAsync(m => m.SourceFileId == id));
            Assert.Equal("AI-assisted (accepted as suggested)", mapping.Origin);      // the operator took the suggestion exactly as given
        }

        output.WriteLine($"{scenario}: {correct} of {total} columns suggested correctly ({(double)correct / total:P1}).");
        Assert.True((double)correct / total >= 0.9, $"{correct} of {total}");
    }

    [LiveAiTheory]
    [InlineData("Gross Amount")]
    public async Task Renaming_a_heading_changes_what_the_real_model_suggests(string heading)
    {
        // the same gateway file, but the amount column is renamed to something that means something else
        var bytes = Bytes("scenario1_main", SourceKind.Gateway);
        var file = TabularReader.Read(bytes, "g.xlsx");
        var csv = new System.Text.StringBuilder();
        csv.AppendLine(string.Join(",", file.Headers.Select(h => h == heading ? "Customer Remarks" : h)));
        foreach (var r in file.Rows) csv.AppendLine(string.Join(",", r.Cells.Select(c => c.Contains(',') ? $"\"{c}\"" : c)));

        using var factory = new ReconWebFactory(null, LiveAi.Url);
        var client = factory.NewClient();
        await client.LoginAsync(factory.Seeded(UserRole.Operator).Email, factory.Seeded(UserRole.Operator).Password);
        var original = (await client.UploadAsync(SourceKind.Gateway, "orig.xlsx", bytes)).FileIdFrom();
        var renamed = (await client.UploadAsync(SourceKind.Gateway, "renamed.csv", System.Text.Encoding.UTF8.GetBytes(csv.ToString()))).FileIdFrom();

        using var a = await client.SuggestAsync(original);
        using var b = await client.SuggestAsync(renamed);
        var idx = file.Headers.ToList().IndexOf(heading);
        var before = a.RootElement.GetProperty("suggestions")[idx];
        var after = b.RootElement.GetProperty("suggestions")[idx];
        output.WriteLine($"'{heading}': {before.GetProperty("field").GetString()} {before.GetProperty("confidence").GetDouble():P0}  ->  'Customer Remarks': " +
                         $"{(after.GetProperty("field").ValueKind == JsonValueKind.Null ? "(ignore)" : after.GetProperty("field").GetString())} {after.GetProperty("confidence").GetDouble():P0}");

        Assert.Equal("Amount", before.GetProperty("field").GetString());
        var stillAmount = after.GetProperty("field").ValueKind != JsonValueKind.Null && after.GetProperty("field").GetString() == "Amount";
        Assert.True(!stillAmount || after.GetProperty("confidence").GetDouble() < before.GetProperty("confidence").GetDouble() - 0.15);
    }
}
