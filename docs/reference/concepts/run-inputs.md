---
id: concept-run-inputs
title: "Run inputs: what a run of a flow may be given, and what it takes"
type: concept
summary: "A registered kind's flow describes the parameters and settings a run may be given; one rule resolves every run's values, the run form offers choices and lookups, and a schedule shows what each flow it runs is given."
keywords:
  - run inputs
  - run parameters
  - parameters
  - run settings
  - choices
  - lookup
  - several values
  - default
  - trigger dialog
  - schedule values
  - FlowRunInputs
  - RunValueResolution
  - DescribeRun
  - IRunParameterLookup
related:
  - concept-control-plane
  - flow-schedule
  - concept-architecture-and-execution
sourceRefs:
  - src/SqlFlow.Core/Runs/FlowRunInputs.cs
  - src/SqlFlow.Core/Runs/RunValuesJsonConverter.cs
  - src/SqlFlow.Yaml/FlowDocumentKinds.cs
  - src/SqlFlow.ControlPlane/Api/RunInputEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/CatalogEndpoints.cs
  - src/SqlFlow.ControlPlane/Api/ScheduleEndpoints.cs
  - src/SqlFlow.ControlPlane/Background/ScheduleFire.cs
  - src/SqlFlow.ControlPlane/Hosting/ControlPlaneModuleServices.cs
  - gui/src/features/runs/TriggerRunDialog.tsx
  - gui/src/features/runs/RunParameterFields.tsx
  - gui/src/features/runs/RunSettingFields.tsx
  - gui/src/features/runs/RunValuePicker.tsx
  - gui/src/features/runs/runInputs.ts
  - gui/src/features/schedules/ScheduleValuesSheet.tsx
  - gui/src/features/schedules/scheduleValues.ts
---

# Run inputs: what a run of a flow may be given, and what it takes

A flow of a registered kind (one a host module adds) can declare parameters in its document and can let a run change
some of its document's settings for itself. What a run may be given is read from the document by the flow's kind, once,
and every place that starts a run or shows one before it starts reads that same description: the run form, the lookups
that fill its lists, a run form's check as the operator types, and a schedule's preview of what each flow it runs is
given. One rule resolves the values, so what a screen shows is what the run takes. SQLFlow's built-in kinds declare no
run inputs; their runs take the substitution parameters the control plane documents (`fullLoad`, a backfill window,
`filePattern`, `sourceFilter`).

## What a flow describes

`IFlowDocumentKind.DescribeRun(yaml, source)` reads a flow's document and answers a `FlowRunInputs`; a kind that
declares nothing answers `FlowRunInputs.None`, which is the default. A document that no longer reads as its kind does
throws a `SqlFlowException` naming the document, and the control plane shows that message instead of the inputs.

| Part | What it holds |
| --- | --- |
| `parameters` | Each parameter the document declares, in its order: `name`, `required`, `default`, `description`, `takesSeveral`, `choices` (the values it may take, enforced) and `lookup` (where its kind reads the values it may take, in words; offered, not enforced). |
| `settings` | Each setting of the document a run may change for itself: the payload property it is given in (`key`), a `label`, an `input` (`WholeNumber`, `Toggle`, `Text` or `Choice`), `help`, where the document writes it (`documentKey`) and the value it gives (`documentValue`), what a run does when neither gives one (`whenUnset`), `minimum`/`maximum` or `choices`, and the `operations` it applies to (empty for all). |
| `parameterOperations` | The operations whose runs resolve the declared parameters, empty when every operation does. A run of another operation takes the values it is given as they are and requires none, so a run form asks it for none and an explanation lists its values as given. |
| `kindValues` | The run values the kind reads beside the declared parameters (`name`, `description`): the partition a run acts in, say. |
| `severalValuesRule` | Which parameters take several values, in the words a refusal names them by; null when the parameters' own `takesSeveral` says it all. |

A setting a run leaves out keeps the document's value; a setting a run gives applies to that run alone. A run gives
settings in its payload, and the kind checks them as it checks every payload.

## The one rule

`RunValueResolution` resolves what a run takes for each declared parameter, in the document's order, then lists each
given value the document does not declare:

| Origin | The run takes | The run is |
| --- | --- | --- |
| `Given` | the value the run, or the schedule firing it, gives | refused when the value is several and the parameter takes one, or a value is outside its `choices` |
| `Default` | the document's default, since the run gives none | started |
| `Unset` | no value: the parameter is optional and declares no default | started |
| `Missing` | no value, and the parameter is required | refused |
| `Kind` | a value of a name the kind reads beside the parameters | started (the kind checks it) |
| `NotDeclared` | a value of a name nothing declares | refused |

A kind resolves a run's values with `RunValueResolution.Resolve(inputs, values, where)` when a run of an operation that
takes them (`FlowRunInputs.TakesParameters`) starts, which
throws a `FlowValidationException` prefixed by `where` with the first refusal, so the run fails naming it; a client
explains values with `RunValueResolution.Explain`, which lists every value with its origin and problem. A trigger and a
schedule fire check only what the kind checks without the document (`IFlowDocumentKind.ValidateParameters`), so a run
form explains a run before it is queued (`POST /pipelines/{id}/parameters/explain`). The refusals read:

