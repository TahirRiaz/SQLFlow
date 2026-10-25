import { useEffect, useState, type ReactNode } from "react";
import { useQuery } from "@tanstack/react-query";
import { Label } from "@/components/ui/label";
import { runValueItems, runValueOf, runValueText } from "@/lib/runValues";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { pipelineApi } from "../../api/endpoints";
import type { DeclaredRunParameter, RunParameterValueOption, RunValue } from "../../api/types";
import { RunValuePicker, TypedValueInput } from "./RunValuePicker";
import { defaultText, parameterOffer, type ParameterFieldValues } from "./runInputs";

/** How long typing rests before a source that holds more values than it listed is asked for the matching ones. */
const SEARCH_DELAY_MS = 300;

export interface RunParameterFieldsProps {
  /** The flow whose parameters the fields fill, or null before one is known (lookups wait for it). */
  pipelineId: string | null;
  /** The parameters its document declares, in its order. */
  parameters: readonly DeclaredRunParameter[];
  values: ParameterFieldValues;
  /** A field's new value: undefined when it is emptied, so the document's default applies. */
  onChange: (name: string, value: RunValue | undefined) => void;
  /** The run values a lookup depends on (the partition a run acts in, say), passed to it as the form holds them. */
  lookupValues?: Readonly<Record<string, RunValue>>;
  /** Ids and testids are `<prefix>-parameter-<name>`. */
  prefix: string;
  /**
   * `stacked` puts each parameter's name above its field with its description, default and problem below, for a form;
   * `inline` puts the name inside the field and the rest on hover, for a toolbar that keeps its controls on one line.
   */
  layout?: "stacked" | "inline";
  /** What refuses each parameter's value, by name, as the control plane explains the form. */
  problems?: Readonly<Record<string, string>>;
  /** The width of each field. */
  className?: string;
}

/**
 * The one set of fields for a flow's parameters, wherever a page asks for them (the trigger dialog, a flow's Preview tab, a
 * mapping's value check): each parameter its document declares, offered as its document says. A parameter with choices
 * picks from them, and a value outside them is an error; one its kind looks up picks from what the lookup reads, with how
 * often each value is held, and still takes a value typed; any other is typed. One that takes several values takes them as
 * chips. A field left empty gives the run no value, so the document's default applies, and the default is what the empty
 * field shows. The fields are returned side by side for the page to lay out.
 */
export function RunParameterFields({
  pipelineId, parameters, values, onChange, lookupValues, prefix, layout = "stacked", problems, className,
}: RunParameterFieldsProps) {
  return (
    <>
      {parameters.map((parameter) => {
        const id = `${prefix}-parameter-${parameter.name}`;
        const fallback = defaultText(parameter);
        const placeholder = fallback ?? (parameter.required ? "required" : "optional");
        const problem = problems?.[parameter.name] ?? null;
        const name = (
          <>
            <span className="font-mono">{parameter.name}</span>
            {parameter.required && fallback === null && <span className="text-destructive">*</span>}
          </>
        );
        const title = [parameter.description, fallback === null ? null : `Default from the flow: ${fallback}`, problem]
          .filter((part): part is string => part !== null && part !== undefined && part !== "")
          .join("\n");
        const inlineLabel = layout === "inline" ? name : null;
        const field = (
          <ParameterField
            id={id}
            pipelineId={pipelineId}
            parameter={parameter}
            value={values[parameter.name]}
            onChange={(value) => onChange(parameter.name, value)}
            lookupValues={lookupValues}
            placeholder={placeholder}
            label={inlineLabel}
            title={title === "" ? undefined : title}
            invalid={problem !== null}
            className={className}
          />
        );

        if (layout === "inline") {
          return <div key={parameter.name}>{field}</div>;
        }

        return (
          <div key={parameter.name} className="flex flex-col gap-1" data-testid={`${id}-field`}>
            <Label htmlFor={id} className="text-[12px] text-muted-foreground">{name}</Label>
            {field}
            {parameter.description !== null && parameter.description !== undefined && parameter.description.trim() !== "" && (
              <p className="text-[11px] text-muted-foreground">{parameter.description}</p>
            )}
            <p className="text-[11px] text-muted-foreground" data-testid={`${id}-default`}>
              {fallback !== null
                ? `Default from the flow: ${fallback}. Leave empty to use it.`
                : parameter.required
                  ? "Required: the flow gives no default."
                  : "Optional: leave empty to give no value."}
            </p>
            {problem !== null && (
              <p className="text-[11px] font-medium text-destructive" data-testid={`${id}-problem`}>{problem}</p>
            )}
          </div>
        );
      })}
    </>
  );
}

