## Summary

<!-- What does this change and why? -->

## Related issue

<!-- e.g. Closes #123 -->

## Checklist

- [ ] `dotnet build` is warning-clean
- [ ] `dotnet test` passes
- [ ] Added/updated tests for behavior changes
- [ ] Concern separation respected (no SQL Server / YAML / I/O deps leaking into `SqlFlow.Core`)
- [ ] No secrets or real connection strings committed
