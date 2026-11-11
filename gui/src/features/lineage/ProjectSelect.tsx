import { useMemo, useState } from "react";
import { CheckIcon, ChevronsUpDown } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import type { LineageProject } from "../../api/types";
import { counted } from "./graphKinds";

/** What the graph is drawn for: a whole repository (`project` empty) or one of its root folders. */
export interface ScopeChoice {
  repoId: string;
  repoName: string;
  project: string;
  flowCount: number;
}

/**
 * The scope picker: every repository, whole or by one of its projects (its root folders), with how many flows each
 * holds, filterable by name. The whole repository leads each group because it is the map everything else is a
 * part of; a project narrows it to that folder's flows and what is downstream of them.
 */
export function ProjectSelect({ items, repoId, project, onSelect }: {
  items: LineageProject[];
  repoId: string;
  project: string;
  onSelect: (choice: ScopeChoice | null) => void;
}) {
  const [open, setOpen] = useState(false);
  const repos = useMemo(() => {
    const byRepo = new Map<string, { repoId: string; repoName: string; flowCount: number; projects: LineageProject[] }>();
    for (const item of items) {
      const repo = byRepo.get(item.repoId)
        ?? byRepo.set(item.repoId, { repoId: item.repoId, repoName: item.repoName, flowCount: 0, projects: [] }).get(item.repoId)!;
      repo.flowCount += item.flowCount;
      repo.projects.push(item);
    }
    return [...byRepo.values()];
  }, [items]);

  const selectedRepo = repos.find((repo) => repo.repoId === repoId);
  const label = selectedRepo === undefined
    ? null
    : project === "" ? selectedRepo.repoName : `${selectedRepo.repoName} / ${project}`;

  const choose = (choice: ScopeChoice | null) => {
    setOpen(false);
    onSelect(choice);
  };

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button
          variant="outline"
          size="sm"
          role="combobox"
          aria-expanded={open}
          aria-label="Scope: a repository or a project"
          data-testid="graph-project-select"
          className="h-8 w-[220px] justify-between font-normal"
        >
          {label !== null
            ? <span className="truncate">{label}</span>
            : <span className="text-muted-foreground">Pick a repository or project</span>}
          <ChevronsUpDown className="text-muted-foreground" />
        </Button>
      </PopoverTrigger>
      <PopoverContent align="start" className="w-80 p-0">
        <Command>
          <CommandInput placeholder="Search repositories and projects" />
          <CommandList>
            <CommandEmpty>No repository or project matches.</CommandEmpty>
            {repoId !== "" && (
              <CommandItem value="clear-scope-selection" onSelect={() => choose(null)} className="text-muted-foreground">
                Clear selection
              </CommandItem>
            )}
            {repos.map((repo) => (
              <CommandGroup key={repo.repoId} heading={repo.repoName}>
                <CommandItem
                  value={`${repo.repoName} whole repository`}
                  data-testid="graph-scope-repo"
                  onSelect={() => choose({ repoId: repo.repoId, repoName: repo.repoName, project: "", flowCount: repo.flowCount })}
                >
                  <div className="min-w-0 flex-1 overflow-hidden">
                    <div className="truncate text-[13px]">Whole repository</div>
                    <div className="truncate text-xs text-muted-foreground">
                      {counted(repo.flowCount, "flow")} in {counted(repo.projects.length, "project")}
                    </div>
                  </div>
                  {repoId === repo.repoId && project === "" && <CheckIcon className="shrink-0" />}
                </CommandItem>
                {repo.projects.map((option) => (
                  <CommandItem
                    key={option.project}
                    value={`${option.repoName} / ${option.project}`}
                    data-testid="graph-scope-project"
                    onSelect={() => choose({ ...option })}
                  >
                    <div className="min-w-0 flex-1 overflow-hidden">
                      <div className="truncate text-[13px]">{option.project}</div>
                      <div className="truncate text-xs text-muted-foreground">
                        {counted(option.flowCount, "flow")}, and what is downstream of them
                      </div>
                    </div>
                    {repoId === option.repoId && project === option.project && <CheckIcon className="shrink-0" />}
                  </CommandItem>
                ))}
              </CommandGroup>
            ))}
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
