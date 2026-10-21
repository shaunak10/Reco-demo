using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReconAI.Core.Importing;
using ReconAI.Data;

namespace ReconAI.Web.Services;

public sealed record UploadOutcome(bool Ok, string? Error, int? FileId);

public sealed record MappingProposal(
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    IReadOnlyList<CanonicalField?>? SavedFields,
    string? SavedBy,
    DateTime? SavedUtc);

public sealed record PreviewRow(int RowNumber, string RowKey, string? TransactionRef, string? SettlementRef, string? Amount, string? Fees, string? TxnDate, string? Status, string? Problem);

public sealed record PreviewResult(
    bool Ok,
    IReadOnlyList<string> Problems,
    int TotalRows,
    int ValidRows,
    int RejectedRows,
    string ValidAmountTotal,
    IReadOnlyList<RejectReason> RejectReasons,
    IReadOnlyList<PreviewRow> SampleValid,
    IReadOnlyList<PreviewRow> SampleRejected);

public sealed record ConfirmOutcome(bool Ok, string? Error, int Valid, int Rejected);

public sealed class FileTotals
{
    public int ValidRows { get; set; }
    public int RejectedRows { get; set; }
    public long AmountPaise { get; set; }
    public long FeesPaise { get; set; }
}

/// <summary>
/// Upload, mapping confirmation, import and voiding of source files. The original bytes are stored untouched and named
/// by checksum, rows are written once (append-only), and nothing becomes a SourceRow until an operator confirms the mapping.
/// </summary>
public sealed class ImportService(
    ReconDbContext db,
    AuditLog audit,
    IWebHostEnvironment env,
    IConfiguration config,
    TimeProvider clock,
    ILogger<ImportService> log)
{
    public static readonly ImportLimits Limits = new();

    private string UploadsRoot
    {
        get
        {
            var configured = config["Storage:UploadsPath"] ?? Path.Combine("App_Data", "uploads");
            return Path.IsPathRooted(configured) ? configured : Path.Combine(env.ContentRootPath, configured);
        }
    }

    // ------------------------------------------------------------------ upload
    public async Task<UploadOutcome> UploadAsync(SourceKind kind, IFormFile? file, ClaimsPrincipal user, CancellationToken ct)
    {
        if (file is null) return Reject(kind, user, "Choose a file to upload.", null);

        var name = CleanName(file.FileName);
        if (file.Length == 0) return Reject(kind, user, $"'{name}' is empty.", name);
        if (file.Length > Limits.MaxBytes)
            return Reject(kind, user, $"'{name}' is {file.Length / 1048576.0:0.#} MB; the limit is {Limits.MaxBytes / 1048576} MB.", name);

        byte[] bytes;
        using (var ms = new MemoryStream((int)file.Length))
        {
            await file.CopyToAsync(ms, ct);
            bytes = ms.ToArray();
        }

        TabularFile parsed;
        try
        {
            parsed = TabularReader.Read(bytes, name, Limits);
        }
        catch (ImportException ex)
        {
            return Reject(kind, user, $"'{name}': {ex.Message}", name);
        }

        var sha = Hashing.Sha256Hex(bytes);
        var existing = await db.SourceFiles.Where(f => f.Sha256 == sha && !f.IsVoided).OrderBy(f => f.Id).FirstOrDefaultAsync(ct);
        if (existing is not null) return Reject(kind, user, DuplicateMessage(kind, name, existing), name, $"Duplicate of file #{existing.Id}");

        var stored = await StoreAsync(user, sha, parsed.Format, bytes, ct);
        var sourceFile = new SourceFile
        {
            TenantId = TenantOf(user),
            Kind = kind,
            OriginalFileName = name,
            Format = parsed.Format,
            SheetName = parsed.SheetName,
            SizeBytes = bytes.Length,
            Sha256 = sha,
            StoredPath = stored,
            HeaderJson = JsonSerializer.Serialize(parsed.Headers),
            Status = SourceFileStatus.AwaitingMapping,
            DataRowCount = parsed.Rows.Count,
            BlankRowsSkipped = parsed.BlankRowsSkipped,
            UploadedByUserId = UserIdOf(user),
            UploadedByEmail = user.FindFirstValue(ClaimTypes.Email) ?? "",
            UploadedUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.SourceFiles.Add(sourceFile);

        try
        {
            await db.SaveChangesAsync(ct);   // the filtered unique index also catches two simultaneous uploads of the same bytes
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await db.SourceFiles.FirstOrDefaultAsync(f => f.Sha256 == sha && !f.IsVoided, ct);
            return Reject(kind, user, winner is null ? "The file could not be saved. Please try again." : DuplicateMessage(kind, name, winner), name);
        }

        audit.Add(user, "FileUploaded", nameof(SourceFile), sourceFile.Id.ToString(),
            $"{SourceSchema.Label(kind)}: {parsed.Rows.Count} data rows, {parsed.Headers.Count} columns, sha256 {sha[..12]}");
        await db.SaveChangesAsync(ct);
        return new UploadOutcome(true, null, sourceFile.Id);
    }

    private UploadOutcome Reject(SourceKind kind, ClaimsPrincipal user, string message, string? fileName, string? auditNote = null)
    {
        audit.Add(user, "UploadRejected", nameof(SourceFile), "", $"{SourceSchema.Label(kind)}{(fileName is null ? "" : $" '{fileName}'")}: {auditNote ?? message}");
        db.SaveChanges();
        return new UploadOutcome(false, message, null);
    }

    private static string DuplicateMessage(SourceKind kind, string name, SourceFile existing) =>
        existing.Kind == kind
            ? $"'{name}' was already uploaded as '{existing.OriginalFileName}' (file #{existing.Id}) by {existing.UploadedByEmail} on {existing.UploadedUtc:dd MMM yyyy HH:mm} UTC. It was not imported a second time, so nothing is double-counted."
            : $"This exact file was already uploaded as a {SourceSchema.Label(existing.Kind).ToLowerInvariant()} (file #{existing.Id}). Check that you chose the right upload box.";

    private async Task<string> StoreAsync(ClaimsPrincipal user, string sha, string format, byte[] bytes, CancellationToken ct)
    {
        var relative = Path.Combine(TenantOf(user).ToString(), $"{sha}.{format}");   // never derived from the user's file name
        var full = Path.Combine(UploadsRoot, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        if (!File.Exists(full)) await File.WriteAllBytesAsync(full, bytes, ct);
        return relative;
    }

    /// <summary>Display name only: no path segments, no control characters, bounded length.</summary>
    public static string CleanName(string? raw)
    {
        var name = Path.GetFileName((raw ?? "").Replace('\\', '/'));
        name = new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (name.Length == 0) name = "upload";
        return name.Length <= 120 ? name : name[..100] + Path.GetExtension(name);
    }

    // ------------------------------------------------------------------ read side
    public Task<SourceFile?> FindAsync(int id, CancellationToken ct) => db.SourceFiles.FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<IReadOnlyList<SourceFile>> ListAsync(bool includeVoided, CancellationToken ct) =>
        await db.SourceFiles.Where(f => includeVoided || !f.IsVoided).OrderByDescending(f => f.UploadedUtc).ThenByDescending(f => f.Id).ToListAsync(ct);

    public IReadOnlyList<string> HeadersOf(SourceFile file) => JsonSerializer.Deserialize<List<string>>(file.HeaderJson) ?? [];

    public async Task<MappingProposal> ProposeAsync(SourceFile file, CancellationToken ct)
    {
        var (parsed, _) = await ReadStoredAsync(file, ct);
        var sample = parsed.Rows.Take(5).Select(r => (IReadOnlyList<string>)r.Cells).ToList();

        var signature = Canonicalizer.HeaderSignature(parsed.Headers);
        var saved = await db.ColumnMappings
            .Where(m => m.Kind == file.Kind && m.HeaderSignature == signature)
            .OrderByDescending(m => m.ConfirmedUtc).FirstOrDefaultAsync(ct);

        IReadOnlyList<CanonicalField?>? savedFields = null;
        if (saved is not null)
        {
            var byHeader = new Dictionary<string, string?>(
                JsonSerializer.Deserialize<Dictionary<string, string?>>(saved.MappingJson) ?? [], StringComparer.OrdinalIgnoreCase);
            savedFields = parsed.Headers
                .Select(h => byHeader.TryGetValue(h, out var f) && Enum.TryParse<CanonicalField>(f, out var parsedField) ? (CanonicalField?)parsedField : null)
                .ToList();
        }
        return new MappingProposal(parsed.Headers, sample, savedFields, saved?.ConfirmedByEmail, saved?.ConfirmedUtc);
    }

    public async Task<FileTotals> TotalsAsync(SourceFile f, CancellationToken ct)
    {
        // Exact integer SUM in SQL (amounts are stored as paise). Raw SQL bypasses the tenant filter, so the tenant is explicit.
        var t = await db.Database.SqlQuery<FileTotals>($@"
            SELECT COALESCE(SUM(CASE WHEN ImportStatus = 'Valid' THEN 1 ELSE 0 END), 0) AS ValidRows,
                   COALESCE(SUM(CASE WHEN ImportStatus = 'Rejected' THEN 1 ELSE 0 END), 0) AS RejectedRows,
                   COALESCE(SUM(CASE WHEN ImportStatus = 'Valid' THEN Amount END), 0) AS AmountPaise,
                   COALESCE(SUM(CASE WHEN ImportStatus = 'Valid' THEN Fees END), 0) AS FeesPaise
            FROM SourceRows WHERE TenantId = {f.TenantId} AND SourceFileId = {f.Id}").SingleAsync(ct);
        return t;
    }

    public async Task<ColumnMapping?> MappingOfAsync(int fileId, CancellationToken ct) =>
        await db.ColumnMappings.FirstOrDefaultAsync(m => m.SourceFileId == fileId, ct);

    public async Task<(IReadOnlyList<SourceRow> Rows, int Total)> RowsAsync(int fileId, RowImportStatus? status, int page, int pageSize, CancellationToken ct)
    {
        var q = db.SourceRows.Where(r => r.SourceFileId == fileId);
        if (status is { } s) q = q.Where(r => r.ImportStatus == s);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(r => r.RowNumber).Skip(Math.Max(0, page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return (rows, total);
    }

    // ------------------------------------------------------------------ mapping preview and confirmation
    public async Task<PreviewResult> PreviewAsync(SourceFile file, IReadOnlyList<string?> fields, CancellationToken ct)
    {
        try
        {
            var (parsed, sha) = await ReadStoredAsync(file, ct);
            var map = ParseMap(parsed, fields);
            var result = Canonicalizer.Run(file.Kind, sha, parsed, map);
            return new PreviewResult(true, [], result.Rows.Count, result.ValidCount, result.RejectedCount,
                Fmt.Money(result.ValidAmountTotal), result.RejectReasons,
                result.Rows.Where(r => r.IsValid).Take(6).Select(r => ToPreview(r, null)).ToList(),
                result.Rows.Where(r => !r.IsValid).Take(6).Select(r => ToPreview(r, string.Join("; ", r.Errors.Select(e => e.Message)))).ToList());
        }
        catch (ImportException ex)
        {
            return new PreviewResult(false, [ex.Message], 0, 0, 0, "0.00", [], [], []);
        }
    }

    private static PreviewRow ToPreview(CanonicalRow r, string? problem) =>
        new(r.RowNumber, r.RowKey, r.TransactionRef, r.SettlementRef, Fmt.Money(r.Amount), Fmt.Money(r.Fees), r.TxnDate?.ToString("yyyy-MM-dd"), r.Status, problem);

    public async Task<ConfirmOutcome> ConfirmAsync(SourceFile file, IReadOnlyList<string?> fields, ClaimsPrincipal user, CancellationToken ct)
    {
        if (file.IsVoided) return new ConfirmOutcome(false, "This file was voided and can no longer be imported.", 0, 0);
        if (file.Status != SourceFileStatus.AwaitingMapping) return new ConfirmOutcome(false, "This file has already been imported; nothing was imported twice.", 0, 0);

        CanonicalizeResult result;
        FieldMap map;
        IReadOnlyList<string> headers;
        try
        {
            var (parsed, sha) = await ReadStoredAsync(file, ct);
            headers = parsed.Headers;
            map = ParseMap(parsed, fields);
            result = Canonicalizer.Run(file.Kind, sha, parsed, map);
        }
        catch (ImportException ex)
        {
            return new ConfirmOutcome(false, ex.Message, 0, 0);
        }

        if (result.ValidCount == 0)
        {
            var top = result.RejectReasons.FirstOrDefault();
            return new ConfirmOutcome(false,
                $"No row passed validation with this mapping, so nothing was imported. {(top is null ? "" : $"Most common problem ({top.Count} rows): {top.Example}. ")}Check which column is mapped to which field.", 0, result.RejectedCount);
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var now = clock.GetUtcNow().UtcDateTime;
            db.ColumnMappings.Add(new ColumnMapping
            {
                TenantId = file.TenantId,
                SourceFileId = file.Id,
                Kind = file.Kind,
                HeaderSignature = Canonicalizer.HeaderSignature(headers),
                MappingJson = JsonSerializer.Serialize(map.ToHeaderDictionary(headers)),
                Origin = "Manual",
                ConfirmedByUserId = UserIdOf(user),
                ConfirmedByEmail = user.FindFirstValue(ClaimTypes.Email) ?? "",
                ConfirmedUtc = now,
            });

            foreach (var r in result.Rows)
            {
                db.SourceRows.Add(new SourceRow
                {
                    TenantId = file.TenantId,
                    SourceFileId = file.Id,
                    Kind = file.Kind,
                    RowNumber = r.RowNumber,
                    RowKey = r.RowKey,
                    RowSha256 = r.RowSha256,
                    RawJson = r.RawJson,
                    ImportStatus = r.IsValid ? RowImportStatus.Valid : RowImportStatus.Rejected,
                    RejectReason = r.IsValid ? null : Truncate(string.Join("; ", r.Errors.Select(e => e.Message)), 600),
                    TransactionRef = r.TransactionRef,
                    SettlementRef = r.SettlementRef,
                    Amount = r.Amount,
                    Fees = r.Fees,
                    Currency = r.Currency,
                    TxnDate = r.TxnDate,
                    Status = r.Status,
                });
            }

            file.Status = SourceFileStatus.Imported;
            file.ValidRowCount = result.ValidCount;
            file.RejectedRowCount = result.RejectedCount;
            file.ImportedUtc = now;

            var assignments = string.Join(", ", headers.Select((h, i) => (h, f: map.ByColumn[i])).Where(x => x.f is not null).Select(x => $"{x.h} -> {x.f}"));
            audit.Add(user, "FileImported", nameof(SourceFile), file.Id.ToString(),
                $"{result.ValidCount} valid, {result.RejectedCount} rejected rows. Mapping confirmed: {assignments}");
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            log.LogWarning(ex, "Import of file {FileId} failed; rolled back", file.Id);
            db.ChangeTracker.Clear();
            return new ConfirmOutcome(false, "The import could not be completed (it may already have been imported in another window). Nothing was imported twice.", 0, 0);
        }
        return new ConfirmOutcome(true, null, result.ValidCount, result.RejectedCount);
    }

    // ------------------------------------------------------------------ void
    public async Task<string?> VoidAsync(SourceFile file, string? reason, ClaimsPrincipal user, CancellationToken ct)
    {
        reason = (reason ?? "").Trim();
        if (reason.Length < 3) return "Give a short reason (at least 3 characters) for voiding the file.";
        if (file.IsVoided) return "This file is already voided.";
        // Step 6 will also refuse to void a file that a reconciliation run has used.
        file.IsVoided = true;
        file.VoidedUtc = clock.GetUtcNow().UtcDateTime;
        file.VoidedByEmail = user.FindFirstValue(ClaimTypes.Email);
        file.VoidReason = Truncate(reason, 300);
        audit.Add(user, "FileVoided", nameof(SourceFile), file.Id.ToString(), $"Voided: {Truncate(reason, 200)}");
        await db.SaveChangesAsync(ct);
        return null;
    }

    // ------------------------------------------------------------------ helpers
    private async Task<(TabularFile Parsed, string Sha)> ReadStoredAsync(SourceFile file, CancellationToken ct)
    {
        var full = Path.Combine(UploadsRoot, file.StoredPath);
        if (!File.Exists(full)) throw new ImportException("The stored copy of this file is missing. Please upload it again.");
        var bytes = await File.ReadAllBytesAsync(full, ct);
        var sha = Hashing.Sha256Hex(bytes);
        if (!string.Equals(sha, file.Sha256, StringComparison.Ordinal))
            throw new ImportException("The stored copy of this file no longer matches its checksum and was not used.");
        return (TabularReader.Read(bytes, $"stored.{file.Format}", Limits), sha);
    }

    private static FieldMap ParseMap(TabularFile parsed, IReadOnlyList<string?> fields)
    {
        if (fields.Count != parsed.Headers.Count) throw new ImportException("The mapping does not match the columns of the file. Reload the page and try again.");
        return FieldMap.Parse(fields);
    }

    private static int TenantOf(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(AppClaims.TenantId)!);

    private static int UserIdOf(ClaimsPrincipal user) => int.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