function ParameterField({
  id, pipelineId, parameter, value, onChange, lookupValues, placeholder, label, title, invalid, className,
}: {
  id: string;
  pipelineId: string | null;
  parameter: DeclaredRunParameter;
  value: RunValue | undefined;
  onChange: (value: RunValue | undefined) => void;
  lookupValues: Readonly<Record<string, RunValue>> | undefined;
  placeholder: string;
  label: ReactNode | null;
  title?: string;
  invalid: boolean;
  className?: string;
}) {
  const offer = parameterOffer(parameter);
  const report = (items: string[]) => onChange(parameter.takesSeveral ? runValueOf(items) : items[items.length - 1]);

  if (offer === "lookup") {
    return (
      <LookupField
        id={id}
        pipelineId={pipelineId}
        parameter={parameter}
        value={runValueItems(value)}
        onChange={report}
        lookupValues={lookupValues}
        placeholder={placeholder}
        label={label}
        title={title}
        className={className}
      />
    );
  }

  if (offer === "choices") {
    return (
      <RunValuePicker
        id={id}
        source="its choices"
        value={runValueItems(value)}
        onChange={report}
        multiple={parameter.takesSeveral}
        closed
        options={parameter.choices.map((choice): RunParameterValueOption => ({ value: choice }))}
        more={false}
        loading={false}
        error={null}
        placeholder={placeholder}
        label={label}
        title={title}
        className={className}
      />
    );
  }

  if (parameter.takesSeveral) {
    return (
      <RunValuePicker
        id={id}
        source="the values typed"
        value={runValueItems(value)}
        onChange={report}
        multiple
        closed={false}
        options={null}
        more={false}
        loading={false}
        error={null}
        placeholder={placeholder}
        label={label}
        title={title}
        className={className}
      />
    );
  }

  return (
    <TypedValueInput
      id={id}
      value={value === undefined ? "" : runValueText(value)}
      onChange={(text) => onChange(text === "" ? undefined : text)}
      placeholder={placeholder}
      label={label}
      title={title}
      invalid={invalid}
      className={className}
    />
  );
}

/**
 * A parameter whose kind looks its values up: the values are read once, and the list filters them as the operator types;
 * when the source holds more values than it listed, what is typed is also asked for, after a short rest.
 */
function LookupField({
  id, pipelineId, parameter, value, onChange, lookupValues, placeholder, label, title, className,
}: {
  id: string;
  pipelineId: string | null;
  parameter: DeclaredRunParameter;
  value: string[];
  onChange: (items: string[]) => void;
  lookupValues: Readonly<Record<string, RunValue>> | undefined;
  placeholder: string;
  label: ReactNode | null;
  title?: string;
  className?: string;
}) {
  const [typed, setTyped] = useState("");
  const [search, setSearch] = useState("");
  useEffect(() => {
    const timer = setTimeout(() => setSearch(typed), SEARCH_DELAY_MS);
    return () => clearTimeout(timer);
  }, [typed]);

  const context = lookupValues ?? {};
  const contextKey = JSON.stringify(Object.entries(context).sort(([a], [b]) => a.localeCompare(b)));
  const all = useQuery({
    queryKey: ["pipeline-parameter-values", pipelineId, parameter.name, contextKey, null],
    queryFn: () => pipelineApi.parameterValues(pipelineId!, parameter.name, { values: { ...context } }),
    enabled: pipelineId !== null,
    staleTime: 60_000,
  });
  const searching = all.data?.more === true && search !== "";
  const found = useQuery({
    queryKey: ["pipeline-parameter-values", pipelineId, parameter.name, contextKey, search],
    queryFn: () => pipelineApi.parameterValues(pipelineId!, parameter.name, { search, values: { ...context } }),
    enabled: pipelineId !== null && searching,
    staleTime: 60_000,
  });

  const answer = searching && found.data !== undefined ? found.data : all.data;
  const failed = all.error ?? (searching ? found.error : null);
  const error = failed === null || failed === undefined
    ? null
    : isApiError(failed) ? failed.detail ?? failed.title : failed instanceof Error ? failed.message : String(failed);

  return (
    <RunValuePicker
      id={id}
      source={answer?.source ?? parameter.lookup ?? "its source"}
      value={value}
      onChange={onChange}
      multiple={parameter.takesSeveral}
      closed={false}
      options={answer?.values}
      more={answer?.more ?? false}
      loading={(all.isFetching && all.data === undefined) || (searching && found.isFetching)}
      error={error}
      placeholder={placeholder}
      label={label}
      title={title}
      className={cn(className)}
      onSearch={setTyped}
    />
  );
}
