import { useEffect, type RefObject } from "react";

/** The nearest ancestor that scrolls vertically: the workbench editor for a page, the panel body for the bottom panel. */
function scrollingAncestor(element: HTMLElement): HTMLElement | null {
  for (let node = element.parentElement; node !== null; node = node.parentElement) {
    const { overflowY } = window.getComputedStyle(node);
    if (overflowY === "auto" || overflowY === "scroll") {
      return node;
    }
  }

  return null;
}

/**
 * Keeps a list inside the room its scrolling surface shows (the workbench editor, or the bottom panel's body): the most
 * it may grow to (`maxHeight`, the default) or its height (`height`, for panes that fill the room whatever they hold) is
 * set so it ends where that surface's visible area does, less `below` (the padding and anything kept under it), and
 * never less than `min`. Its rows then scroll inside it while the page stays put, which a sticky header makes readable.
 *
 * The room is measured against the surface rather than the window, so it follows the surface as it changes: the window
 * resized, the bottom panel opened, dragged or closed under the editor, a header above that wraps. It is measured from
 * the top of the surface's content, so a surface scrolled down when the fit runs gets the same answer as one at the top.
 *
 * `target` picks the element to size inside the one `ref` holds (a table's own scrolling container).
 */
export function useScrollFit(
  ref: RefObject<HTMLElement | null>,
  below: number,
  min: number,
  target?: (element: HTMLElement) => HTMLElement | null,
  property: "maxHeight" | "height" = "maxHeight",
): void {
  useEffect(() => {
    const element = ref.current;
    if (element === null) {
      return undefined;
    }

    const surface = scrollingAncestor(element);
    const fit = () => {
      const fitted = target === undefined ? element : target(element);
      // A hidden element starts nowhere; it is fitted once it is shown, which the observer below sees.
      if (fitted === null || fitted.getClientRects().length === 0) {
        return;
      }

      const top = fitted.getBoundingClientRect().top;
      const room = surface === null
        ? window.innerHeight - top - below
        : surface.clientHeight - (top - surface.getBoundingClientRect().top + surface.scrollTop) - below;
      const size = `${Math.max(min, Math.floor(room))}px`;
      if (fitted.style[property] !== size) {
        fitted.style[property] = size;
      }
    };

    // What is observed: the surface, what is laid out before the element and before each of its ancestors (none of them
    // sized by the fit), and the element itself, so a list that appears inside it later (an empty state that becomes a
    // table, a pane shown again) is fitted too. The size depends only on where the element starts, so the one resize a
    // fit causes finds the size already set, and stops there. The fit waits for the next frame, so a burst of changes is
    // fitted once.
    let frame = 0;
    const schedule = () => {
      window.cancelAnimationFrame(frame);
      frame = window.requestAnimationFrame(fit);
    };

    fit();
    const observer = new ResizeObserver(schedule);
    if (surface !== null) {
      observer.observe(surface);
    }

    observer.observe(element);
    for (let node: Element | null = element; node !== null && node !== surface; node = node.parentElement) {
      for (let before = node.previousElementSibling; before !== null; before = before.previousElementSibling) {
        observer.observe(before);
      }
    }

    window.addEventListener("resize", schedule);
    return () => {
      window.cancelAnimationFrame(frame);
      observer.disconnect();
      window.removeEventListener("resize", schedule);
    };
  }, [ref, below, min, target, property]);
}

/** The scrolling container a DataTable renders its table in: the `target` that sizes a grid's rows rather than its card. */
export function tableContainer(element: HTMLElement): HTMLElement | null {
  return element.querySelector<HTMLElement>("[data-slot=table-container]");
}
