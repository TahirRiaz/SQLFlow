using System.Diagnostics;
using System.Text.Json;
using SqlFlow.Acquire.Landing;
using SqlFlow.Acquire.Runtime;
using SqlFlow.Acquire.Runtime.Protection;
using SqlFlow.Core;
using SqlFlow.Core.Acquire;
using SqlFlow.Core.Runs;
using SqlFlow.Core.Secrets;

namespace SqlFlow.Acquire.Engine;

/// <summary>
/// Orchestrates one acquisition run: it builds the reliability primitives, resolves authentication (once per run, or
/// per iteration when the issuer scopes tokens that way), expands the fan-out iterations into concrete request
/// contexts (date windows, static lists, and id lists discovered from a prior request), runs the selected transport
/// for each context, and assembles the run result and the advanced watermark. Transports do the fetching and land
/// the raw payloads; the engine owns everything around them.
/// </summary>
public sealed class AcquireEngine
{
    private readonly IRawLandingStore _landing;
    private readonly AuthResolver _auth;
    private readonly ISecretResolver _secrets;
    private readonly IReadOnlyList<IAcquireTransport> _transports;
    private readonly TimeProvider _time;
    private readonly Func<AcquireReliability, HttpClient> _httpClientFactory;

    /// <summary>Reads a target-sourced watermark. Optional: only a flow declaring <c>incremental.source: sql</c>
    /// needs one, and such a flow fails with a clear message when the host has not registered it.</summary>
    private readonly IAcquireWatermarkProbe? _watermarkProbe;

    public AcquireEngine(
        IRawLandingStore landing,
        AuthResolver auth,
        ISecretResolver secrets,
        IEnumerable<IAcquireTransport> transports,
        TimeProvider time,
        Func<AcquireReliability, HttpClient>? httpClientFactory = null,
        IAcquireWatermarkProbe? watermarkProbe = null)
    {
        _watermarkProbe = watermarkProbe;
        ArgumentNullException.ThrowIfNull(landing);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(transports);
        ArgumentNullException.ThrowIfNull(time);
        _landing = landing;
        _auth = auth;
        _secrets = secrets;
        _transports = transports.ToList();
        _time = time;
        _httpClientFactory = httpClientFactory ?? HttpClientBuilder.Build;
    }

