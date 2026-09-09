using Microsoft.EntityFrameworkCore;
using SqlFlow.Catalog;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// Every catalog context must retry transient SQL Server failures, whichever host built it. The catalog is a
/// concurrent OLTP workload (one schedule fire enqueues every member flow at once, each run writing its own claim,
/// status, event and statement rows, with <c>CatalogTransaction</c> running its units at SERIALIZABLE), so SQL
/// Server picks deadlock victims (error 1205) as a matter of course. That is retryable, not a fault.
///
/// This suite exists because it once was not uniform: the control plane's pooled registration enabled retry while
/// <see cref="CatalogDatabase.BuildOptions"/>, used by the CLI and by the <c>sqlflow worker</c> the deployed worker
/// container runs, did not. A 23-flow schedule fire then failed 21 of its runs with EF Core's "An exception has been
/// raised that is likely due to a transient failure. Consider enabling transient error resiliency by adding
/// 'EnableRetryOnFailure' to the 'UseSqlServer' call", losing runs to catalog bookkeeping collisions that had
/// nothing to do with the data being loaded. Both hosts now go through <see cref="CatalogDatabase.Configure"/>.
///
/// No database is touched: the execution strategy is a property of the options, resolved without connecting.
/// </summary>
public sealed class CatalogResilienceTests
{
    private const string Connection = "Server=tcp:unreachable.invalid,1433;Database=catalog;User Id=u;Password=p;";

    [Fact]
    public void BuildOptions_EnablesTransientRetry()
    {
        using var context = new CatalogDbContext(CatalogDatabase.BuildOptions(Connection));

        Assert.True(
            context.Database.CreateExecutionStrategy().RetriesOnFailure,
            "The CLI and worker catalog context must retry transient failures; without it a deadlock victim fails the run.");
    }

    [Fact]
    public void Configure_EnablesTransientRetry_ForAnExternallyOwnedBuilder()
    {
        // The shape the control plane's AddDbContextPool registration uses: it owns pooling and the no-tracking read
        // posture, but the provider setup comes from the one shared definition.
        var builder = new DbContextOptionsBuilder<CatalogDbContext>();
        CatalogDatabase.Configure(builder, Connection);
        builder.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);

        using var context = new CatalogDbContext(builder.Options);

        Assert.True(context.Database.CreateExecutionStrategy().RetriesOnFailure);
        Assert.Equal(QueryTrackingBehavior.NoTracking, context.ChangeTracker.QueryTrackingBehavior);
    }

    [Fact]
    public void Configure_RejectsAMissingConnectionString()
    {
        var builder = new DbContextOptionsBuilder<CatalogDbContext>();

        Assert.Throws<ArgumentNullException>(() => CatalogDatabase.Configure(null!, Connection));
        Assert.Throws<ArgumentException>(() => CatalogDatabase.Configure(builder, "  "));
    }
}
