import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { applyAppearance, readStoredAppearance, rememberAppearance, systemPrefersDark } from "./appearance";
import { designById, offeredDesigns, resolveAppearance, type Appearance, type Design, type StoredAppearance, type ThemeMode } from "./designs";

export type { ThemeMode } from "./designs";

interface ThemeModeValue {
  /** The mode worn. */
  mode: ThemeMode;
  /** The design worn. */
  design: Design;
  /** The designs a person may pick from: SQLFlow's alone unless a module adds others. */
  designs: readonly Design[];
  /**
   * Changes whenever the stylesheet's colour tokens do, with the design or the mode: the key a component that reads tokens
   * into literal colours (a chart, the code editor, a graph) rebuilds them on.
   */
  palette: string;
  /** Wears the other mode, in the design worn. */
  toggle: () => void;
  /** Wears a design, a mode, or both; what the choice leaves out stays as it is. */
  choose: (choice: Partial<Appearance>) => void;
}

const ThemeModeContext = createContext<ThemeModeValue | null>(null);

/**
 * The workbench's appearance: a design (designs.ts) in a light or dark mode. A person wears what they wore last in this
 * browser; until they pick, the design and mode a module starts people in, or SQLFlow's design in the operating system's
 * preferred mode, followed live. The tokens live in the stylesheets keyed on what applyAppearance sets on <html>
 * (DESIGN.md section 3), so every surface (Tailwind utilities, Monaco, charts reading tokens at runtime) follows one switch.
 */
export function ThemeModeProvider({ children }: { children: ReactNode }) {
  const [prefersDark, setPrefersDark] = useState(systemPrefersDark);
  const [stored, setStored] = useState<StoredAppearance>(readStoredAppearance);

  // Track the OS preference live, so a system theme change flips the app while a person follows it.
  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = (event: MediaQueryListEvent) => setPrefersDark(event.matches);
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, []);

  const { appearance, followsSystem } = resolveAppearance(stored, prefersDark);
  const { design, mode } = appearance;

  // What a person wears is kept for their next visit, unless it is the operating system's preference, which they follow.
  useEffect(() => {
    if (!followsSystem) {
      rememberAppearance({ design, mode });
    }
  }, [design, mode, followsSystem]);

  // The appearance must be on <html> before children read tokens for it.
  applyAppearance({ design, mode });

  useEffect(() => {
    applyAppearance({ design, mode });
  }, [design, mode]);

  const value = useMemo<ThemeModeValue>(() => ({
    mode,
    design: designById(design),
    designs: offeredDesigns(),
    palette: `${design}:${mode}`,
    toggle: () => setStored({ design, mode: mode === "dark" ? "light" : "dark" }),
    choose: (choice) => setStored({ design: choice.design ?? design, mode: choice.mode ?? mode }),
  }), [design, mode]);

  return <ThemeModeContext.Provider value={value}>{children}</ThemeModeContext.Provider>;
}

export function useThemeMode(): ThemeModeValue {
  const context = useContext(ThemeModeContext);
  if (!context) {
    throw new Error("useThemeMode must be used inside ThemeModeProvider.");
  }

  return context;
}
