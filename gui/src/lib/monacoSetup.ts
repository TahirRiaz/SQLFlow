// Monaco is bundled with the app (no CDN): @monaco-editor/react is pointed at the local monaco-editor package and
// the workers are built by Vite as module workers, so the editor works in an offline/air-gapped deployment.
import * as monaco from "monaco-editor";
import editorWorker from "monaco-editor/esm/vs/editor/editor.worker?worker";
import jsonWorker from "monaco-editor/esm/vs/language/json/json.worker?worker";
import { loader } from "@monaco-editor/react";

self.MonacoEnvironment = {
  getWorker(_workerId: string, label: string): Worker {
    if (label === "json") {
      return new jsonWorker();
    }

    return new editorWorker();
  },
};

loader.config({ monaco });
