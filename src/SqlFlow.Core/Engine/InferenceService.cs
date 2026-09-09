using System.Globalization;
using SqlFlow.Core.Abstractions;
using SqlFlow.Core.Model;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Core.Engine;

/// <summary>
/// The independent inference process: reads a loaded table, profiles each column server-side, and
/// determines the optimal SQL type. The result is an <see cref="InferenceReport"/> (JSON) carrying the
/// per-column types, a runnable transform SELECT, and (via <see cref="ValidateAsync"/>) a sanity check
/// of the conversions against the real data. It does not require the metadata database and is decoupled
/// from loading - it can be hooked into a load or run on its own against any already-loaded table.
/// </summary>
public sealed class InferenceService : IInferenceService
{
    private readonly ISchemaProvider _schema;
    private readonly IColumnProfiler _profiler;
    private readonly TypeInferencer _inferencer;
    private readonly IInferenceValidator _validator;
    private readonly IServerLocaleProvider _locale;
    private readonly ISecretResolver _secrets;

    public InferenceService(
        ISchemaProvider schema,
        IColumnProfiler profiler,
        TypeInferencer inferencer,
        IInferenceValidator validator,
        IServerLocaleProvider locale,
        ISecretResolver secrets)
    {
        _schema = schema;
        _profiler = profiler;
        _inferencer = inferencer;
        _validator = validator;
        _locale = locale;
        _secrets = secrets;
    }

    public async Task<InferenceReport> InferAsync(InferenceRequest request, CancellationToken ct = default)
    {
        var (_, report, _, _) = await InferCoreAsync(request, ct).ConfigureAwait(false);
        return report;
    }

    public async Task<InferenceReport> ValidateAsync(InferenceRequest request, CancellationToken ct = default)
    {
        var (connectionString, report, inferred, locale) = await InferCoreAsync(request, ct).ConfigureAwait(false);

        var checks = inferred
            .Where(c => c.Converted)
            .Select(c => new ConversionCheck
            {
                ColumnName = c.ColumnName,
                DataType = c.DataType,
                Style = c.Style,
                NumericFormat = c.NumericFormat,
            })
            .ToList();

        var counts = checks.Count == 0
            ? []
            : await _validator.CheckAsync(connectionString, request.Schema, request.Table, checks, locale, ct).ConfigureAwait(false);
        var countByColumn = counts.ToDictionary(c => c.ColumnName, StringComparer.Ordinal);

        var columns = inferred.Select(c =>
        {
            if (!c.Converted)
            {
                return new ColumnValidation
                {
                    ColumnName = c.ColumnName,
                    DataType = c.DataType,
                    Status = "kept-string",
                    FitPercent = 100,
                };
            }

            var nonNull = countByColumn.TryGetValue(c.ColumnName, out var r) ? r.NonNull : 0;
            var silentNulls = r?.SilentNulls ?? 0;
            var status = nonNull == 0 ? "empty" : silentNulls == 0 ? "ok" : "lossy";
            var fit = nonNull == 0 ? 100 : Math.Round((double)(nonNull - silentNulls) / nonNull * 100, 2);

            return new ColumnValidation
            {
                ColumnName = c.ColumnName,
                DataType = c.DataType,
                NonNull = nonNull,
                SilentNulls = silentNulls,
                FitPercent = fit,
                Status = status,
            };
        }).ToList();

        return report with
        {
            Validation = new InferenceValidation
            {
                IsValid = columns.All(c => c.Status != "lossy"),
                Columns = columns,
            },
        };
    }

    private async Task<(string ConnectionString, InferenceReport Report, IReadOnlyList<InferredColumn> Inferred, ServerLocale Locale)> InferCoreAsync(
        InferenceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        var connectionString = await _secrets.ResolveAsync(request.Connection, ct).ConfigureAwait(false);
        var table = await _schema.GetTableSchemaAsync(connectionString, request.Schema, request.Table, ct).ConfigureAwait(false)
            ?? throw new SqlFlowException($"Table {request.QualifiedName} does not exist - load the raw data before inferring.");

        var locale = await ResolveLocaleAsync(request.Policy, connectionString, ct).ConfigureAwait(false);

        var columns = table.Columns.Select(c => c.Name).ToList();
        var profiles = await _profiler.ProfileAsync(connectionString, request.Schema, request.Table, columns, request.Policy, locale, ct).ConfigureAwait(false);

        var inferred = profiles.Select(p => _inferencer.Infer(p, request.Policy, locale)).ToList();
        var profileByName = profiles.ToDictionary(p => p.ColumnName, StringComparer.Ordinal);

        var reported = inferred.Select(c => new InferredColumnReport
        {
            ColumnName = c.ColumnName,
            DataType = c.DataType,
            SelectExpression = c.SelectExpression,
            Converted = c.Converted,
            Style = c.Style,
            NumericFormat = c.NumericFormat?.ToString(),
            Sampled = profileByName.TryGetValue(c.ColumnName, out var p) ? p.NonNull : 0,
            Total = profileByName.TryGetValue(c.ColumnName, out var pt) ? pt.Total : 0,
        }).ToList();

        var report = new InferenceReport
        {
            Schema = request.Schema,
            Table = request.Table,
            OnConvertError = request.Policy.OnConvertError.ToString(),
            Culture = locale.Culture,
            DateOrder = locale.DateOrder.ToString(),
            Columns = reported,
            TransformSelect = TransformSelectBuilder.Build(request.Schema, request.Table, inferred),
        };

        return (connectionString, report, inferred, locale);
    }

    /// <summary>
    /// An explicit policy culture overrides the server's locale; otherwise the server's configured
    /// locale is resolved over the connection. The override path needs no database round-trip.
    /// </summary>
    private async Task<ServerLocale> ResolveLocaleAsync(TypeInferencePolicy policy, string connectionString, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(policy.Culture))
        {
            return await _locale.GetServerLocaleAsync(connectionString, ct).ConfigureAwait(false);
        }

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(policy.Culture.Trim());
        }
        catch (CultureNotFoundException ex)
        {
            throw new SqlFlowException($"Inference culture '{policy.Culture}' is not a recognised culture name.", ex);
        }

        return LocaleConversion.FromCulture(culture, LocaleConversion.DateOrderFromCulture(culture));
    }
}
