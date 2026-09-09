// The glue between assistant-ui's LocalRuntime and the control plane's /api/v1/chat surface:
// the chat model adapter streams one question over the authenticated SSE channel (client.ts owns
// auth and error shaping, exactly like every other call), and the dictation adapter records a
// voice message with MediaRecorder and turns it into composer text via the server-side
// transcription endpoint (the same provider account that answers the questions).

import type { ChatModelAdapter, DictationAdapter, ThreadAssistantMessagePart, ThreadMessageLike } from "@assistant-ui/react";
import { toast } from "sonner";
import { chatApi } from "../../api/endpoints";
import { isApiError, type SseFrame } from "../../api/client";
import { parseUtc } from "../../lib/time";
import type {
  ChatMessage, ChatStreamConversation, ChatStreamDelta, ChatStreamDone, ChatStreamError, ChatToolCall,
} from "../../api/types";

/** How one runtime instance tracks its conversation across turns (owned by the chat page). */
export interface ChatSession {
  /** The conversation the next question lands in; null asks the server to mint one. */
  getConversationId(): string | null;
  /** Fired when the server answers with the turn's conversation (minted or echoed). */
  onConversation(conversation: ChatStreamConversation): void;
  /** Fired after a turn fully completes (the answer is persisted server-side). */
  onTurnCompleted(): void;
}

/** One in-flight tool call while the answer streams. */
interface StreamedToolCall {
  name: string;
  status: string;
}

/** Maps the streamed tool state plus accumulated text onto the cumulative content assistant-ui
 * expects (every yield replaces the previous state; deltas would flicker). */
function buildContent(toolCalls: StreamedToolCall[], text: string): ThreadAssistantMessagePart[] {
  const parts: ThreadAssistantMessagePart[] = toolCalls.map((tool, index) => ({
    type: "tool-call",
    toolCallId: `live_${index}`,
    toolName: tool.name,
    args: {},
    argsText: "{}",
    ...(tool.status === "started" ? {} : { result: tool.status, isError: tool.status === "failed" }),
  }));
  if (text.length > 0) {
    parts.push({ type: "text", text });
  }
  return parts;
}

function upsertToolCall(toolCalls: StreamedToolCall[], update: ChatToolCall): void {
  if (update.status === "started") {
    toolCalls.push({ name: update.name, status: update.status });
    return;
  }
  for (let i = toolCalls.length - 1; i >= 0; i--) {
    if (toolCalls[i].name === update.name && toolCalls[i].status === "started") {
      toolCalls[i] = { name: update.name, status: update.status };
      return;
    }
  }
  toolCalls.push({ name: update.name, status: update.status });
}

/**
 * The chat model adapter: sends only the NEW question (text + image data URIs); the control plane
 * holds the durable transcript and rebuilds the model conversation itself, so the browser never
 * re-uploads history. Streaming frames become cumulative content yields.
 */
