import type { CSSProperties } from "react";
import { Moon, Palette, Sun } from "lucide-react";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { branding } from "../../modules/branding";
import { isThemeMode, type DesignMark } from "../../theme/designs";
import { useThemeMode } from "../../theme/ThemeModeContext";

/** The side of the square the title bar's mark sits in, in pixels: the `size-5` the branding's logo takes. */
const MARK_BOX = 20;

/** A mark cut from a larger image: the image scaled so the crop fills the box's longer side, the crop centred in it. */
function CroppedMark({ mark }: { mark: DesignMark & { crop: NonNullable<DesignMark["crop"]> } }) {
  const { crop } = mark;
  const scale = MARK_BOX / Math.max(crop.width, crop.height);
  const style: CSSProperties = {
    width: crop.imageWidth * scale,
    height: crop.imageHeight * scale,
    left: (MARK_BOX - crop.width * scale) / 2 - crop.x * scale,
    top: (MARK_BOX - crop.height * scale) / 2 - crop.y * scale,
    maxWidth: "none",
  };
  return (
    <span className="relative block size-5 shrink-0 overflow-hidden" data-testid="app-bar-mark">
      <img src={mark.src} alt="" className="absolute" style={style} />
    </span>
  );
}

/**
 * The product's mark at the title bar's left: the design's own in the mode worn when the design names one (DESIGN.md 3.7),
 * the branding's logo for a dark title bar otherwise.
 */
export function TitleBarMark() {
  const { design, mode } = useThemeMode();
  const mark = design.marks?.[mode];
  if (mark === undefined) {
    return <img src={branding().logos.white} alt="" className="size-5 object-contain" data-testid="app-bar-mark" />;
  }

  if (mark.crop !== undefined) {
    return <CroppedMark mark={{ ...mark, crop: mark.crop }} />;
  }

  return <img src={mark.src} alt="" className="size-5 object-contain" data-testid="app-bar-mark" />;
}

/** Where the control sits: the title bar, on its ground, or the sign-in page, on the page's own dark field. */
export type AppearancePlace = "title-bar" | "sign-in";

const BUTTON: Record<AppearancePlace, string> = {
  "title-bar": "flex size-7 shrink-0 items-center justify-center rounded-md text-title-bar-foreground/80 hover:bg-title-bar-foreground/10 hover:text-title-bar-foreground",
  "sign-in": "flex size-8 shrink-0 items-center justify-center rounded-md text-foreground/70 transition-colors hover:bg-foreground/10 hover:text-foreground",
};

/**
 * The appearance control (DESIGN.md 3.7). With SQLFlow's design alone, the title bar's flips light and dark, as it always
 * has, and the sign-in page has none. When a module adds designs, it opens a menu to pick the design and the mode, which
 * stays open while a person tries them, so each pick shows at once; on the sign-in page, which stays on its brand field in
 * both modes, the mode is the workbench's, worn once the person is in.
 */
export function AppearanceControl({ place = "title-bar" }: { place?: AppearancePlace }) {
  const { mode, design, designs, toggle, choose } = useThemeMode();
  const prefix = place === "sign-in" ? "login-appearance" : "appearance";

  if (designs.length === 1) {
    if (place === "sign-in") {
      return null;
    }

    return (
      <Tooltip>
        <TooltipTrigger asChild>
          <button data-testid="theme-toggle" aria-label="Toggle theme" onClick={toggle} className={BUTTON[place]}>
            {mode === "dark" ? <Sun className="size-4" /> : <Moon className="size-4" />}
          </button>
        </TooltipTrigger>
        <TooltipContent side="bottom">
          {mode === "dark" ? "Switch to light mode" : "Switch to dark mode"}
        </TooltipContent>
      </Tooltip>
    );
  }

  return (
    <DropdownMenu>
      {/* The tooltip wraps the trigger: `DropdownMenuTrigger asChild` clones its props onto its child, which the tooltip,
          a provider with no element of its own, could not receive. */}
      <Tooltip>
        <TooltipTrigger asChild>
          <DropdownMenuTrigger asChild>
            <button data-testid={`${prefix}-menu-button`} aria-label="Design and mode" className={BUTTON[place]}>
              <Palette className="size-4" />
            </button>
          </DropdownMenuTrigger>
        </TooltipTrigger>
        <TooltipContent side="bottom">{`${design.label}, ${mode}`}</TooltipContent>
      </Tooltip>
      {/* The menu opens outside the sign-in page's dark field, so there it takes the field's dark tokens itself. */}
      <DropdownMenuContent align="end" className={place === "sign-in" ? "dark w-44" : "w-44"} data-testid={`${prefix}-menu`}>
        <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">Design</DropdownMenuLabel>
        <DropdownMenuRadioGroup value={design.id} onValueChange={(id) => choose({ design: id })}>
          {designs.map((offered) => (
            <DropdownMenuRadioItem
              key={offered.id}
              value={offered.id}
              data-testid={`${prefix}-design-${offered.id}`}
              onSelect={(event) => event.preventDefault()}
            >
              {offered.label}
            </DropdownMenuRadioItem>
          ))}
        </DropdownMenuRadioGroup>
        <DropdownMenuSeparator />
        <DropdownMenuLabel className="text-xs font-normal text-muted-foreground">
          {place === "sign-in" ? "Workbench mode" : "Mode"}
        </DropdownMenuLabel>
        <DropdownMenuRadioGroup
          value={mode}
          onValueChange={(value) => {
            if (isThemeMode(value)) {
              choose({ mode: value });
            }
          }}
        >
          <DropdownMenuRadioItem value="light" data-testid={`${prefix}-mode-light`} onSelect={(event) => event.preventDefault()}>
            <Sun className="text-muted-foreground" />
            Light
          </DropdownMenuRadioItem>
          <DropdownMenuRadioItem value="dark" data-testid={`${prefix}-mode-dark`} onSelect={(event) => event.preventDefault()}>
            <Moon className="text-muted-foreground" />
            Dark
          </DropdownMenuRadioItem>
        </DropdownMenuRadioGroup>
      </DropdownMenuContent>
    </DropdownMenu>
  );
}
