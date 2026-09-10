// Runtime configuration: one built artifact serves any environment by swapping public/config.json at deploy
// time. In a production build config.json wins (deploy-time override); in the dev server the VITE_API_BASE_URL
// environment wins (so a shell/e2e harness can point the dev GUI at any control plane without editing files).
// Failing both, the SPA assumes the control plane shares its origin.

export interface RuntimeConfig {
  apiBaseUrl: string;
}

let config: RuntimeConfig | null = null;

export async function loadRuntimeConfig(): Promise<RuntimeConfig> {
  if (config) {
    return config;
  }

  let fromFile: Partial<RuntimeConfig> = {};
  try {
    const response = await fetch("/config.json", { cache: "no-cache" });
    if (response.ok) {
      const parsed: unknown = await response.json();
      if (parsed !== null && typeof parsed === "object" && !Array.isArray(parsed)) {
        fromFile = parsed as Partial<RuntimeConfig>;
      }
    }
  } catch {
    // No config.json (or unreachable): fall through to the build-time value.
  }

  const fileValue = typeof fromFile.apiBaseUrl === "string" && fromFile.apiBaseUrl.trim() !== ""
    ? fromFile.apiBaseUrl
    : null;
  const envValue = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? null;
  const apiBaseUrl = (import.meta.env.DEV ? envValue ?? fileValue : fileValue ?? envValue)
    ?? window.location.origin;
  config = { apiBaseUrl: apiBaseUrl.replace(/\/+$/, "") };
  return config;
}

export function runtimeConfig(): RuntimeConfig {
  if (!config) {
    throw new Error("Runtime configuration is not loaded yet; loadRuntimeConfig() runs before the app renders.");
  }

  return config;
}
