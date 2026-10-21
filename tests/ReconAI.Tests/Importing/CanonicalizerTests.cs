using System.Text;
using ReconAI.Core.Importing;
using ReconAI.Tests.Support;
using static ReconAI.Tests.Support.Scenarios;

namespace ReconAI.Tests.Importing;

/// <summary>
/// Step 3 exit test, importer half: the three real datasets import into canonical rows, deterministically, and the
/// totals agree with the independently authored ground truth.
/// </summary>
public class CanonicalizerTests
{
    private static string Tuple(CanonicalRow r) =>
        $"{r.TransactionRef}|{r.SettlementRef}|{r.Amount}|{r.Fees}|{r.TxnDate:yyyy-MM-dd}|{r.Status}|{r.Currency}";

    [Theory]
    [InlineData("scenario1_main", 61, 61, 7)]
    [InlineData("scenario2_clean", 24, 24, 3)]
    [InlineData("scenario3_changed_headers", 61, 61, 7)]
    public void Every_row_of_every_dataset_imports_cleanly(string scenario, int ledger, int gateway, int bank)
    {
        Assert.Equal((ledger, 0), (Import(scenario, SourceKind.Ledger).ValidCount, Import(scenario, SourceKind.Ledger).RejectedCount));
        Assert.Equal((gateway, 0), (Import(scenario, SourceKind.Gateway).ValidCount, Import(scenario, SourceKind.Gateway).RejectedCount));
        Assert.Equal((bank, 0), (Import(scenario, SourceKind.Bank).ValidCount, Import(scenario, SourceKind.Bank).RejectedCount));
    }

    [Theory]
    [InlineData("scenario1_main")]
    [InlineData("scenario2_clean")]
    public void Gateway_and_bank_totals_agree_with_the_ground_truth_batches(string scenario)
    {
        var batches = Csv(scenario, "ground_truth_settlement_batches.csv");
        var exceptions = Csv(scenario, "ground_truth_exceptions.csv");

        // gateway file = every batch's gross + any duplicate rows the file repeats
        var expectedGateway = batches.Sum(b => D(b["Gross"]))
            + exceptions.Where(e => e["Exception_Type"] == "DUPLICATE_GATEWAY").Sum(e => D(e["Exact_Amount"]));
        Assert.Equal(expectedGateway, Import(scenario, SourceKind.Gateway).ValidAmountTotal);

        // bank file = every credit that is in the file + the credits that belong to no gateway batch
        var expectedBank = batches.Where(b => b["Bank_Credit"] != "").Sum(b => D(b["Bank_Credit"]))
            + exceptions.Where(e => e["Exception_Type"] == "UNEXPLAINED_BANK_CREDIT").Sum(e => D(e["Exact_Amount"]));
        Assert.Equal(expectedBank, Import(scenario, SourceKind.Bank).ValidAmountTotal);
    }

    [Fact]
    public void Gateway_fees_import_as_exact_two_decimal_values()
    {
        var gateway = Import("scenario1_main", SourceKind.Gateway);
        var first = gateway.Rows.First(r => r.TransactionRef == "RCH-261015-0015");   // the over-charged payment
        var batch = Csv("scenario1_main", "ground_truth_exceptions.csv").First(e => e["Exception_Type"] == "FEE_MISMATCH");
        var contract = Math.Round(first.Amount!.Value * 0.0177m, 2, MidpointRounding.AwayFromZero);
        Assert.Equal(D(batch["Exact_Amount"]), first.Fees!.Value - contract);
        Assert.All(gateway.Rows, r => Assert.Equal(r.Fees, Math.Round(r.Fees!.Value, 2)));
    }

    [Theory]
    [InlineData("scenario1_main", SourceKind.Ledger)]
    [InlineData("scenario1_main", SourceKind.Gateway)]
    [InlineData("scenario1_main", SourceKind.Bank)]
    public void The_same_file_always_gives_identical_rows_keys_and_checksums(string scenario, SourceKind kind)
    {
        var a = Import(scenario, kind);
        var b = Import(scenario, kind);
        Assert.Equal(a.Rows.Select(r => (r.RowKey, r.RowSha256, r.RawJson, Tuple(r))), b.Rows.Select(r => (r.RowKey, r.RowSha256, r.RawJson, Tuple(r))));
    }

    [Theory]
    [InlineData(SourceKind.Ledger)]
    [InlineData(SourceKind.Gateway)]
    [InlineData(SourceKind.Bank)]
    public void Same_data_under_different_headings_and_file_formats_gives_the_same_canonical_rows(SourceKind kind)
    {
        var original = Import("scenario1_main", kind).Rows.Select(Tuple).ToList();
        var renamed = Import("scenario3_changed_headers", kind).Rows.Select(Tuple).ToList();
        Assert.Equal(original, renamed);   // CSV<->XLSX, new headings, same numbers, dates, references
    }

    [Fact]
    public void Row_keys_are_stable_unique_and_traceable_to_the_file_checksum()
    {
        var bytes = Bytes("scenario1_main", SourceKind.Gateway);
        var sha = Hashing.Sha256Hex(bytes);
        var rows = Import("scenario1_main", SourceKind.Gateway).Rows;

        Assert.Equal($"G-{sha[..12]}-000001", rows[0].RowKey);
        Assert.Equal(rows.Count, rows.Select(r => r.RowKey).Distinct().Count());
        Assert.Equal(Enumerable.Range(1, rows.Count), rows.Select(r => r.RowNumber));
        Assert.Equal(rows.Count, rows.Select(r => r.RowSha256).Distinct().Count() + 1);   // exactly one repeated row: the gateway duplicate
    }

