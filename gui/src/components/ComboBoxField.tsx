import { useEffect, useId, useRef, useState, type ReactNode } from "react";
import { Check, ChevronsUpDown, Loader2, X } from "lucide-react";
import { Command, CommandEmpty, CommandItem, CommandList } from "@/components/ui/command";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Popover, PopoverAnchor, PopoverContent } from "@/components/ui/popover";
import { cn } from "@/lib/utils";

interface ComboBoxFieldCommonProps<T> {
  /** The options to offer, typically a query result's items (empty while loading). */
  options: readonly T[];
  /** The unique value behind an option; selection is keyed on it and it backs the option's identity. */
  optionValue: (option: T) => string;
  /** The text the input mirrors for a selected option and that typing filters on; defaults to optionValue. */
  optionLabel?: (option: T) => string;
  /** Custom option-row content rendered after the selection check mark; defaults to the option's label. */
  renderOption?: (option: T, selected: boolean) => ReactNode;
  /** A visible label above the input. Omit it (and set ariaLabel) for label-less layouts like filter bars. */
  label?: string;
  /** The input's aria-label when no visible label is rendered. */
  ariaLabel?: string;
  placeholder?: string;
  disabled?: boolean;
  /** True while the options are being fetched: shows a spinner and the loading empty-state text. */
  loading?: boolean;
  loadingMessage?: string;
  emptyMessage?: string;
  /** Rendered as the first option while a selection exists; picking it clears the selection. */
  clearOption?: { label: string; onClear: () => void };
  /**
   * Server-side search mode: every user edit is reported here and client-side filtering is turned off,
   * so the caller drives the option list from the typed text (debounced upstream as it sees fit).
   */
  onSearchChange?: (text: string) => void;
  /** data-testid, placed on the visible input element (what e2e fills and asserts values on). */
  testId?: string;
  /** Extra classes for the outer field wrapper (widths belong here). */
  className?: string;
}

interface SingleComboBoxFieldProps<T> extends ComboBoxFieldCommonProps<T> {
  multiple?: false;
  /** The selected option's value, or null when nothing is selected. */
  value: string | null;
  onChange: (value: string, option: T) => void;
}

interface MultiComboBoxFieldProps<T> extends ComboBoxFieldCommonProps<T> {
  multiple: true;
  /** The selected option values; picking an option toggles its membership. */
  values: readonly string[];
  onToggle: (value: string, option: T) => void;
}

export type ComboBoxFieldProps<T> = SingleComboBoxFieldProps<T> | MultiComboBoxFieldProps<T>;

/**
 * The GUI's one combobox pattern: a real, always-visible Input anchored to a popover Command list. Typing
 * filters (or, with onSearchChange, drives a server-side search); a value is selected only by picking an
 * option, each rendered with role="option" (what the e2e suite clicks). While the list is closed the input
 * mirrors the current selection, including external changes such as a dependent picker resetting, so
 * Playwright's fill() and toHaveValue() work against the input directly, and the disabled state lives on
 * the input element itself.
 *
 * Single-select mirrors the selected option's label into the input (falling back to the raw value while the
 * options have not loaded it yet, so deep-linked selections still display). Multi-select keeps the input as
 * a pure filter box that empties when the list closes; the caller communicates the membership through the
 * placeholder and its own badge row.
 */
