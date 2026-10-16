// How the GUI recognises Entra's answer to a sign-in and how it words MSAL's failures. Kept apart from MSAL, with
// nothing to import, so the unit suite reads it.

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

/**
 * What each MSAL failure a sign-in can meet means. MSAL 5 no longer ships its error text (an error's message is a link
 * to its documentation), so the failures MSAL 3 described keep MSAL 3's sentences, and the ones MSAL 5 added have
 * sentences of their own. An error Entra itself returns carries Entra's description and is not listed.
 */
const signInFailureText = new Map<string, string>([
  // Opening and following the sign-in popup.
  ["popup_window_error", "Error opening popup window. This can happen if you are using IE or if popups are blocked in the browser."],
  ["empty_window_error", "window.open returned null or undefined window object."],
  ["user_cancelled", "User cancelled the flow."],
  ["block_nested_popups", "Request was blocked inside a popup because MSAL detected it was running in a popup."],
  ["interaction_in_progress", "Interaction is currently in progress. Please ensure that this interaction has been completed before calling an interactive API."],
  ["interaction_in_progress_cancelled", "The sign-in in progress was cancelled because another one started."],
  ["timed_out", "Sign-in did not complete in time. If the Microsoft sign-in window was closed before signing in, sign in again."],
  ["empty_response", "The sign-in window holds no answer from Microsoft. Please verify that the request originated from msal."],
  ["redirect_bridge_empty_response", "The sign-in window passed back an empty answer from Microsoft."],
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
