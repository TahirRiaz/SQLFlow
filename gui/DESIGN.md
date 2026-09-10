# SQLFlow GUI Design Book

The binding reference for every screen in the SQLFlow GUI. Any page, component, or change that does not
follow this book is wrong, even if it works. When a rule here conflicts with older code, the book wins and
the code gets ported.

## 1. Product stance

SQLFlow's GUI is a **workbench, not a website**. The model is VS Code: a dense, keyboard-friendly,
panel-based tool an operator keeps open all day. Every design choice follows from that stance:

- **Density over airiness.** Operators scan tables of runs, flows, and objects. Compact rows, small
  controls, 13px base type. Whitespace is spent on grouping, not on padding.
- **Feedback for everything.** No action is silent. Every mutation acknowledges (pending state, then a
  toast); every long process shows live progress; every completion notifies. See section 8.
- **Panels over popups.** Popup dialogs are reserved for two narrow cases (section 7.4). Everything else
  lives in the page, a side sheet, or the bottom panel.
- **Data wears mono.** Identifiers, counts, timestamps, SQL, and paths render in the mono face. Prose and
  labels render in the sans face.
- **Dark first.** Both modes ship and are equally polished, but design decisions are made on the dark
  theme first; it is the workbench default.

## 2. Identity

- Product name: **SQLFlow** (logo in `public/brand/`).
- Brand anchors: deep navy `#283e56` (`--brand-navy`), navy-deep `#1b2c40`, warm cream `#fdf3e7`.
- The navy lives in the chrome (activity bar, title bar); the blue accent carries interaction; the cream
  appears only in the logo mark. No other decorative color.

## 3. Color system

All colors are CSS custom properties defined in `src/index.css`. Components never use raw hex values;
they use the semantic Tailwind utilities (`bg-background`, `text-muted-foreground`, `border-border`,
`bg-primary`, ...). Dark mode is keyed on the `dark` class on `<html>`.

### 3.1 Semantic surfaces and text

| Token | Light | Dark | Use |
|---|---|---|---|
| `background` | `#f6f8fb` | `#111a28` | editor area behind pages |
| `card` | `#ffffff` | `#16202f` | cards, tables, elevated surfaces |
| `popover` | `#ffffff` | `#16202f` | menus, popovers, palettes |
| `foreground` | `#1d2733` | `#dce6f2` | primary text |
| `muted-foreground` | `#5b6b7f` | `#8b9db3` | secondary text, table headers |
| `border` | `#dfe5ee` | `#223146` | hairlines everywhere |
| `input` | `#ccd6e3` | `#2a3b53` | form control borders |
| `primary` | `#2f6fce` | `#4d9dff` | actions, links, selection, focus ring |
| `secondary` | `#e8edf4` | `#1c2839` | secondary buttons, quiet chips |
| `muted` | `#edf1f6` | `#1a2536` | subtle fills, skeletons, hover washes |
| `accent` | `#e4ecf6` | `#1d2c40` | hover/selected rows and menu items |
| `destructive` | `#d1242f` | `#e5484d` | destructive actions only |

### 3.2 Status colors (reserved)

Run/entity states only. Never used as chart series, never decorative. Always paired with an icon or
label; color never carries meaning alone.

| Token | Light | Dark | States |
|---|---|---|---|
| `success` | `#1a7f37` | `#3fb950` | succeeded, online, active, enabled |
| `warning` | `#9a6700` | `#d29922` | queued, degraded, paused, rate-limited |
| `info` | `#0969da` | `#58a6ff` | running, syncing, informational |
| `destructive` | `#d1242f` | `#e5484d` | failed, offline, error |
| `muted-foreground` | | | cancelled, skipped, disabled, unknown |

### 3.3 Workbench chrome

