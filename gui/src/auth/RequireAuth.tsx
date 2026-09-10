import type { ReactNode } from "react";
import { Navigate, useLocation } from "react-router-dom";
import { useAuth } from "./AuthContext";

/** Route guard: unauthenticated visits are sent to the login page, which returns them here after sign-in. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { session } = useAuth();
  const location = useLocation();

  if (!session) {
    const returnTo = encodeURIComponent(`${location.pathname}${location.search}`);
    return <Navigate to={`/login?returnTo=${returnTo}`} replace />;
  }

  return <>{children}</>;
}

/** Scope guard for pages beyond authentication (the admin surface): renders nothing the user cannot use. */
export function RequireScope({ scope, children }: { scope: string; children: ReactNode }) {
  const { session, hasScope } = useAuth();
  const location = useLocation();

  if (!session) {
    const returnTo = encodeURIComponent(`${location.pathname}${location.search}`);
    return <Navigate to={`/login?returnTo=${returnTo}`} replace />;
  }

  if (!hasScope(scope)) {
    return <Navigate to="/" replace />;
  }

  return <>{children}</>;
}
