import { useEffect, useMemo, useRef } from "react";
import {
  AssistantRuntimeProvider,
  CompositeAttachmentAdapter,
  SimpleImageAttachmentAdapter,
  useAuiState,
  useLocalRuntime,
  WebSpeechDictationAdapter,
  type DictationAdapter,
  type ThreadMessageLike,
} from "@assistant-ui/react";
import { Thread } from "@/components/assistant-ui/thread";
import type { ChatCapabilities, ChatStreamConversation } from "../../api/types";
import { AnswerConfirmation } from "./AnswerConfirmation";
import { answerMarkText, answerModelOf, markedAnswerMarkdown } from "./answerProvenance";
import { canRecordAudio, createChatModelAdapter, createTranscribeDictationAdapter } from "./chatAdapters";

/** The mark under each answer: that a model wrote it, and which one when the answer records it. */
function AnswerMark() {
  const custom = useAuiState((s) => s.message.metadata.custom);
  return (
    <span className="text-muted-foreground shrink-0 text-[11px]" data-testid="chat-answer-mark">
      {answerMarkText(answerModelOf(custom))}
    </span>
  );
}

/** An exported answer, marked as generated in its header and its first line. */
function markExport(content: string, custom: Readonly<Record<string, unknown>>): string {
  return markedAnswerMarkdown(content, answerModelOf(custom), new Date());
}

/**
 * One conversation with the assistant: the thread, its composer, and the runtime that streams each answer. The Assistant
 * page mounts it beside its conversation rail; "Explain" mounts it alone, asking its question as the first message. Both
 * say under the composer that a language model writes the answers, and mark each answer with the model that wrote it.
 */
export function ChatThread({
  conversationId,
  initialMessages,
  ask,
  capabilities,
  onConversationMinted,
  onTurnCompleted,
  composerPlaceholder,
}: {
  conversationId: string | null;
  initialMessages: ThreadMessageLike[];
  /** A question handed over to ask as this fresh thread's first message, or null. */
  ask: string | null;
  capabilities: ChatCapabilities;
  onConversationMinted: (conversation: ChatStreamConversation) => void;
  onTurnCompleted: () => void;
  /** What the empty composer invites; the thread's own wording when absent. */
  composerPlaceholder?: string;
}) {
  // The adapter reads the live conversation id from this ref, so the first answer of a fresh chat
  // can bind the runtime to its minted conversation without a remount.
  const conversationRef = useRef<string | null>(conversationId);

  const adapter = useMemo(() => createChatModelAdapter({
    getConversationId: () => conversationRef.current,
    onConversation: (conversation) => {
      if (conversationRef.current === null) {
        conversationRef.current = conversation.id;
        onConversationMinted(conversation);
      }
    },
    onTurnCompleted,
  }), [onConversationMinted, onTurnCompleted]);

  const adapters = useMemo(() => {
    const dictation: DictationAdapter | undefined = capabilities.transcription && canRecordAudio()
      ? createTranscribeDictationAdapter()
      : WebSpeechDictationAdapter.isSupported()
        ? new WebSpeechDictationAdapter()
        : undefined;
    return {
      ...(capabilities.images
        ? { attachments: new CompositeAttachmentAdapter([new SimpleImageAttachmentAdapter()]) }
        : {}),
      ...(dictation ? { dictation } : {}),
    };
  }, [capabilities]);

  const runtime = useLocalRuntime(adapter, { initialMessages, adapters });

  // The handed-over question is sent once the thread is up, and once only (an effect runs twice in development).
  const asked = useRef(false);
  useEffect(() => {
    if (ask !== null && !asked.current) {
      asked.current = true;
      runtime.thread.append(ask);
    }
  }, [ask, runtime]);

  // The learning loop's confirmation moment hangs under every answer. It is a stable identity per
  // capability flag, so the thread is not handed a new component type on each render.
  const components = useMemo(() => ({
    AnswerFooter: () => <AnswerConfirmation enabled={capabilities.questionConfirmation} />,
    AnswerMark,
    disclosure: capabilities.disclosure,
    markExport,
    runQuery: capabilities.runQuery,
    composerPlaceholder,
  }), [capabilities.questionConfirmation, capabilities.disclosure, capabilities.runQuery, composerPlaceholder]);

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <Thread components={components} />
    </AssistantRuntimeProvider>
  );
}
