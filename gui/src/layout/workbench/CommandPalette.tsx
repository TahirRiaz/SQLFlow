import { useEffect } from "react";
import { useNavigate } from "react-router-dom";
import { Search } from "lucide-react";
import {
  CommandDialog,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
  CommandSeparator,
} from "@/components/ui/command";
import { useAuth } from "../../auth/AuthContext";
import { navGroups } from "../nav";

interface CommandPaletteProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
}

/**
 * The command palette (Ctrl+K, or VS Code muscle memory Ctrl+Shift+P): every nav destination grouped
 * as in the side bar, plus the catalog search hand-off.
 */
export function CommandPalette({ open, onOpenChange }: CommandPaletteProps) {
  const navigate = useNavigate();
  const { hasScope } = useAuth();

  useEffect(() => {
    const onKeyDown = (event: KeyboardEvent) => {
      const isPaletteKey = (event.ctrlKey || event.metaKey)
        && (event.key.toLowerCase() === "k" || (event.shiftKey && event.key.toLowerCase() === "p"));
      if (isPaletteKey) {
        event.preventDefault();
        onOpenChange(!open);
      }
    };

    window.addEventListener("keydown", onKeyDown);
    return () => window.removeEventListener("keydown", onKeyDown);
  }, [open, onOpenChange]);

  const go = (to: string) => {
    onOpenChange(false);
    navigate(to);
  };

  return (
    <CommandDialog open={open} onOpenChange={onOpenChange} title="Command palette" description="Go to a page or search the catalog">
      <CommandInput placeholder="Go to a page or search the catalog" />
      <CommandList>
        <CommandEmpty>No matching pages.</CommandEmpty>
        {navGroups
          .filter((group) => group.requiresScope === undefined || hasScope(group.requiresScope))
          .map((group) => (
            <CommandGroup key={group.id} heading={group.label}>
              {group.items.map((item) => (
                <CommandItem key={item.to} value={`${group.label} ${item.label}`} onSelect={() => go(item.to)}>
                  <item.icon />
                  {item.label}
                </CommandItem>
              ))}
            </CommandGroup>
          ))}
        <CommandSeparator />
        <CommandGroup heading="Catalog">
          <CommandItem value="search objects columns definitions" onSelect={() => go("/search")}>
            <Search />
            Search objects, columns, definitions
          </CommandItem>
        </CommandGroup>
      </CommandList>
    </CommandDialog>
  );
}
