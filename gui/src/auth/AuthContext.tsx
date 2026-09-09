import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { isApiError, setAuthToken, setUnauthorizedHandler } from "../api/client";
import { authApi } from "../api/endpoints";
import type { EntraProviderInfo } from "../api/types";
import { saveLoginPrefs } from "./loginPrefs";
import { signInWithEntra } from "./msal";

/** The signed-in session as the GUI holds it. The token also lives in the API client for request headers. */
export interface Session {
  token: string;
  subject: string;
  /** The user's role; null for a bootstrap-secret session (which has scopes but no user behind it). */
  role: string | null;
  scopes: string[];
  expiresAtMs: number;
  /** Which storage backer holds this session, and so whether it outlives the tab. Derived from where it was found
   * on restore rather than trusted from the stored payload. */
  remember: boolean;
  /** Whether this session may roll onto a fresh token. True for a user-backed sign-in; false for the break-glass
   * bootstrap session, which the server refuses to renew and which is meant to lapse. */
  renewable: boolean;
}

/** A session as the sign-in paths build it, before the provider stamps on how it is stored and whether it rolls. */
type NewSession = Omit<Session, "remember" | "renewable">;

// The token is rolled well before it lapses, so a working session never dies under the user. The server caps the
// total rolling age (30 days from the actual sign-in by default); past that, renewal is refused and a real sign-in
// is the only way back in.
const RENEW_LEAD_MS = 5 * 60_000;
/** A renewal that failed on transport (offline, a server blip) retries this often: the token in hand still works,
 * so there is time to keep trying rather than throwing the user out over one bad request. */
const RENEW_RETRY_MS = 60_000;
/** This close to expiry the session is treated as gone: too little runway left to trust a renewal round trip. */
const RENEW_FLOOR_MS = 30_000;
/** No scheduled roll ever lands sooner than this. Only bites when a deployment configures an access-token life
 * shorter than the renew lead, where the lead alone would want to roll immediately and every fresh token would want
 * the same again: this turns that spin into a steady cadence. */
const RENEW_MIN_DELAY_MS = 60_000;

interface AuthContextValue {
  session: Session | null;
  /** Set when the previous session ended involuntarily (expiry or a 401), so the login page can say why. */
  sessionEndedReason: string | null;
  hasScope: (scope: string) => boolean;
  loginLocal: (username: string, password: string, remember: boolean) => Promise<void>;
  loginEntra: (entra: EntraProviderInfo, remember: boolean) => Promise<void>;
  loginBootstrap: (secret: string) => Promise<void>;
  logout: () => void;
}

const AuthContext = createContext<AuthContextValue | null>(null);

const STORAGE_KEY = "sqlflow.session";

// The token lives in memory plus one web-storage backer, chosen at login time:
//   - sessionStorage (default): an F5 keeps the session, but closing the tab ends it.
//   - localStorage ("Keep me signed in on this device"): the session survives a browser restart, and rolls onto a
//     fresh token whenever SQLFlow is opened, so the device stays signed in up to the server's absolute cap. This
//     is the more exposed posture (readable across restarts by any script on the origin), so it is opt-in per
//     sign-in rather than the default.
// The bootstrap secret / password never persists anywhere. Whichever backer is not in use is always cleared, so a
// session never lingers in both.
// Each backer paired with the "remember" posture it represents. Built on call, never at module scope: web storage is
// only reachable once there is a document, and touching it at import time would fault before the app can render.
function backerPairs(): ReadonlyArray<readonly [Storage, boolean]> {
  return [
    [window.localStorage, true],
    [window.sessionStorage, false],
  ];
}

function backers(): Storage[] {
  return backerPairs().map(([store]) => store);
}

