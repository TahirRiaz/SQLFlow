import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter } from "react-router-dom";
import App from "./App";
import { isApiError } from "./api/client";
import { AuthProvider } from "./auth/AuthContext";
import { isEntraSignInResponse } from "./auth/entraSignIn";
import { relayEntraSignInResponse } from "./auth/msal";
import { Toaster } from "./components/ui/sonner";
import { TooltipProvider } from "./components/ui/tooltip";
import { loadRuntimeConfig } from "./config/runtime";
import { applyRuntimeBranding, branding } from "./modules/branding";
import { registerModules, type GuiModule } from "./modules/registry";
import { ThemeModeProvider } from "./theme/ThemeModeContext";

function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // 4xx answers are deterministic (bad request, missing, forbidden): retrying them is waste. Transient
        // failures (network, 5xx) get two retries. 429 is handled by the global polling pause, not by retry.
        retry: (failureCount, error) => {
          if (isApiError(error) && error.status >= 400 && error.status < 500) {
            return false;
          }

          return failureCount < 2;
        },
        staleTime: 5_000,
      },
    },
  });
}

/**
 * Renders the GUI into #root with the given modules installed. SQLFlow's own entry passes none; a module build passes
 * its modules. The entry imports the stylesheet and fonts before calling this, so a module build can put its own
 * stylesheet (which imports this one) in their place.
 */
export function renderApp(modules: readonly GuiModule[] = []): void {
  const container = document.getElementById("root");
  if (container === null) {
    throw new Error("The page has no #root element to render the GUI into.");
  }

  // Entra answers a sign-in by loading the GUI's origin in the sign-in popup. That load is MSAL's redirect bridge, not
  // the workbench: it relays the answer to the window waiting on it before anything else starts or reads the URL.
  if (isEntraSignInResponse(window.location.hash)) {
    relayEntraSignInResponse(container);
    return;
  }

  registerModules(modules);

  const queryClient = createQueryClient();

  // The runtime config (API base URL, branding overrides) must be known before anything renders; a splash-less await
  // keeps it simple. loadRuntimeConfig never rejects: a missing or unreadable config.json falls back to the build.
  void loadRuntimeConfig().then((config) => {
    applyRuntimeBranding(config.branding);
    document.title = branding().documentTitle;
    createRoot(container).render(
      <StrictMode>
        <QueryClientProvider client={queryClient}>
          <ThemeModeProvider>
            <TooltipProvider delayDuration={300}>
              <BrowserRouter>
                <AuthProvider>
                  <App />
                </AuthProvider>
              </BrowserRouter>
              <Toaster position="bottom-right" closeButton />
            </TooltipProvider>
          </ThemeModeProvider>
        </QueryClientProvider>
      </StrictMode>,
    );
  });
}
