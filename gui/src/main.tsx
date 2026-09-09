import "@fontsource-variable/inter";
import "@fontsource-variable/jetbrains-mono";
import "./index.css";
import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { BrowserRouter } from "react-router-dom";
import App from "./App";
import { isApiError } from "./api/client";
import { AuthProvider } from "./auth/AuthContext";
import { Toaster } from "./components/ui/sonner";
import { TooltipProvider } from "./components/ui/tooltip";
import { loadRuntimeConfig } from "./config/runtime";
import { ThemeModeProvider } from "./theme/ThemeModeContext";

const queryClient = new QueryClient({
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

// The runtime config (API base URL) must be known before anything renders; a splash-less await keeps it simple.
void loadRuntimeConfig().then(() => {
  createRoot(document.getElementById("root")!).render(
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
