using System.Collections.Concurrent;
using System.Data.Common;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SqlFlow.ControlPlane.Infrastructure;
using Xunit;

namespace SqlFlow.ControlPlane.Tests;

/// <summary>
/// A request whose client went away is not a failure, whatever the work under it threw: a database provider can report
/// the cancellation of its command as an error of its own (SqlClient's "Operation cancelled by user"), and ASP.NET Core's
/// exception handler takes only a cancellation for an abort. The pipeline is the control plane's, in its order: the
/// exception handler with <see cref="GlobalExceptionHandler"/>, the correlation id, then <see cref="ClientAbortMiddleware"/>.
/// </summary>
public sealed class ClientAbortMiddlewareTests
{
    [Fact]
    public async Task A_provider_error_after_the_client_went_away_is_logged_as_no_failure()
    {
        var served = await ServeAsync(context =>
        {
            context.Abort();
            throw new ProviderError();
        });

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, served.Status);
        Assert.DoesNotContain(served.Lines, IsWarningOrWorse);
        Assert.Contains(served.Lines, line => line.StartsWith($"[Debug] {typeof(ClientAbortMiddleware).FullName}: Request ", StringComparison.Ordinal)
            && line.Contains("ended after its client went away: A severe error occurred on the current command.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_provider_error_while_the_client_waits_is_still_a_failure()
    {
        var served = await ServeAsync(_ => throw new ProviderError());

        Assert.Equal(StatusCodes.Status500InternalServerError, served.Status);
        Assert.Contains(served.Lines, line => line.StartsWith($"[Error] {typeof(GlobalExceptionHandler).FullName}: Request ", StringComparison.Ordinal)
            && line.Contains("failed: A severe error occurred on the current command.", StringComparison.Ordinal));
        Assert.DoesNotContain(served.Lines, line => line.Contains(typeof(ClientAbortMiddleware).FullName!, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_cancellation_after_the_client_went_away_is_left_to_the_exception_handler()
    {
        var served = await ServeAsync(context =>
        {
            context.Abort();
            context.RequestAborted.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        Assert.Equal(StatusCodes.Status499ClientClosedRequest, served.Status);
        Assert.DoesNotContain(served.Lines, IsWarningOrWorse);
        Assert.DoesNotContain(served.Lines, line => line.Contains(typeof(ClientAbortMiddleware).FullName!, StringComparison.Ordinal));
    }

    private static bool IsWarningOrWorse(string line)
        => line.StartsWith("[Warning]", StringComparison.Ordinal) || line.StartsWith("[Error]", StringComparison.Ordinal)
            || line.StartsWith("[Critical]", StringComparison.Ordinal);

    private static async Task<(int Status, IReadOnlyList<string> Lines)> ServeAsync(RequestDelegate endpoint)
    {
        var lines = new ConcurrentQueue<string>();
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders().AddProvider(new CapturingLoggerProvider(lines)).SetMinimumLevel(LogLevel.Debug);
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

        await using var app = builder.Build();
        var status = 0;
        var ended = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            finally
            {
                status = context.Response.StatusCode;
                ended.TrySetResult();
            }
        });
        app.UseExceptionHandler();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseMiddleware<ClientAbortMiddleware>();
        app.Run(endpoint);
        await app.StartAsync();

        using var client = app.GetTestClient();
        try
        {
            using var response = await client.GetAsync(new Uri("/", UriKind.Relative));
        }
        catch (OperationCanceledException)
        {
            // The server aborted the request, as a client that went away would see it; what matters is what the server logged.
        }

        // The client sees the abort as soon as it happens, before the server's pipeline has unwound and logged.
        await ended.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await app.StopAsync();
        return (status, lines.ToArray());
    }

    /// <summary>A provider's own error for a command it cancelled, as SqlClient raises it.</summary>
    private sealed class ProviderError : DbException
    {
        public ProviderError()
            : base("A severe error occurred on the current command.  The results, if any, should be discarded.\nOperation cancelled by user.")
        {
        }
    }
}
