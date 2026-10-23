import { useState, type FormEvent } from "react";
import { Navigate, useNavigate, useSearchParams } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ChevronDown, CircleAlert, Info, Loader2 } from "lucide-react";
import { Alert, AlertDescription, AlertTitle } from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import { Checkbox } from "@/components/ui/checkbox";
import { Collapsible, CollapsibleContent, CollapsibleTrigger } from "@/components/ui/collapsible";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { authApi } from "../api/endpoints";
import { isApiError } from "../api/client";
import { CorrelationError } from "../components/CorrelationError";
import { useAuth } from "./AuthContext";
import { branding } from "../modules/branding";
import { readLoginPrefs } from "./loginPrefs";
import { LoginStreams } from "./LoginStreams";
import { BRAND_FIELD, LOGIN_ACCENT } from "./loginStreamLanes";

/** Which sign-in path is in flight, so only the button that started the work shows the spinner. */
type BusyAction = "local" | "entra" | "bootstrap" | null;

/** The Microsoft four-square mark, per their sign-in branding guidelines. */
function MicrosoftMark() {
  return (
    <svg className="size-4" viewBox="0 0 21 21" aria-hidden="true">
      <rect x="1" y="1" width="9" height="9" fill="#f25022" />
      <rect x="11" y="1" width="9" height="9" fill="#7fba00" />
      <rect x="1" y="11" width="9" height="9" fill="#00a4ef" />
      <rect x="11" y="11" width="9" height="9" fill="#ffb900" />
    </svg>
  );
}

/** The product's lockup: the full logo and, when the product carries one, its "powered by" attribution beside it. */
function Lockup({ logoClassName }: { logoClassName: string }) {
  const brand = branding();
  return (
    <div className="flex items-center gap-4">
      <img src={brand.logos.full} alt={brand.productName} className={`w-auto max-w-full object-contain ${logoClassName}`} />
      {brand.attribution !== null && (
        <>
          <span className="h-5 w-px shrink-0 bg-white/20" aria-hidden="true" />
          <span className="text-[0.8125rem] whitespace-nowrap text-white/55" data-testid="login-attribution">{brand.attribution}</span>
        </>
      )}
    </div>
  );
}

/**
 * The brand panel, shown only on wide screens: the lockup, a picture of the kinds of data the product takes flowing into
 * rings that echo the logo mark, and the product's pitch in two lines and one sentence. It is the first thing a person
 * sees of the product, so it says what the product is for and nothing more; the workbench explains the rest.
 */
function BrandPanel() {
  const brand = branding();
  return (
    <section className="relative hidden flex-col text-white lg:flex lg:w-[54%] xl:w-[56%]" data-testid="login-brand-panel">
      {/* A fine dot grid, brightest around the rings and fading out toward the edges, so the field has a surface. */}
      <div
        className="pointer-events-none absolute inset-0"
        style={{
          backgroundImage: "radial-gradient(color-mix(in srgb, white 11%, transparent) 1px, transparent 1.5px)",
          backgroundSize: "24px 24px",
          maskImage: "radial-gradient(ellipse 75% 60% at 62% 42%, black 0%, transparent 72%)",
        }}
        aria-hidden="true"
      />
      {/* A soft light behind the pitch, so the gradient is not the only depth cue. */}
      <div
        className="pointer-events-none absolute -bottom-48 -left-40 size-[38rem] rounded-full"
        style={{ background: "radial-gradient(circle, color-mix(in srgb, var(--brand-blue) 22%, transparent) 0%, transparent 65%)" }}
        aria-hidden="true"
      />

      <header className="relative px-12 pt-11 xl:px-16 xl:pt-14">
        <Lockup logoClassName="h-9 xl:h-10" />
      </header>

      {/* The picture keeps between the lockup and the pitch; its light spills past them, fading out on its own. */}
      <div className="relative min-h-0 flex-1">
        <LoginStreams streams={brand.login.streams} />
      </div>

      <div className="relative px-12 pb-14 xl:px-16 xl:pb-16 motion-safe:animate-in motion-safe:fade-in-0 motion-safe:slide-in-from-bottom-2 motion-safe:duration-700">
        <h2 className="max-w-2xl text-[2.5rem] leading-[1.08] font-semibold tracking-[-0.025em] xl:text-[3.25rem]">
          <span className="block">{brand.login.headline}</span>
          <span className="block" style={{ color: LOGIN_ACCENT }}>{brand.login.headlineAccent}</span>
        </h2>
        <p className="mt-5 max-w-lg text-[0.9375rem] leading-relaxed text-white/60 xl:text-base">{brand.login.tagline}</p>
      </div>
    </section>
  );
}

