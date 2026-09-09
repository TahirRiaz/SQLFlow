# Contributing to SQLFlow

Thanks for your interest in contributing! This guide gets you productive quickly.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- (Optional, for end-to-end runs) a SQL Server instance - LocalDB, a container, or Azure SQL

## Getting started

```bash
git clone https://github.com/TahirRiaz/SQLFlow.git
cd sqlflow
dotnet build
dotnet test
```

The unit tests are pure and need no database. Integration tests that touch SQL Server are opt-in
and read a connection string from the `SQLFLOW_TEST_DW` environment variable.

## Development principles

This project follows the architecture in [docs/architecture.md](docs/architecture.md). The two rules
that matter most for contributors:

1. **Keep concerns separated.** `Core` holds the model, abstractions, and the engine - it has no
   SQL Server, YAML, or I/O dependencies. SQL Server specifics live in `SqlFlow.SqlServer`, etc.
2. **The engine is stateless.** It must run without a control database (lightweight mode). State is
   an *optional* provider, never a hard dependency.

## Coding standards

- Modern C#: nullable reference types, file-scoped namespaces, `record` for data, primary
  constructors, `async`/`CancellationToken` for I/O.
- Style is enforced by `.editorconfig` and analyzers. Run `dotnet build` - it should be warning-clean.
- Add or update unit tests for behavior changes. Generator changes should be covered by tests that
  assert on the generated SQL.

## Submitting changes

1. Fork and create a topic branch (`feat/…`, `fix/…`).
2. Make your change with tests.
3. Ensure `dotnet build` and `dotnet test` pass.
4. Open a pull request describing the *why*. Link any related issue.

## Reporting bugs / requesting features

Use the [issue templates](.github/ISSUE_TEMPLATE). For security issues, see [SECURITY.md](SECURITY.md)
- please do **not** open a public issue.
