import { createContext, lazy, Suspense, useCallback, useContext, useMemo, useRef, useState, type ReactNode } from "react";
import { useLocation } from "react-router-dom";
import { useQuery } from "@tanstack/react-query";
import { DrawerHeader, LeftDrawer } from "@/components/LeftDrawer";
import { Sheet, SheetContent, SheetDescription, SheetTitle } from "@/components/ui/sheet";
import { Skeleton } from "@/components/ui/skeleton";
import { cn } from "@/lib/utils";
import { chatApi } from "../../api/endpoints";
import { navEntryFor, routeTitle } from "../../layout/nav";
import { useActiveTabTitle } from "../../layout/workbench/TabsContext";
import { branding } from "../../modules/branding";
import {
  changeSubject, codeSubject, explainChangeQuestion, explainCodeQuestion, explainPageQuestion, type ExplainChange,
  type ExplainCode, type ExplainPage, type PageOutline,
} from "./explain";
import { ExplainIcon } from "./ExplainIcon";

// The conversation loads the assistant's thread, which no page needs until something is explained.
const ExplainConversation = lazy(() => import("./ExplainConversation"));

/** The drawer's width, in pixels, until the person drags it: room for a conversation beside the page. */
const DEFAULT_WIDTH = 480;
/** The narrowest the drawer is dragged to; the widest is most of its frame, so the page always shows beside it. */
const MIN_WIDTH = 360;
const WIDTH_KEY = "sqlflow.explain.width";

/** The content of the dialogs whose overlay holds the page, and so the drawer, out of reach. */
const MODAL_CONTENT = '[data-slot="sheet-content"], [data-slot="dialog-content"], [data-slot="alert-dialog-content"]';

export interface ExplainValue {
  /** Explains a piece of code a page shows; `origin` is the element it was asked from. */
  explainCode: (code: ExplainCode, origin: Element | null) => void;
  /** Explains what changed between the two versions a comparison window shows. */
  explainChange: (change: ExplainChange, origin: Element | null) => void;
  /** Explains the page in view: what the feature is for, what it shows, what a person does there. */
  explainPage: () => void;
}

/** One explanation: what it is about, in a few words, and the question that opens its conversation. */
interface Asked {
  id: number;
  subject: string;
  question: string;
  /** Asked from inside a modal dialog, so it opens over the dialog rather than in the frame under it. */
  overDialog: boolean;
}

interface ExplainState {
  value: ExplainValue | null;
  asked: Asked | null;
  open: boolean;
  maximized: boolean;
  setMaximized: (maximized: boolean) => void;
  close: () => void;
}

const ExplainContext = createContext<ExplainState | null>(null);

/** What the page in view shows, read from the frame's content when its explanation is asked. */
function readOutline(): PageOutline {
  const scope = document.querySelector("[data-explain-scope]");
  const texts = (selector: string): string[] =>
    scope === null ? [] : Array.from(scope.querySelectorAll(selector), (element) => element.textContent ?? "");
  return { headings: texts("h1, h2, h3"), tabs: texts('[role="tab"]'), columns: texts('th, [role="columnheader"]') };
}

/**
 * "Explain": a page, or a piece of code on it, explained by the assistant in a conversation of its own, followed up
 * like any other. It slides in from the left over the page (ExplainDrawer, placed by the frame), as a diagram's detail
 * does, its right edge dragged to make it wider or narrower, so the page stays in view beside it; maximized, it covers
 * the frame for the conversation alone, and restoring or closing it shows the page again as it was left. Asked from
 * inside a modal dialog, whose overlay holds the frame out of reach, it opens in a sheet from the left over the dialog.
 * Every explanation is a conversation of its own, kept with the others on the Assistant page. Offered only while the
 * assistant is configured: `useExplain` answers null otherwise, and the buttons stay hidden.
 */