function restoreSession(): Session | null {
  for (const [store, remember] of backerPairs()) {
    try {
      const raw = store.getItem(STORAGE_KEY);
      if (!raw) {
        continue;
      }

      const parsed = JSON.parse(raw) as Session;
      if (!parsed.token || typeof parsed.expiresAtMs !== "number" || parsed.expiresAtMs <= Date.now()) {
        store.removeItem(STORAGE_KEY);
        continue;
      }

      // Both flags are derived, never read back from the payload: which store held it is what "remember" means, and
      // only a user-backed session (one with a role behind it) is one the server will roll.
      return { ...parsed, remember, renewable: parsed.role !== null };
    } catch {
      store.removeItem(STORAGE_KEY);
    }
  }

  return null;
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [session, setSession] = useState<Session | null>(() => {
    const restored = restoreSession();
    setAuthToken(restored?.token ?? null);
    return restored;
  });
  const [sessionEndedReason, setSessionEndedReason] = useState<string | null>(null);
  // Whether this page load's opening roll is settled. A session restored from storage is rolled once as soon as the
  // app loads; a token that just came from the server (a fresh sign-in) needs no such roll. Set only once an outcome
  // lands, never when one is merely started, so an attempt abandoned mid-flight is retried rather than lost.
  const rolledOnLoad = useRef(session === null);

  const endSession = useCallback((reason: string | null) => {
    for (const store of backers()) {
      store.removeItem(STORAGE_KEY);
    }
    // Whatever comes next is a fresh sign-in holding a fresh token: nothing left to roll on this load.
    rolledOnLoad.current = true;
    setAuthToken(null);
    setSession(null);
    setSessionEndedReason(reason);
  }, []);

  const beginSession = useCallback((next: NewSession, remember: boolean, renewable: boolean) => {
    const full: Session = { ...next, remember, renewable };
    const [persistent, ephemeral] = remember
      ? [window.localStorage, window.sessionStorage]
      : [window.sessionStorage, window.localStorage];
    ephemeral.removeItem(STORAGE_KEY);
    persistent.setItem(STORAGE_KEY, JSON.stringify(full));

    // What the next sign-in prefills, recorded here rather than at the form: the subject is the server's answer to
    // who this is, so an Entra sign-in (which types no username) is covered by the same line as a local one, and a
    // local one records the account's canonical name rather than whichever casing was typed. `renewable` is exactly
    // the user-backed test: the break-glass session has no name worth offering back and leaves no trace.
    if (renewable) {
      saveLoginPrefs({ username: full.subject, remember });
    }

    setAuthToken(full.token);
    setSession(full);
    setSessionEndedReason(null);
  }, []);

  // A 401 on an authenticated call means the token no longer works (expired or the signing key rotated).
  useEffect(() => {
    setUnauthorizedHandler(() => endSession("Your session is no longer valid; sign in again."));
    return () => setUnauthorizedHandler(null);
  }, [endSession]);

  // Keep the session alive by rolling its token, rather than watching it die. One issued token stays short-lived
  // (so a leaked one is short-lived too) while the person behind it stays signed in for as long as they keep using
  // SQLFlow, up to the server's absolute cap.
  useEffect(() => {
    if (!session) {
      return;
    }

    let cancelled = false;
    let inFlight = false;
    let timer: number | null = null;

    const clear = () => {
      if (timer !== null) {
        window.clearTimeout(timer);
        timer = null;
      }
    };

    const later = (ms: number) => {
      clear();
      timer = window.setTimeout(() => void attempt(), Math.max(0, ms));
    };

    const attempt = async () => {
      // The timer and the visibility check can both want a roll at once; one in flight is enough.
      if (cancelled || inFlight) {
        return;
      }

      // Too little runway left for a round trip to be worth trusting: the session is over.
      const untilFloor = session.expiresAtMs - RENEW_FLOOR_MS - Date.now();
      if (untilFloor <= 0) {
        endSession("Your session expired; sign in again.");
        return;
      }

      inFlight = true;
      try {
        const response = await authApi.renew();
        if (cancelled) {
          return;
        }

        rolledOnLoad.current = true;
        // The roll re-reads the account server-side, so a role or scope change lands here too.
        beginSession({
          token: response.accessToken,
          subject: response.subject,
          role: response.role,
          scopes: response.scopes,
          expiresAtMs: Date.now() + response.expiresIn * 1000,
        }, session.remember, true);
      } catch (error) {
        if (cancelled) {
          return;
        }

        // 401: past the absolute cap, or the account was deactivated. The client's own 401 handler has already
        // ended the session, so there is nothing to add here.
        if (isApiError(error) && error.status === 401) {
          return;
        }

        // 403: the server refuses to roll this credential at all. Nothing to retry.
        if (isApiError(error) && error.status === 403) {
          endSession("Your session ended; sign in again.");
          return;
        }

        // Anything else is transport (offline, a blip, a rate-limit pause): the token in hand is still valid, so
        // keep trying against the runway that is left instead of signing the user out over one failed request.
        later(Math.min(RENEW_RETRY_MS, untilFloor));
      } finally {
        inFlight = false;
      }
    };

    if (!session.renewable) {
      // The break-glass session never rolls: end it when its token lapses, as before. Deliberately not routed
      // through attempt(), which would spend a request learning what this flag already says.
      timer = window.setTimeout(
        () => endSession("Your session expired; sign in again."),
        Math.max(0, session.expiresAtMs - RENEW_FLOOR_MS - Date.now()),
      );
      return () => {
        cancelled = true;
        clear();
      };
    }

    if (rolledOnLoad.current) {
      later(Math.max(session.expiresAtMs - RENEW_LEAD_MS - Date.now(), RENEW_MIN_DELAY_MS));
    } else {
      // Rolling on load is what makes "keep me signed in on this device" hold overnight: a timer only fires while a
      // tab is open, so the window has to reset when SQLFlow is opened, not just while it is being watched.
      void attempt();
    }

    // A machine that slept does not fire its timers on schedule, and a backgrounded tab has its timers throttled.
    // Re-check on the way back so a session is not lost to a closed lid.
    const onVisible = () => {
      if (document.visibilityState === "visible" && Date.now() >= session.expiresAtMs - RENEW_LEAD_MS) {
        void attempt();
      }
    };

    document.addEventListener("visibilitychange", onVisible);
    return () => {
      cancelled = true;
      clear();
      document.removeEventListener("visibilitychange", onVisible);
    };
  }, [session, endSession, beginSession]);

  const loginLocal = useCallback(async (username: string, password: string, remember: boolean) => {
    const response = await authApi.login(username, password);
    beginSession({
      token: response.accessToken,
      subject: response.subject,
      role: response.role,
      scopes: response.scopes,
      expiresAtMs: Date.now() + response.expiresIn * 1000,
    }, remember, true);
  }, [beginSession]);

  const loginEntra = useCallback(async (entra: EntraProviderInfo, remember: boolean) => {
    const idToken = await signInWithEntra(entra);
    const response = await authApi.exchange(idToken);
    beginSession({
      token: response.accessToken,
      subject: response.subject,
      role: response.role,
      scopes: response.scopes,
      expiresAtMs: Date.now() + response.expiresIn * 1000,
    }, remember, true);
  }, [beginSession]);

  const loginBootstrap = useCallback(async (secret: string) => {
    // The bootstrap secret is the root credential: request everything it grants. A break-glass session is never
    // persisted across a browser restart, so it always uses the ephemeral (sessionStorage) backer, and it never
    // rolls: it is meant to lapse and be re-presented deliberately.
    const scopes = ["read", "operate", "admin"];
    const response = await authApi.bootstrapToken(secret, scopes);
    beginSession({
      token: response.accessToken,
      subject: "bootstrap",
      role: null,
      scopes,
      expiresAtMs: Date.now() + response.expiresIn * 1000,
    }, false, false);
  }, [beginSession]);

  const logout = useCallback(() => endSession(null), [endSession]);

  const value = useMemo<AuthContextValue>(() => ({
    session,
    sessionEndedReason,
    // The privilege model has two tiers, mirroring the control plane's authorization policies: any authenticated
    // user gets the whole operational product, and only user administration (the "admin" scope) is fenced off. So
    // read/operate/author are granted to any live session, and only "admin" consults the token's scopes. This keeps
    // the UI from hiding a feature the API would in fact allow.
    hasScope: (scope: string) => {
      if (session === null) {
        return false;
      }

      return scope === "admin" ? session.scopes.includes("admin") : true;
    },
    loginLocal,
    loginEntra,
    loginBootstrap,
    logout,
  }), [session, sessionEndedReason, loginLocal, loginEntra, loginBootstrap, logout]);

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

export function useAuth(): AuthContextValue {
  const context = useContext(AuthContext);
  if (!context) {
    throw new Error("useAuth must be used inside AuthProvider.");
  }

  return context;
}