    public async Task<AcquireRunResult> RunAsync(
        AcquireFlow flow, Guid runId, IRunEventSink log, string? priorWatermark,
        CancellationToken ct, AcquireRunOverrides? overrides = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(log);
        var run = overrides ?? AcquireRunOverrides.None;

        var stopwatch = Stopwatch.StartNew();
        var now = _time.GetUtcNow();
        var watermark = new WatermarkState(flow.Incremental, priorWatermark);

        // Everything from parameter binding onward runs inside the failure envelope: a configuration error
        // (missing param, bad transport, unresolvable secret, auth failure) surfaces as a FAILED RUN with the
        // cause in the result, exactly like a mid-run fetch failure, so a scheduled run records its failure
        // rather than throwing out of the runner.
        var pages = 0;
        var iterations = 0;
        // Fan-out combinations abandoned on a tolerated non-2xx (reliability.skipStatusCodes). Counted separately
        // from landing skips: nothing was fetched at all, so a run that skipped every combination must not read
        // like a run that fetched everything and found it empty.
        var skippedRequests = 0;
        var success = true;
        string? error = null;
        string? resolvedBase = null;
        // One landing pipeline per item; a multi-endpoint flow accumulates its counters across all of them into one result.
        var pipelines = new List<LandingPipeline>(flow.Items.Count);
        HttpClient? client = null;
        try
        {
            // The connection envelope (transport, auth, reliability) is shared across every item, so the HTTP client and
            // authentication are resolved once per run, not once per item; only the request/fan-out/landing vary per item.
            var envelope = flow.Source;
            var transport = SelectTransport(envelope.Transport);

            var baseVars = new TemplateContext(now).WithDate("now", now);
            BindParams(flow, run.Params, baseVars, log);

            // A lake-sourced watermark resumes from the DATA rather than from a run log: the raw zone is listed and
            // the highest value encoded in the landed file names becomes this run's starting point. It replaces the
            // caller's run-history value outright (the lake is the record, not a cache of it), except on an explicit
            // reprocess, where the caller has already decided the flow re-fetches from its declared bounds.
            if (flow.Incremental is { Source: AcquireWatermarkSource.Lake } && !run.ReprocessFiles)
            {
                watermark = new WatermarkState(flow.Incremental, await LakeWatermarkAsync(flow, baseVars, log, ct).ConfigureAwait(false));
            }

            // A target-sourced watermark resumes from what was LOADED, for a feed whose landed file names cannot
            // encode the resume value and whose run-record value would not survive a redeploy. Same precedence as
            // the lake: the probed value replaces the caller's, except on an explicit reprocess.
            IReadOnlyDictionary<string, string?>? watermarkByEntity = null;
            if (flow.Incremental is { Source: AcquireWatermarkSource.Sql } sqlIncremental && !run.ReprocessFiles)
            {
                var probed = await SqlWatermarkAsync(sqlIncremental, log, ct).ConfigureAwait(false);
                watermarkByEntity = probed.ByKey;
                if (probed.ByKey is null)
                {
                    watermark = new WatermarkState(flow.Incremental, probed.Single);
                }
            }

            if (flow.Incremental is { BindVariable: { } bindVar } && watermark.Before is { } before)
            {
                baseVars.WithString(bindVar, before);
            }

            client = _httpClientFactory(envelope.Reliability);
            var dataHttp = HttpExecutorFor(client, envelope.Reliability, envelope.Reliability.UrlAllowlist);
            // Token and OIDC-discovery calls may target a host outside the data allowlist; they are trusted
            // config, so the auth executor keeps the SSRF IP guard but not the host allowlist.
            var authHttp = HttpExecutorFor(client, envelope.Reliability, []);

            var skipStatuses = envelope.Reliability.SkipStatusCodes;
            var refreshPerIteration = envelope.Auth.Token?.RefreshPerIteration == true;
            var discoveryAuth = await _auth.ResolveAsync(envelope.Auth, authHttp, baseVars, ct).ConfigureAwait(false);

            foreach (var item in flow.Items)
            {
                ct.ThrowIfCancellationRequested();
                var itemBase = await _secrets.ResolveAsync(TemplateEngine.Render(item.Landing.Target, baseVars), ct).ConfigureAwait(false);
                resolvedBase ??= itemBase;

                // Data-protection rules resolve their key material once per run, then scrub every payload of this
                // item inside the landing sink, before any byte is written.
                PayloadProtector? protector = null;
                if (item.Landing.Protect.Count > 0)
                {
                    var protectSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var rule in item.Landing.Protect)
                    {
                        if (rule.Secret is { } secretRef && !protectSecrets.ContainsKey(secretRef))
                        {
                            protectSecrets[secretRef] = await _secrets.ResolveAsync(secretRef, ct).ConfigureAwait(false);
                        }
                    }

                    protector = new PayloadProtector(item.Landing.Protect, protectSecrets, flow.Name);
                }

                var pipeline = new LandingPipeline(item.Landing, _landing, itemBase, runId, log, run.DryRun, run.ReprocessFiles, protector);
                pipelines.Add(pipeline);

                // Credentials inside a request BODY are the norm for SOAP (and for any service whose sign-in is a
                // field rather than a header), so a body's secret references resolve here, once per item, instead of
                // shipping to the service verbatim. Resolved before the discovery call, which needs them too.
                var source = await ResolveRequestSecretsAsync(item.Source, ct).ConfigureAwait(false);

                var contexts = await ExpandAsync(source, baseVars, source.Iterations, 0, discoveryAuth, dataHttp, now, watermark, run, ct).ConfigureAwait(false);

                // Per-entity resume: give each fan-out combination the watermark of the entity it is fetching. An
                // entity the target has never seen gets the seed, so a newly-appearing one is loaded from its start
                // rather than skipped or silently pinned to another entity's position.
                if (watermarkByEntity is not null
                    && flow.Incremental is { KeyVariable: { } keyVar, BindVariable: { } entityBindVar } inc)
                {
                    foreach (var context in contexts)
                    {
                        // An item that does not fan out over the keyed entity (a register or lookup endpoint
                        // alongside the per-entity ones) simply has no watermark to receive. That a flow binds the
                        // key SOMEWHERE is checked up front, so skipping here cannot hide a misconfiguration.
                        if (!context.TryGetString(keyVar, out var entity))
                        {
                            continue;
                        }

                        var resume = watermarkByEntity.TryGetValue(entity, out var value) ? value : null;
                        if ((resume ?? inc.Seed) is { } effective)
                        {
                            context.WithString(entityBindVar, effective);
                        }
                    }
                }

                // One request pipeline per fan-out combination. The combinations are independent (each lands its own
                // file through the shared, thread-safe rate limiter, landing sink, and watermark), so the item runs
                // them with bounded concurrency instead of paying a full round-trip latency per file. Probing (the
                // debugger's single-step Test invoke) always stays sequential so the stepped iteration is
                // deterministic; the aggregate request rate is still bounded by the rate limiter either way.
                async Task RunContextAsync(int i, CancellationToken token)
                {
                    var vars = contexts[i];
                    var iterationAuth = refreshPerIteration
                        ? await _auth.ResolveAsync(envelope.Auth, authHttp, vars, token).ConfigureAwait(false)
                        : discoveryAuth;
                    var fetch = new AcquireFetch
                    {
                        Source = source,
                        Vars = vars,
                        Auth = iterationAuth,
                        Landing = pipeline,
                        Watermark = watermark,
                        Log = log,
                        Secrets = _secrets,
                        Http = transport is HttpTransport ? dataHttp : null,
                        Iteration = i,
                        Probe = run.Probe,
                        MaxPagesOverride = run.MaxPagesOverride,
                    };

                    try
                    {
                        await transport.FetchAsync(fetch, token).ConfigureAwait(false);
                    }
                    catch (HttpStatusException ex) when (skipStatuses.Contains(ex.StatusCode))
                    {
                        // A tolerated per-request rejection: this one combination of the fan-out is not servable
                        // (a decommissioned id, a window the endpoint refuses), and the sweep's remaining
                        // combinations are unaffected. Logged with the endpoint's own message so a newly-broken id
                        // is visible in the run log rather than silently absent, and counted as a skip so the run
                        // summary never reads as if the combination had landed.
                        Interlocked.Increment(ref skippedRequests);
                        log.Log(RunLogLevel.Info, "acquire.skip", ex.Message);
                        return;
                    }

                    Interlocked.Add(ref pages, fetch.Pages);
                    Interlocked.Increment(ref iterations);
                }

                var concurrency = run.Probe is null ? Math.Max(1, envelope.Reliability.Concurrency) : 1;
                if (concurrency <= 1 || contexts.Count <= 1)
                {
                    for (var i = 0; i < contexts.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        await RunContextAsync(i, ct).ConfigureAwait(false);
                    }
                }
                else
                {
                    await Parallel.ForEachAsync(
                        Enumerable.Range(0, contexts.Count),
                        new ParallelOptions { MaxDegreeOfParallelism = concurrency, CancellationToken = ct },
                        async (i, token) => await RunContextAsync(i, token).ConfigureAwait(false)).ConfigureAwait(false);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failure surfaces as a failed (or partial) run: files landed before the failure are kept, the
            // counters reflect what completed, and the watermark is reported but not persisted (the history reader
            // only advances from a successful run), so the next run re-fetches the un-landed remainder. A concurrent
            // fan-out surfaces its first fault wrapped in an AggregateException; unwrap it so the recorded cause is
            // the underlying fetch error, not the generic "one or more errors occurred".
            var cause = ex is AggregateException agg && agg.InnerExceptions.Count > 0 ? agg.InnerExceptions[0] : ex;
            success = false;
            error = SecretHygiene.RedactedMessage(cause);
            log.Log(RunLogLevel.Info, "acquire.error", cause.Message);
        }
        finally
        {
            client?.Dispose();
        }

        stopwatch.Stop();
        return new AcquireRunResult
        {
            RunId = runId,
            Success = success,
            Error = error,
            DurationSeconds = Math.Round(stopwatch.Elapsed.TotalSeconds, 3),
            Iterations = iterations,
            PagesFetched = pages,
            FilesWritten = pipelines.Sum(p => p.FilesWritten),
            Unchanged = pipelines.Sum(p => p.Unchanged),
            Skipped = pipelines.Sum(p => p.Skipped),
            SkippedRequests = skippedRequests,
            BytesWritten = pipelines.Sum(p => p.BytesWritten),
            LandedBase = resolvedBase,
            WatermarkBefore = watermark.Before,
            WatermarkAfter = success ? watermark.Current : watermark.Before,
            Files = pipelines.SelectMany(p => p.Files).ToList(),
        };
    }

    /// <summary>
    /// Resolves the resume point by reading what is already landed. Incremental is single-item by construction (the
    /// run watermark is one value per run), so the flow's one landing target is listed and its names are matched
    /// against the same pathTemplate that produced them. Returns null when nothing matches, which leaves the seed in
    /// force: an empty raw zone is a first run, not a failure. A LISTING failure, by contrast, is NOT swallowed -
    /// treating an unreachable lake as "nothing landed" would silently re-walk the entire history.
    /// </summary>
    private async Task<string?> LakeWatermarkAsync(AcquireFlow flow, TemplateContext vars, IRunEventSink log, CancellationToken ct)
    {
        var item = flow.Items[0];
        var compiled = LakeWatermarkReader.Compile(item.Landing.PathTemplate, flow.Incremental?.Column);
        var landingBase = await _secrets.ResolveAsync(TemplateEngine.Render(item.Landing.Target, vars), ct).ConfigureAwait(false);

        var names = await _landing.ListNamesAsync(landingBase, ct).ConfigureAwait(false);
        var resolved = LakeWatermarkReader.Read(compiled, names);

        log.Log(RunLogLevel.Info, "watermark.lake", resolved is null
            ? $"no landed file under '{landingBase}' matches '{item.Landing.PathTemplate}' ({names.Count} name(s) scanned); starting from the seed."
            : $"resuming from '{resolved}', the highest '{compiled.Selected}' across {names.Count} landed name(s) under '{landingBase}'.");
        return resolved;
    }

    /// <summary>
    /// Resolves the resume point by running the flow's own scalar query against the flow's own connection. Nothing
    /// about the queried object is known here: the statement is passed through verbatim, so this works for any
    /// target shape without the engine learning a single table or column name.
    /// </summary>
    private async Task<SqlWatermarks> SqlWatermarkAsync(AcquireIncremental incremental, IRunEventSink log, CancellationToken ct)
    {
        var connection = incremental.Connection
                         ?? throw new SqlFlowException("A sql watermark requires 'incremental.connection'.");
        var query = incremental.Query
                    ?? throw new SqlFlowException("A sql watermark requires 'incremental.query'.");
        if (_watermarkProbe is null)
        {
            throw new SqlFlowException(
                "A sql watermark needs a database probe, and none is registered in this host. Register an " +
                $"{nameof(IAcquireWatermarkProbe)} (the engine host wiring does) or use a lake/response watermark.");
        }

        var rows = await _watermarkProbe.ReadAsync(
            await _secrets.ResolveAsync(connection, ct).ConfigureAwait(false), query, ct).ConfigureAwait(false);

        if (incremental.KeyVariable is not { } keyVariable)
        {
            var single = rows.Count > 0 ? rows[0].Value : null;
            log.Log(RunLogLevel.Info, "watermark.sql", single is null
                ? "the watermark query returned no value; starting from the seed."
                : $"resuming from '{single}', read from the loaded target.");
            return new SqlWatermarks(single, null);
        }

        // Per-entity: each fan-out combination resumes from its OWN high-water mark, so one lagging entity does not
        // drag the whole sweep back to the oldest position (which is what a single flow-wide watermark would do).
        var byKey = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            if (row.Key is { Length: > 0 } key)
            {
                byKey[key] = row.Value;
            }
        }

        log.Log(RunLogLevel.Info, "watermark.sql",
            $"read {byKey.Count} per-entity watermark(s) keyed by '{keyVariable}' from the loaded target; an entity with no row starts from the seed.");
        return new SqlWatermarks(null, byKey);
    }

