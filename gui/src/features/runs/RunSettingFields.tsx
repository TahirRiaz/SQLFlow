import { RotateCcw } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import type { RunSetting } from "../../api/types";
import type { SettingFieldValues } from "./runInputs";

/** The value a choice field holds when the run leaves the setting to the document. */
const FROM_DOCUMENT = "__from-document__";

export interface RunSettingFieldsProps {
  /** The settings a run of the picked operation may change, as the flow's document gives them. */
  settings: readonly RunSetting[];
  values: SettingFieldValues;
  /** A field's new text: undefined when it is emptied or reset, so the document's value applies. */
  onChange: (key: string, text: string | undefined) => void;
  /** Ids and testids are `<prefix>-setting-<key>`. */
  prefix: string;
}

/**
 * The settings of a flow's document a run may change for itself (the most rows a run reads, say), one field each:
 * the value the document gives it and where the document writes it, or what a run does when it gives none. A field left
 * empty keeps the document's value; one given a value changes it for this run alone, and Reset gives it back. What a value
 * may be is the control plane's to say, as it explains the form.
 */
export function RunSettingFields({ settings, values, onChange, prefix }: RunSettingFieldsProps) {
  return (
    <>
      {settings.map((setting) => {
        const id = `${prefix}-setting-${setting.key}`;
        const text = values[setting.key];
        const changed = text !== undefined && text.trim() !== "";
        const document = setting.documentValue ?? null;
        const where = setting.documentKey ? ` (${setting.documentKey})` : "";
        return (
          <div key={setting.key} className="flex flex-col gap-1" data-testid={`${id}-field`}>
            <div className="flex items-center justify-between gap-2">
              <Label htmlFor={id} className="text-[12px] text-muted-foreground">{setting.label}</Label>
              {changed && (
                <Button
                  type="button"
                  variant="ghost"
                  size="sm"
                  className="h-6 gap-1 px-1.5 text-[11px]"
                  onClick={() => onChange(setting.key, undefined)}
                  data-testid={`${id}-reset`}
                >
                  <RotateCcw className="size-3" />
                  Use the flow&apos;s
                </Button>
              )}
            </div>
            <SettingInput id={id} setting={setting} text={text ?? ""} onChange={(next) => onChange(setting.key, next === "" ? undefined : next)} />
            <p className="text-[11px] text-muted-foreground">{setting.help}</p>
            <p className="text-[11px] text-muted-foreground" data-testid={`${id}-document`}>
              {document !== null
                ? `${changed ? "Changed for this run; the flow's value" : "The flow's value"}${where}: ${document}.`
                : `Not set in the flow${where}${setting.whenUnset ? `: ${setting.whenUnset}` : "."}`}
            </p>
          </div>
        );
      })}
    </>
  );
}

function SettingInput({ id, setting, text, onChange }: {
  id: string;
  setting: RunSetting;
  text: string;
  onChange: (text: string) => void;
}) {
  const placeholder = setting.documentValue ?? "";
  if (setting.input === "Toggle" || setting.input === "Choice") {
    const options = setting.input === "Toggle" ? ["true", "false"] : setting.choices;
    return (
      <Select value={text === "" ? FROM_DOCUMENT : text} onValueChange={(value) => onChange(value === FROM_DOCUMENT ? "" : value)}>
        <SelectTrigger id={id} size="sm" className="h-8 w-full font-mono text-[12px]" data-testid={id}>
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value={FROM_DOCUMENT}>
            {setting.documentValue !== null && setting.documentValue !== undefined ? `The flow's: ${setting.documentValue}` : "The flow's (not set)"}
          </SelectItem>
          {options.map((option) => (
            <SelectItem key={option} value={option} className="font-mono">{option}</SelectItem>
          ))}
        </SelectContent>
      </Select>
    );
  }

  return (
    <Input
      id={id}
      className="h-8 w-40 font-mono text-[12px]"
      inputMode={setting.input === "WholeNumber" ? "numeric" : undefined}
      placeholder={placeholder}
      value={text}
      onChange={(event) => onChange(event.target.value)}
      spellCheck={false}
      autoComplete="off"
      data-testid={id}
    />
  );
}
