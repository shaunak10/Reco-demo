using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ReconAI.Data;

/// <summary>Lets <c>dotnet ef migrations add</c> build the model without starting the web app.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<ReconDbContext>
{
    public ReconDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ReconDbContext>()
            .UseSqlite("Data Source=design-time-only.db")
            .Options;
        return new ReconDbContext(options, new NoTenantContext());
    }
}