| Token | Light | Dark |
|---|---|---|
| `activity-bar` | `#283e56` (brand navy, both modes' chrome anchor) | `#0c1420` |
| `side-bar` | `#eef2f7` | `#0f1826` |
| `tab-bar` | `#eef2f7` | `#0c1420` |
| `tab-active` | `#ffffff` | `#111a28` |
| `panel` | `#ffffff` | `#0f1826` |
| `status-bar` | `#2f6fce` (brand blue, both modes) | `#2f6fce` |

### 3.4 Chart palette (validated)

Categorical series use `--chart-1` ... `--chart-8`, assigned in **fixed slot order, never cycled, never
re-assigned when a filter changes the series count**. This exact order passed the dataviz validator's
six checks on SQLFlow's card surfaces (light `#ffffff`, dark `#16202f`) in July 2026; do not re-order or
substitute steps without re-running the validator.

| Slot | Light | Dark |
|---|---|---|
| 1 blue | `#2a78d6` | `#3987e5` |
| 2 green | `#008300` | `#008300` |
| 3 magenta | `#e87ba4` | `#d55181` |
| 4 yellow | `#eda100` | `#c98500` |
| 5 aqua | `#1baf7a` | `#199e70` |
| 6 orange | `#eb6834` | `#d95926` |
| 7 violet | `#4a3aa7` | `#9085e9` |
| 8 red | `#e34948` | `#e66767` |

Relief rule: in light mode, slots 3, 4, and 5 sit below 3:1 against white, so any chart using them ships
visible direct labels or an adjacent table view. Chart rules beyond color: one axis per chart (never
dual-axis); a legend whenever there are 2+ series; hover tooltips on every plot; text in text tokens,
never in series colors; status colors never appear as series.

## 4. Typography

- Sans: `Inter Variable` (UI text, labels, prose). Mono: `JetBrains Mono Variable` (data).
- Base size **13px** (set on `body`). Scale: 11px (`text-[11px]`) chrome captions and uppercase group
  headers, 12px (`text-xs`) secondary/table meta, 13px body and controls, 14px (`text-sm`) emphasized
  body, 16px (`text-base`) section titles, 18px (`text-lg`) page titles, 24px (`text-2xl`) KPI values.
- Page titles `font-semibold`; section titles `font-medium`; never `font-bold` in body content.
- Mono applies to: ids, hashes, row counts, durations, timestamps, schema/object/flow names, file paths,
  SQL, YAML, connection strings. Aligned numeric columns also get `tabular-nums`.
- Uppercase (`uppercase tracking-wider text-[11px] font-medium text-muted-foreground`) is reserved for
  chrome group headers (side bar sections, panel titles), never for data or buttons.

## 5. Spacing, density, and shape

- 4px grid. Standard gaps: 4/8/12/16/24. Page padding: `p-4 md:p-6`; content measure capped at 1600px.
- Control height **32px** (`h-8`, button `size="sm"`, inputs `className="h-8"`) for toolbar controls,
  filters, and forms; 24px (`size="xs"`) for compact in-row actions. Never the 36px defaults.
- Table rows 32px; header row 32px with `text-xs font-medium text-muted-foreground` uppercase-free.
- **Icon sizes are set by the control, never per call site.** 16px (`size-4`) is the house glyph: nav
  items, toolbar buttons, and every icon-only button down to the 24px `size="icon-xs"` row action, which
  keeps a 16px glyph in its 24px box. 14px (`size-3.5`) is the floor, used only where a glyph sits inside
  text at 11-12px: status pills, `Badge`, the status bar, and inline log/trace markers. 12px glyphs are
  not legible at this stroke weight and are not used. A page should not hand-set an icon size; if one
  looks wrong, fix the variant in `button.tsx`, `badge.tsx`, or `StatusBadge.tsx` so every call site moves
  together.
- Radius: `--radius` 6px. Cards/popovers `rounded-lg`, controls `rounded-md`, badges `rounded-sm` or
  full. Nothing larger; the workbench look is tight, not bubbly.
- Shadows are near-absent: popovers/menus get the shadcn default; cards get borders, not shadows.

## 6. The workbench layout

Fixed viewport frame, no page scroll; only the editor area and panel scroll internally.

```
+------------------------------------------------------------------+
| Title bar (36px): brand |            palette, theme, account      |
+---+--------------------------------------------------------------+
| A | Side bar (resizable | Tab strip (35px)                       |
| c | 200-320px):         +----------------------------------------+
| t | global search,      | Editor: the routed page                |
| B | then grouped nav,   |   (scrolls internally)                 |
| a | collapsible sections+----------------------------------------+
| r |                     | Bottom panel (resizable, closable)     |
+---+---------------------+----------------------------------------+
| Status bar (22px, brand blue)                                    |
+------------------------------------------------------------------+
```

- **Title bar** (`h-9`, `bg-activity-bar`): logo + product name left, everything else right: the command
  palette button (testid `command-palette-button`, a `Ctrl K` chip), the theme toggle (testid
  `theme-toggle`) and the account menu (testids `account-menu-button`, `account-subject`,
  `account-tokens`, `account-notifications`, `account-logout`). Nothing floats in the middle: the bar
  carries chrome only, never a content control. On mobile a hamburger opens the nav sheet.
- **Activity bar** (`w-12`): one icon per nav group, Settings gear pinned at the bottom. Active group
  shows a 2px left indicator + full-intensity icon. Click: reveal the group in the side bar; click on
  the active group toggles the side bar. Tooltips on the right.
- **Side bar** (resizable 12-35%, persisted): the global search first (`SearchInput`, testid
  `global-search`, Enter navigates to `/search?q=`), pinned above the scroll area on its own hairline so a
  long nav never scrolls it away; then all nav groups as collapsible sections (uppercase 11px headers),
  items 28px tall with 16px lucide icons, nav testids preserved (`nav-runs`, ...). Selection follows the
  longest-prefix rule; selected item gets `bg-sidebar-accent` plus a 2px accent inset. The mobile nav
  sheet renders the same search over the same sections.
- **Tab strip** (`h-[35px]`, `bg-tab-bar`): one tab per visited route; active tab wears `bg-tab-active`,
  a 1px top accent line, and its close button always visible; inactive tabs reveal close on hover.
  Middle-click closes. Tabs keep their label (max `w-52`, never squeezed to an icon), so a long strip
  scrolls horizontally and the active tab is scrolled into view on navigation. Right-click opens the tab
  menu: Close, Close Others, Close to the Left, Close to the Right, Close All, Copy Link. The strip ends in an overflow
  button (testid `tabs-overflow-menu`) listing every open tab plus Close Others / Close All. Closing the
  last tab falls back to the dashboard, so the strip is never empty. Tabs persist per browser session.
  Detail pages set real titles via `useTabTitle(...)` once data loads.
- **Editor**: the routed page inside a scroll container with the measure cap. Pages never add their own
  outer padding.
- **Bottom panel** (resizable 15-70%, closable): live surfaces opened by features through `usePanel()`
  (e.g. a run's streaming trace). Header: uppercase 11px title + close. One content at a time.
- **Status bar** (`h-[22px]`, `bg-status-bar`, white text, 11px): left segments show live workload
  (running/queued run counts, click-through to filtered Runs) and the rate-limit pause (testid
  `rate-limit-banner`); right segments show the signed-in subject and role. Segments are flat text +
  14px icons with hover wash; no borders.
- **Command palette** (Ctrl+K, also Ctrl+Shift+P): cmdk dialog listing every nav destination grouped as
  in the side bar, plus a catalog-search action. Fuzzy filter, Enter navigates.

## 7. Components

Shared components live in `src/components/` (app-level) and `src/components/ui/` (shadcn primitives,
never hand-edited except where this book says so). MUI, emotion, and notistack are forbidden imports.

### 7.1 Page scaffold

`Page` + `PageHeader`: testid `page-<name>` preserved; header carries title (18px semibold), optional
description (13px muted), and right-aligned toolbar actions (small buttons). Below the header, optional
`FilterBar`. No breadcrumbs in v1 except detail pages: parent link + entity name. Filter controls are 32px
(`h-8`): text `Input`s for free text, `Select` for a short fixed set, and `FilterCombobox` (a searchable
popover, the empty string meaning "no filter") for a filter over many values like the Runs board's schedule
and batch dropdowns, where a plain `Select` would not scroll usably. Free-text search over a list or tree
goes through `SearchInput` (leading magnifier, trailing clear, filled surface so the field never reads as
empty background); features must not hand-roll that icon/clear arrangement. **Search comes first.** The
free-text field is the leading control of the filter row on every page that has one, ahead of the dropdowns
that narrow it, and it is never pushed to the far right with `ml-auto`: typing a name is the fastest way
into a long list, so it is the first thing the eye and the tab order reach. The same holds for a plain
`Input` used as free text (the Runs board's flow name, the browse page's name filter). A section heading
never shares the search's row: it sits on its own line above, so the field starts at the left edge of its
row exactly as a `FilterBar` does.

### 7.2 Tables

`DataTable` (presentational) and `PagedTable` (server paging) keep their existing prop contracts and
testids (`table-row`, `paged-table`, `group-header-row`, ...). Spec: card surface with border; 32px
rows; hairline row separators; hover `bg-accent/50` on clickable rows; multi-level tree grouping with
chevrons (a `levels` list, each an independently expandable node level above the leaf rows, e.g. the Runs
board's schedule -> batch -> step); skeleton rows while loading; `EmptyState` inside when empty; numeric
columns right-aligned mono tabular; status columns render `StatusBadge`.

### 7.3 StatusBadge

One component maps every domain status (run, node, schedule, user, sync) to {icon, label, status
color}: succeeded = success, failed/error = destructive, running/syncing = info with a spinning
indicator, queued/paused = warning, cancelled/skipped/disabled = muted.

**Two families, never one silhouette.** A status is either an *outcome* (something ran and finished: a
run status, a delivery, a connection test) or a *state* (how an object is configured right now: a
pipeline active, a schedule enabled or paused, a node online, a token revoked). They are told apart by
shape and fill, not only by color, because the pages that show both show them side by side and a green
check meant both "succeeded" and "switched on":

- **Outcome** = filled tinted surface (`bg-<status>/12`), `rounded-full`, result glyphs
  (`CircleCheck`, `CircleX`, `Clock3`, spinning `Loader2`, `Ban`, `SkipForward`).
- **State** = no fill, hairline `ring-1 ring-inset` in the status color, `rounded-md` (pill) or
  `rounded-[6px]` (icon-only), on/off glyphs (`Power`, `PowerOff`, `Pause`, `Wifi`/`WifiOff`).

Badge form: 11px label, 14px icon. Icon-only form (`IconBadge`, used in dense table cells): a 22px
surface around the same 14px glyph, with the word on hover and for assistive tech. Pass
`family="state"` (or use `ActiveBadge`/`ScheduleStateBadge`/`OnlineBadge`/`StatePill`) for a
configuration state; the default is `outcome`. Features must not hand-roll a status pill: a new state
goes through `StatePill` so it cannot drift back onto the outcome check mark.
Testid `status-badge` preserved.

### 7.4 Dialogs, sheets, and the panel

- **AlertDialog** (small modal): only for destructive confirmation (`ConfirmDialog`, testids preserved)
  and for single-field prompts that gate an immediate action. Nothing else is modal.
- **Sheet** (right side, 480-640px): create/edit forms (trigger run, create schedule, create user,
  register source, spawn workers). Sheets are the replacement for every MUI form dialog. Existing
  `*-dialog` testids stay on the sheet content so e2e keeps passing.
- **Bottom panel**: live process output (streaming run trace, sync progress). Never modal.
- **Popover/DropdownMenu**: pickers, row action menus (`user-actions`, ...).

### 7.4a Trace log (`TraceLog`)

The shared terminal-style log behind every trace (run, sync, lineage). Two rules keep a several-thousand
line trace readable:

- **Severity is a glyph, not a tint.** Message text is `foreground` (muted for trace/debug) at every
  level; a warning or error line is marked by a 14px `TriangleAlert` / `OctagonAlert` in the status color
  ahead of the text, and an error row takes a `bg-destructive/6` wash. Tinting whole lines makes a trace
  of 2000 warnings one flat block of amber in which the one error disappears. Status color stays for the
  counts, the problems band, and these glyphs.
- **Line detail is click-only.** Each row truncates to one line and ends in a `Maximize2` button (testid
  `trace-line-expand`, revealed on row hover/focus) that opens a popover with the full message, the SQL
  pretty-printed, any error, and Copy. No hover card: a hover trigger on a dense scrolling list fires on
  every row the pointer crosses and flashes a string of cards nobody asked for.

### 7.5 Forms

react-hook-form + zod (already in place) with shadcn `Input`, `Select`, `Checkbox`, `Switch`,
`Textarea`, `Label`. Field errors inline under the control (12px destructive). Submit buttons show a
pending spinner and disable while the mutation is in flight. Every form field keeps its testid.

### 7.6 Code and data views

`CodeView` renders Monaco with the workbench theme (dark: `vs-dark` on `--card`; light: `vs`), 12px
JetBrains Mono, no shadows, border hairline. YAML/SQL always in `CodeView`, never in a `<pre>`.

### 7.7 KPI tiles and charts

`KpiCard`: card with 11px muted uppercase label, 24px semibold value (mono when numeric), optional
delta with icon + success/destructive text, optional sparkline in slot-1 blue. Charts follow section
3.4 and the dataviz skill's rules (form first, hover tooltips, legends for 2+ series, one axis).

### 7.8 Tooltips, and the reference components

Two tooltip surfaces, one component (`ui/tooltip.tsx`, `variant`):

- **`chip`** (default): the inverted micro-label with an arrow, for a word or two. Every icon-only button
  wears one (section 10). Unchanged from shadcn.
- **`panel`**: a popover-surfaced card for a value too long or too structured for a pill. Wraps, preserves
  line breaks, left-aligns, optional 11px uppercase caption, no arrow. Reach for it through `RichTooltip`,
  never by hand: it sets `disableHoverableContent` and `pointer-events-none`, without which a panel opened
  over a dense grid stays up after the pointer leaves and swallows the hovers of the rows it covers.

A panel is display only. A Radix tooltip closes when the pointer leaves its trigger, so a button inside one
could never be clicked: **actions live in the cell beside the trigger**, which is what the reference
components below do. This is the same reasoning as 7.4a, and its opposite conclusion for the trace log
stands: a hover reveal is right on a bounded grid of tens of rows, wrong on a scrolling log of thousands,
where detail stays click-only.

**Truncate at a pixel cap, never at the column.** Table cells are `whitespace-nowrap` on an auto layout, so
an uncapped cell does not clip: it widens the table until the columns to its right leave the viewport. Every
free-text cell therefore renders through a reference component, each of which caps its own width and reveals
the full value in a panel:

| Component | Shows | Reveals | Copies |
|---|---|---|---|
| `TruncatedText` | the value, clipped at `maxWidth` | on hover, when actually clipped | opt-in (`copy`) |
| `PathRef` | the file name only | the whole path | always |
| `ConnectionRef` | the `${env:}`/`${keyvault:}` identifier | the whole reference | always |
| `LinkRef` | an open-in-new-tab glyph | the whole URL | always |
| `NoteRef` | a note glyph, when there is a note | the whole remark | no |

`TruncatedText` attaches its panel only when the value is genuinely clipped, is multi-line, or was given a
`title`. A short value already fully on screen gets no hover at all, so dragging a pointer across a table
does not trail panels repeating text the reader can already see.

**A glyph beats a clipped opener.** Where every value in a column starts the same way, the thirty characters
a cell can show are the same string down the whole column: `NoteRef` shows that a remark EXISTS and hands the
text to the panel, which is what scanning actually needs and costs a seventh of the width.

**A bare count needs to say what it counts.** Two integer columns side by side ("Reads", "Queries") cannot be
attributed while the eye is on a row, since neither number carries its unit and the header is a row away:
`7  7` reads as one value rendered twice. A count column therefore leads with the glyph for the thing it
counts and gives the words on hover and to assistive tech, rather than repeating the unit in every row.

`LinkRef` is the only way to render a location. A location is free text and is as often a UNC path or a
share as a URL, so only `http(s)` becomes an anchor; anything else gets a muted `Link2Off` glyph and its
copy button, never a link that silently does nothing when clicked. Its `icon` variant keeps a Location
column two glyphs wide whatever the value; `inline` adds the clipped URL for a detail row with the room.

## 8. Feedback rules (non-negotiable)

The old GUI's central failure was silence. These rules bind every feature:

1. **Every mutation acknowledges.** Button enters pending (spinner, disabled) while in flight; on
   success a sonner toast states what happened ("Run 4f2a queued", "Schedule paused"); on failure a
   destructive toast states what failed and why (the API error message), and the page surface shows
   `CorrelationError` where recovery context helps.
2. **Every long process shows liveness.** Anything that outlives the click (runs, syncs, discovery,
   key detection) gets: a live status surface (polling or SSE, via the existing cadence helpers), a
   progress affordance (spinner + counts, or determinate bar when totals are known), and a terminal
   notification (toast on success and on failure) even if the user navigated away. The status bar's
   workload segment reflects running work at all times.
3. **Every data surface has three states.** Loading = skeletons (never spinners-in-space); empty =
   `EmptyState` with an explanation and, where sensible, the next action; error = `CorrelationError`
   with the correlation id. No blank rectangles, ever.
4. **Route transitions** show the slim top progress bar (lazy chunks), never a blank screen.
5. **Toasts** (bottom-right, max 3): success auto-dismiss 5s; errors stay until dismissed; never toast
   what the user is already looking at live (no "loaded 50 rows" noise).

## 9. Motion

120ms ease-out for hovers and reveals, 150-200ms for sheets/panels sliding, no bounce, no scale-up
entrances. `tw-animate-css` utilities only. Anything animating layout must be interruptible.

## 10. Accessibility

Focus visible everywhere (`outline-ring/50` base is set globally); all icon-only buttons carry
`aria-label` + tooltip; keyboard: palette Ctrl+K, tab strip and side bar fully tabbable; contrast:
text >= 4.5:1, chrome text >= 3:1 (the token tables above were chosen for this); status never encoded
by color alone (7.3); tables get real `<table>` semantics (the ui/table primitives).

## 11. Port checklist (apply to every feature page)

- [ ] No `@mui/*`, `@emotion/*`, or `notistack` imports remain in the file.
- [ ] Colors only via semantic utilities; no hex, no `--sf-*` legacy tokens.
- [ ] Every `data-testid` present before the port is present after it.
- [ ] Page scaffold: `Page`/`PageHeader`, page testid, actions as small buttons.
- [ ] Tables through `DataTable`/`PagedTable`; statuses through `StatusBadge`; data in mono.
- [ ] Forms: sheet-based (7.4), pending states, inline errors, success/failure toasts (8.1).
- [ ] Long processes: live surface + terminal toast (8.2). Three data states everywhere (8.3).
- [ ] Detail pages call `useTabTitle` with the entity name.
- [ ] Both themes checked; `tsc -b` and `vite build` clean; e2e for the feature passes.
