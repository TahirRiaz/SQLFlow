// A run form's inputs for a registered kind's flow: a field left empty gives the run no value, so the document's own
// applies (a parameter's default, a setting's value); a field's values go out as the run takes them; a setting's text is
// sent as its JSON type when it reads as one, else as itself for the control plane to name; and what the control plane
// says refuses a run is split between the parameters' own fields and the form.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import type { DeclaredRunParameter, ResolvedRunValue, RunSetting } from "../src/api/types.ts";
import {
  cleanRunValue, defaultText, formProblems, initialParameterFields, initialSettingFields, parameterOffer, parametersFor, parameterValuesToSend,
  problemsByParameter, settingPayloadValue, settingsFor, settingsToPayload,
} from "../src/features/runs/runInputs.ts";

function parameter(overrides: Partial<DeclaredRunParameter> & { name: string }): DeclaredRunParameter {
  return { required: false, takesSeveral: false, choices: [], ...overrides };
}

function setting(overrides: Partial<RunSetting> & { key: string }): RunSetting {
  return { label: overrides.key, input: "WholeNumber", help: "", choices: [], operations: [], ...overrides };
}

const LOG_SOURCE = parameter({ name: "logSource", required: true, takesSeveral: true, lookup: "column log_source" });
const REGION = parameter({ name: "region", default: "north", choices: ["north", "south"] });
const LABEL = parameter({ name: "label" });

describe("a parameter's field", () => {
  it("offers its document's choices first, then its kind's lookup, else a value to type", () => {
    assert.equal(parameterOffer(REGION), "choices");
    assert.equal(parameterOffer(LOG_SOURCE), "lookup");
    assert.equal(parameterOffer(LABEL), "free");
    assert.equal(parameterOffer(parameter({ name: "x", lookup: "  " })), "free");
    assert.equal(parameterOffer(parameter({ name: "x", choices: ["a"], lookup: "column x" })), "choices");
  });

  it("names its default as a line, a list's values separated by commas", () => {
    assert.equal(defaultText(REGION), "north");
    assert.equal(defaultText(parameter({ name: "x", default: ["a", "b"] })), "a, b");
    assert.equal(defaultText(LABEL), null);
  });
});

describe("the parameters a run form asks for", () => {
  const inputs = { parameters: [LOG_SOURCE, REGION], settings: [], kindValues: [] };

  it("are every declared one when every operation resolves them", () => {
    assert.deepEqual(parametersFor(inputs, "verify").map((p) => p.name), ["logSource", "region"]);
    assert.deepEqual(parametersFor({ ...inputs, parameterOperations: [] }, null).map((p) => p.name), ["logSource", "region"]);
  });

  it("are none for an operation that takes the values it is given as they are", () => {
    const reading = { ...inputs, parameterOperations: ["deliver", "plan"] };
    assert.deepEqual(parametersFor(reading, "deliver").map((p) => p.name), ["logSource", "region"]);
    assert.deepEqual(parametersFor(reading, "verify"), []);
    assert.deepEqual(parametersFor(reading, null), []);
  });

  it("are none before the flow's inputs are known", () => {
    assert.deepEqual(parametersFor(undefined, "deliver"), []);
    assert.deepEqual(parametersFor(null, "deliver"), []);
  });
});

describe("the values a run is given", () => {
  it("are the fields that hold a value, each as it holds it; an empty one leaves the document's default", () => {
    assert.deepEqual(
      parameterValuesToSend([LOG_SOURCE, REGION, LABEL], { logSource: ["STAT_COMP", "STAT_CPI"], region: "", label: undefined }),
      { logSource: ["STAT_COMP", "STAT_CPI"] },
    );
    assert.deepEqual(parameterValuesToSend([REGION], { region: "south" }), { region: "south" });
  });

  it("leave out what the flow does not declare, which the kind's own fields carry", () => {
    assert.deepEqual(parameterValuesToSend([REGION], { region: "south", partition: "dev" }), { region: "south" });
  });

  it("trim each value, drop the empty ones, and make one value alone of a list of one", () => {
    assert.equal(cleanRunValue("  north "), "north");
    assert.equal(cleanRunValue("   "), undefined);
    assert.equal(cleanRunValue(["", " a ", " "]), "a");
    assert.deepEqual(cleanRunValue(["a", " b"]), ["a", "b"]);
    assert.equal(cleanRunValue([]), undefined);
    assert.equal(cleanRunValue(undefined), undefined);
  });

  it("open, on a run being repeated, with the values it gave the flow's parameters", () => {
    assert.deepEqual(
      initialParameterFields([LOG_SOURCE, REGION], { logSource: ["STAT_COMP"], partition: "dev" }),
      { logSource: ["STAT_COMP"] },
    );
    assert.deepEqual(initialParameterFields([REGION], {}), {});
  });
});

