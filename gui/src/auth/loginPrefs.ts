/**
 * What the login page prefills on a return visit: the subject of the last successful sign-in, and whether that
 * sign-in asked to be kept on this device.
 *
 * This exists because the obvious alternative does not work: the username cannot be recovered from the session
 * token, because by the time the login page renders there is no token left to read. Sign-out, expiry, and a 401 all
 * wipe it. Keeping a dead token around purely to decode a name out of it would mean parking a bearer credential
 * that is very possibly still inside its validity window, which is a strictly worse trade than parking a username.
 *
 * Deliberately stored apart from the session, and deliberately not cleared by sign-out: a session ending is exactly
 * when these matter most, because the next thing that happens is someone typing their username back in. Nothing
 * secret is ever kept here (no password, no token, no bootstrap secret), so localStorage is the right backer even
 * for a sign-in that chose not to persist its session: "do not keep me signed in" is a statement about the
 * credential, not about the name on the door.
 */
const PREFS_KEY = "sqlflow.login";

export interface LoginPrefs {
  /** The last username signed in with locally. Empty when nobody has signed in on this device yet. */
  username: string;
  /** Whether the last sign-in ticked "Keep me signed in on this device". */
  remember: boolean;
}

const NONE: LoginPrefs = { username: "", remember: false };

/** Reads the prefill for this device, falling back to blank whenever storage is unreadable or holds junk. */
export function readLoginPrefs(): LoginPrefs {
  let raw: string | null;
  try {
    raw = window.localStorage.getItem(PREFS_KEY);
  } catch {
    // Storage blocked entirely (a hardened profile, or third-party-cookie style restrictions). There is no prefill
    // to offer, and that is the whole consequence: the form still works, it just starts empty.
    return NONE;
  }

  if (!raw) {
    return NONE;
  }

  try {
    // Every field is re-checked rather than trusted: this is attacker-writable in the same sense any origin storage
    // is, and a non-string username would reach a controlled input as `value` and break the form.
    const parsed = JSON.parse(raw) as Partial<LoginPrefs>;
    return {
      username: typeof parsed.username === "string" ? parsed.username : "",
      remember: parsed.remember === true,
    };
  } catch {
    // Unparseable: drop it rather than fight it on every page load.
    try {
      window.localStorage.removeItem(PREFS_KEY);
    } catch {
      // Nothing left to do; the read below is what matters and it already has its answer.
    }

    return NONE;
  }
}

/** Merges an update into the stored prefill. Called from beginSession once the server has accepted a user-backed
 * sign-in, so a rejected credential never becomes a prefill. */
export function saveLoginPrefs(update: Partial<LoginPrefs>): void {
  const next: LoginPrefs = { ...readLoginPrefs(), ...update };
  try {
    window.localStorage.setItem(PREFS_KEY, JSON.stringify(next));
  } catch {
    // Storage full or blocked. A prefill that fails to save is a convenience lost, never a reason to fail a sign-in
    // that has already succeeded against the server.
  }
}
