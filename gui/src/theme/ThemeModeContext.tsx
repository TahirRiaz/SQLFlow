import { createContext, useContext, useEffect, useMemo, useState, type ReactNode } from "react";
import { applyBrandMode } from "./branding";

type ThemeMode = "light" | "dark";

interface ThemeModeValue {
  mode: ThemeMode;
  toggle: () => void;
}

const ThemeModeContext = createContext<ThemeModeValue | null>(null);

const STORAGE_KEY = "sqlflow.theme";

/**
 * Light/dark theming: the OS preference is the default, the user's explicit choice (persisted) wins.
 * The design tokens live in index.css keyed on the `dark` class (DESIGN.md section 3); applyBrandMode
 * stamps both that class and the legacy data attribute on <html>, so every surface (Tailwind utilities,
 * Monaco, charts reading tokens at runtime) follows one switch.
 */
export function ThemeModeProvider({ children }: { children: ReactNode }) {
  const [prefersDark, setPrefersDark] = useState(() => window.matchMedia("(prefers-color-scheme: dark)").matches);
  const [stored, setStored] = useState<ThemeMode | null>(() => {
    const raw = window.localStorage.getItem(STORAGE_KEY);
    return raw === "light" || raw === "dark" ? raw : null;
  });

  // Track the OS preference live, so a system theme change flips the app unless the user chose explicitly.
  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const onChange = (event: MediaQueryListEvent) => setPrefersDark(event.matches);
    media.addEventListener("change", onChange);
    return () => media.removeEventListener("change", onChange);
  }, []);

  const mode: ThemeMode = stored ?? (prefersDark ? "dark" : "light");

  useEffect(() => {
    if (stored) {
      window.localStorage.setItem(STORAGE_KEY, stored);
    }
  }, [stored]);

  // The class must be on <html> before children read tokens for the active mode.
  applyBrandMode(mode);

  useEffect(() => {
    applyBrandMode(mode);
  }, [mode]);

  const value = useMemo<ThemeModeValue>(() => ({
    mode,
    toggle: () => setStored(mode === "dark" ? "light" : "dark"),
  }), [mode]);

  return <ThemeModeContext.Provider value={value}>{children}</ThemeModeContext.Provider>;
}

export function useThemeMode(): ThemeModeValue {
  const context = useContext(ThemeModeContext);
  if (!context) {
    throw new Error("useThemeMode must be used inside ThemeModeProvider.");
  }

  return context;
}
