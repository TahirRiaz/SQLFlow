import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

/**
 * Inline monospace text (commit SHAs, keys, paths, ids): the one helper so code-like values render
 * identically everywhere (DESIGN.md section 4).
 */
export function Mono({ children, className }: { children: ReactNode; className?: string }) {
  return <span className={cn("font-mono text-[12px]", className)}>{children}</span>;
}
