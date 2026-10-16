import { AuthError, PublicClientApplication, type AuthenticationResult } from "@azure/msal-browser";
import { broadcastResponseToMainFrame } from "@azure/msal-browser/redirect-bridge";
import type { EntraProviderInfo } from "../api/types";
import { entraSignInFailureMessage } from "./entraSignIn";

// MSAL is created lazily from the providers discovery response (GET /auth/providers), so the SPA needs no
// build-time Entra configuration: the control plane is the single source of the tenant/client ids.

let instance: PublicClientApplication | null = null;
let instanceKey = "";

async function getInstance(entra: EntraProviderInfo): Promise<PublicClientApplication> {
  if (!entra.enabled || !entra.clientId || !entra.authority) {
    throw new Error("Entra sign-in is not enabled on this control plane.");
  }

  const key = `${entra.clientId}|${entra.authority}`;
  if (!instance || instanceKey !== key) {
    instance = new PublicClientApplication({
      auth: {
        clientId: entra.clientId,
        authority: entra.authority,
        // The GUI origin is the redirect URI every app registration names. Entra's answer lands there in the sign-in
        // popup, where the page relays it back to this window (relayEntraSignInResponse) instead of starting the GUI.
        redirectUri: window.location.origin,
      },
      cache: {
        // Session-scoped, matching how the SQLFlow token itself is held.
        cacheLocation: "sessionStorage",
      },
    });
    await instance.initialize();
    instanceKey = key;
  }

  return instance;
}

/**
 * An MSAL failure as the sign-in page shows it: MSAL's own failures carry only a code and a documentation link, so
 * they are described by code, and anything else (an error of the GUI's own) passes through as it is.
 */
function describeSignInFailure(error: unknown): unknown {
  if (error instanceof AuthError) {
    return new Error(entraSignInFailureMessage(error.errorCode, error.message), { cause: error });
  }

  return error;
}

/**
 * Runs the interactive Entra sign-in (popup; auth code + PKCE under the hood) and returns the ID token to
 * exchange at POST /auth/exchange. The openid/profile/email scopes are all the exchange needs: the control plane
 * reads oid, preferred_username, name, and email from the ID token.
 */
export async function signInWithEntra(entra: EntraProviderInfo): Promise<string> {
  let result: AuthenticationResult;
  try {
    const msal = await getInstance(entra);
    result = await msal.loginPopup({
      scopes: ["openid", "profile", "email"],
      prompt: "select_account",
    });
  } catch (error) {
    throw describeSignInFailure(error);
  }

  if (!result.idToken) {
    throw new Error("Entra sign-in completed without an ID token; cannot exchange it for a SQLFlow session.");
  }

  return result.idToken;
}

/**
 * Completes a sign-in in the popup Entra answered (the page load isEntraSignInResponse recognises): MSAL's redirect
 * bridge hands the answer to the window waiting on it and closes the popup. Should the popup stay open, it says how
 * the sign-in went rather than showing an empty page; when the answer never reaches the window that asked, that
 * window's sign-in fails on its own timeout.
 */
export function relayEntraSignInResponse(container: HTMLElement): void {
  broadcastResponseToMainFrame().then(
    () => {
      container.textContent = "Signed in. This window can be closed.";
    },
    (error: unknown) => {
      const failure = describeSignInFailure(error);
      const reason = failure instanceof Error ? failure.message : String(failure);
      container.textContent = `Sign-in could not be completed (${reason}). Close this window and sign in again.`;
    },
  );
}
