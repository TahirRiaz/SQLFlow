import { useEffect, useState } from "react";

/**
 * Tracks whether an element's content overflows the width it is allowed, re-measuring whenever the element or
 * the column it sits in is resized. A callback ref rather than a `useRef`, so the observer follows the node
 * across the remount that attaching a tooltip trigger causes. Exported for the cells that clip a value in a
 * shape of their own (an id whose repeated prefix gives way before its tail) and still owe the reader the hover
 * panel only when something is actually hidden.
 */
export function useClipped(text: string): [(node: HTMLSpanElement | null) => void, boolean] {
  const [node, setNode] = useState<HTMLSpanElement | null>(null);
  const [clipped, setClipped] = useState(false);

  useEffect(() => {
    if (node === null) {
      return;
    }

    // A sub-pixel slack: a fractional layout width can leave scrollWidth a hair over clientWidth on text that
    // is not actually clipped, which would put a panel on every cell in the table.
    const measure = () => setClipped(node.scrollWidth > node.clientWidth + 1);
    measure();

    const observer = new ResizeObserver(measure);
    observer.observe(node);
    return () => observer.disconnect();
  }, [node, text]);

  return [setNode, clipped];
}
