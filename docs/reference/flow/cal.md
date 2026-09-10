---
id: flow-cal
title: "Calendar flow (flowType: cal): a generated date dimension"
type: flow-reference
summary: "flowType: cal generates a date dimension for a declared range from rules alone (no source), then merges it so the target holds exactly that range."
keywords:
  - calendar
  - cal
  - date dimension
  - dim_calendar
  - holidays
  - observances
  - fiscal year
  - easter
  - equinox
  - daylight saving
related:
  - flow-overview
  - flow-schedule
  - flow-ing
  - concept-connections-and-secrets
sourceRefs:
  - src/SqlFlow.Core/Calendar/CalendarFlow.cs
  - src/SqlFlow.Core/Calendar/CalendarRow.cs
  - src/SqlFlow.Core/Calendar/CalendarDimensionBuilder.cs
  - src/SqlFlow.Core/Calendar/CountryCalendar.cs
  - src/SqlFlow.Core/Calendar/NorwegianCalendar.cs
  - src/SqlFlow.Core/Calendar/SeasonEvents.cs
  - src/SqlFlow.Core/Calendar/Observance.cs
  - src/SqlFlow.SqlServer/Calendar/CalendarFlowRunner.cs
  - src/SqlFlow.SqlServer/Calendar/CalendarTable.cs
  - src/SqlFlow.Yaml/YamlCalendarFlowLoader.cs
---

# Calendar flow (flowType: cal)

A `cal` flow GENERATES a date dimension for a declared range and merges it into a table. It is the only document kind with **no data source**: every column is computed from the range, the country and the fiscal-year start, so the result depends on nothing outside the engine. No API, no scraped web page, no reference file, no network call of any kind.

## Minimal example

```yaml
flowType: cal
name: dwh_dim_calendar_00_cal
batch: calendar

connections:
  target: "@dwdwhprod"

calendar:
  server: target
  object: dw-dwh-prod.edw.Dim_Calendar
  from: 2013-01-01
  to: 2035-12-31
  country: NO

schedule:
  name: calendar_manual
  enabled: false
```

## The `calendar:` block

| Key | Required | Default | Meaning |
| --- | --- | --- | --- |
| `server` | yes (or `connection`) | | The connection alias the dimension is written to. Must be SQL Server. |
| `connection` | | | A direct connection literal instead of a named `server`. |
| `provider` | | `mssql` | Provider of a direct `connection`. Anything but mssql/azdb is rejected at parse time. |
| `object` | yes | | Three-part target name, `Database.Schema.Table`. |
| `from` | yes | | First date, inclusive, as `yyyy-MM-dd`. |
| `to` | yes | | Last date, inclusive, as `yyyy-MM-dd`. |
| `country` | yes | | ISO 3166-1 alpha-2. Selects the observance rules and the season names. Supported: `NO`. |
| `culture` | | from `country` | Culture the day and month names come from (`NO` gives `nb-NO`). |
| `timezone` | | from `country` | IANA zone the daylight-saving flag and the astronomical observances resolve in (`NO` gives `Europe/Oslo`). |
| `fiscalYearStartMonth` | | `1` | Month the fiscal year opens, 1 through 12. |
| `observances` | | `full` | `full`, `publicHolidays`, or `none`. |
| `rebuild` | | `false` | Drop and recreate the target instead of merging into it. |

## The range is explicit, never rolling

`from` and `to` are literal dates. The flow never extends its own horizon. A date dimension should hold exactly the span its facts can reference, so widening it is a deliberate edit to the flow file, not a silent side effect of a schedule firing. The engine refuses a range wider than 200 years (`CalendarDimensionBuilder.MaxYears`).

**This makes a calendar flow a manual one.** With a fixed range there is nothing a recurring fire could discover: it would re-assert rows it already wrote and report `0 inserted, 0 updated` forever. The dimension changes when a person decides to generate or extend it, and the edit to `to:` is the trigger. Declare the estate's manual idiom, a named schedule with `enabled: false` and no cron, so the flow validates and can be triggered by hand but nothing fires it:

```yaml
schedule:
  name: calendar_manual
  enabled: false
```

A cron is still accepted if an estate wants a periodic self-repair, and behaves as an **assertion** rather than an extension: it regenerates the declared range and merges, so the table is repaired if anything drifted and left alone otherwise.

- Generation is deterministic, so a re-run over an unchanged range reports `0 inserted, 0 updated`.
- Widening the range in the file inserts only the new days on the next run.
- Narrowing it deletes the days that fell outside (`WHEN NOT MATCHED BY SOURCE THEN DELETE`), so the table is never left holding dates the flow no longer declares.
- Rows are keyed by `PeriodID`, which never moves, so anything downstream holding that key keeps resolving.

Change detection is an `INTERSECT` of the two row images, which treats NULL as equal to NULL. A column-by-column inequality would rewrite every row whose `HolidayName` is NULL on both sides, on every run.

## Generated columns

One column per generated field, in this order, followed by `InsertedDate_DW` and `UpdatedDate_DW`:

`PeriodID`, `Date`, `DayOfMonth`, `DayOfWeekName`, `DayOfWeekNameShort`, `DayOfWeekNumber`, `WeekOfYear`, `MonthNumber`, `MonthName`, `MonthNameShort`, `MonthNumName`, `Quarter`, `Year`, `IsWeekend`, `IsLeapYear`, `IsLastDayOfMonth`, `FiscalWeekOfYear`, `FiscalMonth`, `FiscalQuarter`, `FiscalYear`, `IsHoliday`, `HolidayName`, `Season`, `DaylightSavingTime`, `ISOWeekNumber`.

