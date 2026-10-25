import type { DeclaredRunParameter, FlowRunInputs, ResolvedRunValue, RunSetting, RunValue } from "../../api/types.ts";

// A run form's inputs for a registered kind's flow (FlowRunInputs on the control plane): the parameters its document
// declares and the settings of its document a run may change. A field left empty is a value the run does not give, so the
// document's own applies (the parameter's default, the setting's value), exactly as a run without it takes it. What a run
// with the form's inputs takes, and what would refuse it, is the control plane's to say (POST .../parameters/explain): these
// helpers only turn the fields into the request, and never judge a value themselves.

/** What a parameter's field offers: a fixed list the document writes, values its kind looks up, or a value to type. */
export type ParameterOffer = "choices" | "lookup" | "free";

/** How a parameter's field offers its values: its document's choices first, then its kind's lookup, else typing. */
export function parameterOffer(parameter: DeclaredRunParameter): ParameterOffer {
  if (parameter.choices.length > 0) {
    return "choices";
  }

  return parameter.lookup !== null && parameter.lookup !== undefined && parameter.lookup.trim() !== "" ? "lookup" : "free";
}

/** The values a form holds for a flow's parameters, by name: absent or empty is a parameter the run gives no value. */
export type ParameterFieldValues = Readonly<Record<string, RunValue | undefined>>;

/** A run value with its empty parts left out: trimmed values, none empty, one value alone; undefined when nothing is left. */
export function cleanRunValue(value: RunValue | undefined): RunValue | undefined {
  if (value === undefined) {
    return undefined;
  }

  const items = (typeof value === "string" ? [value] : value).map((item) => item.trim()).filter((item) => item !== "");
  if (items.length === 0) {
    return undefined;
  }

  return items.length === 1 ? items[0] : items;
}

/** The values a run is given for the flow's parameters: each field that holds one, as it holds it; the rest take the document's. */
export function parameterValuesToSend(parameters: readonly DeclaredRunParameter[], fields: ParameterFieldValues): Record<string, RunValue> {
  const values: Record<string, RunValue> = {};
  for (const parameter of parameters) {
    const value = cleanRunValue(fields[parameter.name]);
    if (value !== undefined) {
      values[parameter.name] = value;
    }
  }

  return values;
}

/** The fields a form opens with: the values a run being repeated gave the flow's parameters, and none for a fresh run. */
export function initialParameterFields(
  parameters: readonly DeclaredRunParameter[],
  initialValues: Readonly<Record<string, RunValue>>,
): Record<string, RunValue | undefined> {
  const fields: Record<string, RunValue | undefined> = {};
  for (const parameter of parameters) {
    if (parameter.name in initialValues) {
      fields[parameter.name] = initialValues[parameter.name];
    }
  }

  return fields;
}

/** The text a form holds for each run setting, by its payload key: empty is the document's value. */
export type SettingFieldValues = Readonly<Record<string, string | undefined>>;

/**
 * The parameters a run of `operation` is given fields for: every one the document declares when the operation resolves
 * them (every operation does, unless the kind names those that do), none when it takes the values it is given as they are.
 */
export function parametersFor(inputs: FlowRunInputs | null | undefined, operation: string | null): DeclaredRunParameter[] {
  if (inputs === null || inputs === undefined) {
    return [];
  }

  const operations = inputs.parameterOperations ?? [];
  return operations.length === 0 || (operation !== null && operations.includes(operation)) ? inputs.parameters : [];
}

/** The settings a run of `operation` may change: those of every operation, and those of this one. */
export function settingsFor(settings: readonly RunSetting[], operation: string | null): RunSetting[] {
  return settings.filter((setting) => setting.operations.length === 0 || operation === null || setting.operations.includes(operation));
}

/**
 * A setting's text as its payload value: a whole number as a JSON number and a toggle as a boolean when the text reads as
 * one, any other text as itself, so the control plane names what is wrong with it rather than the form guessing.
 */
export function settingPayloadValue(setting: RunSetting, text: string): unknown {
  const trimmed = text.trim();
  switch (setting.input) {
    case "WholeNumber":
      return /^-?\d+$/.test(trimmed) && Number.isSafeInteger(Number(trimmed)) ? Number(trimmed) : trimmed;
    case "Toggle":
      return trimmed === "true" ? true : trimmed === "false" ? false : trimmed;
    default:
      return trimmed;
  }
}

/** The payload properties a run is given for the settings it changes: each field that holds a value; the rest are the document's. */
export function settingsToPayload(settings: readonly RunSetting[], fields: SettingFieldValues): Record<string, unknown> {
  const payload: Record<string, unknown> = {};
  for (const setting of settings) {
    const text = fields[setting.key];
    if (text !== undefined && text.trim() !== "") {
      payload[setting.key] = settingPayloadValue(setting, text);
    }
  }

  return payload;
}

/** The fields a form opens with for the settings: what a run being repeated gave each, as text. */
export function initialSettingFields(settings: readonly RunSetting[], payload: Readonly<Record<string, unknown>> | null): Record<string, string> {
  const fields: Record<string, string> = {};
  for (const setting of settings) {
    const value = payload?.[setting.key];
    if (typeof value === "string" || typeof value === "number" || typeof value === "boolean") {
      fields[setting.key] = String(value);
    }
  }

  return fields;
}

/** What the control plane said refuses each parameter, by name: the first problem it names for it. */
export function problemsByParameter(values: readonly ResolvedRunValue[]): Record<string, string> {
  const problems: Record<string, string> = {};
  for (const value of values) {
    if (value.problem !== null && !(value.name in problems)) {
      problems[value.name] = value.problem;
    }
  }

  return problems;
}

/**
 * The problems that belong to no one parameter's field: every problem the control plane named that is not one of the
 * parameters' own (a setting out of range, an argument the kind refuses), in its order.
 */
export function formProblems(problems: readonly string[], values: readonly ResolvedRunValue[]): string[] {
  const own = new Set(values.map((value) => value.problem).filter((problem): problem is string => problem !== null));
  return problems.filter((problem) => !own.has(problem));
}

/** A parameter's default as a line: its value, a list's values separated by commas; null when it has none. */
export function defaultText(parameter: DeclaredRunParameter): string | null {
  const value = parameter.default;
  if (value === null || value === undefined) {
    return null;
  }

  return typeof value === "string" ? value : value.join(", ");
}
