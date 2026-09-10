import { PublicClientApplication, type AuthenticationResult } from "@azure/msal-browser";
import type { EntraProviderInfo } from "../api/types";

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
 * Runs the interactive Entra sign-in (popup; auth code + PKCE under the hood) and returns the ID token to
 * exchange at POST /auth/exchange. The openid/profile/email scopes are all the exchange needs: the control plane
 * reads oid, preferred_username, name, and email from the ID token.
 */
export async function signInWithEntra(entra: EntraProviderInfo): Promise<string> {
  const msal = await getInstance(entra);
  const result: AuthenticationResult = await msal.loginPopup({
    scopes: ["openid", "profile", "email"],
    prompt: "select_account",
  });
  if (!result.idToken) {
    throw new Error("Entra sign-in completed without an ID token; cannot exchange it for a SQLFlow session.");
  }

  return result.idToken;
}