`PeriodID` is `yyyyMMdd` as an int and is the clustered primary key. `DayOfWeekNumber` is the .NET `DayOfWeek` ordinal, so Sunday is 0. `WeekOfYear` follows the culture's own calendar week rule; `ISOWeekNumber` follows ISO-8601 (weeks start Monday, week 1 contains the first Thursday), so the two differ in early January.

**`IsHoliday` is `NULL` on an ordinary day, never `0`.** Reporting views branch on `IS NULL` (`WHEN [IsHoliday] IS NULL THEN ''`), so writing 0 would silently change what they return. The engine never writes false into that column.

The runner creates the target (and its schema) when it does not exist, with the shape above. An existing table is merged into and left otherwise untouched unless `rebuild: true`.

## Observances

`observances: full` marks the days a general-purpose calendar carries: statutory public holidays, plus eves, flag days, royal birthdays, advent and the other nth-weekday days, the equinoxes and solstices, and the two daylight-saving transitions. `publicHolidays` keeps only the statutory days. `none` marks nothing and leaves `IsHoliday`/`HolidayName` NULL throughout.

Every date comes from a rule, not a lookup:

- **Fixed dates**: 1 January, 6 February, 17 May, 24-26 December, and so on.
- **Easter-relative**: the Gregorian Easter (anonymous Meeus/Jones/Butcher algorithm) plus an offset. Skjærtorsdag at E-3, Langfredag at E-2, Kristi himmelfartsdag at E+39, Andre pinsedag at E+50.
- **Nth-weekday**: Morsdag is the second Sunday of February, Allehelgensdag the first Sunday of November, and the four advent Sundays count back from the last Sunday on or before Christmas Eve.
- **Equinoxes and solstices**: Meeus, *Astronomical Algorithms* (2nd ed.) chapter 27, a mean instant per event corrected by twenty-four periodic terms and shifted from Dynamical Time to UT by delta-T, then resolved to the local date in the flow's time zone.
- **Clock changes**: read from the time zone's own rules by walking the year, not assumed to be the last Sunday of March and October, so the calendar stays correct if a country changes or abolishes the practice.

### Collisions are resolved by rank

A date carries one name. When two observances land on the same day the higher rank wins: `PublicHoliday` > `Named` > `Marker`. Ties keep the first rule, so the outcome never depends on enumeration order. Two collisions are worth knowing about:

- Easter Sunday occasionally falls on the spring clock change (it did in 2024). Første påskedag wins.
- Bots- og bededag is the last Sunday of October and so is the autumn clock change, **every year**. Bots- og bededag wins, so `Sommertid slutter` never appears in a Norwegian calendar.

## Adding a country

Countries are added in code, by implementing `CountryCalendar` (its culture, time zone, season names and observance rules) and registering it in `CountryCalendar.For`. An unsupported country is a validation error naming the supported set. A date dimension whose holidays are guessed from configuration is worse than one that refuses to generate.

## Validation

`sqlflow validate` catches everything wrong with a calendar without opening a connection: a missing `calendar` block, object, `from`, `to` or `country`; a date not in `yyyy-MM-dd` form; a `to` before `from`; a range wider than 200 years; an unsupported country; a culture or time zone this system does not know; a `fiscalYearStartMonth` outside 1-12; an unrecognized `observances` value. A valid flow prints its range, day count, country and target.

## Differences from the legacy generator

The legacy dimension came from `SQLFlowCore.Utils.Period.DimPeriod`, driven from a button on a Blazor page (`BuildDimPeriod.razor.cs`). `ExecDimPeriod.cs` was an empty stub, so it was never a pipeline and never ran on a schedule. Porting it surfaced defects that are fixed here rather than carried forward:

| Field | Legacy behavior | Now |
| --- | --- | --- |
| `MonthNumName` | Stored a date string (`2024-01-01` for January) that also embedded the year the generator last ran. | The sortable label the code intended, `01-jan`. |
| `FiscalYear` | Always the calendar year **plus one**, because a `day < 1` test could never be true. | The calendar year the fiscal year starts in, so a January start gives the calendar year. |
| `FiscalMonth` | Shifted one month early (`(m - start + 11) % 12 + 1`), so with a January start January reported 12 and February 1. | `(m - start + 12) % 12 + 1`, so a January start makes FiscalMonth equal the calendar month. |
| `FiscalWeekOfYear` | `0` for dates in the first partial week. | Counts from 1 in the week containing the fiscal year's first day. |
| `DaylightSavingTime` | `TimeZoneInfo.Local` on whichever machine happened to run it. | The flow's declared time zone. |
| Holidays | Scraped from a third-party HTML table, so names drifted between years ("Grunnlovsdag" one year, "Grunnlovsdagen" the next) and a failed fetch silently left a year unmarked. | Computed from rules; the same year always produces the same names. |
| Collisions | Whichever row the parser wrote last. | Decided by rank, deterministically. |

The column set, the column order, the types and the `IsHoliday IS NULL` convention are unchanged, so views built on the old table bind against a generated one without edits.
