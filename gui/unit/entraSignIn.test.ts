// Entra's answer to a sign-in, told apart from a visit to the workbench, and the words the sign-in page gives MSAL's
// failures now that MSAL 5 ships only their codes.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { entraSignInFailureMessage, isEntraSignInResponse } from "../src/auth/entraSignIn.ts";

describe("isEntraSignInResponse", () => {
  it("reads a code answer as Entra's", () => {
    assert.equal(isEntraSignInResponse("#code=0.AXk&client_info=eyJ1&state=eyJpZCI6IjEifQ%3D%3D&session_state=0a1b"), true);
  });

  it("reads an error answer as Entra's", () => {
    assert.equal(isEntraSignInResponse("#error=access_denied&error_description=AADSTS65004&state=eyJpZCI6IjEifQ"), true);
  });

  it("reads the fragment with or without its leading #", () => {
    assert.equal(isEntraSignInResponse("code=abc&state=xyz"), true);
  });

  it("reads a load with no fragment, or an empty one, as a visit", () => {
    assert.equal(isEntraSignInResponse(""), false);
    assert.equal(isEntraSignInResponse("#"), false);
  });

  it("asks for the request's state as well as a code or an error", () => {
    assert.equal(isEntraSignInResponse("#code=abc"), false);
    assert.equal(isEntraSignInResponse("#error=access_denied"), false);
    assert.equal(isEntraSignInResponse("#state=xyz"), false);
    assert.equal(isEntraSignInResponse("#codes=abc&states=xyz"), false);
  });

  it("reads a fragment of the page's own as a visit", () => {
    assert.equal(isEntraSignInResponse("#section-2"), false);
    assert.equal(isEntraSignInResponse("#/runs?state=failed"), false);
  });
});

describe("entraSignInFailureMessage", () => {
  const docsLink = (code: string) => `${code}: See https://aka.ms/msal.js.errors#${code} for details`;

  it("words a failure MSAL 3 described as MSAL 3 did", () => {
    assert.equal(entraSignInFailureMessage("user_cancelled", docsLink("user_cancelled")), "user_cancelled: User cancelled the flow.");
    assert.equal(
      entraSignInFailureMessage("popup_window_error", docsLink("popup_window_error")),
      "popup_window_error: Error opening popup window. This can happen if you are using IE or if popups are blocked in the browser.",
    );
  });

  it("words the popup timeout, which is also how MSAL 5 reports a popup closed before signing in", () => {
    assert.equal(
      entraSignInFailureMessage("timed_out", docsLink("timed_out")),
      "timed_out: Sign-in did not complete in time. If the Microsoft sign-in window was closed before signing in, sign in again.",
    );
  });

  it("keeps Entra's own description for an error Entra returned", () => {
    const message = "access_denied: AADSTS65004: User declined to consent to access the app.";
    assert.equal(entraSignInFailureMessage("access_denied", message), message);
  });

  it("keeps MSAL's message for a code it does not describe", () => {
    assert.equal(entraSignInFailureMessage("ear_jwk_empty", docsLink("ear_jwk_empty")), docsLink("ear_jwk_empty"));
  });

  it("takes no wording from the object prototype", () => {
    for (const code of ["constructor", "toString", "__proto__", "hasOwnProperty"]) {
      assert.equal(entraSignInFailureMessage(code, docsLink(code)), docsLink(code), code);
    }
  });
});
