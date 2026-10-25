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
- Brand anchors: deep navy `#283e56` (`--brand-navy`), navy-deep `#1b2c40`, warm cream `#fdf3e7`, and one
  blue, `#2264c2` (`--brand-blue`).
- The navy lives in the chrome (activity bar, title bar); the blue carries interaction and the status bar; the
  cream appears only in the logo mark. No other decorative color.

## 3. Color system

All colors are CSS custom properties defined in `src/index.css`. Components never use raw hex values;
they use the semantic Tailwind utilities (`bg-background`, `text-muted-foreground`, `border-border`,
`bg-primary`, ...). Dark mode is keyed on the `dark` class on `<html>`.

**Three families, and nothing else.** Every color on a screen is one of: the navy neutrals (surfaces,
hairlines and text, all cut from one hue), the one blue (anything a person acts on, anything in motion), and
the status hues of 3.2. A token that is the same thing as another refers to it (`--ring`, `--info` and
`--activity-bar-badge` are `var(--primary)`) instead of repeating a value that then drifts: two blues a few
points apart side by side read as a mistake, not as a palette.

**Each mode declares itself.** `:root` sets `color-scheme: only light` and `.dark` sets `color-scheme: dark`,
so the controls and scrollbars the browser draws follow the workbench's switch rather than the operating
system's, and `index.html` carries `<meta name="darkreader-lock">`: an extension that recolors pages would
otherwise redraw both themes over the tokens, and neither would be the one designed here.

### 3.1 Semantic surfaces and text

| Token | Light | Dark | Use |
|---|---|---|---|
| `background` | `#f6f8fb` | `#111a28` | editor area behind pages |
| `card` | `#ffffff` | `#16202f` | cards, tables, elevated surfaces |
| `popover` | `#ffffff` | `#16202f` | menus, popovers, palettes |
| `foreground` | `#1d2733` | `#dce6f2` | primary text |
| `muted-foreground` | `#5b6b7f` | `#8b9db3` | secondary text, table headers |
| `border` | `#d7dee8` | `#27364b` | hairlines everywhere |
| `input` | `#b8c2cf` | `#3c4e67` | form control borders, graph node outlines |
| `primary` | `#2264c2` | `#4d9dff` | actions, links, selection, focus ring |
| `secondary` | `#e8edf4` | `#1c2839` | secondary buttons, quiet chips |
| `muted` | `#edf1f6` | `#1a2536` | subtle fills, skeletons, hover washes |
| `accent` | `#e4ecf6` | `#1d2c40` | hover/selected rows and menu items |
| `destructive` | `#c41b27` | `#f87171` | destructive actions only |

A hairline (`border`) separates; it sits at 1.3:1 on the card so a table of it stays quiet. A control's edge
(`input`) has to be found, so it sits at 1.8:1 or more.

### 3.2 Status colors (reserved)

Run/entity states only. Never used as chart series, never decorative. Always paired with an icon or
label; color never carries meaning alone.

| Token | Light | Dark | States |
|---|---|---|---|
| `success` | `#09762f` | `#49b861` | succeeded, online, active, enabled |
| `warning` | `#8c5d00` | `#d29922` | queued, degraded, paused, rate-limited |
| `info` | `primary` | `primary` | running, syncing, informational |
| `destructive` | `#c41b27` | `#f87171` | failed, offline, error |
| `muted-foreground` | | | cancelled, skipped, disabled, unknown |

**A status color is measured on its own tint.** A chip fills with 12% to 15% of the color and writes its label
in the color itself, so the pair that has to reach 4.5:1 is the color against that tint, not against the card.
The values above are the lightest (light mode) and darkest (dark mode) steps of each hue that do, and the four
sit level with each other in lightness, so no status is louder than its neighbors in a row of chips. Moving one
means measuring it again against its tint in both modes.

**Informational is the interaction blue.** A second blue for "running" beside the blue of links and buttons
is two near-identical colors with nothing to tell them apart; there is one.

**A status color marks a status.** A count is not one, and neither is a kind of thing: a zero takes no tone
(nothing succeeded, is queued or failed), and a category that wants telling apart takes a glyph in the tone
inside a neutral chip, never a tinted chip per category. A table where every cell is tinted has no status left
to show.

### 3.3 Workbench chrome