    /// <summary>The resolved probe result: one value for the whole flow, or one per entity keyed by a fan-out
    /// variable. Exactly one of the two is populated.</summary>
    private readonly record struct SqlWatermarks(string? Single, IReadOnlyDictionary<string, string?>? ByKey);

    private IAcquireTransport SelectTransport(AcquireTransport transport)
        => _transports.FirstOrDefault(t => t.CanHandle(transport))
           ?? throw new SqlFlowException($"No transport is registered for '{transport}'.");

    /// <summary>
    /// Binds the flow's declared parameters into the run's template context: the declared default first, overlaid
    /// by any runtime value the caller supplied. A runtime value for an undeclared name is rejected (a typo'd
    /// override must fail loudly, not silently fetch the default window), and a parameter declared without a
    /// default that receives no runtime value is a hard error before the first request is sent.
    /// </summary>
    private static void BindParams(
        AcquireFlow flow, IReadOnlyDictionary<string, string>? runtimeParams, TemplateContext vars, IRunEventSink log)
    {
        if (runtimeParams is { Count: > 0 })
        {
            var undeclared = runtimeParams.Keys
                .Where(name => !flow.Params.ContainsKey(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (undeclared.Count > 0)
            {
                throw new SqlFlowException(
                    $"Runtime parameter(s) {string.Join(", ", undeclared.Select(p => $"'{p}'"))} are not declared by " +
                    $"flow '{flow.Name}'. Declare them under 'params:' (name: default) so overrides are typo-safe. " +
                    $"Declared: {(flow.Params.Count == 0 ? "(none)" : string.Join(", ", flow.Params.Keys.OrderBy(k => k, StringComparer.Ordinal)))}.");
            }
        }

        var missing = new List<string>();
        foreach (var (name, fallback) in flow.Params)
        {
            var supplied = runtimeParams is not null && runtimeParams.TryGetValue(name, out var value) ? value : fallback;
            if (supplied is null)
            {
                missing.Add(name);
                continue;
            }

            vars.WithString(name, supplied);
            if (runtimeParams?.ContainsKey(name) == true)
            {
                log.Log(RunLogLevel.Info, "params", $"parameter '{name}' overridden for this run.");
            }
        }

        if (missing.Count > 0)
        {
            throw new SqlFlowException(
                $"Parameter(s) {string.Join(", ", missing.OrderBy(m => m, StringComparer.Ordinal).Select(m => $"'{m}'"))} " +
                $"declare no default and were not supplied for this run of flow '{flow.Name}'.");
        }
    }

    private HttpExecutor HttpExecutorFor(HttpClient client, AcquireReliability reliability, IReadOnlyList<string> allowlist)
        => new(client, new RetryPolicy(reliability.Retry, _time), new RateLimiter(reliability.RateLimitRps, _time), new UrlGuard(allowlist), reliability.MaxResponseBytes, _time);

    private static async Task<List<TemplateContext>> ExpandAsync(
        AcquireSource source,
        TemplateContext ctx,
        IReadOnlyList<AcquireIteration> iterations,
        int index,
        AppliedAuth discoveryAuth,
        HttpExecutor dataHttp,
        DateTimeOffset now,
        WatermarkState watermark,
        AcquireRunOverrides run,
        CancellationToken ct)
    {
        if (index >= iterations.Count)
        {
            return [ctx];
        }

        var iteration = iterations[index];
        var binders = await BindersAsync(source, iteration, ctx, discoveryAuth, dataHttp, now, watermark, run, ct).ConfigureAwait(false);

        var results = new List<TemplateContext>();
        foreach (var binder in binders)
        {
            var child = ctx.Clone();
            binder(child);
            results.AddRange(await ExpandAsync(source, child, iterations, index + 1, discoveryAuth, dataHttp, now, watermark, run, ct).ConfigureAwait(false));
        }

        return results;
    }

    private static async Task<IReadOnlyList<Action<TemplateContext>>> BindersAsync(
        AcquireSource source,
        AcquireIteration iteration,
        TemplateContext ctx,
        AppliedAuth discoveryAuth,
        HttpExecutor dataHttp,
        DateTimeOffset now,
        WatermarkState watermark,
        AcquireRunOverrides run,
        CancellationToken ct)
    {
        switch (iteration.Kind)
        {
            case AcquireIterationKind.DateWindow:
                return DateWindowBinders(iteration, now, watermark, run);

            case AcquireIterationKind.List:
            {
                var variable = iteration.Variable ?? throw new SqlFlowException("A list iteration requires 'variable'.");
                return iteration.Values.Select(value => (Action<TemplateContext>)(c => c.WithString(variable, value))).ToList();
            }

            case AcquireIterationKind.IdsFrom:
                return await IdsFromBindersAsync(source, iteration, ctx, discoveryAuth, dataHttp, ct).ConfigureAwait(false);

            default:
                throw new SqlFlowException($"Unsupported iteration kind '{iteration.Kind}'.");
        }
    }

    private static IReadOnlyList<Action<TemplateContext>> DateWindowBinders(
        AcquireIteration iteration, DateTimeOffset now, WatermarkState watermark, AcquireRunOverrides run)
    {
        // An externally-bounded backfill window (the typed per-run contract) replaces the iteration's own
        // bounds for this run; otherwise the authored from/to (or the watermark, or the 1-day default) apply.
        var from = run.WindowFrom
            ?? (iteration.From is { } f ? RelativeTime.Resolve(f, now)
                : watermark.Before is { } wm && DateTimeOffset.TryParse(wm, out var parsed) ? parsed
                : now.AddDays(-1));
        var to = run.WindowTo ?? (iteration.To is { } t ? RelativeTime.Resolve(t, now) : now);

        if (from > to)
        {
            throw new SqlFlowException($"Date window 'from' ({from:o}) is after 'to' ({to:o}).");
        }

        var binders = new List<Action<TemplateContext>>();
        var cursor = from;
        var guard = 0;
        while (cursor < to && guard++ < 100_000)
        {
            var stepEnd = Step(cursor, iteration.Granularity);
            if (stepEnd > to)
            {
                stepEnd = to;
            }

            // The bound upper edge may reach past the step boundary so that an event straddling it is fully inside
            // this window (see AcquireIteration.OverlapMinutes). The CURSOR still advances by the plain step, so the
            // windows stay aligned; and the overlap never reaches past the run's own upper bound, so it cannot pull
            // in a period the caller did not ask for.
            var boundEnd = iteration.OverlapMinutes > 0
                ? Min(stepEnd.AddMinutes(iteration.OverlapMinutes), to)
                : stepEnd;

            var stepStart = cursor;
            binders.Add(c => c
                .WithDate(iteration.FromVariable, stepStart)
                .WithDate(iteration.ToVariable, boundEnd)
                .WithReferenceDate(stepStart));
            cursor = stepEnd;
        }

        return binders;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    private static DateTimeOffset Step(DateTimeOffset from, AcquireWindowGranularity granularity) => granularity switch
    {
        AcquireWindowGranularity.Hour => from.AddHours(1),
        AcquireWindowGranularity.Day => from.AddDays(1),
        AcquireWindowGranularity.Month => from.AddMonths(1),
        _ => from.AddDays(1),
    };

    /// <summary>
    /// Resolves <c>${scheme:locator}</c> secret references inside an item's request bodies (the data request and any
    /// discovery request), returning a source that carries the resolved text. Bodies are the one request part a
    /// credential legitimately lives in: a SOAP envelope names its username and password as elements, so leaving the
    /// reference unresolved would post the literal <c>${keyvault:...}</c> to the service. URLs, headers, and query
    /// values are not touched here because auth already owns those, and the templated body is rendered per request
    /// after this, so an iteration variable still substitutes normally.
    /// </summary>
    private async Task<AcquireSource> ResolveRequestSecretsAsync(AcquireSource source, CancellationToken ct)
    {
        var request = source.Request;
        var resolvedRequest = request?.Body is { Length: > 0 } body
            ? request with { Body = await _secrets.ResolveAsync(body, ct).ConfigureAwait(false) }
            : request;

        var iterations = new List<AcquireIteration>(source.Iterations.Count);
        var iterationChanged = false;
        foreach (var iteration in source.Iterations)
        {
            if (iteration.IdRequest?.Body is { Length: > 0 } idBody)
            {
                var resolved = await _secrets.ResolveAsync(idBody, ct).ConfigureAwait(false);
                iterations.Add(iteration with { IdRequest = iteration.IdRequest with { Body = resolved } });
                iterationChanged = true;
            }
            else
            {
                iterations.Add(iteration);
            }
        }

        if (ReferenceEquals(resolvedRequest, request) && !iterationChanged)
        {
            return source;
        }

        return source with { Request = resolvedRequest, Iterations = iterations };
    }

    private static async Task<IReadOnlyList<Action<TemplateContext>>> IdsFromBindersAsync(
        AcquireSource source,
        AcquireIteration iteration,
        TemplateContext ctx,
        AppliedAuth discoveryAuth,
        HttpExecutor dataHttp,
        CancellationToken ct)
    {
        var idRequest = iteration.IdRequest ?? throw new SqlFlowException("An idsFrom iteration requires an 'idRequest'.");
        var idPath = iteration.IdPath ?? throw new SqlFlowException("An idsFrom iteration requires an 'idPath'.");

        var result = await dataHttp.SendAsync(() => HttpRequestBuilder.Build(source.BaseUrl, idRequest, ctx, discoveryAuth, new Dictionary<string, string>(StringComparer.Ordinal)), allowStatuses: null, idRequest.ResponseCharset, ct).ConfigureAwait(false);

        // The discovery response decides how it is read: an XML service (SOAP and friends) gets XPath, everything
        // else JSON. Both feed the same binder list, so the fan-out downstream is identical either way.
        var xml = XmlPathReader.TryParse(result.Body, result.ContentType);
        return iteration.IdBindings.Count > 0
            ? RecordBinders(iteration, idPath, result, xml)
            : ScalarBinders(iteration, idPath, result, xml);
    }

    /// <summary>The single-variable fan-out: one bound value per discovered id, optionally joined into batches.</summary>
    private static IReadOnlyList<Action<TemplateContext>> ScalarBinders(
        AcquireIteration iteration, string idPath, HttpFetchResult result, System.Xml.Linq.XElement? xml)
    {
        var variable = iteration.Variable ?? throw new SqlFlowException("An idsFrom iteration requires 'variable'.");

        IReadOnlyList<string> ids;
        if (xml is not null)
        {
            ids = XmlPathReader.SelectValues(xml, idPath);
        }
        else
        {
            using var document = JsonDocument.Parse(result.Body);
            ids = JsonPathReader.SelectValues(document.RootElement, idPath);
        }

        var batchSize = Math.Max(1, iteration.BatchSize);
        var binders = new List<Action<TemplateContext>>();
        for (var i = 0; i < ids.Count; i += batchSize)
        {
            var batch = string.Join(iteration.BatchSeparator, ids.Skip(i).Take(batchSize));
            binders.Add(c => c.WithString(variable, batch));
        }

        return binders;
    }

    /// <summary>
    /// The multi-variable fan-out: <c>idPath</c> selects one element per record and each binding reads a value from
    /// inside it, so a follow-up request can carry more than the id (a per-entity token, a name, a date range).
    /// A record missing a bound path binds the empty string rather than dropping the record, because a request
    /// template that renders an empty token fails loudly at the service instead of silently shrinking the sweep.
    /// </summary>
    private static IReadOnlyList<Action<TemplateContext>> RecordBinders(
        AcquireIteration iteration, string idPath, HttpFetchResult result, System.Xml.Linq.XElement? xml)
    {
        if (iteration.BatchSize > 1)
        {
            throw new SqlFlowException(
                "An idsFrom iteration cannot combine 'idBindings' with a batchSize above 1: a batch of joined ids has no single record to read the other variables from.");
        }

        var binders = new List<Action<TemplateContext>>();
        if (xml is not null)
        {
            foreach (var record in XmlPathReader.SelectNodes(xml, idPath))
            {
                var values = iteration.IdBindings.ToDictionary(
                    binding => binding.Key,
                    binding => XmlPathReader.SelectValue(record, binding.Value) ?? string.Empty,
                    StringComparer.Ordinal);
                binders.Add(c =>
                {
                    foreach (var (name, value) in values)
                    {
                        c.WithString(name, value);
                    }
                });
            }

            return binders;
        }

        using var document = JsonDocument.Parse(result.Body);
        foreach (var record in JsonPathReader.SelectElements(document.RootElement, idPath))
        {
            var values = iteration.IdBindings.ToDictionary(
                binding => binding.Key,
                binding => JsonPathReader.SelectValue(record, binding.Value) ?? string.Empty,
                StringComparer.Ordinal);
            binders.Add(c =>
            {
                foreach (var (name, value) in values)
                {
                    c.WithString(name, value);
                }
            });
        }

        return binders;
    }
}
