using System.Globalization;
using System.IO.Compression;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;

namespace ReconAI.Core.Importing;

public sealed record ImportLimits(
    long MaxBytes = 5L * 1024 * 1024,
    int MaxRows = 50_000,
    int MaxColumns = 60,
    int MaxCellLength = 512,
    long MaxUncompressedBytes = 100L * 1024 * 1024);

/// <param name="RowNumber">1-based position among the non-blank data rows (the header is not counted).</param>
public sealed record TabularRow(int RowNumber, string[] Cells, string? StructuralError);

public sealed record TabularFile(string Format, string? SheetName, IReadOnlyList<string> Headers, IReadOnlyList<TabularRow> Rows, int BlankRowsSkipped);

/// <summary>A problem with the uploaded file whose message is safe to show to the operator.</summary>
public sealed class ImportException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Reads an uploaded .csv or .xlsx into headers + rows of text, enforcing size / row / column limits and refusing files
/// that are not what their extension claims. Cell text is kept exactly as written (only trimmed): numbers and dates are
/// interpreted later by the canonicalizer, never here.
/// </summary>
public static class TabularReader
{
    // ExcelDataReader looks up Windows-1252 even for .xlsx; .NET only ships it through this provider.
    static TabularReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static string FormatOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".csv" => "csv",
        ".xlsx" => "xlsx",
        _ => throw new ImportException("Only .csv and .xlsx files are accepted."),
    };

    public static TabularFile Read(byte[] content, string fileName, ImportLimits? limits = null)
    {
        limits ??= new ImportLimits();
        var format = FormatOf(fileName);
        if (content.Length == 0) throw new ImportException("The file is empty.");
        if (content.Length > limits.MaxBytes)
            throw new ImportException($"The file is {content.Length / 1048576.0:0.#} MB; the limit is {limits.MaxBytes / 1048576} MB.");

        var looksZipped = content.Length >= 4 && content[0] == 0x50 && content[1] == 0x4B && content[2] == 0x03 && content[3] == 0x04;
        return format == "xlsx" ? ReadXlsx(content, looksZipped, limits) : ReadCsv(content, looksZipped, limits);
    }

    // ------------------------------------------------------------------ CSV
    private static TabularFile ReadCsv(byte[] content, bool looksZipped, ImportLimits limits)
    {
        if (looksZipped)
            throw new ImportException("This is an Excel/zip file with a .csv name. Save it as .xlsx or export a real CSV.");
        if (Array.IndexOf(content, (byte)0, 0, Math.Min(content.Length, 8192)) >= 0)
            throw new ImportException("This does not look like a text CSV file (it contains binary data).");

        var text = Decode(content);
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            Delimiter = DetectDelimiter(text),
            IgnoreBlankLines = true,
            MissingFieldFound = null,
            DetectColumnCountChanges = false,
            TrimOptions = TrimOptions.None,
            BadDataFound = args => throw new ImportException($"The CSV is malformed near row {args.Context.Parser?.Row}: check for stray quote characters."),
        };

        var records = new List<string[]>();
        try
        {
            using var parser = new CsvParser(new StringReader(text), config);
            while (parser.Read())
            {
                records.Add(parser.Record ?? []);
                if (records.Count > limits.MaxRows * 2 + 1000)
                    throw new ImportException($"More than {limits.MaxRows:N0} data rows. Split the file and upload the parts separately.");
            }
        }
        catch (CsvHelperException)
        {
            throw new ImportException("The CSV file is malformed and could not be read.");
        }

        return Build("csv", null, records, limits);
    }

    private static string Decode(byte[] content)
    {
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(content);   // legacy Windows exports
        }
    }

    private static string DetectDelimiter(string text)
    {
        var end = text.IndexOf('\n');
        var line = end < 0 ? text : text[..end];
        var best = ",";
        var bestCount = 0;
        foreach (var candidate in new[] { ',', ';', '\t', '|' })
        {
            var count = 0;
            var inQuotes = false;
            foreach (var ch in line)
            {
                if (ch == '"') inQuotes = !inQuotes;
                else if (ch == candidate && !inQuotes) count++;
            }
            if (count > bestCount) { best = candidate.ToString(); bestCount = count; }
        }
        return best;
    }

    // ------------------------------------------------------------------ XLSX
    private static TabularFile ReadXlsx(byte[] content, bool looksZipped, ImportLimits limits)
    {
        if (!looksZipped)
            throw new ImportException("This is not a valid .xlsx workbook (an .xlsx file is a zip archive). Re-save it from Excel as .xlsx.");

        try
        {
            GuardZip(content, limits);
            using var stream = new MemoryStream(content, writable: false);
            using var reader = ExcelReaderFactory.CreateOpenXmlReader(stream);
            do
            {
                if (reader.VisibleState is not null and not "visible") continue;
                var records = new List<string[]>();
                while (reader.Read())
                {
                    var cells = new string[reader.FieldCount];
                    for (var i = 0; i < cells.Length; i++) cells[i] = CellToString(reader.GetValue(i));
                    records.Add(cells);
                    if (records.Count > limits.MaxRows * 2 + 1000)
                        throw new ImportException($"More than {limits.MaxRows:N0} data rows. Split the file and upload the parts separately.");
                }
                if (records.Any(r => r.Any(c => c.Length > 0))) return Build("xlsx", reader.Name, records, limits);
            }
            while (reader.NextResult());
        }
        catch (ImportException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new ImportException("Could not read the Excel file. Is it a valid, unprotected .xlsx workbook?", ex);
        }

        throw new ImportException("The workbook has no visible sheet with data.");
    }

    /// <summary>Cheap zip-bomb and macro checks before the workbook is opened.</summary>
    private static void GuardZip(byte[] content, ImportLimits limits)
    {
        using var zip = new ZipArchive(new MemoryStream(content, writable: false), ZipArchiveMode.Read);
        if (zip.Entries.Count > 2000) throw new ImportException("The workbook contains too many parts and was rejected.");
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            total += entry.Length;
            if (entry.Length > limits.MaxUncompressedBytes || total > limits.MaxUncompressedBytes)
                throw new ImportException("The workbook expands to an unsafe size and was rejected.");
            if (entry.FullName.EndsWith("vbaProject.bin", StringComparison.OrdinalIgnoreCase))
                throw new ImportException("Workbooks that contain macros are not accepted.");
        }
    }

    /// <summary>Numbers go through <see cref="decimal"/> (not double text) so 588.4 stays 588.4; dates become ISO text.</summary>
    internal static string CellToString(object? value) => value switch
    {
        null => "",
        string s => s.Trim(),
        double d => NumberToString(d),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        decimal m => m.ToString(CultureInfo.InvariantCulture),
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
        bool b => b ? "TRUE" : "FALSE",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "",
    };

    private static string NumberToString(double d)
    {
        if (double.IsNaN(d) || double.IsInfinity(d)) return "";
        if (d == Math.Floor(d) && Math.Abs(d) < 1e15) return ((long)d).ToString(CultureInfo.InvariantCulture);
        try { return ((decimal)d).ToString(CultureInfo.InvariantCulture); }
        catch (OverflowException) { return d.ToString("R", CultureInfo.InvariantCulture); }
    }

    // ------------------------------------------------------------------ shared
    private static TabularFile Build(string format, string? sheet, List<string[]> records, ImportLimits limits)
    {
        string[]? header = null;
        var rows = new List<TabularRow>();
        var blanks = 0;
        var number = 0;

        foreach (var record in records)
        {
            var isBlank = record.All(string.IsNullOrWhiteSpace);
            if (header is null)
            {
                if (isBlank) continue;
                header = CleanHeader(record, limits);
                continue;
            }
            if (isBlank) { blanks++; continue; }

            number++;
            if (number > limits.MaxRows)
                throw new ImportException($"More than {limits.MaxRows:N0} data rows. Split the file and upload the parts separately.");
            rows.Add(MakeRow(number, record, header.Length, limits));
        }

        if (header is null) throw new ImportException("No header row found: the file has no data.");
        if (rows.Count == 0) throw new ImportException("The file has a header row but no data rows.");
        return new TabularFile(format, sheet, header, rows, blanks);
    }

    private static string[] CleanHeader(string[] record, ImportLimits limits)
    {
        var last = record.Length - 1;
        while (last >= 0 && string.IsNullOrWhiteSpace(record[last])) last--;   // trailing empty columns are common in Excel
        var headers = record.Take(last + 1).Select(h => h.Trim()).ToArray();

        if (headers.Length > limits.MaxColumns) throw new ImportException($"The file has {headers.Length} columns; the limit is {limits.MaxColumns}.");
        for (var i = 0; i < headers.Length; i++)
        {
            if (headers[i].Length == 0) throw new ImportException($"Column {i + 1} has no heading. Every column in the header row needs a name.");
            if (headers[i].Length > 100) throw new ImportException($"The heading of column {i + 1} is longer than 100 characters.");
            if (headers[i].Any(char.IsControl)) throw new ImportException($"The heading of column {i + 1} contains control characters.");
        }

        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < headers.Length; i++)
        {
            if (seen.TryGetValue(headers[i], out var first))
                throw new ImportException($"The heading '{headers[i]}' appears more than once (columns {first + 1} and {i + 1}). Headings must be unique.");
            seen[headers[i]] = i;
        }
        return headers;
    }

    private static TabularRow MakeRow(int number, string[] record, int width, ImportLimits limits)
    {
        var cells = new string[width];
        string? problem = null;
        for (var i = 0; i < width; i++)
        {
            var cell = i < record.Length ? (record[i] ?? "").Trim() : "";
            if (cell.Length > limits.MaxCellLength) problem ??= $"A value is longer than {limits.MaxCellLength} characters";
            cells[i] = cell;
        }
        for (var i = width; i < record.Length; i++)
        {
            if (!string.IsNullOrWhiteSpace(record[i])) { problem ??= $"The row has more values than the {width} headings"; break; }
        }
        return new TabularRow(number, cells, problem);
    }
}
