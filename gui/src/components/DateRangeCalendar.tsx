import { useState } from "react";
import {
  addMonths, addYears, eachDayOfInterval, endOfMonth, endOfWeek, format, isAfter, isBefore,
  isSameDay, isSameMonth, parse, startOfMonth, startOfWeek, subMonths, subYears,
} from "date-fns";
import {
  CalendarRange, ChevronLeft, ChevronRight, ChevronsLeft, ChevronsRight,
} from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { cn } from "@/lib/utils";

/** A datetime-local value ("yyyy-MM-ddThh:mm") split into its date and "HH:mm" time halves. */
interface Parsed {
  date: Date | null;
  time: string;
}

/** Parse a datetime-local string into a local date (at midnight) plus its "HH:mm" time, using `fallbackTime`
 * when the value is empty or carries no time. */
function parseValue(value: string, fallbackTime: string): Parsed {
  const trimmed = value.trim();
  if (trimmed === "") {
    return { date: null, time: fallbackTime };
  }
  const [datePart, timePart] = trimmed.split("T");
  const date = parse(datePart, "yyyy-MM-dd", new Date(2000, 0, 1));
  return { date: Number.isNaN(date.getTime()) ? null : date, time: (timePart ?? "").slice(0, 5) || fallbackTime };
}

/** Compose a local date and "HH:mm" time back into the "yyyy-MM-ddThh:mm" a datetime-local input expects. */
function buildValue(date: Date, time: string): string {
  return `${format(date, "yyyy-MM-dd")}T${time}`;
}

/** Human summary for the trigger button ("Jul 15, 2026, 11:22 AM"). */
function describe({ date, time }: Parsed): string {
  if (date === null) {
    return "";
  }
  const [h, m] = time.split(":");
  const withTime = new Date(date.getFullYear(), date.getMonth(), date.getDate(), Number(h), Number(m));
  return format(withTime, "MMM d, yyyy, h:mm a");
}

const WEEKDAYS = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];
const DEFAULT_FROM_TIME = "00:00";
const DEFAULT_TO_TIME = "23:59";

interface MonthNavProps {
  month: Date;
  onChange: (next: Date) => void;
}

/** One calendar's own header: year jump (double chevron), month step (single chevron), and its title. Each
 * calendar navigates on its own so choosing the start never drags the other month off the end date. */
function MonthNav({ month, onChange }: MonthNavProps) {
  return (
    <div className="mb-1.5 flex items-center justify-between">
      <div className="flex items-center">
        <Button
          type="button" variant="ghost" size="icon" className="size-7"
          onClick={() => onChange(subYears(month, 1))} aria-label="Previous year"
        >
          <ChevronsLeft className="size-4" />
        </Button>
        <Button
          type="button" variant="ghost" size="icon" className="size-7"
          onClick={() => onChange(subMonths(month, 1))} aria-label="Previous month"
        >
          <ChevronLeft className="size-4" />
        </Button>
      </div>
      <span className="text-[13px] font-medium">{format(month, "MMMM yyyy")}</span>
      <div className="flex items-center">
        <Button
          type="button" variant="ghost" size="icon" className="size-7"
          onClick={() => onChange(addMonths(month, 1))} aria-label="Next month"
        >
          <ChevronRight className="size-4" />
        </Button>
        <Button
          type="button" variant="ghost" size="icon" className="size-7"
          onClick={() => onChange(addYears(month, 1))} aria-label="Next year"
        >
          <ChevronsRight className="size-4" />
        </Button>
      </div>
    </div>
  );
}

interface MonthGridProps {
  month: Date;
  start: Date | null;
  end: Date | null;
  hovered: Date | null;
  onPick: (day: Date) => void;
  onHover: (day: Date | null) => void;
}

