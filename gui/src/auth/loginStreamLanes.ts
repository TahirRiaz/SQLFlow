// The picture on the login page's brand panel: the data a product takes, drawn as labelled records that travel along
// curved lanes to a set of nested rings that echo the logo mark. Each lane meets the outermost ring at a height of its
// own, where its label folds into a point of light that travels on into the rings. Kept apart from the page, with nothing
// to import, so the unit suite reads it.

/** The picture's coordinate space. The panel scales it to cover the box it is given, cropping the edges if it must. */
export const VIEW_WIDTH = 800;
export const VIEW_HEIGHT = 560;

/** The one blue, lightened to read on the navy: the records' mark and the pitch's second line. */
export const LOGIN_ACCENT = "color-mix(in srgb, var(--brand-blue) 42%, white)";

/**
 * The navy field the sign-in page is set on, in both modes, and the home page's hero with it: lightest at the top left,
 * deepening toward the bottom right, so what sits on it reads as one surface with no seam.
 */
export const BRAND_FIELD =
  "linear-gradient(160deg, var(--brand-navy) 0%, var(--brand-navy-deep) 50%, color-mix(in srgb, var(--brand-navy-deep) 72%, black) 100%)";

export interface Point {
  x: number;
  y: number;
}

/** One of the nested rings: an upright ellipse, each one further right, smaller and fainter than the one before. */
export interface Ring {
  cx: number;
  cy: number;
  rx: number;
  ry: number;
  opacity: number;
}

/** A lane a record travels: a curve from beyond the left edge to the outermost ring, then straight on into the rings. */
export interface StreamLane {
  /** The curve's start, its two control points, and its end on the outermost ring. */
  curve: readonly [Point, Point, Point, Point];
  /** The SVG path: the curve, then the line from the outermost ring to the point the rings recede to. */
  path: string;
  /** How far along the path, as a fraction of its length, the label starts to fold into a point. */
  foldFrom: number;
  /** How far along the path, as a fraction of its length, the lane reaches the outermost ring: the label is gone. */
  mouthAt: number;
  /** One trip along the lane, in seconds. */
  seconds: number;
}

/** One record on its lane. */
export interface StreamChip {
  label: string;
  lane: number;
  /** When the trip starts, in seconds, before the picture is drawn: negative, so the lanes are full from the start. */
  beginSeconds: number;
  /** Where the record stands when motion is reduced, or null for a record that is then not drawn. */
  rest: Point | null;
}

/** The outermost ring's centre. */
export const MOUTH: Point = { x: 556, y: 280 };

/** Where the rings recede to, and where a record's trip ends. */
export const VANISH: Point = { x: 708, y: 280 };

export const RINGS: readonly Ring[] = [
  { cx: 556, cy: 280, rx: 66, ry: 214, opacity: 0.3 },
  { cx: 598, cy: 280, rx: 54, ry: 176, opacity: 0.25 },
  { cx: 633, cy: 280, rx: 44, ry: 144, opacity: 0.21 },
  { cx: 662, cy: 280, rx: 36, ry: 118, opacity: 0.17 },
  { cx: 686, cy: 280, rx: 29, ry: 97, opacity: 0.14 },
  { cx: 705, cy: 280, rx: 24, ry: 80, opacity: 0.11 },
];

/** Where each lane starts, beyond the left edge, top to bottom. */
const LANE_STARTS = [36, 124, 212, 340, 432, 522];

/** One trip per lane, in seconds: slow, and different on each lane, so the records never march in step. */
const LANE_SECONDS = [19, 23, 17, 21, 18, 24];

/** How far into its first trip each lane is when the picture is drawn, in seconds. */
const LANE_PHASES = [0, 7.3, 3.1, 11.2, 5.6, 9.4];

/** How many records travel a lane at once, spaced evenly along it. */
const CHIPS_PER_LANE = 2;

