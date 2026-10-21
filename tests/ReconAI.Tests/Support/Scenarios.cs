using System.Globalization;
using ReconAI.Core.Importing;

namespace ReconAI.Tests.Support;

/// <summary>Locates the synthetic datasets and their hand-authored ground truth.</summary>
public static class Scenarios
{
    public static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ReconAI.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root (ReconAI.sln).");
    }

    public static string Path_(string scenario, string file) => Path.Combine(Root, "data", scenario, file);

    public static (string Ledger, string Gateway, string Bank) Files(string scenario) => scenario switch
    {
        "scenario1_main" or "scenario2_clean" => ("merchant_ledger.csv", "gateway_statement.xlsx", "bank_settlement.csv"),
        "scenario3_changed_headers" => ("ledger_export.xlsx", "pg_report.csv", "account_statement.xlsx"),
        _ => throw new ArgumentException(scenario),
    };

    public static string FileOf(string scenario, SourceKind kind) => kind switch
    {
        SourceKind.Ledger => Files(scenario).Ledger,
        SourceKind.Gateway => Files(scenario).Gateway,
        _ => Files(scenario).Bank,
    };

    public static byte[] Bytes(string scenario, SourceKind kind) => File.ReadAllBytes(Path_(scenario, FileOf(scenario, kind)));

    public static List<Dictionary<string, string>> Csv(string scenario, string file)
    {
        var lines = File.ReadAllLines(Path_(scenario, file));
        var header = lines[0].Split(',');
        return lines.Skip(1).Where(l => l.Length > 0).Select(l => SplitCsv(l, header.Length))
            .Select(cells => header.Zip(cells).ToDictionary(p => p.First, p => p.Second)).ToList();
    }

    // Minimal quoted-field splitter, enough for the ground-truth files (quotes only wrap commas).
    private static string[] SplitCsv(string line, int expected)
    {
        var cells = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in line)
        {
            if (ch == '"') quoted = !quoted;
            else if (ch == ',' && !quoted) { cells.Add(current.ToString()); current.Clear(); }
            else current.Append(ch);
        }
        cells.Add(current.ToString());
        while (cells.Count < expected) cells.Add("");
        return cells.ToArray();
    }

    /// <summary>The mapping a human expert wrote in ground_truth_mapping.csv, aligned to the file's headers.</summary>
    public static FieldMap GroundTruthMap(string scenario, SourceKind kind, IReadOnlyList<string> headers)
    {
        var source = kind.ToString().ToLowerInvariant();
        var truth = Csv(scenario, "ground_truth_mapping.csv")
            .Where(r => r["Source"] == source)
            .ToDictionary(r => r["Header"], r => r["Expected_Canonical_Field"]);
        return FieldMap.Parse(headers.Select(h => truth[h] == "IGNORE" ? null : truth[h]));
    }

    /// <summary>The ground-truth mapping as the posted form values: one canonical field name (or "") per column, in file order.</summary>
    public static string[] Fields(string scenario, SourceKind kind)
    {
        var file = TabularReader.Read(Bytes(scenario, kind), FileOf(scenario, kind));
        return GroundTruthMap(scenario, kind, file.Headers).ByColumn.Select(f => f?.ToString() ?? "").ToArray();
    }

    public static decimal D(string text) => decimal.Parse(text, NumberStyles.Number, CultureInfo.InvariantCulture);

    public static CanonicalizeResult Import(string scenario, SourceKind kind)
    {
        var bytes = Bytes(scenario, kind);
        var file = TabularReader.Read(bytes, FileOf(scenario, kind));
        return Canonicalizer.Run(kind, Hashing.Sha256Hex(bytes), file, GroundTruthMap(scenario, kind, file.Headers));
    }
}
