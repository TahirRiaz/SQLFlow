import { format, formatDistanceToNow } from "date-fns";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { parseUtc } from "../lib/time";

interface RelativeTimeProps {
  value: string | null | undefined;
  /** Show the actual local time, with the relative "N ago" moved to the tooltip. This is the default: a wall
   * time is what a reader can actually use, where "10 hours ago" or "in about 14 hours" pins to nothing and
   * two instants seconds apart collapse to the same phrase. Pass `absolute={false}` for the rare surface that
   * genuinely wants the relative phrase on its face. */
  absolute?: boolean;
}

/** A UTC instant rendered for humans: the actual local time by default, or the relative "3 minutes ago" when
 * `absolute` is false. Whichever form is not shown becomes the tooltip. Renders a dash for null. */
export function RelativeTime({ value, absolute = true }: RelativeTimeProps) {
  if (!value) {
    return <span>-</span>;
  }

  const date = parseUtc(value);
  const relative = formatDistanceToNow(date, { addSuffix: true });
  // Local time, seconds included: a run's phases can be seconds apart, and the seconds are the point.
  const clock = format(date, "MMM d, HH:mm:ss");
  const utc = date.toISOString().replace("T", " ").replace(/\.\d+Z$/, " UTC");

  return (
    <Tooltip>
      <TooltipTrigger asChild>
        <span className={absolute ? "font-mono tabular-nums" : undefined}>{absolute ? clock : relative}</span>
      </TooltipTrigger>
      <TooltipContent className="font-mono text-[11px]">
        {absolute ? `${relative} · ${utc}` : utc}
      </TooltipContent>
    </Tooltip>
  );
}
