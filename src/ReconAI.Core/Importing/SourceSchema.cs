namespace ReconAI.Core.Importing;

/// <summary>The three reconciliation sources.</summary>
public enum SourceKind
{
    Ledger = 1,
    Gateway = 2,
    Bank = 3,
}

/// <summary>
/// The canonical schema every source is mapped onto (SourceId and SourceRowId are assigned by the system, not mapped).
/// </summary>
public enum CanonicalField
{
    TransactionRef = 1,
    Amount = 2,
    Currency = 3,
    TxnDate = 4,
    SettlementRef = 5,
    Fees = 6,
    Status = 7,
}

public static class SourceSchema
{
    public static string Label(SourceKind kind) => kind switch
    {
        SourceKind.Ledger => "Merchant ledger",
        SourceKind.Gateway => "Gateway statement",
        SourceKind.Bank => "Bank settlement",
        _ => kind.ToString(),
    };

    public static string Label(CanonicalField field) => field switch
    {
        CanonicalField.TransactionRef => "Transaction reference",
        CanonicalField.Amount => "Amount",
        CanonicalField.Currency => "Currency",
        CanonicalField.TxnDate => "Transaction date",
        CanonicalField.SettlementRef => "Settlement reference",
        CanonicalField.Fees => "Fees",
        CanonicalField.Status => "Status",
        _ => field.ToString(),
    };

    public static string Describe(CanonicalField field) => field switch
    {
        CanonicalField.TransactionRef => "Order / payment reference that ties a ledger entry to a gateway payment",
        CanonicalField.Amount => "Gross amount for ledger and gateway rows; credited amount for bank rows",
        CanonicalField.Currency => "ISO currency code (defaults to INR when not mapped; only INR is supported)",
        CanonicalField.TxnDate => "Transaction or credit date",
        CanonicalField.SettlementRef => "Payout reference (UTR) that ties gateway payments to one bank credit",
        CanonicalField.Fees => "Fee the gateway deducted from this payment",
        CanonicalField.Status => "Optional status text",
        _ => "",
    };

    /// <summary>Fields that may be mapped for a source type (these populate the mapping drop-downs).</summary>
    public static IReadOnlyList<CanonicalField> Allowed(SourceKind kind) => kind switch
    {
        SourceKind.Ledger => [CanonicalField.TransactionRef, CanonicalField.Amount, CanonicalField.TxnDate, CanonicalField.Currency, CanonicalField.Status],
        SourceKind.Gateway => [CanonicalField.TransactionRef, CanonicalField.Amount, CanonicalField.Fees, CanonicalField.SettlementRef, CanonicalField.TxnDate, CanonicalField.Currency, CanonicalField.Status],
        SourceKind.Bank => [CanonicalField.SettlementRef, CanonicalField.Amount, CanonicalField.TxnDate, CanonicalField.Currency, CanonicalField.Status],
        _ => [],
    };

    /// <summary>Fields that must be mapped and present on every imported row.</summary>
    public static IReadOnlyList<CanonicalField> Mandatory(SourceKind kind) => kind switch
    {
        SourceKind.Ledger => [CanonicalField.TransactionRef, CanonicalField.Amount, CanonicalField.TxnDate],
        SourceKind.Gateway => [CanonicalField.TransactionRef, CanonicalField.Amount, CanonicalField.Fees, CanonicalField.SettlementRef, CanonicalField.TxnDate],
        SourceKind.Bank => [CanonicalField.SettlementRef, CanonicalField.Amount, CanonicalField.TxnDate],
        _ => [],
    };

    public static char RowKeyPrefix(SourceKind kind) => kind switch
    {
        SourceKind.Ledger => 'L',
        SourceKind.Gateway => 'G',
        SourceKind.Bank => 'B',
        _ => 'X',
    };
}
