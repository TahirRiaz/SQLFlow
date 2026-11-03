// Entra's answer to a sign-in, told apart from a visit to the workbench and held for the login page; the account a
// sign-in names to Entra; the state it carries there and back; and the words the sign-in page gives MSAL's failures now
// that MSAL 5 ships only their codes.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import {
  decodeEntraSignInState,
  encodeEntraSignInState,
  entraLoginHint,
  entraSignInFailureMessage,
  holdEntraSignInAnswer,
  isEntraSignInResponse,
  sameAppPath,
  takeEntraSignInAnswer,
} from "../src/auth/entraSignIn.ts";

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

describe("takeEntraSignInAnswer", () => {
  it("hands the held answer over once", () => {
    holdEntraSignInAnswer("#code=abc&state=xyz");
    assert.equal(takeEntraSignInAnswer(), "#code=abc&state=xyz");
    assert.equal(takeEntraSignInAnswer(), null);
  });

  it("holds nothing on a visit", () => {
    assert.equal(takeEntraSignInAnswer(), null);
  });
});

describe("entraLoginHint", () => {
  it("names the account an email address is", () => {
    assert.equal(entraLoginHint("TRIAZ@equinor.com"), "TRIAZ@equinor.com");
    assert.equal(entraLoginHint("  ops@contoso.onmicrosoft.com "), "ops@contoso.onmicrosoft.com");
  });

  it("names no account for a local user name or an empty field", () => {
    assert.equal(entraLoginHint("admin"), undefined);
    assert.equal(entraLoginHint(""), undefined);
    assert.equal(entraLoginHint("   "), undefined);
  });

  it("names no account for text that only looks like an address", () => {
    for (const text of ["@contoso.com", "ops@", "a@b@c.com", "first last@contoso.com"]) {
      assert.equal(entraLoginHint(text), undefined, text);
    }
  });
});

describe("sameAppPath", () => {
  it("keeps a path of the GUI's own", () => {
    assert.equal(sameAppPath("/runs?state=failed"), "/runs?state=failed");
    assert.equal(sameAppPath("/"), "/");
  });

  it("turns a foreign or protocol-relative address into the home page", () => {
    for (const path of ["https://evil.example/", "//evil.example/x", "runs", ""]) {
      assert.equal(sameAppPath(path), "/", path);
    }
  });
});

describe("Entra sign-in state", () => {
  it("comes back as it was sent", () => {
    const state = { remember: true, returnTo: "/device?code=ABCD-EFGH" };
    assert.deepEqual(decodeEntraSignInState(encodeEntraSignInState(state)), state);
  });

  it("re-checks the address it brings back", () => {
    assert.deepEqual(
      decodeEntraSignInState(JSON.stringify({ remember: true, returnTo: "//evil.example/" })),
      { remember: true, returnTo: "/" },
    );
  });

  it("reads a sign-in that sent no state of the GUI's as one that keeps nothing and goes home", () => {
    const none = { remember: false, returnTo: "/" };
    for (const state of [undefined, "", "not json", "null", "42", "[]", JSON.stringify({ remember: "yes", returnTo: 7 })]) {
      assert.deepEqual(decodeEntraSignInState(state), none, String(state));
    }
  });
});

describe("entraSignInFailureMessage", () => {
  const docsLink = (code: string) => `${code}: See https://aka.ms/msal.js.errors#${code} for details`;

  it("words a failure MSAL 3 described as MSAL 3 did", () => {
    assert.equal(entraSignInFailureMessage("user_cancelled", docsLink("user_cancelled")), "user_cancelled: User cancelled the flow.");
    assert.equal(entraSignInFailureMessage("state_mismatch", docsLink("state_mismatch")), "state_mismatch: State mismatch error");
  });

  it("words the redirect timeout, MSAL's report of a page that never left for Microsoft's sign-in", () => {
    assert.equal(
      entraSignInFailureMessage("timed_out", docsLink("timed_out")),
      "timed_out: The browser did not open Microsoft's sign-in page in time. Sign in again.",
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
