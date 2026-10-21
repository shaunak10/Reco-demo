using System.Text.Json;

namespace ReconAI.Core.Importing;

/// <summary>Which canonical field each column of a file feeds (null = ignored). Aligned with the file's headers.</summary>
public sealed record FieldMap(IReadOnlyList<CanonicalField?> ByColumn)
{
    /// <summary>Parses posted field names ("" or null = ignore the column).</summary>
    public static FieldMap Parse(IEnumerable<string?> names)
    {
        var list = new List<CanonicalField?>();
        foreach (var name in names)
        {
            if (string.IsNullOrWhiteSpace(name)) { list.Add(null); continue; }
            if (!Enum.TryParse<CanonicalField>(name, ignoreCase: false, out var field) || !Enum.IsDefined(field))
                throw new ImportException($"'{name}' is not a canonical field.");
            list.Add(field);
        }
        return new FieldMap(list);
    }

    public Dictionary<string, string?> ToHeaderDictionary(IReadOnlyList<string> headers) =>
        headers.Select((h, i) => (h, f: i < ByColumn.Count ? ByColumn[i] : null))
               .ToDictionary(x => x.h, x => x.f?.ToString());
}

public readonly record struct RowError(string Code, string Message);

/// <summary>One imported row: immutable identity (RowKey, RowSha256), the original cells, and the canonical values.</summary>
public sealed record CanonicalRow(
    int RowNumber,
    string RowKey,
    string RowSha256,
    string RawJson,
    string? TransactionRef,
    string? SettlementRef,
    decimal? Amount,
    decimal? Fees,
    string Currency,
    DateOnly? TxnDate,
    string? Status,
    IReadOnlyList<RowError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record RejectReason(string Code, int Count, string Example);

public sealed record CanonicalizeResult(
    IReadOnlyList<CanonicalRow> Rows,
    int ValidCount,
    int RejectedCount,
    decimal ValidAmountTotal,
    IReadOnlyList<RejectReason> RejectReasons);

public static class Canonicalizer
{
    public const string DefaultCurrency = "INR";
    public const int MaxRefLength = 64;
    public const int MaxStatusLength = 32;

    /// <summary>Identifies a file layout (the ordered, case-insensitive headings) so a confirmed mapping can be offered again.</summary>
    public static string HeaderSignature(IReadOnlyList<string> headers) =>
        Hashing.Sha256Hex(string.Join('\u001f', headers.Select(h => h.Trim().ToLowerInvariant())));

    /// <summary>Mapping-level problems (missing mandatory field, one field on two columns, field not allowed for the source).</summary>
    public static IReadOnlyList<string> ValidateMapping(SourceKind kind, IReadOnlyList<string> headers, FieldMap map)
    {
        var problems = new List<string>();
        if (map.ByColumn.Count != headers.Count)
        {
            problems.Add("The mapping does not match the columns of the file.");
            return problems;
        }

        var allowed = SourceSchema.Allowed(kind);
        var firstColumnOf = new Dictionary<CanonicalField, int>();
        for (var i = 0; i < map.ByColumn.Count; i++)
        {
            if (map.ByColumn[i] is not { } field) continue;
            if (!allowed.Contains(field))
                problems.Add($"{SourceSchema.Label(field)} cannot be mapped for a {SourceSchema.Label(kind).ToLowerInvariant()} file.");
            else if (firstColumnOf.TryGetValue(field, out var first))
                problems.Add($"{SourceSchema.Label(field)} is mapped to two columns ('{headers[first]}' and '{headers[i]}'). Choose one.");
            else
                firstColumnOf[field] = i;
        }

        foreach (var required in SourceSchema.Mandatory(kind))
        {
            if (!firstColumnOf.ContainsKey(required))
                problems.Add($"Required field '{SourceSchema.Label(required)}' is not mapped to any column.");
        }
        return problems;
    }

    /// <summary>Deterministic: the same bytes and mapping always give the same rows, keys and checksums.</summary>
    public static CanonicalizeResult Run(SourceKind kind, string fileSha256, TabularFile file, FieldMap map)
    {
        var problems = ValidateMapping(kind, file.Headers, map);
        if (problems.Count > 0) throw new ImportException(string.Join(" ", problems));

        var columnOf = new Dictionary<CanonicalField, int>();
        for (var i = 0; i < map.ByColumn.Count; i++)
            if (map.ByColumn[i] is { } f) columnOf[f] = i;

        var mandatory = SourceSchema.Mandatory(kind);
        var prefix = SourceSchema.RowKeyPrefix(kind);
        var keyStem = fileSha256.Length >= 12 ? fileSha256[..12] : fileSha256;
        var rows = new List<CanonicalRow>(file.Rows.Count);

        foreach (var src in file.Rows)
        {
            var errors = new List<RowError>();
            if (src.StructuralError is { } structure) errors.Add(new RowError("ROW_STRUCTURE", structure));

            string Cell(CanonicalField f) => columnOf.TryGetValue(f, out var c) ? src.Cells[c] : "";
            bool Required(CanonicalField f) => mandatory.Contains(f);

            var txnRef = ReadRef(CanonicalField.TransactionRef, Cell(CanonicalField.TransactionRef), Required(CanonicalField.TransactionRef), errors);
            var settleRef = ReadRef(CanonicalField.SettlementRef, Cell(CanonicalField.SettlementRef), Required(CanonicalField.SettlementRef), errors);

            decimal? amount = null;
            if (columnOf.ContainsKey(CanonicalField.Amount))
            {
                if (ValueParsers.TryParseMoney(Cell(CanonicalField.Amount), out var a, out var err))
                {
                    if (a <= 0) errors.Add(new RowError("AMOUNT_NOT_POSITIVE", "Amount must be greater than zero"));
                    else amount = a;
                }
                else errors.Add(new RowError(err.Code, err.Message));
            }

            decimal? fees = null;
            if (columnOf.ContainsKey(CanonicalField.Fees))
            {
                var raw = Cell(CanonicalField.Fees);
                if (raw.Length == 0)
                {
                    if (Required(CanonicalField.Fees)) errors.Add(new RowError("FEES_EMPTY", "Fees is empty (enter 0.00 when no fee was charged)"));
                }
                else if (ValueParsers.TryParseMoney(raw, out var f, out var err))
                {
                    fees = f;   // zero fees are legitimate; negatives never parse
                }
                else errors.Add(new RowError(err.Code.Replace("AMOUNT", "FEES"), "Fees: " + err.Message));
            }

            DateOnly? date = null;
            if (columnOf.ContainsKey(CanonicalField.TxnDate))
            {
                if (ValueParsers.TryParseDate(Cell(CanonicalField.TxnDate), out var d, out var err)) date = d;
                else errors.Add(new RowError(err.Code, err.Message));
            }

            var currency = DefaultCurrency;
            var rawCurrency = Cell(CanonicalField.Currency).ToUpperInvariant();
            if (rawCurrency.Length > 0)
            {
                if (rawCurrency == DefaultCurrency) currency = rawCurrency;
                else errors.Add(new RowError("CURRENCY_UNSUPPORTED", $"Only INR is supported in this prototype (found '{ValueParsers.Shorten(rawCurrency, 10)}')"));
            }

            var status = Cell(CanonicalField.Status);
            if (status.Length > MaxStatusLength) status = status[..MaxStatusLength];
            if (status.Length == 0) status = null;

            rows.Add(new CanonicalRow(
                src.RowNumber,
                $"{prefix}-{keyStem}-{src.RowNumber:D6}",
                Hashing.Sha256Hex(string.Join('\u001f', src.Cells)),
                JsonSerializer.Serialize(src.Cells),
                txnRef, settleRef, amount, fees, currency, date, status, errors));
        }

        var valid = rows.Where(r => r.IsValid).ToList();
        var reasons = rows.Where(r => !r.IsValid)
            .SelectMany(r => r.Errors)
            .GroupBy(e => e.Code)
            .Select(g => new RejectReason(g.Key, g.Count(), g.First().Message))
            .OrderByDescending(r => r.Count)
            .ToList();

        return new CanonicalizeResult(rows, valid.Count, rows.Count - valid.Count, valid.Sum(r => r.Amount ?? 0m), reasons);
    }

    private static string? ReadRef(CanonicalField field, string raw, bool required, List<RowError> errors)
    {
        if (raw.Length == 0)
        {
            if (required) errors.Add(new RowError($"{Code(field)}_EMPTY", $"{SourceSchema.Label(field)} is empty"));
            return null;
        }
        if (raw.Length > MaxRefLength)
        {
            errors.Add(new RowError($"{Code(field)}_TOO_LONG", $"{SourceSchema.Label(field)} is longer than {MaxRefLength} characters"));
            return null;
        }
        if (raw.Any(char.IsControl))
        {
            errors.Add(new RowError($"{Code(field)}_INVALID", $"{SourceSchema.Label(field)} contains control characters"));
            return null;
        }
        return raw;
    }

    private static string Code(CanonicalField field) => field == CanonicalField.TransactionRef ? "TXN_REF" : "SETTLEMENT_REF";
}