- `parameter '<name>' is required. Supply it with --set <name>=value or the run's values.`
- `parameter '<name>' is not declared under parameters.`
- `parameter '<name>' is given <n> values, and takes one.`, followed, when some parameter takes several, by `: only <rule> (<names>) takes several.`
- `parameter '<name>' is given '<value>', which is not one of its choices: <choices>.`

A list value is the run's `RunValueList` text inside SQLFlow and a JSON array at every JSON boundary (a trigger's
`values`, a resolved `value`, a declared `default`).

## Choices and lookups

A parameter's `choices` are the document's own list, and a run giving anything else is refused. A `lookup` is read when
a client asks: the control plane hands the flow's catalog row, the parameter, the text typed so far and the run values
the form holds to the first `IRunParameterLookup` that serves the flow's kind, and serves its answer
(`parameter`, `source` in words, `values` each with an optional `count`, and `more` when the source holds more matching
values than were listed). Looked-up values are offered, not enforced: the source may hold another by the time the run
reads it, so a value the lookup did not list is shown as a warning, never a refusal. A host module registers its lookup
in `IControlPlaneModule.ConfigureServices` with `services.AddRunParameterLookup<TLookup>()`; it is resolved per request.

## The routes

| Endpoint | Policy | Answers |
| --- | --- | --- |
| `GET /pipelines/{id}/parameters` | `read` | The built-in run controls that apply to the flow (`parameters`), and, for a registered kind's flow, `inputs` (the description above) or `inputsProblem` when its document no longer reads. 404 for an unknown pipeline. |
| `POST /pipelines/{id}/parameters/{name}/values` | `operate` | A parameter's values to pick from. Body `{search?, values?}`. Its `choices` when the document lists them (filtered by `search`, ignoring case), else the kind's lookup. 404 for an unknown pipeline or a parameter the document does not declare; 422 when the document no longer reads, or the parameter lists no choices and is not looked up; 502 `Lookup failed` with the source's message, redacted, when the lookup could not read. |
| `POST /pipelines/{id}/parameters/explain` | `operate` | What a run given the body's `{operation?, values?, payload?}` would take for each parameter (`values`: `name`, `value`, `origin`, `problem`) and everything that would refuse it (`problems`), checked as a trigger is checked (`YamlDocumentLoader.ValidateRunParameters`). Nothing is queued. 404 for an unknown pipeline. |
| `GET /schedules/{id}/parameters` | `read` | What each flow the schedule runs is given when it fires (below). 404 for an unknown schedule. |

The lookup and the explanation are under `operate` because a lookup reads the flow's own data, as a run does.

## What a schedule gives each flow

`GET /schedules/{id}/parameters` answers `{scheduleId, name, operation, values, members}`, `values` being the
schedule's own. `members` are the flows a fire expands to, in wave order, from the same expander the fire uses; each
has `flowName`, `flowKind`, `wave`, `pipelineId`, `takesScheduleArguments`, the `operation` it runs, its document's
`parameters`, the `values` its run takes (each with its origin), the `settings` its document gives the run (those of its
operation), its `kindValues`, a `note` and its `problems`.

A member takes the schedule's operation and values when the fire would give them to it, decided by the one test the
fire applies (`ScheduleFire.TakesKindArguments`): a registered kind that declares the operation, or any registered kind
when the schedule names none. A member that does not take them runs its default operation with its document's own
values, and its `note` says why:

- `A built-in kind takes no operation or values: this flow runs as its document defines.`
- `Its kind has no operation '<operation>': this flow runs its default operation (<default>) with its document's own values.`

A member's `problems` are everything that would refuse its run. A refusal by the kind's check of the fire's arguments
fails the whole fire (an automatic fire logs it, a run-now request answers 400, and nothing is enqueued); a value the
member's document refuses (a required parameter the schedule does not give, say) fails that member's run when it starts.

## The GUI

- **The run form** (Trigger run) builds a registered kind's fields from the flow's inputs. **Flow parameters** lists
  each declared parameter the chosen operation takes, with its description and default ("Default from the flow: north. Leave empty to use it."): a list to pick from
  for `choices`, a searchable list with counts for a `lookup` (searched again on the server as the operator types when
  the lookup has `more`), and a typed value otherwise; a parameter that takes several values takes them as chips.
  **Run settings** lists the settings of the chosen operation with the document's value and where it is written, and a
  reset back to it. As the operator types, the form asks the control plane to explain the run and shows each problem
  under its field, and the rest above the buttons; a run that would be refused cannot be started. A field left empty
  gives the run nothing, so the document's own value applies. Repeating a run opens the form with its values.
- **Schedules** shows, on each schedule's row, the operation and values it gives its flows ("Runs with", or
  `defaults` when it gives none); the cell and the values action open a sheet
  with the schedule's own operation and values and, for every member in wave order, each parameter's value and where
  it comes from (the schedule, the flow's default, not set, missing), its run settings, its note and what would refuse
  it. **Run now** lists the same line under each member and says before firing when a member's run would be refused.
