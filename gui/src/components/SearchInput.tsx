import { Search, X } from "lucide-react";
import { Input } from "@/components/ui/input";
import { cn } from "@/lib/utils";
import { activeFilterClass } from "./FilterBar";

interface SearchInputProps {
  value: string;
  onChange: (value: string) => void;
  placeholder: string;
  /** The accessible name of the field, since the magnifier stands in for a visible label. */
  label: string;
  /** Width and placement overrides; the field is full-width below the small breakpoint by default. */
  className?: string;
  /** Base testid; the clear button gets `<testId>-clear`. */
  testId: string;
}

/**
 * The free-text search field used above a list or tree: a filled 32px input with a leading magnifier and a
 * trailing clear button, tinted with {@link activeFilterClass} while a term is typed. The fill is what keeps it
 * from reading as empty page background; a bare bordered input on a dark surface is nearly invisible.
 */
export function SearchInput({ value, onChange, placeholder, label, className, testId }: SearchInputProps) {
  return (
    <div className={cn("relative w-full sm:w-72", className)}>
      <Search className="pointer-events-none absolute left-2.5 top-1/2 size-4 -translate-y-1/2 text-muted-foreground" />
      <Input
        value={value}
        onChange={(e) => onChange(e.target.value)}
        placeholder={placeholder}
        aria-label={label}
        className={cn(
          "h-8 bg-secondary/70 pl-8 pr-8 text-[13px] dark:bg-input/60",
          value !== "" && activeFilterClass,
        )}
        data-testid={testId}
      />
      {value !== "" && (
        <button
          type="button"
          onClick={() => onChange("")}
          aria-label="Clear search"
          className="absolute right-2 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
          data-testid={`${testId}-clear`}
        >
          <X className="size-4" />
        </button>
      )}
    </div>
  );
}
