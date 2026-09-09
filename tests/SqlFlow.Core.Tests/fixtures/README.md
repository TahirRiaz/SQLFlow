# Test fixtures

Drop real Excel files here to have them parsed by `LegacyXlsFixtureTests`:

- A legacy binary **`.xls`** file (the format ClosedXML cannot author, so it must be a real file)
  enables the otherwise-skipped `.xls` coverage. Name it anything ending in `.xls`.
- Any `.xlsx` here is validated too.

The csproj copies these next to the test binary. The test reads each file with the real
`XlsSourceReader` and asserts it parses into columns and rows. By default it asserts the file simply
parses cleanly; once you tell me the expected sheet/columns/values I can tighten the assertions.
