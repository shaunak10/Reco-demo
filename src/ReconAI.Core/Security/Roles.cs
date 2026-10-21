namespace ReconAI.Core.Security;

/// <summary>The two demo roles from the feature plan. A Company Admin can do everything an Operator can.</summary>
public enum UserRole
{
    Operator = 1,
    CompanyAdmin = 2,
}

/// <summary>Role names as they appear in the auth cookie's role claim.</summary>
public static class Roles
{
    public const string Operator = "Operator";
    public const string CompanyAdmin = "CompanyAdmin";

    public static string ToClaim(UserRole role) => role switch
    {
        UserRole.Operator => Operator,
        UserRole.CompanyAdmin => CompanyAdmin,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown role"),
    };
}

/// <summary>Authorization policy names. Rule approval and activation is CompanyAdminOnly - AI can never activate a rule.</summary>
public static class Policies
{
    public const string CompanyAdminOnly = "CompanyAdminOnly";
    public const string OperatorOrAdmin = "OperatorOrAdmin";
}
