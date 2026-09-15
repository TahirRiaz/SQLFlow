using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Options;

namespace SqlFlow.ControlPlane.Hosting;

/// <summary>
/// The limits the control plane owns and modules may raise. One instance per host, registered as a singleton; the host
/// applies it to the server when the server's options are first read, after every module has been configured.
/// </summary>
public sealed class ControlPlaneLimits
{
    /// <summary>The request body limit when no module raises it: Kestrel's own default, so a host without modules behaves as before.</summary>
    public const long DefaultMaxRequestBodySize = 30_000_000;

    /// <summary>The largest request body limit a module may ask for (4 GiB).</summary>
    public const long MaxRequestBodySizeCeiling = 4L * 1024 * 1024 * 1024;

    private readonly Lock _gate = new();
    private long _maxRequestBodySize = DefaultMaxRequestBodySize;
    private string? _maxRequestBodySizeRaisedBy;

    internal ControlPlaneLimits()
    {
    }

    /// <summary>The largest request body, in bytes, the control plane accepts.</summary>
    public long MaxRequestBodySize
    {
        get
        {
            lock (_gate)
            {
                return _maxRequestBodySize;
            }
        }
    }

    /// <summary>The module whose request set <see cref="MaxRequestBodySize"/>, or null when it is the default.</summary>
    public string? MaxRequestBodySizeRaisedBy
    {
        get
        {
            lock (_gate)
            {
                return _maxRequestBodySizeRaisedBy;
            }
        }
    }

    internal void RaiseMaxRequestBodySize(string moduleName, long bytes)
    {
        if (bytes is < 1 or > MaxRequestBodySizeCeiling)
        {
            throw new ControlPlaneModuleException(
                moduleName,
                $"Control plane module '{moduleName}' asked for a request body limit of {bytes} bytes; it must be between 1 and {MaxRequestBodySizeCeiling} bytes.");
        }

        lock (_gate)
        {
            if (bytes > _maxRequestBodySize)
            {
                _maxRequestBodySize = bytes;
                _maxRequestBodySizeRaisedBy = moduleName;
            }
        }
    }

    /// <summary>The host's limits instance in <paramref name="services"/>, registered there first when absent.</summary>
    internal static ControlPlaneLimits GetOrAdd(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ControlPlaneLimits) && descriptor.ImplementationInstance is ControlPlaneLimits existing)
            {
                return existing;
            }
        }

        var limits = new ControlPlaneLimits();
        services.AddSingleton(limits);
        services.AddSingleton<IConfigureOptions<KestrelServerOptions>, KestrelLimits>();
        return limits;
    }

    /// <summary>Applies the limits to Kestrel when its options are first resolved, which is after every module has run.</summary>
    private sealed class KestrelLimits(ControlPlaneLimits limits) : IConfigureOptions<KestrelServerOptions>
    {
        public void Configure(KestrelServerOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            options.Limits.MaxRequestBodySize = limits.MaxRequestBodySize;
        }
    }
}
