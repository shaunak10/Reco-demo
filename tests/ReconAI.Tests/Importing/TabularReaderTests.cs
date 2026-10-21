using System.IO.Compression;
using System.Text;
using ReconAI.Core.Importing;

namespace ReconAI.Tests.Importing;

public class TabularReaderTests
{
    private static TabularFile Csv(string text, Encoding? encoding = null, bool bom = false)
    {
        var bytes = (encoding ?? new UTF8Encoding(false)).GetBytes(text);
        if (bom) bytes = [0xEF, 0xBB, 0xBF, .. bytes];
        return TabularReader.Read(bytes, "t.csv");
    }

    private static string ErrorOf(Action act) => Assert.Throws<ImportException>(act).Message;

    [Fact]
    public void Reads_headers_rows_quoted_commas_and_a_byte_order_mark()
    {
        var f = Csv("Ref,Amount,Note\nA1,\"1,234.50\",\"has, comma\"\nA2,10,x\n", bom: true);
        Assert.Equal(["Ref", "Amount", "Note"], f.Headers);
        Assert.Equal(2, f.Rows.Count);
        Assert.Equal(["A1", "1,234.50", "has, comma"], f.Rows[0].Cells);
        Assert.Equal(1, f.Rows[0].RowNumber);
    }

    [Theory]
    [InlineData(";")]
    [InlineData("\t")]
    [InlineData("|")]
    public void Detects_other_delimiters(string d)
    {
        var f = Csv($"Ref{d}Amount\nA1{d}10\n");
        Assert.Equal(["Ref", "Amount"], f.Headers);
        Assert.Equal(["A1", "10"], f.Rows[0].Cells);
    }

    [Fact]
    public void Falls_back_to_latin1_for_legacy_exports()
    {
        var f = Csv("Ref,Name\nA1,café\n", Encoding.Latin1);
        Assert.Equal("café", f.Rows[0].Cells[1]);
    }

    [Fact]
    public void Skips_blank_lines_and_counts_blank_rows_and_pads_ragged_rows()
    {
        var f = Csv("A,B,C\n1,2,3\n,,\n4,5\n");
        Assert.Equal(2, f.Rows.Count);
        Assert.Equal(1, f.BlankRowsSkipped);
        Assert.Equal(["4", "5", ""], f.Rows[1].Cells);
        Assert.Null(f.Rows[1].StructuralError);
    }

    [Fact]
    public void Flags_rows_with_more_values_than_headings()
    {
        var f = Csv("A,B\n1,2,3\n");
        Assert.NotNull(f.Rows[0].StructuralError);
    }

    [Fact]
    public void Drops_trailing_empty_columns_but_rejects_a_blank_heading_in_the_middle()
    {
        Assert.Equal(["A", "B"], Csv("A,B,,\n1,2,,\n").Headers);
        Assert.Contains("Column 2 has no heading", ErrorOf(() => Csv("A,,C\n1,2,3\n")));
    }

    [Fact]
    public void Rejects_duplicate_headings_case_insensitively()
    {
        Assert.Contains("appears more than once", ErrorOf(() => Csv("Amount,amount\n1,2\n")));
    }

    [Theory]
    [InlineData("", "The file is empty.")]
    [InlineData("\n\n\n", "No header row")]
    [InlineData("A,B\n", "no data rows")]
    public void Rejects_empty_or_data_less_files(string text, string expected)
    {
        Assert.Contains(expected, ErrorOf(() => Csv(text)));
    }

    [Fact]
    public void Rejects_unsupported_extensions_and_content_that_does_not_match_its_extension()
    {
        Assert.Contains("Only .csv and .xlsx", ErrorOf(() => TabularReader.Read([1, 2, 3], "a.exe")));
        Assert.Contains("Only .csv and .xlsx", ErrorOf(() => TabularReader.Read([1, 2, 3], "a.xls")));
        Assert.Contains("zip", ErrorOf(() => TabularReader.Read([0x50, 0x4B, 0x03, 0x04, 0, 0], "a.csv")));
        Assert.Contains("not a valid .xlsx", ErrorOf(() => TabularReader.Read(Encoding.UTF8.GetBytes("A,B\n1,2\n"), "a.xlsx")));
        Assert.Contains("binary", ErrorOf(() => TabularReader.Read([(byte)'A', 0, (byte)'B', 0], "a.csv")));
    }

    [Fact]
    public void Enforces_size_row_and_column_limits()
    {
        var tiny = new ImportLimits(MaxBytes: 50, MaxRows: 3, MaxColumns: 2);
        Assert.Contains("limit is", ErrorOf(() => TabularReader.Read(new byte[51], "a.csv", tiny)));
        Assert.Contains("More than 3", ErrorOf(() => TabularReader.Read(Encoding.UTF8.GetBytes("A\n1\n2\n3\n4\n"), "a.csv", tiny)));
        Assert.Contains("3 columns", ErrorOf(() => TabularReader.Read(Encoding.UTF8.GetBytes("A,B,C\n1,2,3\n"), "a.csv", tiny)));
    }

    [Fact]
    public void Rejects_a_garbage_xlsx_and_a_workbook_with_macros()
    {
        Assert.Contains("Could not read the Excel", ErrorOf(() => TabularReader.Read([0x50, 0x4B, 0x03, 0x04, 9, 9, 9, 9], "a.xlsx")));

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("xl/vbaProject.bin").Open());
            w.Write("macro");
        }
        Assert.Contains("macros", ErrorOf(() => TabularReader.Read(ms.ToArray(), "a.xlsx")));
    }

    [Fact]
    public void Rejects_a_zip_that_expands_beyond_the_safety_limit()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var w = new StreamWriter(zip.CreateEntry("xl/sheet1.xml").Open());
            w.Write(new string('A', 2_000_000));
        }
        var limits = new ImportLimits(MaxUncompressedBytes: 1_000_000);
        Assert.Contains("unsafe size", ErrorOf(() => TabularReader.Read(ms.ToArray(), "a.xlsx", limits)));
    }

    [Fact]
    public void Excel_numbers_and_dates_are_read_exactly()
    {
        Assert.Equal("588.4", TabularReader.CellToString(588.4));
        Assert.Equal("5073", TabularReader.CellToString(5073.0));
        Assert.Equal("0.1", TabularReader.CellToString(0.1));
        Assert.Equal("2026-10-15", TabularReader.CellToString(new DateTime(2026, 10, 15)));
        Assert.Equal("2026-10-15 14:22:10", TabularReader.CellToString(new DateTime(2026, 10, 15, 14, 22, 10)));
        Assert.Equal("", TabularReader.CellToString(null));
    }
}
