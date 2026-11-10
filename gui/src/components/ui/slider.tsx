import * as React from "react"
import { Slider as SliderPrimitive } from "radix-ui"

import { cn } from "@/lib/utils"

/**
 * A slider with one thumb per value: a single value, or the two ends of a range. Each thumb takes its accessible
 * name from `thumbLabels`, since a range's thumbs are otherwise indistinguishable to a screen reader.
 */
function Slider({
  className,
  value,
  defaultValue,
  thumbLabels,
  ...props
}: React.ComponentProps<typeof SliderPrimitive.Root> & { thumbLabels?: readonly string[] }) {
  const thumbs = value ?? defaultValue ?? [0]
  return (
    <SliderPrimitive.Root
      data-slot="slider"
      value={value}
      defaultValue={defaultValue}
      className={cn(
        "relative flex w-full touch-none items-center select-none data-[disabled]:opacity-50",
        className,
      )}
      {...props}
    >
      <SliderPrimitive.Track
        data-slot="slider-track"
        className="relative h-1 w-full grow overflow-hidden rounded-full bg-muted"
      >
        <SliderPrimitive.Range data-slot="slider-range" className="absolute h-full bg-primary" />
      </SliderPrimitive.Track>
      {thumbs.map((_, index) => (
        <SliderPrimitive.Thumb
          data-slot="slider-thumb"
          key={index}
          aria-label={thumbLabels?.[index]}
          className="block size-3.5 shrink-0 rounded-full border border-primary bg-background shadow-xs transition-[color,box-shadow] outline-none hover:ring-[3px] hover:ring-ring/40 focus-visible:ring-[3px] focus-visible:ring-ring/50 disabled:pointer-events-none"
        />
      ))}
    </SliderPrimitive.Root>
  )
}

export { Slider }
