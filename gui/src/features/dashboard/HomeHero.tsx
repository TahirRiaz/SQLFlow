import { useState, type FormEvent } from "react";
import { useNavigate } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { ArrowRight, Sparkles } from "lucide-react";
import { Button } from "@/components/ui/button";
import { chatApi } from "../../api/endpoints";
import { useAuth } from "../../auth/AuthContext";
import { LoginStreams } from "../../auth/LoginStreams";
import { BRAND_FIELD } from "../../auth/loginStreamLanes";
import { branding } from "../../modules/branding";
import { shownHomePrompts } from "../../modules/homePrompts";
import { moduleHomePrompts } from "../../modules/registry";
import { chatHandoff } from "../chat/chatHandoff";

/** The dot grid the sign-in page's brand panel carries, so the field has a surface. */
const DOT_GRID = {
  backgroundImage: "radial-gradient(color-mix(in srgb, white 10%, transparent) 1px, transparent 1.5px)",
  backgroundSize: "24px 24px",
  maskImage: "radial-gradient(ellipse 70% 90% at 80% 50%, black 0%, transparent 75%)",
} as const;

/** The name the greeting uses: an e-mail's name before the domain, any other subject as it is. */
function greetingName(subject: string): string {
  const at = subject.indexOf("@");
  return at > 0 ? subject.slice(0, at) : subject;
}

/**
 * The home page's opening (DESIGN.md 12): the brand's navy field in both modes, a greeting and what the product is for,
 * and the assistant's question box, the page's one task. A question typed, or one of the questions offered, opens the
 * chat asking it (chatHandoff). Beside it, on wide screens, the sign-in page's picture of the data the product takes.
 */
export function HomeHero() {
  const brand = branding();
  const { session } = useAuth();
  const navigate = useNavigate();
  const [question, setQuestion] = useState("");
  const capabilities = useQuery({ queryKey: ["chat", "capabilities"], queryFn: () => chatApi.capabilities(), staleTime: 5 * 60_000 });
  // Until the control plane says, the box is open: the chat says itself when the assistant is not configured.
  const off = capabilities.data !== undefined && !capabilities.data.enabled;
  const prompts = shownHomePrompts(moduleHomePrompts());

  const ask = (text: string) => {
    if (off || text.trim() === "") {
      return;
    }

    const { to, state } = chatHandoff(text);
    void navigate(to, { state });
  };
  const submit = (event: FormEvent) => {
    event.preventDefault();
    ask(question);
  };

  return (
    <section
      className="dark relative overflow-hidden rounded-lg border border-white/10 text-foreground motion-safe:animate-in motion-safe:fade-in-0 motion-safe:duration-500"
      style={{ background: BRAND_FIELD }}
      data-testid="home-hero"
    >
      <div className="pointer-events-none absolute inset-0" style={DOT_GRID} aria-hidden="true" />
      <div className="relative grid lg:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]">
        <div className="flex min-w-0 flex-col gap-5 px-6 py-6 md:px-8 md:py-7">
          <div className="flex flex-col gap-2">
            <h1 className="text-2xl font-semibold tracking-[-0.02em] text-white" data-testid="home-greeting">
              {session === null ? `Welcome to ${brand.productName}` : `Welcome back, ${greetingName(session.subject)}`}
            </h1>
            <p className="max-w-xl text-sm leading-relaxed text-white/60">{brand.login.tagline}</p>
          </div>

          <form className="flex flex-col gap-3" onSubmit={submit} data-testid="home-ask">
            <label htmlFor="home-ask-input" className="text-[13px] font-medium text-white/85">Ask the assistant</label>
            <div className="relative">
              <Sparkles className="pointer-events-none absolute top-1/2 left-3.5 size-4 -translate-y-1/2 text-white/50" aria-hidden="true" />
              <input
                id="home-ask-input"
                value={question}
                onChange={(event) => setQuestion(event.target.value)}
                disabled={off}
                placeholder={off ? "The assistant is not configured here" : "Ask about your flows, runs, records and lineage"}
                autoComplete="off"
                className="h-11 w-full rounded-lg border border-white/15 bg-white/[0.06] pr-24 pl-10 text-sm text-white shadow-xs outline-none transition-[color,box-shadow] placeholder:text-white/40 focus-visible:border-ring focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:cursor-not-allowed disabled:opacity-60"
                data-testid="home-ask-input"
              />
              <Button
                type="submit"
                size="sm"
                className="absolute top-1/2 right-1.5 -translate-y-1/2"
                disabled={off || question.trim() === ""}
                data-testid="home-ask-submit"
              >
                Ask
                <ArrowRight />
              </Button>
            </div>
            {off && (
              <p className="text-xs text-white/55" data-testid="home-ask-off">
                An administrator enables the assistant with ControlPlane:Assistant on the control plane.
              </p>
            )}
            {!off && capabilities.data?.runQuery === true && (
              <p className="text-xs text-white/45">Start a question with ! to ask the data itself.</p>
            )}
            {!off && (
              <div className="flex flex-wrap items-center gap-2" data-testid="home-prompts">
                <span className="text-xs text-white/45">Try</span>
                {prompts.map((prompt) => (
                  <button
                    key={prompt}
                    type="button"
                    onClick={() => ask(prompt)}
                    className="max-w-full truncate rounded-full border border-white/12 bg-white/[0.04] px-3 py-1 text-left text-xs text-white/75 transition-colors hover:border-white/25 hover:bg-white/[0.09] hover:text-white focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
                    title={prompt}
                    data-testid="home-prompt"
                  >
                    {prompt}
                  </button>
                ))}
              </div>
            )}
          </form>
        </div>

        {/* The sign-in page's picture, fading in from the left so it never meets the question box at an edge. */}
        <div
          className="relative hidden min-h-[15rem] lg:block"
          style={{ maskImage: "linear-gradient(to right, transparent 0%, black 30%)" }}
          aria-hidden="true"
        >
          <LoginStreams streams={brand.login.streams} />
        </div>
      </div>
    </section>
  );
}
