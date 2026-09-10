import { useMemo } from "react";
import type {
  DataStream, StreamDetectorName, StreamPattern, StreamStage, StreamStatus,
} from "../../api/types";
import { useThemeMode } from "../../theme/ThemeModeContext";

/** The detectors as a person reads them, in the order the ensemble ranks them: the three that answer "is data
 * arriving at all" first, then the three that measure how much. */
export const detectorLabels: Record<StreamDetectorName, string> = {
  silence: "No data now",
  nullDays: "Missing days",
  cadence: "Not running",
  rateChange: "Volume rate",
  levelShift: "Level shift",
  volumeOutlier: "Outlying day",
};

/** What each detector actually tests, shown beside its verdict so a reader can weigh the evidence rather than
 * take the label on trust. */
export const detectorMethods: Record<StreamDetectorName, string> = {
  silence: "Gap since the last load against this stream's own tolerated gap",
  nullDays: "Empty days against the median week's delivery rate",
  cadence: "Gap since the last run against its execution cadence",
  rateChange: "Overdispersion-adjusted count rate, recent slice against baseline",
  levelShift: "PELT change point over the residuals",
  volumeOutlier: "Generalized ESD over trend-and-weekday residuals",
};

/** The pipeline stages in the words the estate uses for them, and what a failure at each one MEANS, which is
 * the whole reason the stage is shown: the same "no data" reads as the vendor's problem at one stage and ours
 * at the next. */
export const stageLabels: Record<StreamStage, string> = {
  integration: "Vendor fetch",
  "file-ingestion": "File ingestion",
  archive: "Load to archive",
  derived: "Derived",
};

export const stageMeaning: Record<StreamStage, string> = {
  integration: "Fetches from the vendor. Nothing here usually means the vendor sent nothing.",
  "file-ingestion": "Lands what was fetched. A failure here is ours, on their data.",
  archive: "Loads the landed data into the archive (silver). A failure here is ours.",
  derived: "Built from the archive onwards. Entirely our own processing.",
};

/** The verdicts in words an operator uses, not words the algorithm uses. "Degraded" and "watch" describe how
 * sure the detector is; these describe what is actually happening to the data. */
export const statusLabels: Record<StreamStatus, string> = {
  stalled: "Stopped",
  degraded: "Missing data",
  watch: "Worth a look",
  healthy: "OK",
  "insufficient-history": "Too new",
};

/** Verdicts worst-first, for ordering a group's summary so the reason to open it comes first. */
export function statusRank(status: StreamStatus): number {
  switch (status) {
    case "stalled":
      return 0;
    case "degraded":
      return 1;
    case "watch":
      return 2;
    case "healthy":
      return 3;
    default:
      return 4;
  }
}

/**
 * The headline for one row: what is wrong with this table, as a sentence with its number in it. The category
 * alone ("gap-days") says nothing to a reader, and the raw summary is a paragraph, so this is the middle
 * ground the board needs.
 */
export function whatIsWrong(stream: DataStream): string {
  const days = stream.profile.daysSinceLastLoad;
  const missed = stream.profile.unexpectedNullDays;
  switch (stream.category) {
    case "stalled":
      return days === null ? "No data ever" : `No data for ${formatDayCount(days)}`;
    case "failing":
      return days === null ? "Failing, no data" : `Failing, no data for ${formatDayCount(days)}`;
    case "gap-days":
      return `Did not run on ${missed} day${missed === 1 ? "" : "s"}`;
    case "idle-days":
      return `${missed} day${missed === 1 ? "" : "s"} with nothing new`;
    case "not-running":
      return "Flow stopped running";
    case "less-than-normal":
      return "Less data than usual";
    case "more-than-normal":
      return "More data than usual";
    case "never-loaded":
      return "Has never loaded";
    case "insufficient-history":
      return "Too new to judge";
    default:
      return "Loading normally";
  }
}

/** How often the table normally loads, spelled out. The shape name alone ("several-days-a-week") is the
 * algorithm's vocabulary; this is a person's. */
export function howOftenItLoads(pattern: StreamPattern, expectedGapDays: number): string {
  switch (pattern.shape) {
    case "daily":
      return "Every day";
    case "weekdays":
      return "Weekdays only";
    case "weekly":
      return pattern.loadDays.length === 1 ? `Every ${pattern.loadDays[0]}` : "Once a week";
    case "several-days-a-week":
      return `${pattern.loadDays.length} days a week`;
    case "periodic":
      return `Every ${formatDayCount(expectedGapDays)}`;
    default:
      return "No fixed rhythm";
  }
}

/**
 * How much to trust a finding, in a word. The underlying number is how many of the six independent checks
 * agreed, which is meaningful but not something a reader should have to interpret: two agreeing is the bar a
 * finding has to clear before it can be called critical, so that is where "high" starts.
 */
export function confidenceLabel(stream: DataStream): { label: string; title: string } | null {
  if (stream.agreeingDetectors === 0) {
    return null;
  }

  const title = `${stream.agreeingDetectors} of ${stream.signals.length} independent checks agree`;
  if (stream.agreeingDetectors >= 2) {
    return { label: stream.confidence >= 0.5 ? "High" : "Medium", title };
  }

  return { label: "Low", title: `${title}; a single check is a lead, not a finding` };
}

/**
 * Chart ink from the app's own custom properties (DESIGN.md 3.4), re-read when the theme flips. Slot 1 carries
 * the one data series; the expectation line and the axes wear recessive text ink because a reference is not a
 * second series; a flagged day wears the reserved destructive status color, which is why it always ships with
 * a label rather than standing on color alone.
 */
export function useChartInk() {
  const { mode } = useThemeMode();
  return useMemo(() => {
    const token = (name: string) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
    return {
      series: token("--chart-1"),
      flagged: token("--destructive"),
      axis: token("--muted-foreground"),
      grid: token("--border"),
      cursor: token("--muted"),
    };
  }, [mode]);
}

/** Row counts, compact enough for a table cell or an axis tick when asked. */
export function formatRows(rows: number, compact = false): string {
  if (!Number.isFinite(rows)) {
    return "-";
  }

  const value = Math.round(rows);
  if (!compact) {
    return value.toLocaleString();
  }

  if (Math.abs(value) >= 1_000_000) {
    return `${(value / 1_000_000).toFixed(1)}M`;
  }

  return Math.abs(value) >= 1_000 ? `${(value / 1_000).toFixed(0)}k` : String(value);
}

/** A bare span of days: "2 days", "1 day". */
export function formatDayCount(days: number): string {
  const whole = Math.round(days);
  return whole === 1 ? "1 day" : `${whole} days`;
}

/** A day count as an age. Null means it has never happened, which is not the same as "0 days ago". */
export function formatDays(days: number | null): string {
  if (days === null) {
    return "Never";
  }

  if (days < 1) {
    return "Today";
  }

  return days < 2 ? "Yesterday" : `${Math.round(days)} days ago`;
}