export function ExplainProvider({ children }: { children: ReactNode }) {
  const capabilities = useQuery({ queryKey: ["chat", "capabilities"], queryFn: () => chatApi.capabilities() });
  const { pathname } = useLocation();
  const tabTitle = useActiveTabTitle();
  const counter = useRef(0);
  const [asked, setAsked] = useState<Asked | null>(null);
  const [open, setOpen] = useState(false);
  const [maximized, setMaximized] = useState(false);

  const page = useCallback((): ExplainPage => {
    const entry = navEntryFor(pathname);
    return {
      title: tabTitle ?? routeTitle(pathname).title,
      path: pathname,
      area: entry?.label ?? null,
      description: entry?.description ?? null,
    };
  }, [pathname, tabTitle]);

  const ask = useCallback((subject: string, question: string, origin: Element | null) => {
    counter.current += 1;
    setAsked({ id: counter.current, subject, question, overDialog: origin?.closest(MODAL_CONTENT) != null });
    setOpen(true);
  }, []);

  const close = useCallback(() => {
    setOpen(false);
    setMaximized(false);
  }, []);

  const enabled = capabilities.data?.enabled === true;
  const value = useMemo((): ExplainValue | null => !enabled ? null : {
    explainCode: (code, origin) =>
      ask(codeSubject(code), explainCodeQuestion(code, page(), branding().productName), origin),
    explainChange: (change, origin) =>
      ask(capitalized(changeSubject(change)), explainChangeQuestion(change, page(), branding().productName), origin),
    explainPage: () => {
      const current = page();
      ask(`The ${current.title} page`, explainPageQuestion(current, readOutline(), branding().productName), null);
    },
  }, [enabled, ask, page]);

  const state = useMemo(
    (): ExplainState => ({ value, asked, open, maximized, setMaximized, close }),
    [value, asked, open, maximized, close],
  );

  return (
    <ExplainContext.Provider value={state}>
      {children}
      <Sheet open={open && asked?.overDialog === true} onOpenChange={(next) => !next && close()}>
        <SheetContent
          side="left"
          showCloseButton={false}
          className={cn("gap-0 bg-card p-0", maximized ? "w-screen sm:max-w-none" : "w-[480px] sm:max-w-[85vw]")}
          data-testid="explain-sheet"
        >
          <SheetDescription className="sr-only">
            The assistant explains it here; ask follow-up questions below.
          </SheetDescription>
          {asked !== null && (
            <ExplainSurface
              asked={asked}
              maximized={maximized}
              onMaximizedChange={setMaximized}
              onClose={close}
              title={<SheetTitle className="text-[13px] leading-5 font-semibold">Explain</SheetTitle>}
            />
          )}
        </SheetContent>
      </Sheet>
    </ExplainContext.Provider>
  );
}

function capitalized(text: string): string {
  return text.charAt(0).toUpperCase() + text.slice(1);
}

/**
 * The explanation over the page, placed by the frame inside the box it covers (the page's area of the workbench, a
 * window page's whole window), which must be positioned. `maximizedClassName` places it maximized, fixed to the
 * viewport: the workbench leaves its title and status bars showing.
 */
export function ExplainDrawer({ maximizedClassName }: { maximizedClassName: string }) {
  const state = useContext(ExplainContext);
  if (state === null || !state.open || state.asked === null || state.asked.overDialog) {
    return null;
  }

  const { asked, maximized, setMaximized, close } = state;
  return (
    <LeftDrawer
      label="Explain"
      widthKey={WIDTH_KEY}
      defaultWidth={DEFAULT_WIDTH}
      minWidth={MIN_WIDTH}
      maximized={maximized}
      maximizedClassName={maximizedClassName}
      layerClassName="z-30"
      testId="explain-drawer"
    >
      <ExplainSurface
        asked={asked}
        maximized={maximized}
        onMaximizedChange={setMaximized}
        onClose={close}
        title={<h2 className="text-[13px] leading-5 font-semibold">Explain</h2>}
      />
    </LeftDrawer>
  );
}

/**
 * An explanation's header and conversation, whichever surface holds it: what it is about under the Explain heading,
 * maximize or restore (a double-click on the header does too, as on a window's), and close.
 */
function ExplainSurface({ asked, maximized, onMaximizedChange, onClose, title }: {
  asked: Asked;
  maximized: boolean;
  onMaximizedChange: (maximized: boolean) => void;
  onClose: () => void;
  title: ReactNode;
}) {
  return (
    <>
      <DrawerHeader
        icon={<ExplainIcon className="size-4" />}
        title={title}
        subtitle={asked.subject}
        maximized={maximized}
        onMaximizedChange={onMaximizedChange}
        onClose={onClose}
        maximizeHint="Maximize: hide the page and keep the conversation"
        restoreHint="Restore: show the page again"
        closeHint="Close: the conversation stays on the Assistant page"
        testIdPrefix="explain"
      />
      <div className="min-h-0 flex-1">
        <Conversation key={asked.id} question={asked.question} />
      </div>
    </>
  );
}

function Conversation({ question }: { question: string }) {
  return (
    <Suspense
      fallback={(
        <div className="flex flex-col gap-3 p-4">
          <Skeleton className="h-12 w-2/3 self-end" />
          <Skeleton className="h-24 w-3/4" />
        </div>
      )}
    >
      <ExplainConversation question={question} />
    </Suspense>
  );
}

/** "Explain", while the assistant is configured and the page is inside a frame that offers it; null otherwise. */
export function useExplain(): ExplainValue | null {
  return useContext(ExplainContext)?.value ?? null;
}
