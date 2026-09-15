namespace SqlFlow.Core.Hosting;

/// <summary>
/// How a host names the product it runs: the product name (notification subjects, the OpenAPI document title), the lockup
/// line where the product introduces itself (for example "Product, powered by SQLFlow"), and the CLI banner's tagline.
/// <see cref="SqlFlow"/> is SQLFlow's own text, which every host uses unless it passes branding of its own.
/// </summary>
public sealed class ProductBranding
{
    /// <summary>SQLFlow's product name.</summary>
    public const string SqlFlowProductName = "SQLFlow";

    /// <summary>SQLFlow's CLI tagline, after <c>sqlflow - </c> in the banner.</summary>
    public const string SqlFlowCliTagline = "metadata-driven ETL for SQL Server";

    /// <summary>The longest text a branding value may carry.</summary>
    public const int MaxLength = 200;

    /// <param name="productName">The product's name, for example in notification subjects.</param>
    /// <param name="lockup">The line where the product introduces itself, for example "Product, powered by SQLFlow".</param>
    /// <param name="cliTagline">The CLI banner's text after <c>sqlflow - </c>; the lockup when null.</param>
    /// <exception cref="ArgumentException">A value is blank, longer than <see cref="MaxLength"/>, padded, or not a single line of text.</exception>
    public ProductBranding(string productName, string lockup, string? cliTagline = null)
    {
        ProductName = Validated(productName, nameof(productName));
        Lockup = Validated(lockup, nameof(lockup));
        CliTagline = cliTagline is null ? Lockup : Validated(cliTagline, nameof(cliTagline));
    }

    /// <summary>SQLFlow's own branding: the text every host uses unless it passes its own.</summary>
    public static ProductBranding SqlFlow { get; } = new(SqlFlowProductName, SqlFlowProductName, SqlFlowCliTagline);

    /// <summary>The product's name.</summary>
    public string ProductName { get; }

    /// <summary>The line where the product introduces itself.</summary>
    public string Lockup { get; }

    /// <summary>The CLI banner's text after <c>sqlflow - </c>.</summary>
    public string CliTagline { get; }

    /// <summary>True for <see cref="SqlFlow"/> itself: the host passed no branding of its own.</summary>
    public bool IsSqlFlow => ReferenceEquals(this, SqlFlow);

    private static string Validated(string? value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A branding value must not be blank.", parameter);
        }

        if (value.Length > MaxLength)
        {
            throw new ArgumentException($"A branding value must be at most {MaxLength} characters; this one has {value.Length}.", parameter);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Any(char.IsControl))
        {
            throw new ArgumentException("A branding value must be a single line of text without leading or trailing whitespace.", parameter);
        }

        return value;
    }
}
