---
id: concept-azure-storage-endpoints
title: Azure Storage over its Blob or Data Lake endpoint
type: concept
summary: Azure Storage is reached over its Blob or its Data Lake (dfs) endpoint, whichever this host can reach, with the same results either way.
keywords:
  - azure storage
  - adls gen2
  - abfss
  - blob endpoint
  - dfs endpoint
  - data lake endpoint
  - private endpoint
  - privatelink
  - authorizationfailure
  - firewall
  - fallback
  - SQLFLOW_AZURE_STORAGE_ENDPOINT
related:
  - concept-environment-variables
  - concept-connections-and-secrets
  - concept-file-discovery-and-lifecycle
  - flow-source
  - flow-cpy
  - flow-exp
  - flow-sftp
sourceRefs:
  - src/SqlFlow.Azure/Storage/AzureStorageAccess.cs
  - src/SqlFlow.Azure/Storage/AzureStorageContainer.cs
  - src/SqlFlow.Azure/Storage/AzureStorageEndpointSelector.cs
  - src/SqlFlow.Azure/Storage/AzureStorageFailures.cs
  - src/SqlFlow.Azure/Storage/AzureStorageAuth.cs
  - src/SqlFlow.Azure/Storage/BlobStorageBackend.cs
  - src/SqlFlow.Azure/Storage/DfsStorageBackend.cs
  - src/SqlFlow.Azure/Storage/DfsRestClient.cs
  - src/SqlFlow.Azure/Storage/DfsStreams.cs
  - src/SqlFlow.Azure/Storage/DfsSignature.cs
  - src/SqlFlow.Core/Storage/AzureBlobLocation.cs
---

# Azure Storage over its Blob or Data Lake endpoint

An Azure Storage account serves its files on two REST endpoints: the Blob endpoint,
`<account>.blob.core.windows.net`, and the Data Lake Storage endpoint, `<account>.dfs.core.windows.net`. Both read and
write the same files. A network can admit either, both or neither of them. A private endpoint and its `privatelink`
DNS zone are made per endpoint, so a network wired for only one of them reaches the account only there; the other
name resolves to the account's public address, which the account's firewall refuses with
`403 AuthorizationFailure`.

SQLFlow reaches every account over whichever endpoint this host can reach, whatever host a location names. A
location written `abfss://landing@lake.dfs.core.windows.net/raw` is read over the Blob endpoint when that is the one
this host reaches, and `https://lake.blob.core.windows.net/landing/raw` over the Data Lake endpoint when that is the
one. The location keeps its spelling everywhere it is shown: provenance columns, path masks, run manifests and lineage
see the URI as written.

## What it covers

Every part of SQLFlow that reads or writes Azure Storage through .NET takes its containers from one service,
`AzureStorageAccess` (src/SqlFlow.Azure/Storage/AzureStorageAccess.cs), and so shares one decision per account:

- file discovery and loads of file sources (csv, json, xml, xls, parquet);
- landing, by an acquisition flow (`api`);
- exports (`exp`) and the documents a translate flow (`trl`) saves and reads back;
- copies (`cpy`), with the ambient identity or with a declared connection string, account key or shared access
  signature;
- the lake side of an SFTP transfer (`sftp`);
- the files a module built on SQLFlow writes and reads with the same service.

A `duckdb` or `delta` source is read by DuckDB's own Azure extension, not by this service. That extension reads
files through the Azure C++ Data Lake library, whose downloads go to the Blob endpoint, so such a source needs a
network that reaches the account's Blob endpoint.

## How the endpoint is chosen

The first request of the process to an account decides the endpoint, and every container and credential of that
account shares the decision (src/SqlFlow.Azure/Storage/AzureStorageEndpointSelector.cs):

1. `SQLFLOW_AZURE_STORAGE_ENDPOINT` set to `blob` or `dfs` uses that endpoint and nothing else. Unset or `auto`
   detects. A value it does not take fails every Azure Storage request with
   `SQLFLOW_AZURE_STORAGE_ENDPOINT is '<value>'; it takes 'auto' (the default: whichever endpoint this host reaches), 'blob' or 'dfs'.`
2. Both host names are resolved. One that resolves only to private addresses (10/8, 172.16/12, 192.168/16,
   100.64/10, fc00::/7) is tried first: it is a private endpoint wired into this host's network. Otherwise the Blob
   endpoint goes first.
3. Each endpoint in turn is probed with one request to the container's properties. The probe is never retried and
   gives up after 10 seconds. Any answer from the service proves the endpoint can be used: a missing container, or a
   permission error the real request will report as itself. A network rejection (`403 AuthorizationFailure`), a host
   name that does not resolve, a connection that is refused or not made, or no answer within the timeout proves it
   cannot, and the next endpoint is probed.

A credential that cannot sign in is not an endpoint's fault: the probe surfaces it as the authentication failure it
is. When neither endpoint answers, the request fails naming both, for example:

