# MUI-to-shadcn porting guide (working document for the redesign)

Binding context for porting one feature area of the SQLFlow GUI from MUI to the new Tailwind v4 +
shadcn/ui workbench. Read `gui/DESIGN.md` FIRST; it is the authority. This file adds the mechanics.
`gui/src/features/runs/RunsPage.tsx` is the ported exemplar; match its idioms exactly.

## Hard rules

- Remove every `@mui/*`, `@emotion/*`, and `notistack` import from the files you port. Do not add any
  new dependency. Do not edit files outside your assigned set, ESPECIALLY not `src/components/*` shared
  components, `src/components/ui/*` primitives, `src/index.css`, or the layout/shell.
- Preserve every `data-testid` that exists in the file you replace, on an equivalent element (an input
  testid stays on the `<input>`, a dialog testid moves onto the replacing sheet/dialog content).
- The em dash character is forbidden everywhere. No TODOs, no stubs, no commented-out code, no
  "will polish later": every file you hand back is finished.
- Behavior is preserved: same queries, same mutations, same routes, same keyboard/UX affordances,
  unless DESIGN.md dictates a pattern change (dialogs to sheets, toasts for mutations).

## Building blocks

- shadcn primitives (import from `@/components/ui/<name>`): button, badge, card, dialog, alert-dialog,
  sheet, dropdown-menu, tooltip, input, label, select, separator, skeleton, table, tabs, scroll-area,
  command, popover, checkbox, switch, textarea, progress, resizable, alert, collapsible, avatar,
  toggle, toggle-group, breadcrumb, radio-group. `cn()` from `@/lib/utils`. Icons from `lucide-react`.
- Shared app components (import from `../../components/<Name>`, contracts unchanged): Page, PageHeader,
  FilterBar, PagedTable/DataTable (+ Column, TableGrouping), EmptyState, StatusBadge exports
  (RunStatusBadge, OnlineBadge, ActiveBadge, ScheduleStateBadge), ConfirmDialog, CopyButton,
  CorrelationError, DetailHeaderCard, DetailPair, Mono (`className` instead of `sx`), RelativeTime,
  TruncatedText, KpiCard, CodeView, LineageJumpButton, TopProgressBar.
- Toasts: `import { toast } from "sonner"`; `toast.success("Run 4f2a queued")`,
  `toast.error(message)`. notistack's `enqueueSnackbar(msg, { variant })` maps 1:1.
- Tab titles: detail pages call `useTabTitle(name)` from `../../layout/workbench/TabsContext` once the
  entity is loaded (e.g. the pipeline name), so the workbench tab reads the entity instead of "Pipeline".
- Bottom panel (optional, only where the old page showed a live streaming surface inline):
  `usePanel()` from `../../layout/workbench/PanelContext`.

## MUI translation table

| MUI | Replacement |
|---|---|
| `Box`/`Stack`/`Grid` | `div` + flex/grid utilities (`flex flex-col gap-4`, `grid gap-4 md:grid-cols-3`) |
| `Typography variant="h5/h6"` | `h2 className="text-base font-medium"` (section) or PageHeader/DetailHeaderCard |
| `Typography variant="body2"` | `text-[13px]`; `caption` -> `text-xs text-muted-foreground` |
| `Button variant="contained"` | `<Button size="sm">` |
| `Button variant="outlined"/"text"` | `<Button variant="outline" size="sm">` / `variant="ghost"` |
| `IconButton` | `<Button variant="ghost" size="icon-xs" aria-label=...>` + Tooltip |
| `TextField` | `<Input className="h-8" />` (+ `<Label>` above, or `aria-label`/placeholder for filters) |
| `TextField select` / `Select` | ui select: Select/SelectTrigger(`className="h-8"`)/SelectContent/SelectItem |
| `Chip` | `<Badge variant="secondary">` or a status badge from StatusBadge.tsx |
| `Dialog` (form) | `<Sheet>` right side, `<SheetContent className="sm:max-w-xl">`; keep the old dialog's testid on SheetContent |
| `Dialog` (confirm) | shared `ConfirmDialog` |
| `Menu`/`MenuItem` | DropdownMenu family |
| `Tabs`/`Tab` | ui Tabs/TabsList/TabsTrigger/TabsContent (keep testids on triggers) |
| `Alert` | ui Alert (+ CorrelationError for API errors) |
| `CircularProgress` | `<Loader2 className="size-4 animate-spin" />` |
| `LinearProgress` | ui Progress (determinate) or `TopProgressBar` (route-level only) |
| `Skeleton` | ui Skeleton |
| `Tooltip` | ui Tooltip (Trigger `asChild` around a real element) |
| `ToggleButtonGroup` | ui ToggleGroup `type="single" variant="outline" size="sm"` |
| `Switch`+`FormControlLabel` | ui Switch inside a `<Label className="flex items-center gap-2 text-[13px] font-normal">` |
| `Autocomplete` | ui Command inside a Popover (input filters CommandItems), or Select when options are few |
| `@mui/x-tree-view` | hand-rolled tree: nested divs, ChevronDown rotate on collapse, 28px rows (mirror SideBar.tsx's section pattern) |
| `useMediaQuery(up("md"))` | Tailwind responsive classes (`hidden md:flex`), or `window.matchMedia` when logic needs it |
| `theme.palette.X` / `--sf-*` vars | semantic utilities (`text-destructive`, `bg-card`...); charts keep `seriesColor(i)` from `../../theme/branding` |

## Feedback rules you must implement (DESIGN.md section 8)

Every mutation: button pending state (`disabled` + `<Loader2 className="animate-spin" />` inside),
success toast naming the object, error toast with the API message (`isApiError(e) ? e.detail ?? e.title
: String(e)`). Every list/detail surface: skeleton loading, EmptyState when empty, CorrelationError on
API error. Long-running processes keep their polling/SSE and get a terminal toast.

## Verification

Run `npx tsc -b --pretty` from `gui/` (node lives at `C:\Program Files\nodejs`). Errors mentioning
files OUTSIDE your assigned set are expected while other areas are mid-port; errors in YOUR files are
yours to fix. Do not run the vite build or e2e; the coordinator does that after all areas land.
