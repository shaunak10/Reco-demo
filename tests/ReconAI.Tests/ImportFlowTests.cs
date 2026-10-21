using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Importing;
using ReconAI.Core.Security;
using ReconAI.Data;
using ReconAI.Tests.Support;
using static ReconAI.Tests.Support.Scenarios;

namespace ReconAI.Tests;

/// <summary>
/// Step 3 exit tests against the real running pipeline (upload, validation, storage, mapping, import):
/// "same files produce the same canonical rows; duplicate upload does not double-count", plus the error feedback,
/// tenant isolation and exact-money requirements.
/// </summary>
public sealed class ImportFlowTests : IDisposable
{
    private const string Scenario = "scenario1_main";
    private readonly ReconWebFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<HttpClient> SignInAsync(ReconWebFactory factory, string email, string password)
    {
        var client = factory.NewClient();
        Assert.Equal(HttpStatusCode.Redirect, (await client.LoginAsync(email, password)).StatusCode);
        return client;
    }

    private Task<HttpClient> OperatorAsync() =>
        SignInAsync(_factory, _factory.Seeded(UserRole.Operator).Email, _factory.Seeded(UserRole.Operator).Password);

    /// <summary>Uploads a scenario file and confirms the expert (ground-truth) mapping. Returns the new file id.</summary>
    private static async Task<int> ImportAsync(HttpClient client, string scenario, SourceKind kind)
    {
        var upload = await client.UploadAsync(kind, FileOf(scenario, kind), Bytes(scenario, kind));
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        var id = upload.FileIdFrom();
        var confirm = await client.PostMappingAsync(id, "Confirm", Fields(scenario, kind));
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        return id;
    }

    /// <summary>Exact integer SUM in SQL. The column name is whitelisted (an identifier cannot be a SQL parameter).</summary>
    private static Task<long> SumPaiseAsync(ReconWebFactory f, int fileId, string column = "Amount")
    {
        if (column is not ("Amount" or "Fees")) throw new ArgumentException(column);
        return f.WithDbAsync(db => db.Database.SqlQueryRaw<long>(
            $"SELECT COALESCE(SUM({column}), 0) AS Value FROM SourceRows WHERE SourceFileId = {{0}} AND ImportStatus = 'Valid'", fileId).SingleAsync());
    }

    private static Task<int> CountAsync<T>(ReconWebFactory f) where T : class => f.WithDbAsync(db => db.Set<T>().IgnoreQueryFilters().CountAsync());

    // ------------------------------------------------------------------ the happy path
    [Fact]
    public async Task Operator_uploads_maps_and_imports_a_file_end_to_end()
    {
        var client = await OperatorAsync();
        var upload = await client.UploadAsync(SourceKind.Ledger, "merchant_ledger.csv", Bytes(Scenario, SourceKind.Ledger));
        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        Assert.StartsWith("/Recon/Mapping/", upload.Location());

        var mapPage = await (await client.GetAsync(upload.Location())).Content.ReadAsStringAsync();
        Assert.Contains("Order_Ref", mapPage);                       // the file's own headings are shown
        Assert.Contains("Confirm mapping and import", mapPage);

        // nothing is a SourceRow until the mapping is confirmed
        Assert.Equal(0, await CountAsync<SourceRow>(_factory));

        var id = upload.FileIdFrom();
        var confirm = await client.PostMappingAsync(id, "Confirm", Fields(Scenario, SourceKind.Ledger));
        Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);
        Assert.Equal($"/Recon/File/{id}", confirm.Location());

        var file = await _factory.WithDbAsync(db => db.SourceFiles.IgnoreQueryFilters().SingleAsync());
        Assert.Equal(SourceFileStatus.Imported, file.Status);
        Assert.Equal((61, 0), (file.ValidRowCount, file.RejectedRowCount));
        Assert.Equal(Hashing.Sha256Hex(Bytes(Scenario, SourceKind.Ledger)), file.Sha256);
        Assert.Equal(61, await CountAsync<SourceRow>(_factory));
        Assert.Equal(1, await CountAsync<ColumnMapping>(_factory));

