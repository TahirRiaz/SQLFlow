"use client"

import * as React from "react"
import { Tooltip as TooltipPrimitive } from "radix-ui"

import { cn } from "@/lib/utils"

function TooltipProvider({
  delayDuration = 0,
  ...props
}: React.ComponentProps<typeof TooltipPrimitive.Provider>) {
  return (
    <TooltipPrimitive.Provider
      data-slot="tooltip-provider"
      delayDuration={delayDuration}
      {...props}
    />
  )
}

function Tooltip({
  ...props
}: React.ComponentProps<typeof TooltipPrimitive.Root>) {
  return <TooltipPrimitive.Root data-slot="tooltip" {...props} />
}

function TooltipTrigger({
  ...props
}: React.ComponentProps<typeof TooltipPrimitive.Trigger>) {
  return <TooltipPrimitive.Trigger data-slot="tooltip-trigger" {...props} />
}

/** Shared entrance/exit motion, identical for both surfaces (DESIGN.md 9). */
const motion =
  "z-50 w-fit origin-(--radix-tooltip-content-transform-origin) animate-in fade-in-0 zoom-in-95 data-[side=bottom]:slide-in-from-top-2 data-[side=left]:slide-in-from-right-2 data-[side=right]:slide-in-from-left-2 data-[side=top]:slide-in-from-bottom-2 data-[state=closed]:animate-out data-[state=closed]:fade-out-0 data-[state=closed]:zoom-out-95"

/**
 * The two tooltip surfaces (DESIGN.md 7.8).
 *
 * `chip` is the default micro-label: an inverted pill for a word or two ("Copy path", "Succeeded"), with an
 * arrow pointing at its trigger. `panel` is the reading surface for a value too long or too structured for a
 * pill (a multi-line note, a description, a URL): a popover-surfaced card that wraps, preserves line breaks,
 * and left-aligns, because a paragraph balanced across an inverted 12px pill is unreadable. The panel drops
 * the arrow: at card width the pointer is already inside the trigger and the arrow only adds a notch that
 * fights the border.
 */
const surface = {
  chip: "rounded-md bg-foreground px-3 py-1.5 text-xs text-balance text-background",
  panel:
    "max-w-md rounded-lg border border-border bg-popover px-3 py-2 text-left text-[12px] leading-relaxed text-popover-foreground shadow-md",
} as const

function TooltipContent({
  className,
  sideOffset = 0,
  variant = "chip",
  children,
  ...props
}: React.ComponentProps<typeof TooltipPrimitive.Content> & { variant?: keyof typeof surface }) {
  return (
    <TooltipPrimitive.Portal>
      <TooltipPrimitive.Content
        data-slot="tooltip-content"
        data-variant={variant}
        sideOffset={variant === "panel" ? Math.max(sideOffset, 4) : sideOffset}
        className={cn(motion, surface[variant], className)}
        {...props}
      >
        {children}
        {variant === "chip" && (
          <TooltipPrimitive.Arrow className="z-50 size-2.5 translate-y-[calc(-50%_-_2px)] rotate-45 rounded-[2px] bg-foreground fill-foreground" />
        )}
      </TooltipPrimitive.Content>
    </TooltipPrimitive.Portal>
  )
}

export { Tooltip, TooltipTrigger, TooltipContent, TooltipProvider }
