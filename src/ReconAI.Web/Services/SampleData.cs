namespace ReconAI.Web.Services;

/// <summary>
/// Serves the repository's synthetic datasets so the demo can be run without hunting for files. Only names that
/// actually exist in a scenario folder are served (the request is matched against a directory listing, never joined
/// into a path), and only when <c>Demo:SampleData</c> is on.
/// </summary>
public sealed class SampleData(IWebHostEnvironment env, IConfiguration config)
{
    private static readonly string[] Extensions = [".csv", ".xlsx"];

    public bool Enabled => config.GetValue<bool>("Demo:SampleData");

    private string Root => Path.GetFullPath(config["Demo:SampleDataPath"] ?? Path.Combine(env.ContentRootPath, "..", "..", "data"));

    public IReadOnlyList<SampleScenario> Scenarios()
    {
        if (!Enabled || !Directory.Exists(Root)) return [];
        return Directory.GetDirectories(Root, "scenario*").OrderBy(d => d, StringComparer.Ordinal)
            .Select(dir => new SampleScenario(
                Path.GetFileName(dir),
                Directory.GetFiles(dir).Select(Path.GetFileName).OfType<string>()
                    .Where(n => Extensions.Contains(Path.GetExtension(n).ToLowerInvariant()))
                    .OrderBy(n => n.StartsWith("ground_truth", StringComparison.Ordinal)).ThenBy(n => n, StringComparer.Ordinal).ToList()))
            .ToList();
    }

    /// <summary>The full path when (and only when) the scenario and file are in the listing; otherwise null.</summary>
    public string? Resolve(string? scenario, string? file)
    {
        var match = Scenarios().FirstOrDefault(s => s.Name == scenario);
        if (match is null || file is null || !match.Files.Contains(file)) return null;
        return Path.Combine(Root, match.Name, file);
    }
}

public sealed record SampleScenario(string Name, IReadOnlyList<string> Files);
