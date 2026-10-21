using ReconAI.Core.Ai;
using ReconAI.Core.Importing;

namespace ReconAI.Tests.Ai;

/// <summary>The AI service is untrusted: every answer is re-checked here before the operator sees it.</summary>
public class AiMappingValidatorTests
{
    private static readonly string[] Headings = ["Ledger_ID", "Order_Ref", "Amount_INR"];
    private static readonly RawModel Model = new("reconai-column-ranker", "abcd1234", "logistic regression", 5000);

    private static RawSuggestion S(int i, string? field, double conf = 0.9, string? header = null, string? band = "high", List<string>? reasons = null, List<RawAlternative>? alts = null) =>
        new(i, header ?? Headings[i], field, conf, band, reasons ?? ["why"], alts ?? []);

    private static ValidatedMapping Run(SourceKind kind, params RawSuggestion[] s) =>
        AiMappingValidator.Validate(kind, Headings, new RawMappingResponse(Model, 12, s.ToList()));

    [Fact]
    public void A_well_formed_answer_is_accepted_for_every_column()
    {
        var v = Run(SourceKind.Ledger, S(0, null, 0.97), S(1, "TransactionRef", 0.93), S(2, "Amount", 0.88));
        Assert.Empty(v.Problems);
        Assert.Equal([null, CanonicalField.TransactionRef, CanonicalField.Amount], v.Suggestions.Select(x => x.Field));
        Assert.Equal("reconai-column-ranker", v.Model.Name);
    }

    [Fact]
    public void The_confidence_band_is_recomputed_never_trusted()
    {
        var v = Run(SourceKind.Ledger, S(0, null, 0.97), S(1, "TransactionRef", 0.30, band: "high"), S(2, "Amount", 0.60, band: "high"));
        Assert.Equal(["high", "low", "medium"], v.Suggestions.Select(x => x.Band));
    }

    [Fact]
    public void An_unknown_field_name_is_dropped_and_reported()
    {
        var v = Run(SourceKind.Ledger, S(0, null), S(1, "DROP TABLE Users", 0.99), S(2, "Amount"));
        Assert.Null(v.Suggestions[1].Field);
        Assert.Equal(0, v.Suggestions[1].Confidence);
        Assert.Contains(v.Problems, p => p.Contains("column 2") && p.Contains("not a field"));
    }

    [Fact]
    public void A_field_the_source_cannot_have_is_dropped()
    {
        var v = Run(SourceKind.Bank, S(0, null), S(1, "TransactionRef", 0.9), S(2, "Fees", 0.9));   // a bank statement has neither
        Assert.All(v.Suggestions, s => Assert.Null(s.Field));
        Assert.Equal(2, v.Problems.Count);
    }

    [Fact]
    public void Two_columns_claiming_one_field_keep_only_the_more_confident()
    {
        var v = Run(SourceKind.Ledger, S(0, null), S(1, "Amount", 0.55), S(2, "Amount", 0.91));
        Assert.Null(v.Suggestions[1].Field);
        Assert.Equal(CanonicalField.Amount, v.Suggestions[2].Field);
        Assert.Contains(v.Problems, p => p.Contains("several columns"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    public void Confidence_outside_zero_to_one_is_rejected(double confidence)
    {
        var v = Run(SourceKind.Ledger, S(0, null), S(1, "TransactionRef", confidence), S(2, "Amount"));
        Assert.Null(v.Suggestions[1].Field);
        Assert.Contains(v.Problems, p => p.Contains("confidence"));
    }

    [Fact]
    public void A_suggestion_whose_heading_does_not_match_the_column_is_dropped()
    {
        var v = Run(SourceKind.Ledger, S(0, null), S(1, "TransactionRef", header: "Some Other Column"), S(2, "Amount"));
        Assert.Null(v.Suggestions[1].Field);
        Assert.Contains(v.Problems, p => p.Contains("heading did not match"));
    }

    [Fact]
    public void Missing_non_existent_and_repeated_columns_are_handled()
    {
        var v = Run(SourceKind.Ledger, S(1, "TransactionRef"), S(1, "Amount"), new RawSuggestion(9, "x", "Amount", 0.9, "high", [], []));
        Assert.Equal(3, v.Suggestions.Count);                                           // every column still gets an entry
        Assert.Equal(CanonicalField.TransactionRef, v.Suggestions[1].Field);            // the first claim for column 2 wins
        Assert.Equal("low", v.Suggestions[0].Band);
        Assert.Contains("No usable suggestion", v.Suggestions[2].Reasons[0]);
        Assert.Equal(2, v.Problems.Count);
    }

    [Fact]
    public void Reasons_are_cleaned_clipped_and_limited()
    {
        var long_ = new string('x', 500);
        var v = Run(SourceKind.Ledger, S(0, null), S(1, "TransactionRef", reasons: ["ok\u0007\u001b[31m", long_, "", "c", "d", "e"]), S(2, "Amount"));
        var reasons = v.Suggestions[1].Reasons;
        Assert.Equal(AiMappingValidator.MaxReasons, reasons.Count);
        Assert.DoesNotContain(reasons, r => r.Any(char.IsControl));
        Assert.All(reasons, r => Assert.True(r.Length <= AiMappingValidator.MaxReasonLength + 3));
    }

    [Fact]
    public void Alternatives_are_validated_and_limited_to_three()
    {
        var alts = new List<RawAlternative> { new("Amount", 0.3), new("Nope", 0.2), new(null, 0.2), new("TxnDate", 0.1), new("Status", 0.05), new("Currency", 9.0) };
        var v = Run(SourceKind.Ledger, S(0, null), S(1, "TransactionRef", alts: alts), S(2, "Amount"));
        var kept = v.Suggestions[1].Alternatives;
        Assert.Equal([CanonicalField.Amount, null, CanonicalField.TxnDate], kept.Select(a => a.Field));
    }

    [Fact]
    public void An_answer_without_a_model_or_list_is_refused_outright()
    {
        Assert.Throws<AiResponseException>(() => AiMappingValidator.Validate(SourceKind.Ledger, Headings, null));
        Assert.Throws<AiResponseException>(() => AiMappingValidator.Validate(SourceKind.Ledger, Headings, new RawMappingResponse(null, 0, [])));
        Assert.Throws<AiResponseException>(() => AiMappingValidator.Validate(SourceKind.Ledger, Headings, new RawMappingResponse(new RawModel("", "1", "", 0), 0, [])));
        Assert.Throws<AiResponseException>(() => AiMappingValidator.Validate(SourceKind.Ledger, Headings, new RawMappingResponse(Model, 0, null)));
    }
}
