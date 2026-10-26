import type { PipelineSummary, RepoTreeEntry } from "../../api/types.ts";
import { projectOf, ROOT_PROJECT } from "../repos/project.ts";

/**
 * One folder of a project, with everything it directly holds and the folders under it. A project is a repository's
 * top-level folder; this is the structure INSIDE it, so a repo laid out as `<project>/flows`, `<project>/mappings`
 * and so on reads as the folders it actually has rather than as one flat list of long paths.
 */
export interface FolderNode {
  /** The folder's own name, the last segment of its path. Empty for the project's own root node. */
  name: string;
  /** The folder's repo-relative path, which is what a row is keyed by. Empty for the project's own root node. */
  path: string;
  /** The folders directly under this one, by name. */
  folders: FolderNode[];
  /** The registered pipelines whose file sits directly in this folder, by name. */
  pipelines: PipelineSummary[];
  /** The non-pipeline files sitting directly in this folder, by path. */
  files: RepoTreeEntry[];
  /** Pipelines in this folder and every folder under it, which is what the folder's badge counts. */
  pipelineCount: number;
  /** Files in this folder and every folder under it. */
  fileCount: number;
}

/** The last segment of a path: what to call a file sitting in a folder that already says where it is. */
export function baseName(path: string): string {
  return path.slice(path.lastIndexOf("/") + 1);
}

/**
 * The path of something inside a project, relative to that project's folder. A flow at the repository root belongs to
 * the root project, whose folder is the repository itself, so its path is already relative.
 */
function withinProject(project: string, path: string): string {
  return project === ROOT_PROJECT ? path : path.slice(project.length + 1);
}

/** The folder a node's children are placed in, created on the way down when a path names a folder not yet seen. */
function descend(root: FolderNode, segments: string[]): FolderNode {
  let node = root;
  for (const segment of segments) {
    let child = node.folders.find((f) => f.name === segment);
    if (child === undefined) {
      child = {
        name: segment,
        path: node.path === "" ? segment : `${node.path}/${segment}`,
        folders: [],
        pipelines: [],
        files: [],
        pipelineCount: 0,
        fileCount: 0,
      };
      node.folders.push(child);
    }
    node = child;
  }
  return node;
}

/** Sorts every level the way a file browser does (folders first, each list by name) and fills in the subtree counts. */
function settle(node: FolderNode): FolderNode {
  node.folders.sort((a, b) => a.name.localeCompare(b.name));
  node.pipelines.sort((a, b) => a.name.localeCompare(b.name));
  node.files.sort((a, b) => a.path.localeCompare(b.path));

  node.pipelineCount = node.pipelines.length;
  node.fileCount = node.files.length;
  for (const child of node.folders) {
    settle(child);
    node.pipelineCount += child.pipelineCount;
    node.fileCount += child.fileCount;
  }
  return node;
}

/**
 * The folder tree of one project, built from the repo-relative paths of what it holds. Paths outside the project are
 * ignored rather than misplaced, so a caller that groups by project cannot leak a row into the wrong tree.
 *
 * `folders` are the project's folder paths as the repository itself lists them. They are what makes an empty folder
 * visible: a folder holding neither a pipeline nor a file appears in no path, and would otherwise vanish from an
 * outline that is supposed to show what the repository actually holds.
 */
export function buildFolderTree(
  project: string,
  pipelines: readonly PipelineSummary[],
  files: readonly RepoTreeEntry[] = [],
  folders: readonly string[] = [],
): FolderNode {
  const root: FolderNode = {
    name: "",
    path: "",
    folders: [],
    pipelines: [],
    files: [],
    pipelineCount: 0,
    fileCount: 0,
  };

  const mine = (path: string) => projectOf(path) === project;

  for (const folder of folders) {
    if (mine(folder)) {
      const segments = withinProject(project, folder).split("/").filter((s) => s !== "");
      if (segments.length > 0) {
        descend(root, segments);
      }
    }
  }

  for (const pipeline of pipelines) {
    if (mine(pipeline.relativePath)) {
      const segments = withinProject(project, pipeline.relativePath).split("/");
      descend(root, segments.slice(0, -1)).pipelines.push(pipeline);
    }
  }

  for (const file of files) {
    if (mine(file.path)) {
      const segments = withinProject(project, file.path).split("/");
      descend(root, segments.slice(0, -1)).files.push(file);
    }
  }

  return settle(root);
}

/**
 * Whether a folder shows its contents: as the user last opened or closed it, else shut. A project opens onto its
 * top-level folders and its own rows and is read a level at a time, the way a file browser reads a repository, since
 * one that carries thousands of files beside its flows (a data drop, a folder per well) cannot unfold into all of them
 * when its project opens. Only a tree of a search's or a filter's matches (`filtered`) starts with every folder open:
 * it holds just the folders on the way to them, so no match is left behind a collapsed folder.
 */
export function opensFolder(overrides: Readonly<Record<string, boolean>>, path: string, filtered: boolean): boolean {
  return overrides[path] ?? filtered;
}

/** Every folder of the tree that shows its contents (`opensFolder`), by path: the shape the tree primitive reads. */
export function expandedFolders(
  tree: FolderNode,
  overrides: Readonly<Record<string, boolean>>,
  filtered: boolean,
  into: Set<string> = new Set<string>(),
): Set<string> {
  for (const child of tree.folders) {
    if (opensFolder(overrides, child.path, filtered)) {
      into.add(child.path);
    }
    expandedFolders(child, overrides, filtered, into);
  }
  return into;
}
