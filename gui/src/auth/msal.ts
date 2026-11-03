import { AuthError, PublicClientApplication, type AuthenticationResult } from "@azure/msal-browser";
import type { EntraProviderInfo } from "../api/types";
import { decodeEntraSignInState, encodeEntraSignInState, entraSignInFailureMessage, type EntraSignInState } from "./entraSignIn";

// MSAL is created lazily from the providers discovery response (GET /auth/providers), so the SPA needs no
// build-time Entra configuration: the control plane is the single source of the tenant/client ids.

let instance: PublicClientApplication | null = null;
let instanceKey = "";

/** The openid/profile/email scopes are all the exchange needs: the control plane reads oid, preferred_username, name,
 * and email from the ID token. */
const SIGN_IN_SCOPES = ["openid", "profile", "email"];

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
        // The GUI origin is the redirect URI every app registration names. Entra's answer loads it in this tab, where
        // the bootstrap holds the answer for the login page (finishEntraSignIn) instead of starting the workbench there.
        redirectUri: window.location.origin,
      },
      cache: {
        // Session-scoped, matching how the SQLFlow token itself is held. The tab that leaves for Microsoft's sign-in
        // is the one Entra's answer returns to, so the request MSAL keeps for it is still there.
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
 * Starts the interactive Entra sign-in (auth code + PKCE under the hood) in this tab: the page leaves for Microsoft's
 * sign-in, and Entra answers by loading the GUI's origin again, carrying `state` back with it. Naming the account
 * (`loginHint`) lets Entra sign it in without asking which account to use when the browser already holds it, as Edge
 * does for the account Windows is signed in with. The returned promise only ever rejects: when the page could not
 * leave, or when the person comes back from Microsoft's page with the browser's Back button.
 */
export async function startEntraSignIn(
  entra: EntraProviderInfo,
  loginHint: string | undefined,
  state: EntraSignInState,
): Promise<never> {
  try {
    const msal = await getInstance(entra);
    // A sign-in that left for Microsoft and never came back (Back on a page the browser loaded again) leaves its
    // request marked in progress, and MSAL refuses another while one is. Settling this page load, which carries no
    // answer, clears it.
    await msal.handleRedirectPromise({ navigateToLoginRequestUrl: false });
    await msal.loginRedirect({ scopes: SIGN_IN_SCOPES, loginHint, state: encodeEntraSignInState(state) });
  } catch (error) {
    throw describeSignInFailure(error);
  }

  // MSAL settles a redirect only when the navigation was stopped, so the page is still here and nothing signed in.
  throw new Error("The browser stayed on this page instead of opening Microsoft's sign-in. Sign in again.");
}

/** A finished Entra sign-in: the ID token to exchange at POST /auth/exchange, and the state the sign-in started with. */
export interface EntraSignIn {
  idToken: string;
  state: EntraSignInState;
}

/**
 * Finishes the sign-in Entra answered (the answer the bootstrap held, see holdEntraSignInAnswer): MSAL matches it to the
 * request this tab sent and redeems its code for the ID token.
 */
export async function finishEntraSignIn(entra: EntraProviderInfo, answer: string): Promise<EntraSignIn> {
  let result: AuthenticationResult | null;
  try {
    const msal = await getInstance(entra);
    result = await msal.handleRedirectPromise({ hash: answer, navigateToLoginRequestUrl: false });
  } catch (error) {
    throw describeSignInFailure(error);
  }

  if (result === null) {
    // MSAL keeps no request this answer belongs to: the sign-in was started in another tab, or already finished.
    throw new Error("Microsoft answered a sign-in this tab is not waiting for. Sign in again.");
  }

  if (!result.idToken) {
    throw new Error("Entra sign-in completed without an ID token; cannot exchange it for a SQLFlow session.");
  }

  return { idToken: result.idToken, state: decodeEntraSignInState(result.state) };
}