```text
Azure Storage account 'lake' cannot be reached from this host over either of its endpoints:
blob (lake.blob.core.windows.net, resolves to public 20.209.193.139): the account's network rules refused this host (403 AuthorizationFailure);
dfs (lake.dfs.core.windows.net, resolves to public 20.209.193.140): the account's network rules refused this host (403 AuthorizationFailure).
It is reached once this host's network admits one of them: a private endpoint for the account's 'blob' or 'dfs'
sub-resource with its privatelink DNS zone linked to the network, or a storage firewall rule that admits this host.
```

A failed detection is not remembered, so the next request detects again once the network is fixed. A successful one
is logged once, with the endpoint, its host and what it resolved to, and, when the other endpoint was refused, why.

## When the network changes while the host runs

A request the network refuses after the decision (public access switched off, a DNS zone removed) makes the account's
endpoint be decided again. When the decision moves to the other endpoint, the same request is sent there once, and
the move is logged. A request is only ever sent twice when its failure proves the service never processed it: a
`403 AuthorizationFailure`, a host name that does not resolve, or no connection made. Any other failure (a timeout
after the request was sent, a 500) is the request's own outcome and surfaces as it is. A listing starts over on the
other endpoint only when nothing has been returned yet, and an upload is sent again only from content that can be
read again from its start. With `SQLFLOW_AZURE_STORAGE_ENDPOINT` fixing the endpoint, nothing moves.

A failed request's error names the endpoint it went to:
`Azure Storage request failed for 'lake/landing' over the dfs endpoint (status 404): ...`.

## Authentication on each endpoint

| Credential | Blob endpoint | Data Lake endpoint |
|---|---|---|
| The ambient identity (`SQLFLOW_AZURE_AUTH`) | Bearer token | The same bearer token |
| A shared access signature | Appended to each request | Appended to each request, once per request |
| An account key | Each request signed with the key | An account signature for the Blob service (every permission on the service, its containers and files), signed locally with the key, valid for an hour and renewed ten minutes before it expires; the key never travels |
| A connection string | As the connection string says | The Data Lake twin of its Blob endpoint, with its shared access signature or, failing that, an account signature signed with its key |

A connection string whose Blob endpoint is not a standard Azure host (an emulator, a custom domain), or that carries
neither a key nor a signature, has no Data Lake endpoint: it is reached over its Blob endpoint only, and
`SQLFLOW_AZURE_STORAGE_ENDPOINT=dfs` fails its requests saying so.

## The same results over both endpoints

Every operation gives the same result over either endpoint, which the storage tests check by running one script over
both and comparing what it saw:

- A listing names files and directories by their full in-container path. A directory is an entry of its own: over the
  Blob endpoint it is a listing prefix, a zero-length blob whose name ends in a slash, or the zero-length blob carrying
  `hdi_isfolder=true` that ADLS Gen2 keeps for every directory, listed once; over the Data Lake endpoint it is a path
  with `isDirectory`. A directory that does not exist lists nothing; a container that does not exist fails with
  status 404.
- A path's properties tell a file from a directory, and give its length, modification time, entity tag and stored
  content hash. Nothing at the path gives no properties.
- A read returns the same bytes, whole, by range, or through a seekable stream that fetches 4 MB ranges on demand and
  fails with status 412 if the file changes while it is read. A dropped connection mid-read resumes from the byte
  reached, pinned to the file's version, up to five times.
- A write stamps the same stored MD5 (the Data Lake flush's `x-ms-content-md5`). A create-only write refuses an
  existing file with status 409 and leaves it as it was. A delete reports whether something was there. Creating a
  container that exists is not an error.

Two differences remain, both in how the Data Lake protocol works:

- A Data Lake write creates the file, appends its bytes and flushes them, the protocol the Data Lake SDK and Hadoop's
  ABFS driver use. The file exists, empty, from its create until its final flush, where a Blob upload appears whole. A
  whole-file upload that fails after its create removes the file it created, so a later create-only write is not
  refused by what a failed one left behind. A streamed write commits on flush and on dispose, as the Blob write
  stream does. Every final flush closes the file (`close=true`), the flush Azure Storage's change events report as
  a completed Data Lake write.
- A Data Lake listing carries no content hashes. Where a listing's hash matters (a copy that skips files whose
  content has not changed), the stored hash of each matched file is read from its properties, which is still no
  download. A file written over the Blob endpoint in one request without a hash gets one computed by the service; one
  written over the Data Lake endpoint without a hash stores none.

## Networks with private endpoints

Microsoft recommends a private endpoint for both the `blob` and the `dfs` sub-resource of an ADLS Gen2 account,
because tools that address the Data Lake endpoint can still send some requests to the Blob endpoint. SQLFlow does not
need both: with only one wired into the network, it uses that one. A `duckdb` or `delta` source still needs the Blob
endpoint (see [What it covers](#what-it-covers)).
