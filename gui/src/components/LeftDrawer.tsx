import { useCallback, useRef, useState, type KeyboardEvent, type PointerEvent, type ReactNode } from "react";
import { Maximize2, Minimize2, X } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useLocalStorageState } from "@/hooks/useLocalStorageState";
import { cn } from "@/lib/utils";

/** The widest a drawer is dragged to: most of its frame, so the page always shows beside it. */
const MAX_SHARE = 0.85;
/** How far an arrow key moves the drawer's edge. */
const KEY_STEP = 32;

export interface LeftDrawerProps {
  /** Names the drawer to assistive technology. */
  label: string;
  /** Where the width the person dragged it to is kept, so the drawer opens at it next time. */
  widthKey: string;
  /** The width, in pixels, until the person drags it. */
  defaultWidth: number;
  /** The narrowest it is dragged to. */
  minWidth: number;
  /** Maximized, it covers its frame, fixed to the viewport where `maximizedClassName` places it. */
  maximized: boolean;
  maximizedClassName: string;
  /** Its stacking layer over the page (a `z-*` class), so one drawer opens over another. */
  layerClassName: string;
  /** The drawer's test id; its edge is `${testId}-resize`. */
  testId: string;
  children: ReactNode;
}

/**
 * A panel that slides in from the left over the page, placed by the frame inside the box it covers (the page's area of
 * the workbench, a window page's whole window), which must be positioned. Its right edge is dragged, or moved with the
 * arrow keys, to make it wider or narrower, between `minWidth` and most of the frame, and the width is kept for the next
 * time it opens; a double-click on the edge sets it back. Maximized, it covers the frame and the page is out of view until
 * it is restored. Explain and the code window are drawers of this kind, each with a header of its own (DrawerHeader).
 */
export function LeftDrawer({
  label, widthKey, defaultWidth, minWidth, maximized, maximizedClassName, layerClassName, testId, children,
}: LeftDrawerProps) {
  const [storedWidth, setStoredWidth] = useLocalStorageState(widthKey, defaultWidth);
  const [dragWidth, setDragWidth] = useState<number | null>(null);
  const asideRef = useRef<HTMLElement | null>(null);

  // The widest the drawer may be in the frame it is in now: most of it, never below the narrowest.
  const clamp = useCallback((width: number): number => {
    const frame = asideRef.current?.parentElement?.clientWidth ?? window.innerWidth;
    return Math.round(Math.max(minWidth, Math.min(width, Math.max(minWidth, frame * MAX_SHARE))));
  }, [minWidth]);

  const onPointerDown = useCallback((event: PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) {
      return;
    }

    event.preventDefault();
    event.currentTarget.setPointerCapture(event.pointerId);
    setDragWidth(asideRef.current?.getBoundingClientRect().width ?? storedWidth);
  }, [storedWidth]);

  const onPointerMove = useCallback((event: PointerEvent<HTMLDivElement>) => {
    if (dragWidth === null || asideRef.current === null) {
      return;
    }

    setDragWidth(clamp(event.clientX - asideRef.current.getBoundingClientRect().left));
  }, [dragWidth, clamp]);

  const onPointerUp = useCallback((event: PointerEvent<HTMLDivElement>) => {
    if (dragWidth !== null) {
      setStoredWidth(dragWidth);
      setDragWidth(null);
    }

    if (event.currentTarget.hasPointerCapture(event.pointerId)) {
      event.currentTarget.releasePointerCapture(event.pointerId);
    }
  }, [dragWidth, setStoredWidth]);

  const onKeyDown = useCallback((event: KeyboardEvent<HTMLDivElement>) => {
    const step = event.key === "ArrowRight" ? KEY_STEP : event.key === "ArrowLeft" ? -KEY_STEP : 0;
    if (step !== 0) {
      event.preventDefault();
      setStoredWidth(clamp(storedWidth + step));
    }
  }, [clamp, storedWidth, setStoredWidth]);

  const width = dragWidth ?? storedWidth;
  return (
    <aside
      ref={asideRef}
      className={cn(
        "flex flex-col bg-card shadow-lg duration-200 animate-in fade-in-0 slide-in-from-left-8",
        layerClassName,
        maximized ? cn("fixed", maximizedClassName) : "absolute inset-y-0 left-0 max-w-[85%] border-r",
      )}
      style={maximized ? undefined : { width, minWidth: Math.min(minWidth, width) }}
      aria-label={label}
      data-maximized={maximized || undefined}
      data-testid={testId}
    >
      {children}
      {!maximized && (
        <div
          role="separator"
          aria-orientation="vertical"
          aria-label="Drawer width"
          aria-valuenow={width}
          aria-valuemin={minWidth}
          tabIndex={0}
          title="Drag to resize; double-click for the default width"
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
          onPointerCancel={onPointerUp}
          onDoubleClick={() => setStoredWidth(clamp(defaultWidth))}
          onKeyDown={onKeyDown}
          className="group absolute inset-y-0 -right-1.5 z-10 flex w-3 cursor-col-resize justify-center outline-none"
          data-testid={`${testId}-resize`}
        >
          <span
            className={cn(
              "h-full w-0.5 transition-colors group-hover:bg-primary/60 group-focus-visible:bg-primary",
              dragWidth !== null && "bg-primary",
            )}
          />
        </div>
      )}
    </aside>
  );
}

