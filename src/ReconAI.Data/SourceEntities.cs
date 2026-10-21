using ReconAI.Core.Importing;

namespace ReconAI.Data;

public enum SourceFileStatus
{
    /// <summary>Stored and parsed, waiting for the operator to confirm the column mapping. No rows exist yet.</summary>
    AwaitingMapping = 1,
    Imported = 2,
}

public enum RowImportStatus
{
    Valid = 1,
    Rejected = 2,
}

/// <summary>An uploaded file. The original bytes are kept on disk (named by checksum) and never edited.</summary>
public class SourceFile : ITenantScoped
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public SourceKind Kind { get; set; }
    public string OriginalFileName { get; set; } = "";
    public string Format { get; set; } = "";
    public string? SheetName { get; set; }
    public long SizeBytes { get; set; }

    /// <summary>SHA-256 of the file bytes (lower-case hex). The same bytes cannot be imported twice for the same source.</summary>
    public string Sha256 { get; set; } = "";
    public string StoredPath { get; set; } = "";
    public string HeaderJson { get; set; } = "[]";

    public SourceFileStatus Status { get; set; }
    public int DataRowCount { get; set; }
    public int BlankRowsSkipped { get; set; }
    public int ValidRowCount { get; set; }
    public int RejectedRowCount { get; set; }

    public int UploadedByUserId { get; set; }
    public string UploadedByEmail { get; set; } = "";
    public DateTime UploadedUtc { get; set; }
    public DateTime? ImportedUtc { get; set; }

    public bool IsVoided { get; set; }
    public DateTime? VoidedUtc { get; set; }
    public string? VoidedByEmail { get; set; }
    public string? VoidReason { get; set; }
}

/// <summary>The column mapping an operator confirmed for a file. Append-only record of who confirmed what.</summary>
public class ColumnMapping : ITenantScoped, IAppendOnly
{
    public int Id { get; set; }
    public int TenantId { get; set; }
    public int SourceFileId { get; set; }
    public SourceFile? SourceFile { get; set; }
    public SourceKind Kind { get; set; }

    /// <summary>Hash of the file's headings, used to offer this mapping again for the same layout.</summary>
    public string HeaderSignature { get; set; } = "";

    /// <summary>JSON object: heading -> canonical field name, or null when the column is ignored.</summary>
    public string MappingJson { get; set; } = "{}";

    /// <summary>How the mapping came about: "Manual", "AI-assisted (accepted as suggested)" or "AI-assisted (operator changed N of M columns)".</summary>
    public string Origin { get; set; } = "Manual";

    /// <summary>The AI call whose suggestions the operator reviewed, if any. The AI never confirms anything itself.</summary>
    public long? AiInferenceId { get; set; }
    public int AiSuggestedCount { get; set; }
    public int AiAcceptedCount { get; set; }
    public int AiChangedColumns { get; set; }
    public int ConfirmedByUserId { get; set; }
    public string ConfirmedByEmail { get; set; } = "";
    public DateTime ConfirmedUtc { get; set; }
}

/// <summary>
/// One row of an imported file: its immutable identity (<see cref="RowKey"/>, <see cref="RowSha256"/>), the original
/// cell values (<see cref="RawJson"/>, aligned with the file's headings) and the canonical fields derived from them.
/// Append-only: the database context refuses updates and deletes.
/// </summary>
public class SourceRow : ITenantScoped, IAppendOnly
{
    public long Id { get; set; }
    public int TenantId { get; set; }
    public int SourceFileId { get; set; }
    public SourceFile? SourceFile { get; set; }
    public SourceKind Kind { get; set; }
    public int RowNumber { get; set; }
    public string RowKey { get; set; } = "";
    public string RowSha256 { get; set; } = "";
    public string RawJson { get; set; } = "[]";

    public RowImportStatus ImportStatus { get; set; }
    public string? RejectReason { get; set; }

    public string? TransactionRef { get; set; }
    public string? SettlementRef { get; set; }
    public decimal? Amount { get; set; }
    public decimal? Fees { get; set; }
    public string Currency { get; set; } = "INR";
    public DateOnly? TxnDate { get; set; }
    public string? Status { get; set; }
}