/** Where along its curve (by the curve's parameter) each lane's first record stands when motion is reduced. */
const LANE_RESTS = [0.62, 0.38, 0.74, 0.5, 0.3, 0.56];

const START_X = -170;

/** How far apart the lanes meet the outermost ring, against how far apart they start: close enough to read as one stream. */
const MOUTH_SPREAD = 0.55;

/** How long, in the picture's units, a label takes to fold into its point before the outermost ring. */
const FOLD_LENGTH = 96;

/** The point at `t` (0 to 1) along a cubic Bezier curve. */
export function pointOnCurve(curve: readonly [Point, Point, Point, Point], t: number): Point {
  const [p0, p1, p2, p3] = curve;
  const u = 1 - t;
  const a = u * u * u;
  const b = 3 * u * u * t;
  const c = 3 * u * t * t;
  const d = t * t * t;
  return {
    x: a * p0.x + b * p1.x + c * p2.x + d * p3.x,
    y: a * p0.y + b * p1.y + c * p2.y + d * p3.y,
  };
}

function curveLength(curve: readonly [Point, Point, Point, Point]): number {
  const steps = 96;
  let length = 0;
  let previous = curve[0];
  for (let step = 1; step <= steps; step++) {
    const next = pointOnCurve(curve, step / steps);
    length += Math.hypot(next.x - previous.x, next.y - previous.y);
    previous = next;
  }

  return length;
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}

function lane(startY: number, seconds: number): StreamLane {
  // The lane meets the outermost ring on its stroke, at the lane's own height, and the label is gone by then.
  const y = MOUTH.y + (startY - MOUTH.y) * MOUTH_SPREAD;
  const outer = RINGS[0];
  const entry: Point = { x: outer.cx - outer.rx * Math.sqrt(1 - ((y - outer.cy) / outer.ry) ** 2), y };
  const curve: [Point, Point, Point, Point] = [
    { x: START_X, y: startY },
    { x: START_X + 330, y: startY },
    { x: entry.x - 220, y: entry.y },
    entry,
  ];
  const [p0, p1, p2, p3] = curve.map((point) => `${round(point.x)} ${round(point.y)}`);
  const toMouth = curveLength(curve);
  const length = toMouth + Math.hypot(VANISH.x - entry.x, VANISH.y - entry.y);
  return {
    curve,
    path: `M ${p0} C ${p1}, ${p2}, ${p3} L ${round(VANISH.x)} ${round(VANISH.y)}`,
    foldFrom: (toMouth - FOLD_LENGTH) / length,
    mouthAt: toMouth / length,
    seconds,
  };
}

/** The lanes, top to bottom. */
export const LANES: readonly StreamLane[] = LANE_STARTS.map((startY, index) => lane(startY, LANE_SECONDS[index]));

/**
 * The records to draw for `labels`, the kinds of data a product takes: one per label, at least one on every lane and at
 * most as many as the lanes carry. Every lane's first record takes the first labels in order, and the second records
 * the next ones; with fewer labels than lanes, the labels repeat, and with more than the lanes carry, the last ones are
 * left out. No labels, no records.
 */
export function streamChips(labels: readonly string[]): StreamChip[] {
  const named = labels.map((label) => label.trim()).filter((label) => label !== "");
  if (named.length === 0) {
    return [];
  }

  const count = Math.min(LANES.length * CHIPS_PER_LANE, Math.max(LANES.length, named.length));
  const chips: StreamChip[] = [];
  for (let index = 0; index < count; index++) {
    const pass = Math.floor(index / LANES.length);
    const laneIndex = index % LANES.length;
    const each = LANES[laneIndex];
    const into = (LANE_PHASES[laneIndex] + (pass * each.seconds) / CHIPS_PER_LANE) % each.seconds;
    chips.push({
      label: named[index % named.length],
      lane: laneIndex,
      beginSeconds: -into,
      rest: pass === 0 ? pointOnCurve(each.curve, LANE_RESTS[laneIndex]) : null,
    });
  }

  return chips;
}
