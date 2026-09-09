import { useState, type FormEvent } from "react";
import { Navigate, useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, CircleAlert, Info, Loader2, ShieldCheck, Sparkles, Waypoints, Workflow } from "lucide-react";
import type { LucideIcon } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Checkbox } from "@/components/ui/checkbox";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { authApi } from "../api/endpoints";
import { isApiError } from "../api/client";
import { CorrelationError } from "../components/CorrelationError";
import { useAuth } from "./AuthContext";
import { readLoginPrefs } from "./loginPrefs";

/** Which sign-in path is in flight, so only the button that started the work shows the spinner. */
type BusyAction = "local" | "entra" | "bootstrap" | null;

/** The Microsoft four-square mark, per their sign-in branding guidelines. */
function MicrosoftMark() {
  return (
    <svg width="16" height="16" viewBox="0 0 21 21" aria-hidden="true">
      <rect x="1" y="1" width="9" height="9" fill="#f25022" />
      <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
      <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
      <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
    </svg>
  );
}

/** What the product does, said once, in the panel that only wide screens have room for. */
const CAPABILITIES: ReadonlyArray<{ icon: LucideIcon; title: string; body: string }> = [
  {
    icon: Workflow,
    title: "Pipelines as code",
    body: "Flows live as YAML in your repository, versioned and reviewed like the rest of your codebase.",
  },
  {
    icon: Waypoints,
    title: "Lineage end to end",
    body: "Follow every table and column from the source system through to the warehouse it lands in.",
  },
  {
    icon: ShieldCheck,
    title: "Governed execution",
    body: "Managed identity, secrets from the vault, and an audited history of every run.",
  },
  {
    icon: Sparkles,
    title: "AI built in",
    body: "Ask the assistant about your flows, lineage, and runs; it answers from your own catalog.",
  },
];

/**
 * The branded left column, shown only on wide screens: the SQLFlow lockup, what the product does, and the
 * capabilities worth naming, over a navy field carrying concentric arcs that echo the logo mark. It
 * carries the page on large displays, where a lone card would otherwise float in an empty background.
 * Purely decorative, so it is hidden from assistive tech and never rendered on the narrow, form-only layout.
 */
function BrandPanel() {
  return (
    <div
      className="relative hidden overflow-hidden lg:flex lg:w-[52%] lg:flex-col xl:w-[55%]"
      style={{ background: "linear-gradient(155deg, var(--brand-navy) 0%, var(--brand-navy-deep) 100%)" }}
      aria-hidden="true"
    >
      {/* Concentric arcs echoing the logo mark, oversized and bleeding off the bottom-right corner so the
          field still reads as branded on a 4K display rather than as flat navy. */}
      <svg
        className="pointer-events-none absolute -bottom-[22rem] -right-[16rem] h-[58rem] w-[58rem]"
        viewBox="0 0 200 200"
        fill="none"
      >
        {[30, 55, 80, 105, 130].map((r, i) => (
          <circle
            key={r}
            cx="100"
            cy="100"
            r={r}
            stroke="var(--brand-cream)"
            strokeWidth="0.75"
            strokeOpacity={0.18 - i * 0.03}
          />
        ))}
      </svg>
      {/* A soft light source behind the headline, so the gradient is not the only depth cue. */}
      <div
        className="pointer-events-none absolute -left-40 -top-40 size-[42rem] rounded-full"
        style={{ background: "radial-gradient(circle, rgba(146,182,224,0.18) 0%, rgba(146,182,224,0) 65%)" }}
      />

      {/* Content stays a readable column and centres itself once the panel grows past it. */}
      <div className="relative mx-auto flex h-full w-full max-w-xl flex-col justify-center px-12 py-14 xl:px-16 xl:py-16">
        <img src="/brand/logo-full.png" alt="" className="h-20 w-auto self-start object-contain xl:h-24" />

        <div className="mt-14">
          <h2 className="text-3xl font-semibold leading-[1.15] tracking-tight text-white xl:text-[2.5rem]">
            Move data with confidence.
          </h2>
          <p className="mt-4 max-w-md text-[15px] leading-relaxed text-white/60">
            Orchestrate ingestion, transformation, and lineage across your estate from a single control plane.
          </p>

          <ul className="mt-12 flex flex-col gap-7">
            {CAPABILITIES.map(({ icon: Icon, title, body }) => (
              <li key={title} className="flex gap-4">
                <span className="mt-0.5 flex size-9 shrink-0 items-center justify-center rounded-lg border border-white/10 bg-white/5">
                  <Icon className="size-[18px] text-white/70" strokeWidth={1.75} />
                </span>
                <div>
                  <p className="text-sm font-medium text-white/90">{title}</p>
                  <p className="mt-1 max-w-sm text-[13px] leading-relaxed text-white/50">{body}</p>
                </div>
              </li>
            ))}
          </ul>
        </div>
      </div>
    </div>
  );
}