| Token | Light | Dark |
|---|---|---|
| `activity-bar` | `#283e56` (brand navy, both modes' chrome anchor) | `#0c1420` |
| `side-bar` | `#eef2f7` | `#0f1826` |
| `tab-bar` | `#eef2f7` | `#0c1420` |
| `tab-active` | `#ffffff` | `#111a28` |
| `panel` | `#ffffff` | `#0f1826` |
| `status-bar` | `#2264c2` (`--brand-blue`, both modes) | `#2264c2` |

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

### 3.5 Flow kind colors

Every flow kind has a name, a lucide glyph and a tone (`src/modules/flowKinds.ts`; a GUI module gives the kinds it adds
theirs through `FlowKindContribution.identity`). A tone is a chart slot or the neutral text tone, and SQLFlow's own
kinds take theirs by what they do with data:

| Tone | Token | Kinds |
|---|---|---|
| blue | `chart-1` | bring data in: `file`, `api`, `cpy`, `sftp` |
| violet | `chart-7` | load and shape tables: `ing`, `sp`, `cal` |
| orange | `chart-6` | send data out: `exp`, `trl` |
| neutral | `muted-foreground` | check, register and orchestrate: `hc`, `inv`, `scm`, `sch`, `batch` |
| magenta | `chart-3` | left for a module's kinds |

Green, yellow and red are never a kind's tone, because they read as a status (3.2), and neither is aqua (slot 5), which
reads as green on the dark card. The tone colors a glyph, a chip's fill and edge, and a header card's top edge; the
kind's name stays in the text tokens, so magenta's low contrast on white (3.4) never costs the label.

### 3.6 Surfaces a library draws

A library that brings a palette of its own is bound to the tokens at the library's override points, in
`src/index.css`, never styled around per page. React Flow (the lineage graph) reads the `--xy-*` variables set
on `.react-flow`: the canvas is `background`, a node is a `card` outlined in `input`, an edge's label is
`muted-foreground` on the canvas color (so it interrupts the line it names and draws no box), and the minimap
and zoom controls are cards. A rule on one of the library's classes is the wrong tool: its stylesheet loads
with the graph's chunk, after ours, and wins.

## 4. Typography

- Sans: `Inter Variable` (UI text, labels, prose). Mono: `JetBrains Mono Variable` (data).
- Base size **13px** (set on `body`). Scale: 11px (`text-[11px]`) chrome captions and uppercase panel
  headers, 12px (`text-xs`) secondary/table meta, 13px body, controls and tree rows (a branch's name
  included, never smaller than its leaves), 14px (`text-sm`) emphasized body, 16px (`text-base`) section
  titles, 18px (`text-lg`) page titles, 24px (`text-2xl`) KPI values.
- Page titles `font-semibold`; section titles `font-medium`; never `font-bold` in body content.
- Mono applies to: ids, hashes, row counts, durations, timestamps, schema/object/flow names, file paths,
  SQL, YAML, connection strings. Aligned numeric columns also get `tabular-nums`.
- Uppercase (`uppercase tracking-wider text-[11px] font-medium text-muted-foreground`) is reserved for
  chrome panel titles, never for data, buttons, or the side bar's menu, whose groups are tree branches.

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
  long nav never scrolls it away; then the menu, one tree on the workbench's tree primitives (section
  7.2a, tree label `Pages`): each nav group a top-level branch named in 13px `font-semibold`, its entries
  leaves under it with their 16px lucide icons, nav testids preserved (`nav-runs`, ...) on the rows. A
  group whose entries are of more than one sort (a module's group) declares sections: each is a branch
  under the group, named in 13px `font-medium`, with the entries under it as leaves, and folds on its own
  like a group. Which groups and sections are collapsed is kept in the browser
  (`sqlflow.workbench.sections`, a section as `group/section`), so folding a group and opening it again
  shows its sections as they were left; before the browser keeps any, the menu starts expanded, or every
  group and section collapsed when a module asks (`GuiModule.sideBar.menuCollapsedOnFirstVisit`).
  Entries of no section come first, straight under the group. SQLFlow's own groups declare none.
  Selection follows the longest-prefix rule over each entry's path and the paths it owns (a page reached
  from the entry rather than from the menu, such as a builder opened from its list, lights that entry);
  the selected entry is the tree's selected row, announced as the current page (`aria-current="page"`).
  The mobile nav sheet renders the same search over the same tree. Open or closed is the person's choice,
  kept in the browser (`sqlflow.workbench.sidebar`); before
  they have made one, the side bar starts open, or closed when a module asks
  (`GuiModule.sideBar.closedOnFirstVisit`), with the activity bar beside it to open it.
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
  (e.g. a run's streaming trace). Header: a 36px `card` bar under a 2px `primary` top edge (the accent the
  active tab wears), uppercase 11px title in `foreground` + close; while it is open, the page above fades into a
  shade along its bottom edge, so the panel reads as a surface of its own rather than one more grid header of the
  page. A detail shown there leads with a sticky header carrying its actions, then bordered cards. One content at
  a time. What it
  shows belongs to the page that opened it, so going to another page from a menu (side bar, its search,
  command palette, account menu) closes it through `useMenuNavigate()`; choosing the page already shown
  keeps it.