export function createChatModelAdapter(session: ChatSession): ChatModelAdapter {
  return {
    async *run({ messages, abortSignal }) {
      const last = messages.at(-1);
      let question = "";
      const images: string[] = [];
      if (last?.role === "user") {
        for (const part of last.content) {
          if (part.type === "text" && part.text.length > 0) {
            question += (question.length > 0 ? "\n" : "") + part.text;
          } else if (part.type === "image") {
            images.push(part.image);
          }
        }
        // Image attachments ride on the message's attachment list; their content parts carry the
        // same data-URI shape as inline image parts.
        for (const attachment of last.attachments ?? []) {
          for (const part of attachment.content ?? []) {
            if (part.type === "image" && !images.includes(part.image)) {
              images.push(part.image);
            }
          }
        }
      }

      // Bridge the callback-based SSE client into this generator: frames land in a queue, the
      // generator drains it, and a parked promise wakes it when new frames (or the end) arrive.
      const queue: SseFrame[] = [];
      let wake: (() => void) | null = null;
      let ended = false;
      let failure: unknown = null;
      const notify = () => {
        wake?.();
        wake = null;
      };
      void chatApi.ask(
        { conversationId: session.getConversationId(), question, images },
        (frame) => {
          queue.push(frame);
          notify();
        },
        abortSignal,
      ).then(
        () => {
          ended = true;
          notify();
        },
        (error: unknown) => {
          failure = error;
          ended = true;
          notify();
        },
      );

      const toolCalls: StreamedToolCall[] = [];
      let text = "";
      let done: ChatStreamDone | null = null;
      for (;;) {
        while (queue.length > 0) {
          const frame = queue.shift()!;
          switch (frame.event) {
            case "conversation":
              session.onConversation(JSON.parse(frame.data) as ChatStreamConversation);
              break;
            case "delta":
              text += (JSON.parse(frame.data) as ChatStreamDelta).text;
              break;
            case "tool":
              upsertToolCall(toolCalls, JSON.parse(frame.data) as ChatToolCall);
              break;
            case "done":
              done = JSON.parse(frame.data) as ChatStreamDone;
              break;
            case "error":
              throw new Error((JSON.parse(frame.data) as ChatStreamError).message);
            default:
              break;
          }
          yield { content: buildContent(toolCalls, text) };
        }
        if (ended) {
          break;
        }
        await new Promise<void>((resolve) => {
          wake = resolve;
        });
      }

      // A user cancellation ends the run quietly (the server keeps the partial answer). The signal
      // is the reliable tell: fetch rejects with whatever reason the caller aborted with, which is
      // not always a DOMException.
      if (abortSignal.aborted) {
        return;
      }
      if (failure !== null) {
        throw failure instanceof Error ? failure : new Error(String(failure));
      }
      if (done === null) {
        // The stream ended without its terminal `done` frame: the connection dropped, or the server
        // died mid-answer. Saying so beats returning quietly, which would leave an empty assistant
        // bubble that reads as the assistant having ignored the question.
        throw new Error("The answer stream ended before the assistant finished. Ask again.");
      }

      session.onTurnCompleted();
      yield {
        content: buildContent(toolCalls, done.text),
        status: { type: "complete", reason: "stop" },
      };
    },
  };
}

/** The recording formats browsers produce, in preference order; the transcription models accept all of them. */
const RECORDING_MIME_TYPES = ["audio/webm;codecs=opus", "audio/webm", "audio/mp4", "audio/ogg;codecs=opus"];

function pickRecordingMimeType(): string | undefined {
  if (typeof MediaRecorder === "undefined") {
    return undefined;
  }
  return RECORDING_MIME_TYPES.find((candidate) => MediaRecorder.isTypeSupported(candidate));
}

function fileExtensionFor(mimeType: string): string {
  if (mimeType.includes("webm")) return "webm";
  if (mimeType.includes("mp4")) return "mp4";
  if (mimeType.includes("ogg")) return "ogg";
  if (mimeType.includes("wav")) return "wav";
  return "webm";
}

/** Whether this browser can record audio at all (the mic is hidden otherwise). */
export function canRecordAudio(): boolean {
  return typeof MediaRecorder !== "undefined"
    && typeof navigator !== "undefined"
    && !!navigator.mediaDevices?.getUserMedia;
}

/**
 * The dictation adapter behind the composer's microphone: records until stopped, then transcribes
 * the recording server-side and hands the text to the composer. Cancel discards the recording.
 * Errors toast (DESIGN.md 8.1: no silent failure) and end the session in the error state.
 */
