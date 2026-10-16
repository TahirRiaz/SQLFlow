// Monaco is bundled with the app (no CDN): @monaco-editor/react is pointed at the local monaco-editor package and
// the workers are built by Vite as module workers, so the editor works in an offline/air-gapped deployment.
import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/editor/editor.worker?worker";
import jsonWorker from "monaco-editor/languages/features/json/json.worker?worker";
import { loader } from "@monaco-editor/react";

/**
 * The Monaco API the editors run on: the bundled package handed to the loader below. Typed from monaco-editor itself,
 * since @monaco-editor/react's own `Monaco` type names a module path that monaco-editor's exports no longer publish.
 */
export type Monaco = typeof monaco;

self.MonacoEnvironment = {
  getWorker(_workerId: string, label: string): Worker {
    if (label === "json") {
      return new jsonWorker();
    }

    return new editorWorker();
  },
};

loader.config({ monaco });