/**
 * The sign-in page, the one surface that renders outside the workbench shell: a centered card on the
 * editor background with the SQLFlow lockup above it. Username/password for regular SQLFlow users,
 * "Sign in with Microsoft" when the control plane has Entra enabled, and the break-glass bootstrap
 * secret tucked behind an expander when one is configured. Which options render is driven by
 * GET /auth/providers, so this page never guesses the server's configuration.
 */
export default function LoginPage() {
  const { session, sessionEndedReason, loginLocal, loginEntra, loginBootstrap } = useAuth();
  const navigate = useNavigate();
  const [params] = useSearchParams();
  const returnTo = decodeReturnTo(params.get("returnTo"));

  const providers = useQuery({ queryKey: ["auth", "providers"], queryFn: authApi.providers, staleTime: 60_000 });

  // Read once per mount, not on every render: the stored values seed the form, and from then on the form is the
  // truth. `prefs` is kept so focus can land on the field that is actually still empty.
  const [prefs] = useState(readLoginPrefs);
  const [username, setUsername] = useState(prefs.username);
  const [password, setPassword] = useState("");
  const [remember, setRemember] = useState(prefs.remember);
  const [secret, setSecret] = useState("");
  const [busy, setBusy] = useState<BusyAction>(null);
  const [error, setError] = useState<unknown>(null);

  if (session) {
    return <Navigate to={returnTo} replace />;
  }

  const anyBusy = busy !== null;

  const run = async (action: Exclude<BusyAction, null>, work: () => Promise<void>) => {
    setBusy(action);
    setError(null);
    try {
      await work();
      navigate(returnTo, { replace: true });
    } catch (caught) {
      setError(caught);
    } finally {
      setBusy(null);
    }
  };

  // Every sign-in path records its own prefill from the server's answer, inside beginSession; nothing to do here.
  const submitLocal = (event: FormEvent) => {
    event.preventDefault();
    void run("local", () => loginLocal(username, password, remember));
  };

  const submitBootstrap = (event: FormEvent) => {
    event.preventDefault();
    void run("bootstrap", () => loginBootstrap(secret));
  };

  return (
    <div className="flex min-h-screen bg-background">
      <BrandPanel />

      <div className="relative flex flex-1 flex-col items-center justify-center gap-6 p-6 sm:p-10">
        {/* A barely-there wash behind the card, so the form column reads as lit rather than as dead space
            on a large display. */}
        <div
          className="pointer-events-none absolute inset-0"
          style={{ background: "radial-gradient(60rem 40rem at 50% 40%, color-mix(in oklab, var(--primary) 7%, transparent) 0%, transparent 70%)" }}
          aria-hidden="true"
        />

        {/* The lockup sits with the form on narrow screens, where the brand panel is hidden. The navy plaque
            keeps the light-blue mark legible on the light theme's near-white background. */}
        <div className="relative rounded-xl px-6 py-3.5 lg:hidden" style={{ backgroundColor: "var(--brand-navy)" }}>
          <img src="/brand/logo-full.png" alt="SQLFlow" className="h-11 w-auto object-contain" />
        </div>

        <Card
          className="relative w-full max-w-[25rem] gap-0 rounded-xl border-border/70 p-7 shadow-xl shadow-black/5 sm:p-8"
          data-testid="login-card"
        >
          <div className="flex flex-col gap-5">
            <div>
              <h1 className="text-[22px] font-semibold tracking-tight">Welcome back</h1>
              <p className="mt-1.5 text-[13px] text-muted-foreground">Sign in to your SQLFlow account to continue.</p>
            </div>

          {sessionEndedReason && (
            <Alert data-testid="session-ended-notice">
              <Info />
              <AlertDescription>{sessionEndedReason}</AlertDescription>
            </Alert>
          )}

          {error !== null && (
            isApiError(error)
              ? <CorrelationError error={error} data-testid="login-error" />
              : (
                <Alert variant="destructive" data-testid="login-error">
                  <CircleAlert />
                  <AlertTitle>Sign-in failed</AlertTitle>
                  <AlertDescription>{String((error as Error).message ?? error)}</AlertDescription>
                </Alert>
              )
          )}

          <form onSubmit={submitLocal} className="flex flex-col gap-3">
            {/* A password manager keys off name + autocomplete together: with autocomplete alone the field is
                recognised inconsistently, so both are set here and the pair is what makes save/autofill offer. */}
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="login-username">Username</Label>
              <Input
                id="login-username"
                name="username"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                autoComplete="username"
                // A returning user's username is already filled in, so the caret belongs on the password instead.
                autoFocus={prefs.username === ""}
                className="h-8"
                data-testid="login-username"
              />
            </div>
            <div className="flex flex-col gap-1.5">
              <Label htmlFor="login-password">Password</Label>
              <Input
                id="login-password"
                name="password"
                type="password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                autoComplete="current-password"
                autoFocus={prefs.username !== ""}
                className="h-8"
                data-testid="login-password"
              />
            </div>
            <Label className="flex items-center gap-2 text-[13px] font-normal">
              <Checkbox
                checked={remember}
                onCheckedChange={(checked) => setRemember(checked === true)}
                data-testid="login-remember"
              />
              Keep me signed in on this device
            </Label>
            <Button
              type="submit"
              size="sm"
              className="w-full"
              disabled={anyBusy || username.trim() === "" || password === ""}
              data-testid="login-submit"
            >
              {busy === "local" && <Loader2 className="animate-spin" />}
              Sign in
            </Button>
          </form>

          {providers.data?.entra.enabled && (
            <>
              <div className="flex items-center gap-3">
                <div className="h-px flex-1 bg-border" />
                <span className="text-xs text-muted-foreground">or</span>
                <div className="h-px flex-1 bg-border" />
              </div>
              <Button
                type="button"
                variant="outline"
                size="sm"
                className="w-full"
                disabled={anyBusy}
                onClick={() => void run("entra", () => loginEntra(providers.data.entra, remember))}
                data-testid="login-entra"
              >
                {busy === "entra" ? <Loader2 className="animate-spin" /> : <MicrosoftMark />}
                Sign in with Microsoft
              </Button>
            </>
          )}

          {providers.data?.bootstrap && (
            <Collapsible>
              <CollapsibleTrigger
                className="group flex w-full items-center gap-1 text-[13px] text-muted-foreground hover:text-foreground"
                data-testid="login-bootstrap-expander"
              >
                <ChevronDown className="size-4 shrink-0 transition-transform duration-120 group-data-[state=open]:rotate-180" />
                Advanced: bootstrap secret
              </CollapsibleTrigger>
              <CollapsibleContent>
                <form onSubmit={submitBootstrap} className="flex flex-col gap-3 pt-3">
                  <p className="text-xs text-muted-foreground">
                    The break-glass credential from the control plane configuration. Grants full access; use it
                    only to provision real users.
                  </p>
                  <div className="flex flex-col gap-1.5">
                    <Label htmlFor="login-bootstrap-secret">Bootstrap secret</Label>
                    <Input
                      id="login-bootstrap-secret"
                      type="password"
                      value={secret}
                      onChange={(e) => setSecret(e.target.value)}
                      autoComplete="off"
                      className="h-8"
                      data-testid="login-bootstrap-secret"
                    />
                  </div>
                  <Button
                    type="submit"
                    variant="outline"
                    size="sm"
                    className="w-full"
                    disabled={anyBusy || secret === ""}
                    data-testid="login-bootstrap-submit"
                  >
                    {busy === "bootstrap" && <Loader2 className="animate-spin" />}
                    Sign in with bootstrap secret
                  </Button>
                </form>
              </CollapsibleContent>
            </Collapsible>
          )}
          </div>
        </Card>

        <p className="relative max-w-[25rem] text-center text-xs text-muted-foreground">
          Trouble signing in? Your SQLFlow administrator can reset the account or issue a new one.
        </p>
      </div>
    </div>
  );
}

function decodeReturnTo(raw: string | null): string {
  if (!raw) {
    return "/";
  }

  const decoded = decodeURIComponent(raw);
  // Only same-app absolute paths: a foreign origin in returnTo must never become a redirect target.
  return decoded.startsWith("/") && !decoded.startsWith("//") ? decoded : "/";
}