/** One month's 6x7 grid, painting the connected range fill and the two rounded endpoints. */
function MonthGrid({ month, start, end, hovered, onPick, onHover }: MonthGridProps) {
  const gridStart = startOfWeek(startOfMonth(month), { weekStartsOn: 1 });
  const gridEnd = endOfWeek(endOfMonth(month), { weekStartsOn: 1 });
  const days = eachDayOfInterval({ start: gridStart, end: gridEnd });

  // While an end is being chosen, the hovered day previews the closing edge so the fill tracks the cursor.
  const effectiveEnd = end ?? (start !== null && hovered !== null && !isBefore(hovered, start) ? hovered : null);

  return (
    <div className="w-full">
      <div className="grid grid-cols-7">
        {WEEKDAYS.map((label) => (
          <div key={label} className="pb-1 text-center text-[11px] font-normal text-muted-foreground">{label}</div>
        ))}
        {days.map((day) => {
          const outside = !isSameMonth(day, month);
          const isStart = start !== null && isSameDay(day, start);
          const isEnd = effectiveEnd !== null && isSameDay(day, effectiveEnd);
          const isEndpoint = isStart || isEnd;
          const inRange = start !== null && effectiveEnd !== null
            && !isBefore(day, start) && !isAfter(day, effectiveEnd);
          const weekday = (day.getDay() + 6) % 7; // Monday index 0
          return (
            <div
              key={day.toISOString()}
              className={cn(
                "flex justify-center py-0.5",
                inRange && !isEndpoint && "bg-accent",
                inRange && isStart && "rounded-l-full bg-accent",
                inRange && isEnd && "rounded-r-full bg-accent",
                inRange && !isEndpoint && weekday === 0 && "rounded-l-full",
                inRange && !isEndpoint && weekday === 6 && "rounded-r-full",
              )}
            >
              <button
                type="button"
                onClick={() => onPick(day)}
                onMouseEnter={() => onHover(day)}
                className={cn(
                  "flex size-8 items-center justify-center rounded-full text-[13px] tabular-nums transition-colors",
                  outside ? "text-muted-foreground/50" : "text-foreground",
                  !isEndpoint && "hover:bg-primary/15",
                  isEndpoint && "bg-primary font-medium text-primary-foreground hover:bg-primary",
                )}
                data-selected={isEndpoint ? "true" : undefined}
              >
                {day.getDate()}
              </button>
            </div>
          );
        })}
      </div>
    </div>
  );
}

export interface DateRangeCalendarProps {
  /** Datetime-local strings ("yyyy-MM-ddThh:mm") or "". */
  from: string;
  to: string;
  onChange: (from: string, to: string) => void;
  disabled?: boolean;
  /** data-testid stem; the trigger, both time inputs, Clear, and Apply derive their ids from it. */
  testId?: string;
}

/**
 * A single-window range picker (two months side by side, click a start then an end, the days between marked)
 * over the same "yyyy-MM-ddThh:mm" datetime-local contract the two plain inputs used, so the surrounding form
 * and its submit path are unchanged. Per-edge time inputs keep the backfill window's hours selectable.
 *
 * The selection is staged locally: clicking days and editing the times only touch a draft, and nothing is
 * committed to the form (`onChange`) until Apply. So a stray click in the calendar cannot silently alter the
 * committed window, and the run/schedule is only ever launched by its own explicit trigger afterwards. Cancel
 * (or closing the popover) discards the draft; the committed value shown on the trigger is untouched.
 */
