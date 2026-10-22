import { useId, useLayoutEffect, useRef, useState, useSyncExternalStore } from "react";
import {
  LANES,
  LOGIN_ACCENT,
  MOUTH,
  RINGS,
  VANISH,
  VIEW_HEIGHT,
  VIEW_WIDTH,
  streamChips,
  type StreamChip,
  type StreamLane,
} from "./loginStreamLanes";

const REDUCED_MOTION = "(prefers-reduced-motion: reduce)";

function subscribeToReducedMotion(onChange: () => void): () => void {
  const query = window.matchMedia(REDUCED_MOTION);
  query.addEventListener("change", onChange);
  return () => query.removeEventListener("change", onChange);
}

function reducedMotion(): boolean {
  return window.matchMedia(REDUCED_MOTION).matches;
}

const PILL_HEIGHT = 26;
const PILL_FONT_SIZE = 12.5;
/** The pill's left edge to the start of its label: the padding, the dot, and the gap after it. */
const PILL_LEAD = 25;
const PILL_TAIL = 12;

/** A label's width before it is measured: a fair average for the sans face at the pill's size. */
function estimatedWidth(label: string): number {
  return label.length * 6.6;
}

/**
 * A record: its label in a rounded outline, led by a dot of the accent, centred on its own origin so it can shrink where
 * it stands. The outline fits the label as drawn, measured once it is on screen and again when the web font arrives.
 */
function Pill({ label }: { label: string }) {
  const text = useRef<SVGTextElement>(null);
  const [labelWidth, setLabelWidth] = useState(() => estimatedWidth(label));

  useLayoutEffect(() => {
    let current = true;
    const measure = () => {
      const node = text.current;
      // A test DOM implements no SVG text metrics, and an element not yet laid out measures nothing: keep the estimate.
      const measured = current && node !== null && typeof node.getComputedTextLength === "function" ? node.getComputedTextLength() : 0;
      if (measured > 0) {
        setLabelWidth(measured);
      }
    };

    measure();
    void document.fonts?.ready.then(measure);
    return () => {
      current = false;
    };
  }, [label]);

  const width = PILL_LEAD + labelWidth + PILL_TAIL;
  const left = -width / 2;
  return (
    <g>
      <rect
        x={left}
        y={-PILL_HEIGHT / 2}
        width={width}
        height={PILL_HEIGHT}
        rx={PILL_HEIGHT / 2}
        fill="white"
        fillOpacity={0.07}
        stroke="white"
        strokeOpacity={0.16}
      />
      <circle cx={left + 13} cy={0} r={3.25} style={{ fill: LOGIN_ACCENT }} />
      <text
        ref={text}
        x={left + PILL_LEAD}
        y={0}
        dominantBaseline="central"
        fontSize={PILL_FONT_SIZE}
        fontWeight={500}
        fill="white"
        fillOpacity={0.86}
      >
        {label}
      </text>
    </g>
  );
}

/**
 * A record on the move, along its lane at an even pace: its label fades in as it enters from the left, and folds into a
 * point of light just before the outermost ring; the point travels on into the rings and fades where they recede. Every
 * record repeats its trip for as long as the page is open.
 */
function MovingChip({ chip, lane }: { chip: StreamChip; lane: StreamLane }) {
  const timing = { dur: `${lane.seconds}s`, begin: `${chip.beginSeconds}s`, repeatCount: "indefinite" } as const;
  const foldFrom = lane.foldFrom.toFixed(3);
  const mouthAt = lane.mouthAt.toFixed(3);
  return (
    <g>
      <animateMotion path={lane.path} calcMode="paced" {...timing} />
      <g opacity={0}>
        <animate attributeName="opacity" values="0;1;1;0;0" keyTimes={`0;0.07;${foldFrom};${mouthAt};1`} {...timing} />
        <g>
          <animateTransform
            attributeName="transform"
            type="scale"
            values="1;1;0.55;0.55"
            keyTimes={`0;${foldFrom};${mouthAt};1`}
            {...timing}
          />
          <Pill label={chip.label} />
        </g>
      </g>
      <g opacity={0} style={{ fill: LOGIN_ACCENT }}>
        <animate attributeName="opacity" values="0;0;1;0" keyTimes={`0;${foldFrom};${mouthAt};1`} {...timing} />
        <animateTransform
          attributeName="transform"
          type="scale"
          values="1;1;1;0.35"
          keyTimes={`0;${foldFrom};${mouthAt};1`}
          {...timing}
        />
        <circle r={9} fillOpacity={0.22} />
        <circle r={3.25} />
      </g>
    </g>
  );
}

