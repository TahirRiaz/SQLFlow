using System.Net.Http;
using System.Net.Sockets;
using Azure;

namespace SqlFlow.Azure.Storage;

/// <summary>
/// Reads an Azure Storage failure for what it says about the endpoint it was sent to, rather than about the request.
/// Two kinds of failure prove the request was never processed, so the same request may be sent to the account's other
/// endpoint without doing anything twice: the account's network rules refused this client on that endpoint (HTTP 403
/// with the error code <c>AuthorizationFailure</c>, which the service returns before it authenticates or reads the
/// request), or no connection was made at all (the host name did not resolve, or the connection or its TLS handshake
/// failed). Anything else (a 404, a 409, a permission error, a timeout after the request was sent) is the request's own
/// outcome and is never retried elsewhere.
/// </summary>
public static class AzureStorageFailures
{
    /// <summary>The error code Azure Storage returns when the account's firewall or private-access rules refuse a client.</summary>
    internal const string NetworkRejectionCode = "AuthorizationFailure";

    /// <summary>The <see cref="Exception.Data"/> key under which the storage layer records the endpoint a request went to.</summary>
    internal const string EndpointDataKey = "SqlFlow.AzureStorage.Endpoint";

    /// <summary>True when every attempt behind <paramref name="exception"/> failed before the service processed it.</summary>
    internal static bool NeverProcessed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var leaves = Leaves(exception).ToList();
        return leaves.Count > 0 && leaves.All(leaf => IsNetworkRejection(leaf) || IsConnectionFailure(leaf));
    }

    /// <summary>True when the account answered, refusing this client on this endpoint by its network rules.</summary>
    internal static bool IsNetworkRejection(Exception exception)
        => exception is RequestFailedException { Status: 403 } rfe
           && string.Equals(rfe.ErrorCode, NetworkRejectionCode, StringComparison.OrdinalIgnoreCase);

    /// <summary>True when no connection to the endpoint was made: the name did not resolve, or connecting failed.</summary>
    internal static bool IsConnectionFailure(Exception exception)
    {
        // Azure.Core wraps a transport failure as a RequestFailedException with status 0 around the HttpRequestException.
        if (exception is RequestFailedException { Status: not 0 })
        {
            return false;
        }

        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError }:
                    return true;
                case SocketException socket when socket.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData
                    or SocketError.TryAgain or SocketError.ConnectionRefused or SocketError.NetworkUnreachable
                    or SocketError.HostUnreachable or SocketError.NetworkDown:
                    return true;
            }
        }

        return false;
    }

    /// <summary>The single failures behind an exception: an <see cref="AggregateException"/> (a retry policy's
    /// "retry failed after N tries") contributes each attempt's failure, anything else is itself.</summary>
    internal static IEnumerable<Exception> Leaves(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
            {
                foreach (var leaf in Leaves(inner))
                {
                    yield return leaf;
                }
            }

            yield break;
        }

        yield return exception;
    }

    /// <summary>The endpoint a failed request went to, as the storage layer recorded it, or <c>null</c>.</summary>
    public static AzureStorageEndpoint? EndpointOf(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Data.Contains(EndpointDataKey) && current.Data[EndpointDataKey] is AzureStorageEndpoint endpoint)
            {
                return endpoint;
            }
        }

        return null;
    }

    /// <summary>The lower-case name of an endpoint, as its host spells it.</summary>
    public static string HostLabel(this AzureStorageEndpoint endpoint)
        => endpoint == AzureStorageEndpoint.Dfs ? "dfs" : "blob";

    /// <summary>
    /// A short account of a probe or request failure for a message: the network rejection by name, a connection failure
    /// by its socket-level cause, anything else by its status and code. Never the request URI, which can carry a signature.
    /// </summary>
    internal static string Describe(Exception exception)
    {
        var leaf = Leaves(exception).Last();
        if (IsNetworkRejection(leaf))
        {
            return "the account's network rules refused this host (403 AuthorizationFailure)";
        }

        if (IsConnectionFailure(leaf))
        {
            for (var current = leaf; current is not null; current = current.InnerException)
            {
                if (current is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
                {
                    return "the host name did not resolve";
                }

                if (current is SocketException socket)
                {
                    return $"no connection could be made ({socket.SocketErrorCode})";
                }
            }

            return "no connection could be made";
        }

        return leaf switch
        {
            RequestFailedException { Status: 0 } rfe => $"the request failed in transit ({rfe.InnerException?.GetType().Name ?? "no response"})",
            RequestFailedException rfe => $"status {rfe.Status}{(string.IsNullOrEmpty(rfe.ErrorCode) ? string.Empty : $" {rfe.ErrorCode}")}",
            OperationCanceledException => "no answer before the timeout",
            _ => leaf.GetType().Name,
        };
    }
}