export function ComboBoxField<T>(props: ComboBoxFieldProps<T>) {
  const {
    options, optionValue, optionLabel, renderOption, label, ariaLabel, placeholder, disabled, loading,
    loadingMessage, emptyMessage, clearOption, onSearchChange, testId, className,
  } = props;
  const inputId = useId();
  const [open, setOpen] = useState(false);
  const anchorRef = useRef<HTMLDivElement>(null);

  const labelOf = optionLabel ?? optionValue;
  const isSelected = (option: T) =>
    props.multiple === true ? props.values.includes(optionValue(option)) : props.value === optionValue(option);

  // Single-select: the text the closed input shows. The raw value stands in until the options carry it
  // (a deep link renders its selection before, or without, the option list resolving).
  const selectedLabel = props.multiple === true
    ? ""
    : props.value === null
      ? ""
      : (() => {
        const selected = options.find((option) => optionValue(option) === props.value);
        return selected !== undefined ? labelOf(selected) : props.value;
      })();
  const [text, setText] = useState(selectedLabel);

  useEffect(() => {
    if (!open) {
      setText(selectedLabel);
    }
  }, [open, selectedLabel]);

  // A picker that becomes disabled (its upstream scope cleared) must not leave its list hanging open.
  useEffect(() => {
    if (disabled === true) {
      setOpen(false);
    }
  }, [disabled]);

  // With the untouched selection in the box, show every option (so re-opening the list is a browse, not a
  // one-item filter); once the user edits the text, filter by it. In server-search mode the caller already
  // filtered the options, so they pass through untouched.
  const filter = text.trim().toLowerCase();
  const matches = onSearchChange !== undefined || filter === "" || text === selectedLabel
    ? options
    : options.filter((option) => labelOf(option).toLowerCase().includes(filter));

  const hasSelection = props.multiple === true ? props.values.length > 0 : props.value !== null;

  const pick = (option: T) => {
    const value = optionValue(option);
    if (props.multiple === true) {
      props.onToggle(value, option);
      setText("");
    } else {
      props.onChange(value, option);
      setText(labelOf(option));
    }

    setOpen(false);
  };

  return (
    <div className={cn("flex flex-col gap-1.5", className)}>
      {label !== undefined && <Label htmlFor={inputId}>{label}</Label>}
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverAnchor asChild>
          <div ref={anchorRef} className="relative">
            <Input
              id={inputId}
              className="h-8 w-full pr-8"
              role="combobox"
              aria-expanded={open}
              aria-label={label === undefined ? ariaLabel : undefined}
              autoComplete="off"
              placeholder={placeholder}
              value={text}
              disabled={disabled}
              data-testid={testId}
              onClick={() => setOpen(true)}
              onKeyDown={(event) => {
                if (event.key === "ArrowDown") {
                  setOpen(true);
                }
              }}
              onChange={(event) => {
                setText(event.target.value);
                setOpen(true);
                onSearchChange?.(event.target.value);
              }}
            />
            <span className="pointer-events-none absolute top-1/2 right-2.5 -translate-y-1/2 text-muted-foreground">
              {loading === true
                ? <Loader2 className="size-4 animate-spin" />
                : <ChevronsUpDown className="size-4 opacity-60" />}
            </span>
          </div>
        </PopoverAnchor>
        <PopoverContent
          className="w-[var(--radix-popover-trigger-width)] p-0"
          align="start"
          onOpenAutoFocus={(event) => event.preventDefault()}
          onInteractOutside={(event) => {
            // A click back into the input must not dismiss the list it anchors.
            if (event.target instanceof Node && anchorRef.current?.contains(event.target)) {
              event.preventDefault();
            }
          }}
        >
          <Command shouldFilter={false}>
            <CommandList>
              <CommandEmpty>
                {loading === true ? loadingMessage ?? "Loading..." : emptyMessage ?? "No matches."}
              </CommandEmpty>
              {clearOption !== undefined && hasSelection && (
                <CommandItem
                  value="__combobox-clear__"
                  onSelect={() => {
                    clearOption.onClear();
                    setText("");
                    setOpen(false);
                  }}
                >
                  <X />
                  {clearOption.label}
                </CommandItem>
              )}
              {matches.map((option) => {
                const selected = isSelected(option);
                return (
                  <CommandItem key={optionValue(option)} value={optionValue(option)} onSelect={() => pick(option)}>
                    <Check className={cn(selected ? "opacity-100" : "opacity-0")} />
                    {renderOption !== undefined ? renderOption(option, selected) : labelOf(option)}
                  </CommandItem>
                );
              })}
            </CommandList>
          </Command>
        </PopoverContent>
      </Popover>
    </div>
  );
}
