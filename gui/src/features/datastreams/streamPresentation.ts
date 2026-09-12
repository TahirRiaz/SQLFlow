import { useMemo } from "react";
import type {
  DataStream, StreamDetectorName, StreamPattern, StreamSparkline, StreamStage, StreamStatus,
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

/** What each verdict means in a few words, for the tile that counts it. */
export const statusCaptions: Record<StreamStatus, string> = {
  stalled: "no data arriving",
  degraded: "skipped expected days",
  watch: "one check fired",
  healthy: "loading on pattern",
  "insufficient-history": "not enough history",
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
 * The judged part of the stream's last week, averaged per day, from the row's own sparkline: what actually
 * arrived against what the model expected over the same days. Null when fewer than three judged days exist,
 * because an average of two days is a coincidence with a decimal point.
 */
export function recentWeek(sparkline: StreamSparkline): { actual: number; expected: number } | null {
  const judged = sparkline.rows.length - sparkline.immatureDays;
  const start = Math.max(0, judged - 7);
  const days = judged - start;
  if (days < 3) {
    return null;
  }

  let actual = 0;
  let expected = 0;
  for (let i = start; i < judged; i++) {
    actual += sparkline.rows[i];
    expected += sparkline.expected[i];
  }

  return { actual: actual / days, expected: expected / days };
}

/** A volume finding with its size in it, when the last week points the same way the finding does. When it
 * does not (an outlying day the week has since recovered from), the bare label is the honest headline and the
 * numbers stay in the hover panel and the detail sheet. */
function volumeFinding(stream: DataStream, label: string, direction: "below" | "above"): string {
  const week = recentWeek(stream.sparkline);
  if (week === null || week.expected <= 0) {
    return label;
  }

  const agrees = direction === "below" ? week.actual < week.expected : week.actual > week.expected;
  return agrees
    ? `${label}: ${formatRows(week.actual, true)}/day vs ${formatRows(week.expected, true)} expected`
    : label;
}

/**
 * The headline for one row: what is wrong with this table, as a sentence with its number in it. The category
 * alone ("gap-days") says nothing to a reader, and the raw summary is a paragraph, so this is the middle
 * ground the board needs. Every number carries its unit, because a bare "0" in a grid reads as "0 rows" to
 * one reader and "0 days" to the next.
 */
export function finding(stream: DataStream): string {
  const profile = stream.profile;
  const days = profile.daysSinceLastLoad;
  const missed = profile.unexpectedNullDays;
  switch (stream.category) {
    case "stalled":
      return days === null ? "No data ever" : `No data for ${formatDayCount(days)}`;
    case "failing":
      return days === null
        ? "Every run has failed, no data yet"
        : `Every run has failed, no data for ${formatDayCount(days)}`;
    case "gap-days":
      return `Missed ${missed} of ${profile.expectedDays} expected days`;
    case "idle-days":
      return `Ran with nothing new on ${missed} day${missed === 1 ? "" : "s"}`;
    case "not-running":
      return profile.daysSinceLastRun === null
        ? "Has never run"
        : `Has not run for ${formatDayCount(profile.daysSinceLastRun)}`;
    case "less-than-normal":
      return volumeFinding(stream, "Less data than usual", "below");
    case "more-than-normal":
      return volumeFinding(stream, "More data than usual", "above");
    case "rarely-changes":
      // Not a fault and not a shrug: this table writes only when its source changes, and the last change is
      // the fact a reader wants. An empty chart beside it is the table at rest, not a gap.
      return days === null
        ? "Changes rarely, no change recorded yet"
        : `Changes rarely, last change ${formatDays(days).toLowerCase()}`;
    case "never-loaded":
      return "Has never loaded";
    case "insufficient-history":
      return `Only ${profile.loadedDays} loading day${profile.loadedDays === 1 ? "" : "s"} so far`;
    default:
      return "Loading on pattern";
  }
}

/**
 * How much to trust the finding, as the count it rests on: how many of the six independent checks agree.
 * Two agreeing is the bar a finding has to clear before it can be called critical, so that is where "high"
 * starts; one alone is a lead to look at, unless it is the one case that needs no corroboration (a stream
 * that loaded before the window and never since, which the detector marks critical on its own).
 */
export function evidence(stream: DataStream): string {
  const total = stream.signals.length;
  const agreeing = stream.agreeingDetectors;
  if (stream.status === "insufficient-history") {
    return "not enough history to check";
  }

  if (agreeing === 0) {
    return `all ${total} checks quiet`;
  }

  if (agreeing === 1) {
    return stream.severity === "critical"
      ? `1 of ${total} checks fired, beyond doubt`
      : `1 of ${total} checks fired, a lead not a finding`;
  }

  return `${agreeing} of ${total} checks agree, ${stream.confidence >= 0.5 ? "high" : "medium"} confidence`;
}

/**
 * The recurring delivery in a few words, for the one line a board row can spare: "bigger load every 14 days",
 * "bigger load once a month". The full sentence, with sizes and the next due date, is in the detail sheet.
 */
export function cycleHint(pattern: StreamPattern): string | null {
  const cycle = pattern.cycle;
  if (cycle === null) {
    return null;
  }

  const size = cycle.heavier ? "bigger" : "smaller";
  if (cycle.monthly) {
    return `${size} load once a month`;
  }

  return `${size} load every ${formatDayCount(cycle.periodDays)}`;
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
    case "fortnightly":
      return "Every two weeks";
    case "monthly":
      return "Once a month";
    case "periodic":
      return `Every ${formatDayCount(expectedGapDays)}`;
    default:
      return "No fixed rhythm";
  }
}

/** The rhythm as the caption under a "last data" age: "expected every day", "expected weekdays only", or
 * "no fixed rhythm" for the stream nothing can be expected of. */
export function expectedRhythm(pattern: StreamPattern, expectedGapDays: number): string {
  // A change-driven table has no delivery rhythm to expect: saying "expected every day" of a reference table
  // read every morning is the claim that produced the false findings in the first place.
  if (pattern.changeDriven) {
    return "changes rarely";
  }

  const often = howOftenItLoads(pattern, expectedGapDays);
  if (pattern.shape === "sporadic") {
    return "no fixed rhythm";
  }

  return `expected ${often.charAt(0).toLowerCase()}${often.slice(1)}`;
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

  // One decimal below ten of a unit, none above: "8.9k" and "17k" are both honest to two figures, where a
  // flat "9k" beside "9k expected" reads as no difference at all when there was a 5% one.
  const scaled = (amount: number, unit: string) =>
    `${(Math.abs(amount) < 10 ? amount.toFixed(1) : amount.toFixed(0)).replace(/\.0$/, "")}${unit}`;
  if (Math.abs(value) >= 1_000_000) {
    return scaled(value / 1_000_000, "M");
  }

  return Math.abs(value) >= 1_000 ? scaled(value / 1_000, "k") : String(value);
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