/**
 * The sign-in page, the one surface that renders outside the workbench shell: the brand panel on wide screens, and the
 * sign-in form on the editor background beside it (under a navy band carrying the lockup on narrow screens).
 * Username/password for the product's regular users, "Sign in with Microsoft" when the control plane has Entra enabled,
 * and the break-glass bootstrap secret tucked behind an expander when one is configured. Which options render is driven
 * by GET /auth/providers, so this page never guesses the server's configuration.
 */
export default function LoginPage() {
  const brand = branding();
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

  // The page is the product's first impression, so it is set on the brand's navy in both modes: the dark tokens apply
  // to everything on it, the form's controls included, whichever mode the workbench is in. It clips what the brand
  // panel's lights spill past the page's edges, and lets them spill toward the form.
  return (
    <div
      className="dark relative flex min-h-screen flex-col overflow-hidden text-[0.8125rem] text-foreground lg:flex-row"
      style={{ background: BRAND_FIELD }}
    >
      <BrandPanel />

      {/* The lockup on narrow screens, where the brand panel is hidden. */}
      <header className="px-6 pt-8 sm:px-10 lg:hidden">
        <Lockup logoClassName="h-8" />
      </header>

      <main className="relative flex min-w-0 flex-1 flex-col">
        {/* A faint light behind the form, so the column reads as lit rather than as dead space on a large display. */}
        <div
          className="pointer-events-none absolute inset-0"
          style={{ background: "radial-gradient(40rem 32rem at 50% 45%, color-mix(in srgb, var(--brand-blue) 9%, transparent) 0%, transparent 70%)" }}
          aria-hidden="true"
        />

        <div className="relative flex flex-1 items-center justify-center px-6 py-12 sm:px-10">
          <div
            className="flex w-full max-w-[23rem] flex-col gap-8 motion-safe:animate-in motion-safe:fade-in-0 motion-safe:slide-in-from-bottom-2 motion-safe:duration-500"
            data-testid="login-card"
          >
            <div>
              <h1 className="text-[1.75rem] leading-tight font-semibold tracking-[-0.02em]">Welcome back</h1>
              <p className="mt-2 text-sm text-muted-foreground">{`Sign in to continue to ${brand.productName}.`}</p>
            </div>

            <div className="flex flex-col gap-5">
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

              <form onSubmit={submitLocal} className="flex flex-col gap-4">
                {/* A password manager keys off name + autocomplete together: with autocomplete alone the field is
                    recognised inconsistently, so both are set here and the pair is what makes save/autofill offer. */}
                <div className="flex flex-col gap-2">
                  <Label htmlFor="login-username">Username</Label>
                  <Input
                    id="login-username"
                    name="username"
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                    autoComplete="username"
                    // A returning user's username is already filled in, so the caret belongs on the password instead.
                    autoFocus={prefs.username === ""}
                    className="h-10 md:text-sm"
                    data-testid="login-username"
                  />
                </div>
                <div className="flex flex-col gap-2">
                  <Label htmlFor="login-password">Password</Label>
                  <Input
                    id="login-password"
                    name="password"
                    type="password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="current-password"
                    autoFocus={prefs.username !== ""}
                    className="h-10 md:text-sm"
                    data-testid="login-password"
                  />
                </div>
                <Label className="flex items-center gap-2 text-[0.8125rem] font-normal text-muted-foreground">
                  <Checkbox
                    checked={remember}
                    onCheckedChange={(checked) => setRemember(checked === true)}
                    data-testid="login-remember"
                  />
                  Keep me signed in on this device
                </Label>
                <Button
                  type="submit"
                  size="lg"
                  className="mt-1 w-full text-sm"
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
                    size="lg"
                    className="w-full text-sm"
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
                    className="group flex w-full items-center gap-1 text-[0.8125rem] text-muted-foreground hover:text-foreground"
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
                      <div className="flex flex-col gap-2">
                        <Label htmlFor="login-bootstrap-secret">Bootstrap secret</Label>
                        <Input
                          id="login-bootstrap-secret"
                          type="password"
                          value={secret}
                          onChange={(e) => setSecret(e.target.value)}
                          autoComplete="off"
                          className="h-10 md:text-sm"
                          data-testid="login-bootstrap-secret"
                        />
                      </div>
                      <Button
                        type="submit"
                        variant="outline"
                        size="lg"
                        className="w-full text-sm"
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
          </div>
        </div>

        <p className="relative px-6 pb-8 text-center text-xs text-muted-foreground sm:px-10">
          {`Trouble signing in? Ask your ${brand.productName} administrator.`}
        </p>
      </main>
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
