import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { Command, Menu, Moon, Sun } from "lucide-react";
import { Avatar, AvatarFallback } from "@/components/ui/avatar";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import { Sheet, SheetContent, SheetTitle, SheetTrigger } from "@/components/ui/sheet";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { useAuth } from "../../auth/AuthContext";
import { useThemeMode } from "../../theme/ThemeModeContext";
import { SideBarSearch, SideBarSections } from "./SideBar";

/**
 * The workbench title bar (DESIGN.md section 6): brand on the left, command palette, theme toggle and
 * account menu on the right. Global search lives at the top of the navigation, not here. On mobile, a
 * hamburger opens the navigation sheet, which carries the same search.
 */
export function TitleBar({ onOpenPalette }: { onOpenPalette: () => void }) {
  const { session, logout } = useAuth();
  const { mode, toggle } = useThemeMode();
  const navigate = useNavigate();
  const [mobileNavOpen, setMobileNavOpen] = useState(false);

  return (
    <header
      data-testid="app-bar"
      className="flex h-9 shrink-0 select-none items-center gap-2 bg-activity-bar px-2 text-white"
    >
      <Sheet open={mobileNavOpen} onOpenChange={setMobileNavOpen}>
        <SheetTrigger
          aria-label="Open navigation"
          className="flex size-7 items-center justify-center rounded-md text-white/80 hover:bg-white/10 hover:text-white md:hidden"
        >
          <Menu className="size-4" />
        </SheetTrigger>
        <SheetContent side="left" className="w-72 gap-0 bg-sidebar p-0">
          <SheetTitle className="sr-only">Navigation</SheetTitle>
          <div className="flex h-full min-h-0 flex-col">
            {/* The right padding clears the sheet's own close button. */}
            <SideBarSearch className="pr-10" onNavigate={() => setMobileNavOpen(false)} />
            <nav aria-label="Main navigation" className="min-h-0 flex-1 overflow-y-auto py-1">
              <SideBarSections reveal={null} onNavigate={() => setMobileNavOpen(false)} />
            </nav>
          </div>
        </SheetContent>
      </Sheet>

      <div className="flex items-center gap-2 pl-1">
        <img src="/brand/logo-white.png" alt="" className="size-5 object-contain" />
        <span className="hidden text-[13px] font-semibold sm:block">SQLFlow</span>
      </div>

      <div className="flex-1" />

      <Tooltip>
        <TooltipTrigger asChild>
          <button
            data-testid="command-palette-button"
            aria-label="Open command palette"
            onClick={onOpenPalette}
            className="hidden h-6 shrink-0 items-center gap-1.5 rounded-md bg-white/10 px-2 text-white/70 transition-colors hover:bg-white/15 hover:text-white sm:flex"
          >
            <Command className="size-3.5" />
            <span className="font-mono text-[10px] leading-none">Ctrl K</span>
          </button>
        </TooltipTrigger>
        <TooltipContent side="bottom">Command palette</TooltipContent>
      </Tooltip>

      <Tooltip>
        <TooltipTrigger asChild>
          <button
            data-testid="theme-toggle"
            aria-label="Toggle theme"
            onClick={toggle}
            className="flex size-7 shrink-0 items-center justify-center rounded-md text-white/80 hover:bg-white/10 hover:text-white"
          >
            {mode === "dark" ? <Sun className="size-4" /> : <Moon className="size-4" />}
          </button>
        </TooltipTrigger>
        <TooltipContent side="bottom">
          {mode === "dark" ? "Switch to light mode" : "Switch to dark mode"}
        </TooltipContent>
      </Tooltip>

      <DropdownMenu>
        <DropdownMenuTrigger
          data-testid="account-menu-button"
          aria-label="Account"
          className="flex size-7 shrink-0 items-center justify-center rounded-md hover:bg-white/10"
        >
          <Avatar className="size-6">
            <AvatarFallback className="bg-primary text-[11px] font-semibold text-primary-foreground">
              {(session?.subject ?? "?").slice(0, 1).toUpperCase()}
            </AvatarFallback>
          </Avatar>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end" className="w-56">
          <DropdownMenuLabel data-testid="account-subject" className="font-normal text-muted-foreground">
            {session?.subject}
            {session?.role ? ` (${session.role})` : ""}
          </DropdownMenuLabel>
          <DropdownMenuSeparator />
          <DropdownMenuItem data-testid="account-tokens" onSelect={() => navigate("/settings/tokens")}>
            Personal access tokens
          </DropdownMenuItem>
          <DropdownMenuItem data-testid="account-notifications" onSelect={() => navigate("/settings/notifications")}>
            Notification settings
          </DropdownMenuItem>
          <DropdownMenuSeparator />
          <DropdownMenuItem data-testid="account-logout" onSelect={() => logout()}>
            Sign out
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </header>
  );
}