export interface DrawerHeaderProps {
  /** The drawer's glyph, in its tinted square. */
  icon: ReactNode;
  /** The drawer's heading. */
  title: ReactNode;
  /** What it shows, on one line under the heading, in full on hover. */
  subtitle: string;
  /** Buttons of the drawer's own, before maximize and close. */
  actions?: ReactNode;
  maximized: boolean;
  onMaximizedChange: (maximized: boolean) => void;
  onClose: () => void;
  /** What maximizing, restoring and closing do, said on hover. */
  maximizeHint: string;
  restoreHint: string;
  closeHint: string;
  /** The test ids: `${testIdPrefix}-header`, `-maximize` and `-close`. */
  testIdPrefix: string;
}

/**
 * A drawer's header: its glyph, heading and what it shows, its own buttons, then maximize or restore (a double-click on
 * the header does too, as on a window's) and close.
 */
export function DrawerHeader({
  icon, title, subtitle, actions, maximized, onMaximizedChange, onClose, maximizeHint, restoreHint, closeHint, testIdPrefix,
}: DrawerHeaderProps) {
  return (
    <header
      className="flex shrink-0 items-center gap-2.5 border-b px-3 py-2"
      onDoubleClick={(event) => {
        if (!(event.target as Element).closest("button")) {
          onMaximizedChange(!maximized);
        }
      }}
      data-testid={`${testIdPrefix}-header`}
    >
      <span className="flex size-7 shrink-0 items-center justify-center rounded-md bg-primary/10 text-primary">{icon}</span>
      <div className="min-w-0 flex-1">
        {title}
        <p className="truncate text-[11.5px] leading-4 text-muted-foreground" title={subtitle}>{subtitle}</p>
      </div>
      {actions}
      <Tooltip>
        <TooltipTrigger asChild>
          <Button
            variant="ghost"
            size="icon-xs"
            onClick={() => onMaximizedChange(!maximized)}
            aria-label={maximized ? "Restore" : "Maximize"}
            aria-pressed={maximized}
            data-testid={`${testIdPrefix}-maximize`}
          >
            {maximized ? <Minimize2 /> : <Maximize2 />}
          </Button>
        </TooltipTrigger>
        <TooltipContent>{maximized ? restoreHint : maximizeHint}</TooltipContent>
      </Tooltip>
      <Tooltip>
        <TooltipTrigger asChild>
          <Button variant="ghost" size="icon-xs" onClick={onClose} aria-label="Close" data-testid={`${testIdPrefix}-close`}>
            <X />
          </Button>
        </TooltipTrigger>
        <TooltipContent>{closeHint}</TooltipContent>
      </Tooltip>
    </header>
  );
}