describe("the run settings", () => {
  const MAX = setting({ key: "maxRecords", documentValue: "100", operations: ["deliver", "plan", "intake"] });
  const ALL = setting({ key: "everywhere" });

  it("a run may change are those of every operation and those of the one picked", () => {
    assert.deepEqual(settingsFor([MAX, ALL], "deliver").map((s) => s.key), ["maxRecords", "everywhere"]);
    assert.deepEqual(settingsFor([MAX, ALL], "verify").map((s) => s.key), ["everywhere"]);
    assert.deepEqual(settingsFor([MAX, ALL], null).map((s) => s.key), ["maxRecords", "everywhere"]);
  });

  it("go out as their JSON type when the text reads as one, and as the text itself otherwise", () => {
    assert.equal(settingPayloadValue(MAX, " 500 "), 500);
    assert.equal(settingPayloadValue(MAX, "0"), 0);
    assert.equal(settingPayloadValue(MAX, "-3"), -3);
    assert.equal(settingPayloadValue(MAX, "lots"), "lots");
    assert.equal(settingPayloadValue(MAX, "1.5"), "1.5");
    assert.equal(settingPayloadValue(MAX, "99999999999999999999"), "99999999999999999999");
    assert.equal(settingPayloadValue(setting({ key: "t", input: "Toggle" }), "true"), true);
    assert.equal(settingPayloadValue(setting({ key: "t", input: "Toggle" }), "false"), false);
    assert.equal(settingPayloadValue(setting({ key: "t", input: "Toggle" }), "yes"), "yes");
    assert.equal(settingPayloadValue(setting({ key: "c", input: "Choice" }), " a "), "a");
  });

  it("a run is given are the fields that hold a value; an empty one keeps the document's", () => {
    assert.deepEqual(settingsToPayload([MAX, ALL], { maxRecords: "250", everywhere: "  " }), { maxRecords: 250 });
    assert.deepEqual(settingsToPayload([MAX], {}), {});
  });

  it("open, on a run being repeated, with what its payload gave them, as text", () => {
    assert.deepEqual(initialSettingFields([MAX, ALL], { maxRecords: 50, force: true, everywhere: { nested: 1 } }), { maxRecords: "50" });
    assert.deepEqual(initialSettingFields([MAX], null), {});
  });
});

describe("what refuses a run", () => {
  const values: ResolvedRunValue[] = [
    { name: "logSource", value: null, origin: "Missing", problem: "parameter 'logSource' is required." },
    { name: "region", value: "west", origin: "Given", problem: "parameter 'region' is given 'west', which is not one of its choices: north, south." },
    { name: "label", value: null, origin: "Unset", problem: null },
  ];

  it("is said under each parameter's own field, the first problem it names", () => {
    assert.deepEqual(problemsByParameter(values), {
      logSource: "parameter 'logSource' is required.",
      region: "parameter 'region' is given 'west', which is not one of its choices: north, south.",
    });
  });

  it("is said for the form when it belongs to no parameter, in the control plane's order", () => {
    const problems = [
      "parameter 'logSource' is required.",
      "payload maxRecords is 5000; a run reads the first 1 to 1,000 records of its scope.",
      "parameter 'region' is given 'west', which is not one of its choices: north, south.",
    ];
    assert.deepEqual(formProblems(problems, values), ["payload maxRecords is 5000; a run reads the first 1 to 1,000 records of its scope."]);
    assert.deepEqual(formProblems([], values), []);
  });
});
