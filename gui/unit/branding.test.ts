// The product's branding: what a module sets, what a deployment's config.json may override, and what stays SQLFlow's.

import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { applyRuntimeBranding, branding, installModuleBranding, parseRuntimeBranding } from "../src/modules/branding.ts";

describe("installModuleBranding", () => {
  it("keeps SQLFlow's pitch and streams when no module brands the product", () => {
    installModuleBranding([]);
    applyRuntimeBranding(undefined);
    const login = branding().login;
    assert.equal(login.headline, "Move data with confidence.");
    assert.ok(login.headlineAccent.length > 0);
    assert.deepEqual(login.streams, ["CSV", "Parquet", "JSON", "Excel", "XML"]);
  });

  it("takes a module's pitch and streams, and keeps SQLFlow's for what the module leaves out", () => {
    installModuleBranding([{ moduleId: "m", branding: { login: { headline: "Ship it.", streams: ["A", "B"] } } }]);
    applyRuntimeBranding(undefined);
    const login = branding().login;
    assert.equal(login.headline, "Ship it.");
    assert.deepEqual(login.streams, ["A", "B"]);
    assert.equal(login.headlineAccent, "Follow it end to end.");
    installModuleBranding([]);
  });

  it("refuses a second module that brands the product", () => {
    assert.throws(
      () => installModuleBranding([{ moduleId: "a", branding: {} }, { moduleId: "b", branding: {} }]),
      /Only one GUI module may brand the product/,
    );
  });
});

describe("parseRuntimeBranding", () => {
  it("reads the pitch's lines from config.json, and ignores blank and non-string values", () => {
    const overrides = parseRuntimeBranding({ login: { headline: "H", headlineAccent: "A", tagline: " " } });
    assert.deepEqual(overrides?.login, { headline: "H", headlineAccent: "A", tagline: undefined });
    assert.deepEqual(parseRuntimeBranding({ login: { headlineAccent: 7 } })?.login, {
      headline: undefined,
      headlineAccent: undefined,
      tagline: undefined,
    });
  });

  it("lets a deployment override the accent line over the module's, and nothing it does not name", () => {
    installModuleBranding([{ moduleId: "m", branding: { login: { headline: "Ship it.", headlineAccent: "Fast." } } }]);
    applyRuntimeBranding(parseRuntimeBranding({ login: { headlineAccent: "Faster." } }));
    assert.equal(branding().login.headline, "Ship it.");
    assert.equal(branding().login.headlineAccent, "Faster.");
    installModuleBranding([]);
    applyRuntimeBranding(undefined);
  });
});