        var page = await (await client.GetAsync($"/Recon/File/{id}")).Content.ReadAsStringAsync();
        Assert.Contains("RCH-261015-0001", page);                    // rows are browsable
        Assert.Contains("Confirmed mapping", page);
    }

    [Fact]
    public async Task Imported_totals_in_sql_equal_the_ground_truth_to_the_paisa()
    {
        var client = await OperatorAsync();
        var ledger = await ImportAsync(client, Scenario, SourceKind.Ledger);
        var gateway = await ImportAsync(client, Scenario, SourceKind.Gateway);   // .xlsx with real date and number cells
        var bank = await ImportAsync(client, Scenario, SourceKind.Bank);          // amounts like "4,983.20"

        var batches = Csv(Scenario, "ground_truth_settlement_batches.csv");
        var exceptions = Csv(Scenario, "ground_truth_exceptions.csv");

        var expectedLedger = Csv(Scenario, "merchant_ledger.csv").Sum(r => D(r["Amount_INR"]));
        var expectedGateway = batches.Sum(b => D(b["Gross"])) + exceptions.Where(e => e["Exception_Type"] == "DUPLICATE_GATEWAY").Sum(e => D(e["Exact_Amount"]));
        var expectedBank = batches.Where(b => b["Bank_Credit"] != "").Sum(b => D(b["Bank_Credit"]))
            + exceptions.Where(e => e["Exception_Type"] == "UNEXPLAINED_BANK_CREDIT").Sum(e => D(e["Exact_Amount"]));

        Assert.Equal(expectedLedger, Money.FromPaise(await SumPaiseAsync(_factory, ledger)));
        Assert.Equal(expectedGateway, Money.FromPaise(await SumPaiseAsync(_factory, gateway)));
        Assert.Equal(expectedBank, Money.FromPaise(await SumPaiseAsync(_factory, bank)));

        // fees recorded by the gateway: batch totals plus the duplicated row's fee (149.00 x 1.77%)
        var expectedFees = batches.Sum(b => D(b["Fees_Recorded"])) + Math.Round(149.00m * 0.0177m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(expectedFees, Money.FromPaise(await SumPaiseAsync(_factory, gateway, "Fees")));
    }

    [Fact]
    public async Task The_same_files_produce_identical_canonical_rows_in_two_separate_databases()
    {
        async Task<List<string>> Snapshot(ReconWebFactory f)
        {
            var client = await SignInAsync(f, f.Seeded(UserRole.Operator).Email, f.Seeded(UserRole.Operator).Password);
            foreach (var kind in new[] { SourceKind.Ledger, SourceKind.Gateway, SourceKind.Bank }) await ImportAsync(client, Scenario, kind);
            return await f.WithDbAsync(async db =>
            {
                var rows = await db.SourceRows.IgnoreQueryFilters().OrderBy(r => r.Kind).ThenBy(r => r.RowNumber).ToListAsync();
                var files = await db.SourceFiles.IgnoreQueryFilters().OrderBy(x => x.Kind).Select(x => $"{x.Kind}|{x.Sha256}|{x.ValidRowCount}|{x.RejectedRowCount}").ToListAsync();
                return files.Concat(rows.Select(r => $"{r.Kind}|{r.RowKey}|{r.RowSha256}|{r.RawJson}|{r.TransactionRef}|{r.SettlementRef}|{r.Amount}|{r.Fees}|{r.TxnDate:yyyy-MM-dd}|{r.Status}|{r.Currency}|{r.ImportStatus}")).ToList();
            });
        }

        using var second = new ReconWebFactory();
        var a = await Snapshot(_factory);
        var b = await Snapshot(second);
        Assert.Equal(61 + 61 + 7 + 3, a.Count);
        Assert.Equal(a, b);
    }

    [Fact]
    public async Task Preview_reports_counts_without_storing_anything()
    {
        var client = await OperatorAsync();
        var id = (await client.UploadAsync(SourceKind.Gateway, "gateway_statement.xlsx", Bytes(Scenario, SourceKind.Gateway))).FileIdFrom();

        var res = await client.PostMappingAsync(id, "Preview", Fields(Scenario, SourceKind.Gateway));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var json = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(61, json.RootElement.GetProperty("validRows").GetInt32());
        Assert.Equal(0, json.RootElement.GetProperty("rejectedRows").GetInt32());
        Assert.Equal(0, await CountAsync<SourceRow>(_factory));
        Assert.Equal(0, await CountAsync<ColumnMapping>(_factory));
    }

    // ------------------------------------------------------------------ duplicates
    [Fact]
    public async Task Uploading_the_same_file_twice_does_not_double_count()
    {
        var client = await OperatorAsync();
        var first = await ImportAsync(client, Scenario, SourceKind.Ledger);
        var before = await SumPaiseAsync(_factory, first);
        Assert.Equal(5118900L, before);                              // 51,189.00 - a real, non-zero total to compare against

        var again = await client.UploadAsync(SourceKind.Ledger, "merchant_ledger - copy.csv", Bytes(Scenario, SourceKind.Ledger));   // same bytes, new name
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);                                                                       // page re-rendered with an error
        var html = await again.Content.ReadAsStringAsync();
        Assert.Contains("already uploaded", html);
        Assert.Contains("nothing is double-counted", html);

        Assert.Equal(1, await CountAsync<SourceFile>(_factory));
        Assert.Equal(61, await CountAsync<SourceRow>(_factory));
        Assert.Equal(before, await SumPaiseAsync(_factory, first));
    }

    [Fact]
    public async Task A_duplicate_is_caught_even_before_the_first_copy_was_mapped()
    {
        var client = await OperatorAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await client.UploadAsync(SourceKind.Bank, "b.csv", Bytes(Scenario, SourceKind.Bank))).StatusCode);
        var again = await client.UploadAsync(SourceKind.Bank, "b2.csv", Bytes(Scenario, SourceKind.Bank));
        Assert.Contains("already uploaded", await again.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountAsync<SourceFile>(_factory));
    }

    [Fact]
    public async Task The_same_file_in_the_wrong_upload_box_is_flagged()
    {
        var client = await OperatorAsync();
        await ImportAsync(client, Scenario, SourceKind.Ledger);
        var wrongBox = await client.UploadAsync(SourceKind.Gateway, "oops.csv", Bytes(Scenario, SourceKind.Ledger));
        Assert.Contains("right upload box", await wrongBox.Content.ReadAsStringAsync());
        Assert.Equal(1, await CountAsync<SourceFile>(_factory));
    }

    // ------------------------------------------------------------------ clear error feedback
    public static TheoryData<string, byte[], string> BadUploads() => new()
    {
        { "notes.txt", Encoding.UTF8.GetBytes("hello"), "Only .csv and .xlsx" },
        { "macro.xlsm", [1, 2, 3], "Only .csv and .xlsx" },
        { "empty.csv", [], "is empty" },
        { "big.csv", new byte[6 * 1024 * 1024], "limit is 5 MB" },
        { "fake.xlsx", Encoding.UTF8.GetBytes("A,B\n1,2\n"), "not a valid .xlsx" },
        { "binary.csv", [(byte)'A', 0, (byte)'B', 0, 1, 2], "binary data" },
        { "headers_only.csv", Encoding.UTF8.GetBytes("A,B\n"), "no data rows" },
        { "dup.csv", Encoding.UTF8.GetBytes("Amount,amount\n1,2\n"), "appears more than once" },
        { "blank_heading.csv", Encoding.UTF8.GetBytes("A,,C\n1,2,3\n"), "has no heading" },
    };

    [Theory]
    [MemberData(nameof(BadUploads))]
    public async Task Bad_uploads_are_refused_with_a_clear_message_and_store_nothing(string name, byte[] bytes, string expected)
    {
        var client = await OperatorAsync();
        var res = await client.UploadAsync(SourceKind.Ledger, name, bytes);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(expected, WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()));
        Assert.Equal(0, await CountAsync<SourceFile>(_factory));
        Assert.Equal(0, await CountAsync<SourceRow>(_factory));
        Assert.False(Directory.Exists(_factory.UploadsPath) && Directory.EnumerateFiles(_factory.UploadsPath, "*", SearchOption.AllDirectories).Any());

        var audit = await _factory.WithDbAsync(db => db.AuditEvents.IgnoreQueryFilters().Where(a => a.Action == "UploadRejected").CountAsync());
        Assert.Equal(1, audit);                                      // the refusal is on record
    }

    [Fact]
    public async Task Upload_without_an_antiforgery_token_is_rejected()
    {
        var client = await OperatorAsync();
        var res = await client.UploadAsync(SourceKind.Ledger, "x.csv", Encoding.UTF8.GetBytes("A\n1\n"), withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, await CountAsync<SourceFile>(_factory));
    }

    [Fact]
    public async Task Path_tricks_in_the_file_name_cannot_escape_the_upload_folder()
    {
        var client = await OperatorAsync();
        var res = await client.UploadAsync(SourceKind.Ledger, @"..\..\..\evil\..\ledger.csv", Bytes(Scenario, SourceKind.Ledger));
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);

        var file = await _factory.WithDbAsync(db => db.SourceFiles.IgnoreQueryFilters().SingleAsync());
        Assert.Equal("ledger.csv", file.OriginalFileName);           // only the display name is kept, stripped to its last segment
        Assert.Equal($"{file.TenantId}{Path.DirectorySeparatorChar}{file.Sha256}.csv", file.StoredPath);   // storage name comes from the checksum
        Assert.True(File.Exists(Path.Combine(_factory.UploadsPath, file.StoredPath)));
    }

    [Fact]
    public async Task A_wrong_mapping_imports_nothing_and_leaves_the_file_waiting()
    {
        var client = await OperatorAsync();
        var id = (await client.UploadAsync(SourceKind.Ledger, "l.csv", Bytes(Scenario, SourceKind.Ledger))).FileIdFrom();

        var headers = TabularReader.Read(Bytes(Scenario, SourceKind.Ledger), "l.csv").Headers.ToList();
        var wrong = new string[headers.Count];
        Array.Fill(wrong, "");
        wrong[headers.IndexOf("Order_Ref")] = "TransactionRef";
        wrong[headers.IndexOf("Booking_Date")] = "TxnDate";
        wrong[headers.IndexOf("Operator")] = "Amount";               // a text column mapped as the amount

        var res = await client.PostMappingAsync(id, "Confirm", wrong);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("No row passed validation", WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()));

        Assert.Equal(0, await CountAsync<SourceRow>(_factory));
        Assert.Equal(SourceFileStatus.AwaitingMapping, (await _factory.WithDbAsync(db => db.SourceFiles.IgnoreQueryFilters().SingleAsync())).Status);

        // the operator can fix the mapping and import properly afterwards
        Assert.Equal(HttpStatusCode.Redirect, (await client.PostMappingAsync(id, "Confirm", Fields(Scenario, SourceKind.Ledger))).StatusCode);
        Assert.Equal(61, await CountAsync<SourceRow>(_factory));
    }

    [Fact]
    public async Task A_missing_required_field_is_refused_with_its_name()
    {
        var client = await OperatorAsync();
        var id = (await client.UploadAsync(SourceKind.Gateway, "g.xlsx", Bytes(Scenario, SourceKind.Gateway))).FileIdFrom();
        var headers = TabularReader.Read(Bytes(Scenario, SourceKind.Gateway), "g.xlsx").Headers.ToList();
        var fields = Fields(Scenario, SourceKind.Gateway);
        fields[headers.IndexOf("UTR")] = "";                         // leave the settlement reference unmapped

        var res = await client.PostMappingAsync(id, "Confirm", fields);
        Assert.Contains("Settlement reference", WebUtility.HtmlDecode(await res.Content.ReadAsStringAsync()));
        Assert.Equal(0, await CountAsync<SourceRow>(_factory));
    }

    [Fact]
    public async Task Confirming_twice_cannot_import_the_rows_twice()
    {
        var client = await OperatorAsync();
        var id = await ImportAsync(client, Scenario, SourceKind.Ledger);

        // the mapping page of an imported file just sends you to the file; a replayed POST is refused
        var replay = await client.PostMappingAsync(id, "Confirm", Fields(Scenario, SourceKind.Ledger), tokenPage: "/Recon/New");
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Contains("already been imported", await replay.Content.ReadAsStringAsync());
        Assert.Equal(61, await CountAsync<SourceRow>(_factory));
    }

    // ------------------------------------------------------------------ saved mapping
    [Fact]
    public async Task A_confirmed_mapping_is_offered_again_for_the_same_headings()
    {
        var client = await OperatorAsync();
        await ImportAsync(client, Scenario, SourceKind.Ledger);

        // a different file (so not a duplicate) with exactly the same layout
        var text = Encoding.UTF8.GetString(Bytes(Scenario, SourceKind.Ledger)) + "LDG-999999,RCH-261099-0001,2026-10-19,DTH,Tata Play,250.00,UPI,SUCCESS\n";
        var next = await client.UploadAsync(SourceKind.Ledger, "ledger_next_day.csv", Encoding.UTF8.GetBytes(text));
        Assert.Equal(HttpStatusCode.Redirect, next.StatusCode);

        var html = await (await client.GetAsync(next.Location())).Content.ReadAsStringAsync();
        Assert.Contains("Saved mapping applied", html);
        Assert.Contains("<option value=\"TransactionRef\" selected", html);
    }

    // ------------------------------------------------------------------ void
    [Fact]
    public async Task Voiding_keeps_the_rows_on_record_and_allows_a_corrected_reupload()
    {
        var client = await OperatorAsync();
        var id = await ImportAsync(client, Scenario, SourceKind.Ledger);

        var noReason = await client.PostAsync($"/Recon/File/{id}?handler=Void", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["reason"] = " ", ["__RequestVerificationToken"] = await client.TokenFromAsync($"/Recon/File/{id}") }));
        Assert.Contains("reason", await noReason.Content.ReadAsStringAsync());   // a reason is required

        var voided = await client.PostAsync($"/Recon/File/{id}?handler=Void", new FormUrlEncodedContent(new Dictionary<string, string>
        { ["reason"] = "Wrong day imported", ["__RequestVerificationToken"] = await client.TokenFromAsync($"/Recon/File/{id}") }));
        Assert.Equal(HttpStatusCode.Redirect, voided.StatusCode);

        Assert.Equal(61, await CountAsync<SourceRow>(_factory));                  // immutable rows are kept for audit
        var reupload = await client.UploadAsync(SourceKind.Ledger, "merchant_ledger.csv", Bytes(Scenario, SourceKind.Ledger));
        Assert.Equal(HttpStatusCode.Redirect, reupload.StatusCode);               // checksum freed
        Assert.NotEqual(id, reupload.FileIdFrom());
        Assert.Equal(2, await CountAsync<SourceFile>(_factory));
    }

    // ------------------------------------------------------------------ tenancy and access
    [Fact]
    public async Task Another_company_cannot_see_open_or_preview_my_files_and_may_upload_the_same_bytes()
    {
        var mine = await OperatorAsync();
        var id = await ImportAsync(mine, Scenario, SourceKind.Ledger);

        await _factory.AddTenantUserAsync("Other Co (Synthetic)", "operator@other.local", "Other-Op-Passw0rd!", UserRole.Operator);
        var theirs = await SignInAsync(_factory, "operator@other.local", "Other-Op-Passw0rd!");

        Assert.Equal(HttpStatusCode.NotFound, (await theirs.GetAsync($"/Recon/File/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await theirs.GetAsync($"/Recon/Mapping/{id}")).StatusCode);
        var theirList = await (await theirs.GetAsync("/Recon/New")).Content.ReadAsStringAsync();
        Assert.Contains("No files uploaded yet", theirList);

        // the same bytes are not a duplicate for a different company, and the two copies never mix
        var theirUpload = await theirs.UploadAsync(SourceKind.Ledger, "merchant_ledger.csv", Bytes(Scenario, SourceKind.Ledger));
        Assert.Equal(HttpStatusCode.Redirect, theirUpload.StatusCode);
        var theirId = theirUpload.FileIdFrom();
        Assert.Equal(HttpStatusCode.Redirect, (await theirs.PostMappingAsync(theirId, "Confirm", Fields(Scenario, SourceKind.Ledger))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await mine.GetAsync($"/Recon/File/{theirId}")).StatusCode);
        Assert.Equal(122, await CountAsync<SourceRow>(_factory));
    }

    [Theory]
    [InlineData("/Recon/New")]
    [InlineData("/Recon/File/1")]
    [InlineData("/Recon/Mapping/1")]
    public async Task Anonymous_users_cannot_reach_the_import_pages(string url)
    {
        var res = await _factory.NewClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, res.StatusCode);
        Assert.StartsWith("/Account/Login", res.Location());
    }

    [Fact]
    public async Task Upload_import_and_void_are_audited_without_financial_values()
    {
        var client = await OperatorAsync();
        var id = await ImportAsync(client, Scenario, SourceKind.Gateway);
        var token = await client.TokenFromAsync($"/Recon/File/{id}");
        await client.PostAsync($"/Recon/File/{id}?handler=Void", new FormUrlEncodedContent(new Dictionary<string, string> { ["reason"] = "test", ["__RequestVerificationToken"] = token }));

        var events = await _factory.WithDbAsync(db => db.AuditEvents.IgnoreQueryFilters().OrderBy(a => a.Id).Select(a => new { a.Action, a.Detail, a.Actor }).ToListAsync());
        var actions = events.Select(e => e.Action).ToList();
        Assert.Contains("FileUploaded", actions);
        Assert.Contains("FileImported", actions);
        Assert.Contains("FileVoided", actions);
        Assert.All(events.Where(e => e.Action.StartsWith("File")), e => Assert.Equal(_factory.Seeded(UserRole.Operator).Email, e.Actor));
        Assert.DoesNotContain(events, e => e.Detail.Contains("RCH-26") || e.Detail.Contains("UTRPG"));   // no row values in the audit trail
    }

    // ------------------------------------------------------------------ sample data download
    [Fact]
    public async Task Sample_downloads_serve_listed_files_only()
    {
        var client = await OperatorAsync();
        var ok = await client.GetAsync("/Recon/New?handler=Sample&scenario=scenario1_main&file=merchant_ledger.csv");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(Bytes(Scenario, SourceKind.Ledger), await ok.Content.ReadAsByteArrayAsync());

        foreach (var (scenario, file) in new[]
        {
            ("scenario1_main", "../scenario2_clean/merchant_ledger.csv"),
            ("..", "ReconAI.sln"),
            ("scenario1_main", "..%2f..%2fsrc%2fReconAI.Web%2fappsettings.json"),
            ("scenario1_main", "api_bank_credits.json"),      // not a listed file type
            ("scenario1_main", "does_not_exist.csv"),
            ("nope", "merchant_ledger.csv"),
        })
        {
            var res = await client.GetAsync($"/Recon/New?handler=Sample&scenario={Uri.EscapeDataString(scenario)}&file={file}");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }
    }
}
