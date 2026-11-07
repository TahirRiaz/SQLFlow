"use client"

import {
  CircleCheckIcon,
  InfoIcon,
  Loader2Icon,
  OctagonXIcon,
  TriangleAlertIcon,
  XIcon,
} from "lucide-react"
import { Toaster as Sonner, type ToasterProps } from "sonner"
import { useThemeMode } from "@/theme/ThemeModeContext"

/**
 * The workbench's toasts (DESIGN.md 8.5): a popover card that says what happened in a line, with why or what next under
 * it, led by the outcome's glyph in its status color on a tint of that color (the outcome pill's pairing, DESIGN.md 3.2
 * and 7.3), so success and failure are told apart before the words are read. The close button sits inside the card's top
 * right corner, quiet until the pointer or focus reaches it, and an action is a small outline button at the end.
 *
 * Sonner draws them unstyled and these classes draw everything. Its stylesheet still colors a toast's description and close
 * button in its dark theme whether a toast is styled or not, with selectors that outweigh a class, so those few colors
 * are marked important here. Toasts behind the front one, while the stack is folded, hide their contents as the styled
 * toasts do, so only their edges show.
 */
const Toaster = ({ ...props }: ToasterProps) => {
  const { mode } = useThemeMode()

  return (
    <Sonner
      theme={mode}
      className="toaster group"
      gap={8}
      // Clear of the status bar along the bottom edge, rather than resting on it.
      offset={{ bottom: 40, right: 16 }}
      icons={{
        success: <CircleCheckIcon />,
        info: <InfoIcon />,
        warning: <TriangleAlertIcon />,
        error: <OctagonXIcon />,
        loading: <Loader2Icon className="animate-spin" />,
        close: <XIcon />,
      }}
      toastOptions={{
        duration: 5000,
        unstyled: true,
        classNames: {
          toast:
            "group/toast flex w-(--width) items-start gap-3 rounded-lg border border-border bg-popover py-3 pr-10 pl-3 font-sans text-popover-foreground shadow-lg data-[front=false]:data-[expanded=false]:*:opacity-0",
          icon:
            "flex size-7 shrink-0 items-center justify-center rounded-md bg-muted text-muted-foreground [&>svg]:size-4 group-data-[type=success]/toast:bg-success/12 group-data-[type=success]/toast:text-success group-data-[type=error]/toast:bg-destructive/12 group-data-[type=error]/toast:text-destructive group-data-[type=warning]/toast:bg-warning/12 group-data-[type=warning]/toast:text-warning group-data-[type=info]/toast:bg-info/12 group-data-[type=info]/toast:text-info group-data-[type=loading]/toast:bg-info/12 group-data-[type=loading]/toast:text-info",
          content: "flex min-w-0 flex-1 flex-col gap-0.5 self-center",
          title: "text-[13px] font-medium leading-5 text-popover-foreground",
          description: "text-xs leading-snug text-muted-foreground!",
          actionButton:
            "inline-flex h-7 shrink-0 items-center self-center rounded-md border border-input bg-transparent px-2.5 text-xs font-medium text-foreground transition-colors hover:bg-accent focus-visible:outline-1 focus-visible:outline-ring",
          cancelButton:
            "inline-flex h-7 shrink-0 items-center self-center rounded-md px-2.5 text-xs font-medium text-muted-foreground transition-colors hover:bg-accent hover:text-foreground focus-visible:outline-1 focus-visible:outline-ring",
          closeButton:
            "absolute top-2 right-2 inline-flex size-6 items-center justify-center rounded-md border-0! bg-transparent! text-muted-foreground! opacity-70 transition-[opacity,background-color,color] hover:bg-accent! hover:text-foreground! hover:opacity-100 focus-visible:opacity-100 focus-visible:outline-1 focus-visible:outline-ring [&>svg]:size-4",
        },
      }}
      {...props}
    />
  )
}

export { Toaster }
