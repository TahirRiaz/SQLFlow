namespace SqlFlow.Catalog.Modules;

/// <summary>How a module database stands against the build and the catalog.</summary>
public enum ModuleDatabaseState
{
    /// <summary>Every migration this build knows is applied, none it does not know, and the catalog has the migration the module needs.</summary>
    Current,

    /// <summary>The database the module's connection names does not exist.</summary>
    Missing,

    /// <summary>Migrations this build knows are not applied.</summary>
    Behind,

    /// <summary>The database has applied migrations this build does not know: a newer build migrated it.</summary>
    Ahead,

    /// <summary>Both: migrations are pending and others this build does not know are applied.</summary>
    Diverged,

    /// <summary>The module database is current, but the catalog has not applied the catalog migration the module needs.</summary>
    CatalogBehind,
}

/// <summary>
/// A module database's migrations as a host sees them: what the build knows, what the database has applied, the version the
/// module recorded, and, when the host has a catalog, whether the catalog has the migration the module needs.
/// <see cref="Summary"/> is the one-line report the CLI prints and the control plane logs; <see cref="ThrowIfNotCurrent"/> is
/// the refusal both hosts raise.
/// </summary>
public sealed class ModuleDatabaseStatus
{
    private const int NamesShown = 5;

    /// <param name="database">The module database.</param>
    /// <param name="databaseExists">False when the database the module's connection names does not exist.</param>
    /// <param name="known">Every migration this build knows, oldest first.</param>
    /// <param name="applied">Every migration the database has applied, oldest first.</param>
    /// <param name="recorded">The version the module recorded, or null.</param>
    /// <param name="catalogAppliedMigrations">The catalog's applied migrations, or null when the host has no catalog to check.</param>
    public ModuleDatabaseStatus(
        ModuleDatabase database,
        bool databaseExists,
        IEnumerable<string> known,
        IEnumerable<string> applied,
        ModuleRecordedVersion? recorded,
        IReadOnlyCollection<string>? catalogAppliedMigrations)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(known);
        ArgumentNullException.ThrowIfNull(applied);
        Module = database.Module;
        Schema = database.Schema;
        Description = database.Describe();
        BuildVersion = database.Version;
        DatabaseExists = databaseExists;
        Known = [.. known];
        Applied = [.. applied];
        var knownSet = Known.ToHashSet(StringComparer.Ordinal);
        var appliedSet = Applied.ToHashSet(StringComparer.Ordinal);
        Pending = [.. Known.Where(m => !appliedSet.Contains(m))];
        Unknown = [.. Applied.Where(m => !knownSet.Contains(m))];
        Recorded = recorded;
        MinimumCatalogMigration = database.MinimumCatalogMigration;
        CatalogHasMinimumMigration = MinimumCatalogMigration is null || catalogAppliedMigrations is null
            ? null
            : catalogAppliedMigrations.Contains(MinimumCatalogMigration, StringComparer.Ordinal);
    }

    /// <summary>The module the database belongs to.</summary>
    public string Module { get; }

    /// <summary>The module's schema.</summary>
    public string Schema { get; }

    /// <summary>The module, schema and connection, secret-free.</summary>
    public string Description { get; }

    /// <summary>The module's schema version in this build.</summary>
    public string BuildVersion { get; }

    /// <summary>False when the database the module's connection names does not exist.</summary>
    public bool DatabaseExists { get; }

    /// <summary>Every migration this build knows, oldest first.</summary>
    public IReadOnlyList<string> Known { get; }

    /// <summary>Every migration the database has applied, oldest first.</summary>
    public IReadOnlyList<string> Applied { get; }

    /// <summary>Migrations this build knows that the database has not applied, oldest first.</summary>
    public IReadOnlyList<string> Pending { get; }

    /// <summary>Migrations the database has applied that this build does not know, oldest first.</summary>
    public IReadOnlyList<string> Unknown { get; }

    /// <summary>The version the module recorded in its database, or null when it records none or has not been migrated.</summary>
    public ModuleRecordedVersion? Recorded { get; }

    /// <summary>The catalog migration the module needs, or null.</summary>
    public string? MinimumCatalogMigration { get; }

    /// <summary>Whether the catalog has applied <see cref="MinimumCatalogMigration"/>; null when there is none to check or the host has no catalog.</summary>
    public bool? CatalogHasMinimumMigration { get; }

    public ModuleDatabaseState State
    {
        get
        {
            if (!DatabaseExists)
            {
                return ModuleDatabaseState.Missing;
            }

            return (Pending.Count, Unknown.Count) switch
            {
                (> 0, > 0) => ModuleDatabaseState.Diverged,
                (> 0, 0) => ModuleDatabaseState.Behind,
                (0, > 0) => ModuleDatabaseState.Ahead,
                _ => CatalogHasMinimumMigration == false ? ModuleDatabaseState.CatalogBehind : ModuleDatabaseState.Current,
            };
        }
    }

    /// <summary>True when the database is <see cref="ModuleDatabaseState.Current"/>.</summary>
    public bool IsCurrent => State == ModuleDatabaseState.Current;

    /// <summary>One line: the module, its state, the migration counts, the versions and the catalog check.</summary>
    public string Summary() => State switch
    {
        ModuleDatabaseState.Current =>
            $"{Description}: current at '{(Applied.Count > 0 ? Applied[^1] : "(no migrations)")}' ({Applied.Count} migration(s) applied, 0 pending; {Details()}).",
        ModuleDatabaseState.Missing =>
            $"{Description}: the database does not exist ({Known.Count} migration(s) to apply; {Details()}).",
        ModuleDatabaseState.Behind =>
            $"{Description}: behind this build ({Applied.Count} migration(s) applied, {Pending.Count} pending; {Details()}).",
        ModuleDatabaseState.Ahead =>
            $"{Description}: ahead of this build ({Unknown.Count} applied migration(s) this build does not know; {Details()}).",
        ModuleDatabaseState.Diverged =>
            $"{Description}: diverged from this build ({Pending.Count} pending, {Unknown.Count} applied migration(s) this build does not know; {Details()}).",
        _ =>
            $"{Description}: current, but the catalog has not applied migration '{MinimumCatalogMigration}', which the module needs ({Details()}).",
    };

    /// <summary>What is wrong, naming the module and the migrations, or null when the database is current.</summary>
    public string? Problem() => State switch
    {
        ModuleDatabaseState.Current => null,
        ModuleDatabaseState.Missing =>
            $"The database of {Description} does not exist. Provision it with 'sqlflow db migrate --create', or point the module's connection at an existing database.",
        ModuleDatabaseState.Behind =>
            $"The database of {Description} is behind this build: {Pending.Count} migration(s) are not applied ({Names(Pending)}). Apply them with 'sqlflow db migrate'.",
        ModuleDatabaseState.Ahead =>
            $"The database of {Description} is ahead of this build: it has {Unknown.Count} migration(s) this build does not know ({Names(Unknown)}; {Versions()}). Run a build of module '{Module}' that includes them.",
        ModuleDatabaseState.Diverged =>
            $"The database of {Description} has diverged from this build: {Names(Unknown)} applied but unknown to this build, and {Names(Pending)} known but not applied.",
        _ => MissingCatalogMigration(Module, MinimumCatalogMigration!),
    };

    /// <summary>Throws <see cref="ModuleDatabaseException"/> with <see cref="Problem"/> unless the database is current.</summary>
    public void ThrowIfNotCurrent()
    {
        if (Problem() is { } problem)
        {
            throw new ModuleDatabaseException(Module, problem, this);
        }
    }

    /// <summary>The refusal when the catalog has not applied the migration a module needs.</summary>
    internal static string MissingCatalogMigration(string module, string migration)
        => $"Module '{module}' needs the catalog migration '{migration}', which the catalog has not applied. Migrate the catalog first ('sqlflow db migrate').";

    private string Details()
    {
        var catalog = MinimumCatalogMigration is null
            ? string.Empty
            : CatalogHasMinimumMigration switch
            {
                true => $", catalog has '{MinimumCatalogMigration}'",
                false => $", catalog lacks '{MinimumCatalogMigration}'",
                null => $", catalog migration '{MinimumCatalogMigration}' not checked",
            };
        return Versions() + catalog;
    }

    private string Versions()
        => Recorded is null
            ? $"build version {BuildVersion}, no version recorded"
            : $"build version {BuildVersion}, recorded version {Recorded.Version}";

    private static string Names(IReadOnlyList<string> migrations)
    {
        var shown = string.Join(", ", migrations.Take(NamesShown).Select(m => $"'{m}'"));
        return migrations.Count > NamesShown ? $"{shown} and {migrations.Count - NamesShown} more" : shown;
    }
}