export function DateRangeCalendar({ from, to, onChange, disabled, testId }: DateRangeCalendarProps) {
  const [open, setOpen] = useState(false);
  // The staged selection while the popover is open; seeded from the committed value each time it opens.
  const [draftFrom, setDraftFrom] = useState("");
  const [draftTo, setDraftTo] = useState("");
  const [hovered, setHovered] = useState<Date | null>(null);
  // The two calendars navigate independently, so each keeps its own view month.
  const [viewLeft, setViewLeft] = useState(() => startOfMonth(new Date()));
  const [viewRight, setViewRight] = useState(() => addMonths(startOfMonth(new Date()), 1));

  const committedFrom = parseValue(from, DEFAULT_FROM_TIME);
  const committedTo = parseValue(to, DEFAULT_TO_TIME);
  const draftParsedFrom = parseValue(draftFrom, DEFAULT_FROM_TIME);
  const draftParsedTo = parseValue(draftTo, DEFAULT_TO_TIME);

  const openChange = (next: boolean) => {
    if (disabled) {
      return;
    }
    if (next) {
      // Seed the draft from the committed value and open the left calendar on the start month (or today),
      // the right on the end month when it sits later, otherwise the month after the start.
      setDraftFrom(from);
      setDraftTo(to);
      setHovered(null);
      const startMonth = startOfMonth(committedFrom.date ?? new Date());
      const endMonth = committedTo.date === null ? null : startOfMonth(committedTo.date);
      setViewLeft(startMonth);
      setViewRight(endMonth !== null && isAfter(endMonth, startMonth) ? endMonth : addMonths(startMonth, 1));
    }
    setOpen(next);
  };

  const pick = (day: Date) => {
    const choosingStart = draftParsedFrom.date === null || draftParsedTo.date !== null
      || isBefore(day, draftParsedFrom.date);
    if (choosingStart) {
      // Start a fresh range: set the from edge (keeping its chosen time) and clear the to edge.
      setDraftFrom(buildValue(day, draftParsedFrom.time));
      setDraftTo("");
    } else {
      setDraftTo(buildValue(day, draftParsedTo.time));
    }
  };

  const setFromTime = (time: string) => {
    if (draftParsedFrom.date !== null && time !== "") {
      setDraftFrom(buildValue(draftParsedFrom.date, time));
    }
  };
  const setToTime = (time: string) => {
    if (draftParsedTo.date !== null && time !== "") {
      setDraftTo(buildValue(draftParsedTo.date, time));
    }
  };

  const apply = () => {
    onChange(draftFrom, draftTo);
    setOpen(false);
  };
  const clearDraft = () => {
    setDraftFrom("");
    setDraftTo("");
    setHovered(null);
  };

  // Half a range (a start with no end) cannot be applied: the form only accepts a full window or an empty one.
  const draftIncomplete = draftParsedFrom.date !== null && draftParsedTo.date === null;
  const draftDirty = draftFrom !== from || draftTo !== to;

  const summary = committedFrom.date === null
    ? "Select a date range"
    : committedTo.date === null
      ? describe(committedFrom)
      : `${describe(committedFrom)}  →  ${describe(committedTo)}`;

  return (
    <Popover open={open} onOpenChange={openChange}>
      <PopoverTrigger asChild>
        <button
          type="button"
          disabled={disabled}
          data-testid={testId}
          className={cn(
            "flex h-8 w-full items-center gap-2 rounded-md border border-input bg-transparent px-3 text-[13px]",
            "shadow-xs outline-none transition-colors focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50",
            "disabled:cursor-not-allowed disabled:opacity-50",
            committedFrom.date === null && "text-muted-foreground",
          )}
        >
          <CalendarRange className="size-4 shrink-0 text-muted-foreground" />
          <span className="truncate">{summary}</span>
        </button>
      </PopoverTrigger>
      <PopoverContent className="w-auto p-3" align="start" onMouseLeave={() => setHovered(null)}>
        <div className="mb-2 text-center text-[11px] text-muted-foreground">
          Click a start, then an end. Nothing is applied until you confirm.
        </div>
        <div className="flex gap-4">
          <div className="w-[15.5rem]">
            <MonthNav month={viewLeft} onChange={setViewLeft} />
            <MonthGrid
              month={viewLeft}
              start={draftParsedFrom.date} end={draftParsedTo.date} hovered={hovered}
              onPick={pick} onHover={setHovered}
            />
          </div>
          <div className="w-[15.5rem]">
            <MonthNav month={viewRight} onChange={setViewRight} />
            <MonthGrid
              month={viewRight}
              start={draftParsedFrom.date} end={draftParsedTo.date} hovered={hovered}
              onPick={pick} onHover={setHovered}
            />
          </div>
        </div>
        <div className="mt-3 flex items-end gap-3 border-t border-border pt-3">
          <div className="flex flex-1 flex-col gap-1">
            <Label className="text-xs font-normal text-muted-foreground">From time</Label>
            <Input
              type="time" className="h-8" value={draftParsedFrom.time}
              onChange={(e) => setFromTime(e.target.value)}
              disabled={draftParsedFrom.date === null}
              data-testid={testId ? `${testId}-from-time` : undefined}
            />
          </div>
          <div className="flex flex-1 flex-col gap-1">
            <Label className="text-xs font-normal text-muted-foreground">To time</Label>
            <Input
              type="time" className="h-8" value={draftParsedTo.time}
              onChange={(e) => setToTime(e.target.value)}
              disabled={draftParsedTo.date === null}
              data-testid={testId ? `${testId}-to-time` : undefined}
            />
          </div>
        </div>
        <div className="mt-3 flex items-center gap-2 border-t border-border pt-3">
          <Button
            type="button" variant="ghost" size="sm" className="h-8"
            onClick={clearDraft}
            disabled={draftParsedFrom.date === null}
            data-testid={testId ? `${testId}-clear` : undefined}
          >
            Clear
          </Button>
          <span className="grow" />
          <Button
            type="button" variant="ghost" size="sm" className="h-8"
            onClick={() => setOpen(false)}
          >
            Cancel
          </Button>
          <Button
            type="button" size="sm" className="h-8"
            onClick={apply}
            disabled={draftIncomplete || !draftDirty}
            data-testid={testId ? `${testId}-apply` : undefined}
          >
            Apply
          </Button>
        </div>
      </PopoverContent>
    </Popover>
  );
}
