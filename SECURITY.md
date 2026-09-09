# Security Policy

## Supported Versions

SQLFlow v3 is pre-1.0 and under active development. Security fixes are applied to the `main` branch.

## Reporting a Vulnerability

**Please do not report security vulnerabilities through public GitHub issues.**

Instead, report them privately via [GitHub Security Advisories](https://github.com/TahirRiaz/SQLFlow/security/advisories/new)
or by email to **security@sqlflow.io**.

Please include:

- A description of the issue and its impact
- Steps to reproduce
- Affected version / commit

We aim to acknowledge reports within 3 business days and to provide a remediation timeline after
triage.

## Handling of credentials

SQLFlow never stores secrets in pipeline YAML. Connection strings and secrets are supplied by
reference and resolved at runtime, on every platform .NET runs on. The canonical contract lives in
[docs/environment-variables.md](docs/environment-variables.md):

- A bare connection name in a document resolves `${env:SQLFLOW_CONN_<NAME>}` by convention, so
  enterprise documents carry no references at all, let alone values.
- Explicit references: `${env:NAME}` and `${keyvault:vault/secret}`.
- Local development values belong in the git-ignored `.sqlflow/env` file; the process environment
  (CI, Kubernetes, schedulers) always wins over it.
- `sqlflow validate` and `sqlflow run` warn loudly when a document embeds a `Password=`-style
  literal, without echoing the value. Resolved connection strings are redacted before they reach
  any log, trace, error message, or run artifact.

Please follow this convention in any contribution and never commit real connection strings or
credentials.