    [Fact]
    public void Dates_are_read_correctly_from_csv_text_and_excel_date_cells()
    {
        // ledger: ISO text; gateway: Excel date cells; bank: dd-MMM-yyyy text
        Assert.Equal(new DateOnly(2026, 10, 15), Import("scenario1_main", SourceKind.Ledger).Rows[0].TxnDate);
        Assert.Equal(new DateOnly(2026, 10, 15), Import("scenario1_main", SourceKind.Gateway).Rows[0].TxnDate);
        Assert.Equal(new DateOnly(2026, 10, 15), Import("scenario1_main", SourceKind.Bank).Rows[0].TxnDate);
    }

    // ---------------------------------------------------------------- mapping validation
    private static (TabularFile File, string Sha) Read(string scenario, SourceKind kind)
    {
        var bytes = Bytes(scenario, kind);
        return (TabularReader.Read(bytes, FileOf(scenario, kind)), Hashing.Sha256Hex(bytes));
    }

    [Fact]
    public void A_missing_mandatory_field_blocks_the_import()
    {
        var (file, sha) = Read("scenario1_main", SourceKind.Gateway);
        var truth = GroundTruthMap("scenario1_main", SourceKind.Gateway, file.Headers);
        var map = FieldMap.Parse(truth.ByColumn.Select((f, i) => file.Headers[i] == "UTR" ? null : f?.ToString()));   // leave the settlement reference unmapped
        var ex = Assert.Throws<ImportException>(() => Canonicalizer.Run(SourceKind.Gateway, sha, file, map));
        Assert.Contains("Settlement reference", ex.Message);
    }

    [Fact]
    public void Mapping_one_field_onto_two_columns_or_an_unsupported_field_is_refused()
    {
        var (file, _) = Read("scenario1_main", SourceKind.Bank);
        var headers = file.Headers.ToList();

        var twice = new string?[headers.Count];
        twice[headers.IndexOf("Credit Amount")] = "Amount";
        twice[headers.IndexOf("Withdrawal Amt")] = "Amount";
        twice[headers.IndexOf("Bank Reference")] = "SettlementRef";
        twice[headers.IndexOf("Credit Date")] = "TxnDate";
        Assert.Contains(Canonicalizer.ValidateMapping(SourceKind.Bank, headers, FieldMap.Parse(twice)), p => p.Contains("mapped to two columns"));

        var fees = (string?[])twice.Clone();
        fees[headers.IndexOf("Withdrawal Amt")] = "Fees";   // a bank statement has no gateway fee
        Assert.Contains(Canonicalizer.ValidateMapping(SourceKind.Bank, headers, FieldMap.Parse(fees)), p => p.Contains("cannot be mapped"));

        Assert.Throws<ImportException>(() => FieldMap.Parse(["Nonsense"]));
    }

    [Fact]
    public void A_wrong_mapping_rejects_every_row_instead_of_importing_garbage()
    {
        var (file, sha) = Read("scenario1_main", SourceKind.Ledger);
        var headers = file.Headers.ToList();
        var map = new string?[headers.Count];
        map[headers.IndexOf("Order_Ref")] = "TransactionRef";
        map[headers.IndexOf("Booking_Date")] = "TxnDate";
        map[headers.IndexOf("Operator")] = "Amount";   // text column mapped as the amount

        var result = Canonicalizer.Run(SourceKind.Ledger, sha, file, FieldMap.Parse(map));
        Assert.Equal(0, result.ValidCount);
        Assert.Equal(61, result.RejectedCount);
        Assert.Equal("AMOUNT_INVALID", result.RejectReasons[0].Code);
    }

    [Fact]
    public void Row_level_problems_reject_only_that_row_and_say_why()
    {
        var csv = "Ref,Amt,Dt\nA1,100.00,2026-10-01\nA2,100.005,2026-10-01\n,50.00,2026-10-01\nA4,abc,2026-10-01\nA5,10.00,31/02/2026\nA6,0,2026-10-01\nA7,25.00,2026-10-02\n";
        var bytes = Encoding.UTF8.GetBytes(csv);
        var file = TabularReader.Read(bytes, "t.csv");
        var map = FieldMap.Parse(["TransactionRef", "Amount", "TxnDate"]);
        var result = Canonicalizer.Run(SourceKind.Ledger, Hashing.Sha256Hex(bytes), file, map);

        Assert.Equal(2, result.ValidCount);                       // A1 and A7
        Assert.Equal(5, result.RejectedCount);
        Assert.Equal(125.00m, result.ValidAmountTotal);           // only valid rows are summed
        var codes = result.Rows.Where(r => !r.IsValid).SelectMany(r => r.Errors).Select(e => e.Code).ToHashSet();
        Assert.True(new HashSet<string> { "AMOUNT_DECIMALS", "TXN_REF_EMPTY", "AMOUNT_INVALID", "DATE_INVALID", "AMOUNT_NOT_POSITIVE" }.SetEquals(codes));
    }

    [Fact]
    public void Non_inr_currency_is_rejected_and_a_blank_currency_defaults_to_inr()
    {
        var bytes = Encoding.UTF8.GetBytes("Ref,Amt,Dt,Ccy\nA1,10,2026-10-01,INR\nA2,10,2026-10-01,USD\nA3,10,2026-10-01,\n");
        var file = TabularReader.Read(bytes, "t.csv");
        var result = Canonicalizer.Run(SourceKind.Ledger, "abc", file, FieldMap.Parse(["TransactionRef", "Amount", "TxnDate", "Currency"]));
        Assert.Equal(["INR", "INR", "INR"], result.Rows.Select(r => r.Currency));
        Assert.Contains(result.Rows[1].Errors, e => e.Code == "CURRENCY_UNSUPPORTED");
        Assert.Equal(2, result.ValidCount);
    }
}
