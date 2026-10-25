import { useRef, useState, type ChangeEvent, type KeyboardEvent, type ReactNode } from "react";
import { Check, ChevronsUpDown, Loader2, TriangleAlert, X } from "lucide-react";
import { Command, CommandEmpty, CommandItem, CommandList } from "@/components/ui/command";
import { Input } from "@/components/ui/input";
import { Popover, PopoverAnchor, PopoverContent } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import type { RunParameterValueOption } from "../../api/types";

const count = new Intl.NumberFormat("en-US");

/** The input surface every field here wears, so a field with its name inside it reads as the inputs beside it. */
export const FIELD_SURFACE = "rounded-md border border-input bg-transparent shadow-xs transition-[color,box-shadow] dark:bg-input/30";

const BARE = "h-full min-w-0 flex-1 bg-transparent px-2 font-mono text-[12px] outline-none placeholder:text-muted-foreground";

/**
 * A field whose name sits inside it, before the value, on the input's own surface: what a toolbar uses to keep a value on
 * one line. The whole field takes the focus ring its input would.
 */
function Shell({ id, label, title, className, flagged, children }: {
  id: string;
  label: ReactNode;
  title?: string;
  className?: string;
  flagged?: "warning" | "error" | null;
  children: ReactNode;
}) {
  return (
    <div
      className={cn(
        "relative flex h-8 items-center", FIELD_SURFACE,
        "focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/50",
        flagged === "warning" && "border-warning",
        flagged === "error" && "border-destructive",
        className,
      )}
      title={title}
    >
      <label htmlFor={id} className="flex h-full shrink-0 items-center gap-0.5 border-r px-2 text-[11px] text-muted-foreground">{label}</label>
      {children}
    </div>
  );
}

/** A value typed into a field of its own: one value, in a form or (with a label inside it) in a toolbar. */
export function TypedValueInput({ id, value, onChange, placeholder, label, title, className, invalid }: {
  id: string;
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  /** The name shown inside the field, for a toolbar; null for a form, which labels the field above it. */
  label: ReactNode | null;
  title?: string;
  className?: string;
  invalid?: boolean;
}) {
  if (label === null) {
    return (
      <Input
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className={cn("h-8 font-mono text-[12px]", invalid && "border-destructive", className)}
        spellCheck={false}
        autoComplete="off"
        aria-invalid={invalid ? true : undefined}
        data-testid={id}
      />
    );
  }

  return (
    <Shell id={id} label={label} title={title} className={className} flagged={invalid ? "error" : null}>
      <input
        id={id}
        value={value}
        onChange={(event) => onChange(event.target.value)}
        placeholder={placeholder}
        className={BARE}
        spellCheck={false}
        autoComplete="off"
        data-testid={id}
      />
    </Shell>
  );
}

/** A value picked, as a chip in the field: the value, a mark when the source does not hold it, and a button that drops it. */
function ValueChip({ value, flagged, flagText, onDrop, testId }: {
  value: string;
  flagged: "warning" | "error" | null;
  flagText: string;
  onDrop: () => void;
  testId: string;
}) {
  return (
    <span
      className="inline-flex h-5 max-w-[10rem] shrink-0 items-center gap-0.5 rounded border bg-muted/60 pr-0.5 pl-1.5 font-mono text-[11.5px]"
      data-testid={testId}
      data-value={value}
    >
      {flagged !== null && (
        <span className={cn("flex shrink-0", flagged === "error" ? "text-destructive" : "text-warning")} title={flagText}>
          <TriangleAlert className="size-3" />
        </span>
      )}
      <span className="min-w-0 truncate" title={value}>{value}</span>
      <button
        type="button"
        className="rounded-sm p-0.5 text-muted-foreground hover:bg-accent hover:text-foreground"
        onClick={onDrop}
        aria-label={`Drop ${value}`}
      >
        <X className="size-3" />
      </button>
    </span>
  );
}

export interface RunValuePickerProps {
  /** The field's id and testid; its parts are `<id>-picked`, `<id>-value`, `<id>-values`, `<id>-more` and `<id>-unknown`. */
  id: string;
  /** Where the values come from, in words, for what the list and its notes say ("column log_source", "its choices"). */
  source: string;
  /** Every value the field holds: those picked, then the one being typed, if any. */
  value: string[];
  onChange: (value: string[]) => void;
  /** True when the field takes several values; a field of one value replaces it when another is picked. */
  multiple: boolean;
  /**
   * True when the values listed are all a run may take (a document's choices): a value outside them is an error. False when
   * they are what a source holds now (a lookup): a value outside them is taken, and only flagged, since the run may still
   * find it.
   */
  closed: boolean;
  /** The values offered; undefined while they are read, null for a field that offers none and takes what is typed. */
  options: readonly RunParameterValueOption[] | undefined | null;
  /** True when the source holds more values than are offered. */
  more: boolean;
  loading: boolean;
  /** Why the values could not be read, when they could not. */
  error: string | null;
  placeholder: string;
  /** The name shown inside the field, for a toolbar; null for a form. */
  label: ReactNode | null;
  title?: string;
  className?: string;
  /** Reports what is typed, so a source that holds more values than it offers can be asked for the matching ones. */
  onSearch?: (text: string) => void;
}