export function createTranscribeDictationAdapter(): DictationAdapter {
  return {
    listen(): DictationAdapter.Session {
      const speechStart = new Set<() => void>();
      const speech = new Set<(result: DictationAdapter.Result) => void>();
      const speechEnd = new Set<(result: DictationAdapter.Result) => void>();
      const chunks: Blob[] = [];
      let recorder: MediaRecorder | null = null;
      let tracks: MediaStreamTrack[] = [];
      let cancelled = false;

      const session: DictationAdapter.Session = {
        status: { type: "starting" },
        stop: async () => {
          await ready;
          const active = recorder;
          if (cancelled || active === null) {
            session.status = { type: "ended", reason: "stopped" };
            return;
          }
          if (active.state !== "inactive") {
            await new Promise<void>((resolve) => {
              active.addEventListener("stop", () => resolve(), { once: true });
              active.stop();
            });
          }
          tracks.forEach((track) => track.stop());
          const mimeType = active.mimeType || "audio/webm";
          const blob = new Blob(chunks, { type: mimeType });
          if (blob.size === 0) {
            session.status = { type: "ended", reason: "stopped" };
            return;
          }
          try {
            const { text } = await chatApi.transcribe(blob, `recording.${fileExtensionFor(mimeType)}`);
            const result: DictationAdapter.Result = { transcript: text, isFinal: true };
            speech.forEach((cb) => cb(result));
            speechEnd.forEach((cb) => cb(result));
            session.status = { type: "ended", reason: "stopped" };
          } catch (error) {
            session.status = { type: "ended", reason: "error" };
            toast.error("Could not transcribe the recording", {
              description: isApiError(error) ? error.message : "The transcription service did not answer.",
            });
          }
        },
        cancel: () => {
          cancelled = true;
          try {
            if (recorder && recorder.state !== "inactive") {
              recorder.stop();
            }
          } catch {
            // A recorder that already stopped throws; cancellation only cares that it is not running.
          }
          tracks.forEach((track) => track.stop());
          session.status = { type: "ended", reason: "cancelled" };
        },
        onSpeechStart: (callback) => {
          speechStart.add(callback);
          return () => speechStart.delete(callback);
        },
        onSpeech: (callback) => {
          speech.add(callback);
          return () => speech.delete(callback);
        },
        onSpeechEnd: (callback) => {
          speechEnd.add(callback);
          return () => speechEnd.delete(callback);
        },
      };

      const ready = navigator.mediaDevices.getUserMedia({ audio: true }).then(
        (stream) => {
          if (cancelled) {
            stream.getTracks().forEach((track) => track.stop());
            return;
          }
          tracks = stream.getTracks();
          const mimeType = pickRecordingMimeType();
          recorder = new MediaRecorder(stream, mimeType ? { mimeType } : undefined);
          recorder.addEventListener("dataavailable", (event) => {
            if (event.data.size > 0) {
              chunks.push(event.data);
            }
          });
          recorder.start();
          session.status = { type: "running" };
          speechStart.forEach((cb) => cb());
        },
        () => {
          session.status = { type: "ended", reason: "error" };
          toast.error("Microphone unavailable", {
            description: "Allow microphone access in the browser to use voice input.",
          });
        },
      );

      return session;
    },
  };
}

/** Maps a persisted transcript onto the runtime's initial messages, so a re-opened conversation
 * renders exactly as it streamed: tool activity first, then the answer text. */
type ThreadMessagePart = Exclude<ThreadMessageLike["content"], string>[number];

export function toThreadMessages(messages: ChatMessage[]): ThreadMessageLike[] {
  return messages.map((message) => {
    const parts: ThreadMessagePart[] = [];
    if (message.role === "assistant") {
      message.toolCalls.forEach((tool, index) => {
        parts.push({
          type: "tool-call",
          toolCallId: `db_${message.id}_${index}`,
          toolName: tool.name,
          args: {},
          argsText: "{}",
          ...(tool.status === "started" ? {} : { result: tool.status, isError: tool.status === "failed" }),
        });
      });
    } else {
      for (const image of message.images) {
        parts.push({ type: "image", image });
      }
    }
    if (message.text.length > 0) {
      parts.push({ type: "text", text: message.text });
    }
    return {
      id: `db_${message.id}`,
      role: message.role,
      content: parts,
      createdAt: parseUtc(message.createdUtc),
    };
  });
}
