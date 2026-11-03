// How the GUI recognises Entra's answer to a sign-in, what a sign-in carries through Entra and back, and how it words
// MSAL's failures. Kept apart from MSAL, with nothing to import, so the unit suite reads it.

/**
 * Whether a page load is Entra answering a sign-in rather than a visit to the workbench. MSAL asks for the answer in
 * the URL fragment (its default response mode), and an answer always carries the request's state with either an
 * authorization code or an error. The GUI's own routes never use the fragment, so no visit reads as one.
 */
export function isEntraSignInResponse(hash: string): boolean {
  const content = hash.startsWith("#") ? hash.slice(1) : hash;
  if (content === "") {
    return false;
  }

  const params = new URLSearchParams(content);
  return params.has("state") && (params.has("code") || params.has("error"));
}

/** Entra's answer, held from the page load that brought it until the login page finishes the sign-in with it. */
let heldAnswer: string | null = null;

/** Holds Entra's answer (the URL fragment Entra loaded the GUI with) for the login page to finish the sign-in with. */
export function holdEntraSignInAnswer(hash: string): void {
  heldAnswer = hash;
}

/**
 * Hands over the held answer, once: a sign-in is finished a single time, so a second call (React's strict mode runs
 * the login page's effects twice) finds none.
 */
export function takeEntraSignInAnswer(): string | null {
  const answer = heldAnswer;
  heldAnswer = null;
  return answer;
}

/**
 * The account a sign-in names to Entra, so that Entra signs that account in without asking which one to use: the
 * username on the login form when it reads as an email address, which an Entra user name is and which the form holds
 * after an Entra sign-in (it prefills the last sign-in's name). A local user name names no Microsoft account and is not
 * sent; Entra then asks which account only when the browser holds more than one.
 */
export function entraLoginHint(username: string): string | undefined {
  const name = username.trim();
  const at = name.indexOf("@");
  const isAddress = at > 0 && at === name.lastIndexOf("@") && at < name.length - 1 && !/\s/.test(name);
  return isAddress ? name : undefined;
}

/**
 * The address a sign-in may send the page to: a path of the GUI's own. Anything else (a foreign origin, or a
 * protocol-relative "//host") must never become a navigation target, and the page goes to the workbench's home instead.
 */
export function sameAppPath(path: string): string {
  return path.startsWith("/") && !path.startsWith("//") ? path : "/";
}

/** What a sign-in carries through Entra and back in the request's state: how to keep the session, and where to go. */
export interface EntraSignInState {
  /** Whether the sign-in asked to be kept on this device. */
  remember: boolean;
  /** The GUI address the sign-in was asked for from, where the page goes once it is signed in. */
  returnTo: string;
}

/** The state a sign-in sends to Entra, which Entra hands back unchanged with its answer. */
export function encodeEntraSignInState(state: EntraSignInState): string {
  return JSON.stringify({ remember: state.remember, returnTo: state.returnTo });
}

/**
 * Reads the state an answer brought back. MSAL has already matched the answer to the request this tab sent, so the
 * state is the GUI's own; a sign-in that a GUI before this one started sent none, and it signs in as a sign-in that
 * keeps nothing and asks for the home page does. The address is re-checked as any other returnTo is.
 */
export function decodeEntraSignInState(state: string | undefined): EntraSignInState {
  const fallback: EntraSignInState = { remember: false, returnTo: "/" };
  if (state === undefined || state === "") {
    return fallback;
  }

  let parsed: unknown;
  try {
    parsed = JSON.parse(state);
  } catch {
    return fallback;
  }

  if (typeof parsed !== "object" || parsed === null) {
    return fallback;
  }

  const { remember, returnTo } = parsed as Partial<Record<keyof EntraSignInState, unknown>>;
  return {
    remember: remember === true,
    returnTo: typeof returnTo === "string" ? sameAppPath(returnTo) : "/",
  };
}

/**
 * What each MSAL failure a sign-in can meet means. MSAL 5 no longer ships its error text (an error's message is a link
 * to its documentation), so the failures MSAL 3 described keep MSAL 3's sentences, and the ones MSAL 5 added have
 * sentences of their own. An error Entra itself returns carries Entra's description and is not listed.
 */
const signInFailureText = new Map<string, string>([
  // Leaving for Microsoft's sign-in page.
  ["user_cancelled", "User cancelled the flow."],
  ["interaction_in_progress", "Interaction is currently in progress. Please ensure that this interaction has been completed before calling an interactive API."],
  ["interaction_in_progress_cancelled", "The sign-in in progress was cancelled because another one started."],
  ["timed_out", "The browser did not open Microsoft's sign-in page in time. Sign in again."],
  ["redirect_in_iframe", "Microsoft sign-in cannot open inside a frame. Open the GUI in a browser tab of its own and sign in there."],
  // Reading Entra's answer.
  ["hash_empty_error", "Hash value cannot be processed because it is empty. Please verify that your redirectUri is not clearing the hash."],
  ["hash_does_not_contain_known_properties", "Hash does not contain known properties. Please verify that your redirectUri is not changing the hash."],
  ["no_state_in_hash", "Hash does not contain state. Please verify that the request originated from msal."],
  ["unable_to_parse_state", "Unable to parse state. Please verify that the request originated from msal."],
  ["state_interaction_type_mismatch", "Hash contains state but the interaction type does not match the caller."],
  ["hash_not_deserialized", "The hash parameters could not be deserialized"],
  ["invalid_state", "State was not the expected format"],
  ["state_mismatch", "State mismatch error"],
  ["state_not_found", "State not found"],
  ["nonce_mismatch", "Nonce mismatch error"],
  ["authorization_code_missing_from_server_response", "Server response does not contain an authorization code to proceed"],
  // Reaching Entra.
  ["no_network_connectivity", "No network connectivity. Check your internet connection."],
  ["network_error", "Network request failed"],
  ["post_request_failed", "Network request failed: If the browser threw a CORS error, check that the redirectUri is registered in the Azure App Portal as type 'SPA'"],
  ["get_request_failed", "Network request failed. Please check the network trace to determine root cause."],
  ["failed_to_parse_response", "Failed to parse network response. Check network trace."],
  ["endpoints_resolution_error", "Endpoints cannot be resolved"],
  ["openid_config_error", "Could not retrieve endpoints. Check your authority and verify the .well-known/openid-configuration endpoint returns the required endpoints."],
  // The authority the control plane names, and the browser the GUI runs in.
  ["authority_uri_insecure", "Authority URIs must use https."],
  ["url_parse_error", "URL could not be parsed into appropriate segments."],
  ["empty_url_error", "URL was empty or null."],
  ["untrusted_authority", "The provided authority is not a trusted authority. Please include this authority in the knownAuthorities config parameter."],
  ["redirect_uri_empty", "A redirect URI is required for all calls, and none has been set."],
  ["crypto_nonexistent", "The crypto object or function is not available."],
  ["pkce_not_created", "The PKCE code challenge and verifier could not be generated."],
  ["non_browser_environment", "Login and token requests are not supported in non-browser environments."],
]);

/**
 * The message the sign-in page shows for an MSAL error: its code and what it means, or, for a code not described here
 * (an error Entra returned, which carries Entra's own description), the error's own message.
 */
export function entraSignInFailureMessage(errorCode: string, message: string): string {
  const text = signInFailureText.get(errorCode);
  return text === undefined ? message : `${errorCode}: ${text}`;
}