/**
 * The one picker for a run value whose values are offered (a document's choices, or what a lookup reads): the values picked,
 * as chips, then the value being typed, which counts as one of them as it stands. The list under the field offers the
 * values, filtered by what is typed; picking one adds it (or drops it, when it is picked already) and leaves the list open
 * for the next, or, for a field of one value, makes it the value. Enter takes what is typed, and Backspace in an empty
 * field drops the last value. A value the complete list does not hold is marked once the list is closed: an error for a
 * document's choices, which a run refuses, a warning for a lookup, whose source may hold it by the time the run reads it.
 */
export function RunValuePicker({
  id, source, value, onChange, multiple, closed, options, more, loading, error, placeholder, label, title, className, onSearch,
}: RunValuePickerProps) {
  const [open, setOpen] = useState(false);
  const [text, setText] = useState("");
  const anchorRef = useRef<HTMLDivElement>(null);
  const inputRef = useRef<HTMLInputElement>(null);
  // What is typed is the last of the values the page holds, so the values picked are the ones before it. Values the page
  // replaced from outside no longer end with it, and the text goes with them.
  const held = text.trim() === "" || (value.length > 0 && value[value.length - 1] === text.trim());
  if (!held) {
    setText("");
  }

  const typed = held ? text.trim() : "";
  const picked = typed !== "" ? value.slice(0, -1) : value;
  const listless = options === null;
  const offered = options ?? [];
  const exact = offered.some((option) => option.value === typed);
  const filter = typed.toLowerCase();
  const matches = typed === "" || exact ? offered : offered.filter((option) => option.value.toLowerCase().includes(filter));
  const complete = Array.isArray(options) && !more;
  const isUnknown = (candidate: string) => complete && !offered.some((option) => option.value === candidate);
  // Said once the list is closed: while it is open, the list itself shows what the text matches.
  const unknownTyped = !open && typed !== "" && isUnknown(typed);
  const unknownPicked = picked.filter(isUnknown);
  const unknown = unknownTyped || unknownPicked.length > 0;
  const unknownNamed = [...unknownPicked, ...(unknownTyped ? [typed] : [])];
  const flag = unknown ? (closed ? "error" : "warning") : null;
  const flagText = (named: readonly string[]) => (closed
    ? `${named.join(", ")} ${named.length === 1 ? "is" : "are"} not one of ${source}`
    : `${source} holds no ${named.join(", ")}`);

  const report = (nextPicked: readonly string[], nextText: string) => {
    const pending = nextText.trim();
    onChange(pending === "" ? [...nextPicked] : [...nextPicked, pending]);
  };
  const add = (candidate: string) => (multiple ? (picked.includes(candidate) ? picked : [...picked, candidate]) : [candidate]);
  const choose = (candidate: string) => {
    setText("");
    if (!multiple) {
      report(picked.includes(candidate) ? [] : [candidate], "");
      setOpen(false);
      return;
    }

    report(picked.includes(candidate) ? picked.filter((other) => other !== candidate) : [...picked, candidate], "");
    inputRef.current?.focus();
  };
  const drop = (candidate: string) => report(picked.filter((other) => other !== candidate), text);

  // A form lists every value picked; a toolbar keeps to one line, so it shows one token, the first value and how many
  // more, each named on hover, and leaves the value being typed its room. The list drops a value there.
  const inline = label !== null;
  const chips = inline
    ? picked.length > 0 && (
      <span
        className="inline-flex h-5 min-w-0 items-center gap-1 rounded border bg-muted/60 px-1.5 font-mono text-[11.5px]"
        title={picked.join("\n")}
        data-testid={`${id}-picked`}
        data-value={picked.join("\n")}
      >
        <span className="min-w-0 truncate">{picked[0]}</span>
        {picked.length > 1 && <span className="shrink-0 text-muted-foreground" data-testid={`${id}-more`}>{`+${picked.length - 1}`}</span>}
      </span>
    )
    : picked.map((chip) => (
      <ValueChip
        key={chip}
        value={chip}
        flagged={isUnknown(chip) ? (closed ? "error" : "warning") : null}
        flagText={flagText([chip])}
        onDrop={() => drop(chip)}
        testId={`${id}-picked`}
      />
    ));
  const icon = listless ? null : (
    <span className="pointer-events-none flex shrink-0 items-center pr-2.5 text-muted-foreground">
      {loading ? <Loader2 className="size-4 animate-spin" /> : <ChevronsUpDown className="size-4 opacity-60" />}
    </span>
  );
  const inputProps = {
    ref: inputRef,
    id,
    role: listless ? undefined : "combobox",
    "aria-expanded": listless ? undefined : open,
    "aria-autocomplete": listless ? undefined : ("list" as const),
    "aria-invalid": flag === "error" ? true : undefined,
    value: text,
    onChange: (event: ChangeEvent<HTMLInputElement>) => {
      setText(event.target.value);
      // A field of one value holds what is typed in place of what was picked.
      report(multiple ? picked : [], event.target.value);
      onSearch?.(event.target.value.trim());
      setOpen(!listless);
    },
    onClick: () => setOpen(!listless),
    onKeyDown: (event: KeyboardEvent<HTMLInputElement>) => {
      if (event.key === "ArrowDown") {
        setOpen(!listless);
      } else if (event.key === "Escape") {
        setOpen(false);
      } else if (event.key === "Enter" && typed !== "") {
        // Enter takes the value typed; with nothing typed it is the form's, as it was.
        event.preventDefault();
        setText("");
        report(add(typed), "");
        if (!multiple) {
          setOpen(false);
        }
      } else if (event.key === "Backspace" && text === "" && picked.length > 0) {
        report(picked.slice(0, -1), "");
      }
    },
    placeholder: picked.length === 0 ? placeholder : "",
    spellCheck: false,
    autoComplete: "off",
    "data-testid": id,
  };

  return (
    <div className={cn("flex flex-col gap-1", !inline && className)}>
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverAnchor asChild>
          <div ref={anchorRef}>
            {!inline
              ? (
                <div
                  className={cn(
                    "relative flex min-h-8 w-full flex-wrap items-center gap-1 py-[5px] pr-8 pl-1.5", FIELD_SURFACE,
                    "focus-within:border-ring focus-within:ring-[3px] focus-within:ring-ring/50",
                    flag === "warning" && "border-warning",
                    flag === "error" && "border-destructive",
                  )}
                >
                  {chips}
                  <input {...inputProps} className="h-5 min-w-[6rem] flex-1 bg-transparent px-0.5 font-mono text-[12px] outline-none placeholder:text-muted-foreground" />
                  <span className="absolute top-[7px] right-0">{icon}</span>
                </div>
              )
              : (
                <Shell id={id} label={label} title={title} className={className} flagged={flag}>
                  {picked.length > 0 && <div className="flex min-w-0 max-w-[60%] items-center pl-1.5">{chips}</div>}
                  <input {...inputProps} className={cn(BARE, "min-w-12")} />
                  {/* A toolbar keeps to one line, so a value outside the list is flagged inside the field. */}
                  {flag !== null && (
                    <span
                      className={cn("flex shrink-0", flag === "error" ? "text-destructive" : "text-warning")}
                      title={flagText(unknownNamed)}
                      data-testid={`${id}-unknown`}
                    >
                      <TriangleAlert className="size-3.5" />
                    </span>
                  )}
                  {icon}
                </Shell>
              )}
          </div>
        </PopoverAnchor>
        <PopoverContent
          className="w-[max(var(--radix-popover-trigger-width),16rem)] p-0"
          align="start"
          onOpenAutoFocus={(event) => event.preventDefault()}
          onInteractOutside={(event) => {
            // A click back into the field must not dismiss the list it anchors.
            if (event.target instanceof Node && anchorRef.current?.contains(event.target)) {
              event.preventDefault();
            }
          }}
          data-testid={`${id}-values`}
        >
          <Command shouldFilter={false}>
            <CommandList>
              <CommandEmpty>
                {loading
                  ? `Reading the values of ${source}...`
                  : error !== null
                    ? `The values of ${source} could not be read: ${error}`
                    : offered.length === 0 ? `${source} holds no value.` : `No value of ${source} holds "${typed}".`}
              </CommandEmpty>
              {matches.map((option) => {
                const chosen = picked.includes(option.value) || option.value === typed;
                return (
                  <CommandItem
                    key={option.value}
                    value={option.value}
                    onSelect={() => choose(option.value)}
                    className="flex items-center gap-2"
                    data-testid={`${id}-value`}
                    data-picked={chosen ? "true" : "false"}
                  >
                    <Check className={cn("size-3.5 shrink-0", chosen ? "opacity-100" : "opacity-0")} />
                    <span className={cn("min-w-0 flex-1 truncate font-mono text-[12px]", chosen && "font-semibold")}>{option.value}</span>
                    {option.count !== null && option.count !== undefined && (
                      <span className="shrink-0 font-mono text-[11px] text-muted-foreground tabular-nums" title={`${source} holds it ${count.format(option.count)} times`}>
                        {count.format(option.count)}
                      </span>
                    )}
                  </CommandItem>
                );
              })}
            </CommandList>
          </Command>
          <p className="border-t px-2 py-1.5 text-[11px] text-muted-foreground">
            {more
              ? `${source} holds more values than the ${count.format(offered.length)} listed here; type to find one, or type one to use it.`
              : closed
                ? multiple ? "Pick one or several." : "Pick one."
                : multiple ? "Pick several to take each; Enter takes the value typed." : "Pick one, or type one and press Enter."}
          </p>
        </PopoverContent>
      </Popover>
      {flag !== null && !inline && (
        <p className={cn("flex items-center gap-1 text-[11px]", flag === "error" ? "text-destructive" : "text-warning")} data-testid={`${id}-unknown`}>
          <TriangleAlert className="size-3.5 shrink-0" />
          {flagText(unknownNamed)}
        </p>
      )}
    </div>
  );
}
