import { useState } from "react";
import { Check, ChevronsUpDown } from "lucide-react";
import { Button } from "@/components/ui/button";
import {
  Command,
  CommandEmpty,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";
import { activeFilterClass } from "./FilterBar";

/** One choice in a {@link FilterCombobox}: the `value` the filter emits, the `label` shown, and an optional
 * `hint` line (a count, a repo) shown under the label and included in the search text. */
export interface FilterOption {
  value: string;
  label: string;
  hint?: string;
}

/**
 * A searchable single-select filter control for the list-page FilterBar: a 32px outline trigger that opens a
 * type-to-filter command list, so a filter over hundreds of values (every schedule or batch in the estate)
 * stays usable where a plain dropdown would not. The empty string is the "no filter" value; selecting an option
 * emits its `value`, and a Clear row resets to it. Matches the lineage project picker's shape (DESIGN.md 7.1).
 */
export function FilterCombobox({
  options, value, onChange, placeholder, searchPlaceholder, emptyText = "No matches.",
  ariaLabel, testId, className,
}: {
  options: FilterOption[];
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  searchPlaceholder: string;
  emptyText?: string;
  ariaLabel: string;
  testId?: string;
  className?: string;
}) {
  const [open, setOpen] = useState(false);
  const selected = options.find((option) => option.value === value) ?? null;

  const choose = (next: string) => {
    setOpen(false);
    onChange(next);
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          size="sm"
          role="combobox"
          aria-expanded={open}
          aria-label={ariaLabel}
          data-testid={testId}
          className={cn("h-8 justify-between font-normal", value !== "" && activeFilterClass, className)}
        >
          {selected !== null
            ? <span className="truncate">{selected.label}</span>
            : <span className="text-muted-foreground">{placeholder}</span>}
          <ChevronsUpDown className="text-muted-foreground" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-64 p-0">
        <Command>
          <CommandInput placeholder={searchPlaceholder} />
          <CommandList>
            <CommandEmpty>{emptyText}</CommandEmpty>
            {value !== "" && (
              <CommandItem value="__clear__" onSelect={() => choose("")} className="text-muted-foreground">
                Clear filter
              </CommandItem>
            )}
            {options.map((option) => (
              <CommandItem
                // The command value is what type-ahead matches on, and must be unique; the raw value keeps two
                // like-labelled options (same batch name in two repos) distinct while label + hint stay searchable.
                key={option.value}
                value={`${option.label} ${option.hint ?? ""} ${option.value}`}
                onSelect={() => choose(option.value)}
              >
                <div className="min-w-0 flex-1 overflow-hidden">
                  <div className="truncate text-[13px]">{option.label}</div>
                  {option.hint !== undefined && (
                    <div className="truncate text-xs text-muted-foreground">{option.hint}</div>
                  )}
                </div>
                {option.value === value && <Check className="shrink-0" />}
              </CommandItem>
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