- **Status bar** (`h-[22px]`, `bg-status-bar`, white text, 11px): left segments show live workload
  (running/queued run counts, click-through to filtered Runs) and the rate-limit pause (testid
  `rate-limit-banner`); right segments show the signed-in subject and role. Segments are flat text +
  14px icons with hover wash; no borders.
- **Command palette** (Ctrl+K, also Ctrl+Shift+P): cmdk dialog listing every nav destination grouped as
  in the side bar, a group's sections headed `Group · Section`, plus a catalog-search action. Fuzzy
  filter, Enter navigates.

## 7. Components

Shared components live in `src/components/` (app-level) and `src/components/ui/` (shadcn primitives,
never hand-edited except where this book says so). MUI, emotion, and notistack are forbidden imports.

### 7.1 Page scaffold

`Page` + `PageHeader`: testid `page-<name>` preserved; header carries title (18px semibold), optional
description (13px muted), and right-aligned toolbar actions (small buttons) that stay at the top right however
long the description runs: the title block wraps its description beside them, and they drop below it only when the
header is too narrow for both. Below the header, optional
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

### 7.2a Trees

Every tree (the side bar's menu, the catalog, schema changes, a repository's folders, the AI context
tables, a module's own) is built on `components/Tree.tsx`, never on rows of its own. `TreeRoot` is the
`role="tree"` root and the tree's one tab stop: focus arriving from outside goes to the selected row, or
the first; focus coming back from a row (Shift+Tab) stays on the root, so the next Shift+Tab leaves.
`TreeNode` is a 28px row of 13px text, indented 14px per level from an 8px inset, with a 16px chevron
leading a branch (a leaf keeps the space, so siblings align) and a 1px `bg-border` guide under each
ancestor's chevron, which joins from row to row down the branch. Hover is `bg-sidebar-accent/60`; the
selected row is `bg-sidebar-accent font-medium` with a 2px primary inset. ArrowUp/ArrowDown move,
ArrowRight opens a branch then steps into it, ArrowLeft folds it then steps to the parent, Enter
selects. A navigation tree (`TreeState.navigation`) announces its selected row as the current page.

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

### 7.3.1 Flow kind

A flow's kind is shown the same way wherever a pipeline or a run is (3.5), so two kinds never read as the same page:

- **`FlowKindBadge`**: the kind's glyph in its tone and its name, on a 15% tint of the tone with a 40% edge,
  `rounded-sm` (square-cornered, so it never reads as an outcome pill or a state ring beside one); the kind's value
  (`ing`) on hover. Pipeline rows, the runs board's Kind column, a run's header.
- **`FlowKindTile`**: the glyph at 20px on a 40px tile of the same tint, leading a pipeline's header, with
  "<name> flow" under the pipeline's name and the card's top edge (3px) in the tone.
- **Workbench tab**: a pipeline's tab wears its kind's glyph in the tone instead of the route's quiet glyph.

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
delta with icon + success/destructive text, optional sparkline in slot-1 blue. A tile either links away
(`linkTo`) or acts on its own page (`onClick` + `selected`): a row of tiles whose counts ARE the page's
categories is that page's filter, the selected tile wearing the accent ring and `aria-pressed`, rather
than a second row of chips repeating the same words (the Data streams verdicts). Charts follow section
3.4 and the dataviz skill's rules (form first, hover tooltips, legends for 2+ series, one axis). A
per-row sparkline in a table is inline SVG, never a chart-library instance per row.

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

## 11. The sign-in page

The one surface outside the workbench (`src/auth/LoginPage.tsx`), and the first thing a person sees of the product.
It is a first impression rather than a tool, so where this book's workbench rules would make it look unfinished, these
rules take their place, and only on this page:

- **One navy field, in both modes.** The whole page is set on the brand navy, lightest where the lockup sits and
  deepening toward the form, and renders with the dark tokens whichever mode the workbench is in. No seam between the
  brand panel and the form: they are one surface.
- **Picture, pitch, form.** On wide screens the brand panel carries the lockup (with the "powered by" attribution when the
  product has one), a picture of the data the product takes (`LoginStreams`, from the branding's `streams`), and the
  pitch: two lines (`headline`, then `headlineAccent` in the accent) and one sentence (`tagline`). No feature lists. On
  narrow screens only the lockup stays above the form.
- **Colors.** The identity's navy, cream (the rings, as in the logo mark) and the one blue, which carries the motion (the
  records' points and the light they travel into) and, lightened to read on the navy, the pitch's accent line. Nothing
  else.
- **Sizes.** Every size is in rem. The pitch is set large (2.5rem, 3.25rem from `xl`); the form's controls are 40px
  (`h-10`, button `size="lg"`) at 14px type, because the form is the page's one task.
- **Motion.** The picture moves slowly and without end (records travel a lane in 17 to 24 seconds, a pulse runs down the
  rings) and holds still, one record resting on each lane, when the system asks for reduced motion. The pitch and the
  form fade in once (`motion-safe:` only). Light fades out on its own; nothing is clipped where it would leave an edge.

## 12. The home page

The page a person lands on in the workbench (`src/features/dashboard/DashboardPage.tsx`, route `/`). It is a workbench
page in every way but its hero, which carries the sign-in page's identity into the workbench so the two read as one
product. The headline counts (7.7) open the page on their own, with no heading over them (the cards name themselves),
so what the estate is doing now is the first thing read; the hero follows them:

- **The hero.** A card set on the sign-in page's navy field (`BRAND_FIELD`) in both modes, rendered with the dark
  tokens, `rounded-lg` with a white/10 hairline: a greeting (24px semibold, white), the product's tagline (14px, white/60),
  and the assistant's question box, its one task. On wide screens the sign-in page's picture (`LoginStreams`) sits beside
  them, fading in from the left so it never meets the box at an edge, and moving as it does there (still under reduced
  motion). Nothing else on the page is navy.
- **The question box.** A 44px input (`h-11`, 14px) with the `Ask` button inside it, and up to three questions offered
  under it as quiet pills (white/12 hairline on white/4). A question typed, or one picked, opens the chat asking it in a
  fresh conversation (`chatHandoff`, carried in the navigation's state, so a reload never asks it twice). The modules offer
  the questions (`HomeContribution.prompts`), SQLFlow's own standing in when none does. When the assistant is not
  configured, the box says so and asks nothing.
- **Below it, the workbench.** Sections set in `HomeSection` (16px medium heading, what it is as of or the way into its
  area on the right): each section a module adds (`HomeContribution.sections`, each in a
  suspense boundary of its own), then the newest runs beside the run-state distribution. Status colors keep to 3.2.

## 13. Port checklist (apply to every feature page)

- [ ] No `@mui/*`, `@emotion/*`, or `notistack` imports remain in the file.
- [ ] Colors only via semantic utilities; no hex, no `--sf-*` legacy tokens.
- [ ] Every `data-testid` present before the port is present after it.
- [ ] Page scaffold: `Page`/`PageHeader`, page testid, actions as small buttons.
- [ ] Tables through `DataTable`/`PagedTable`; statuses through `StatusBadge`; data in mono.
- [ ] Forms: sheet-based (7.4), pending states, inline errors, success/failure toasts (8.1).
- [ ] Long processes: live surface + terminal toast (8.2). Three data states everywhere (8.3).
- [ ] Detail pages call `useTabTitle` with the entity name.
- [ ] Both themes checked; `tsc -b` and `vite build` clean; e2e for the feature passes.