/**
 * The brand panel's picture: the kinds of data a product takes (`streams`, from its branding), travelling along curved
 * lanes into nested rings that echo the logo mark, while a slow pulse runs down the rings. Purely decorative, so it is
 * hidden from assistive tech. When the person's system asks for reduced motion, it holds still: one record rests on each
 * lane, and the rings keep their light.
 */
export function LoginStreams({ streams }: { streams: readonly string[] }) {
  const id = useId();
  const still = useSyncExternalStore(subscribeToReducedMotion, reducedMotion, () => true);
  const chips = streamChips(streams);
  const coreGlow = `${id}-core`;
  const mouthGlow = `${id}-mouth`;

  const frame = {
    className: "pointer-events-none absolute inset-0 size-full",
    viewBox: `0 0 ${VIEW_WIDTH} ${VIEW_HEIGHT}`,
    preserveAspectRatio: "xMidYMid slice",
    "aria-hidden": true,
  } as const;

  return (
    <>
      {/* The light behind the rings, on a layer nothing clips: it reaches past the picture's box toward the lockup, the
          pitch and the form, and fades out on its own instead of ending at an edge. */}
      <svg {...frame} className={`${frame.className} overflow-visible`}>
        <defs>
          <radialGradient id={mouthGlow}>
            <stop offset="0%" stopColor="var(--brand-blue)" stopOpacity={0.32} />
            <stop offset="100%" stopColor="var(--brand-blue)" stopOpacity={0} />
          </radialGradient>
          <radialGradient id={coreGlow}>
            <stop offset="0%" stopColor="white" stopOpacity={0.5} />
            <stop offset="30%" stopColor="var(--brand-blue)" stopOpacity={0.45} />
            <stop offset="100%" stopColor="var(--brand-blue)" stopOpacity={0} />
          </radialGradient>
        </defs>
        <circle cx={MOUTH.x + 60} cy={MOUTH.y} r={300} fill={`url(#${mouthGlow})`} />
        <circle cx={VANISH.x} cy={VANISH.y} r={70} fill={`url(#${coreGlow})`} />
      </svg>

      {/* The lanes, the rings and the records, kept inside the picture's box. */}
      <svg {...frame} data-testid="login-streams">

        {LANES.map((lane) => (
          <path
            key={lane.path}
            d={lane.path}
            fill="none"
            stroke="white"
            strokeOpacity={0.09}
            strokeWidth={1}
            strokeDasharray="1 7"
            strokeLinecap="round"
          >
            {!still && <animate attributeName="stroke-dashoffset" from="0" to="-16" dur="1.6s" repeatCount="indefinite" />}
          </path>
        ))}

        {RINGS.map((ring, index) => (
          <ellipse
            key={ring.cx}
            cx={ring.cx}
            cy={ring.cy}
            rx={ring.rx}
            ry={ring.ry}
            fill="none"
            stroke="var(--brand-cream)"
            strokeWidth={1.25}
            strokeOpacity={ring.opacity}
          >
            {!still && (
              <animate
                attributeName="stroke-opacity"
                values={`${ring.opacity};${Math.min(ring.opacity * 2.2, 0.7)};${ring.opacity}`}
                dur="4.8s"
                begin={`${index * 0.4}s`}
                repeatCount="indefinite"
              />
            )}
          </ellipse>
        ))}

        {chips.map((chip, index) => {
          if (!still) {
            return <MovingChip key={index} chip={chip} lane={LANES[chip.lane]} />;
          }

          return chip.rest === null ? null : (
            <g key={index} transform={`translate(${chip.rest.x} ${chip.rest.y})`}>
              <Pill label={chip.label} />
            </g>
          );
        })}
      </svg>
    </>
  );
}
