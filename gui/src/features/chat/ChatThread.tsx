import { useEffect, useMemo, useRef } from "react";
import {
  AssistantRuntimeProvider,
  CompositeAttachmentAdapter,
  SimpleImageAttachmentAdapter,
  useLocalRuntime,
  WebSpeechDictationAdapter,
  type DictationAdapter,
  type ThreadMessageLike,
} from "@assistant-ui/react";
import { Thread } from "@/components/assistant-ui/thread";
import type { ChatCapabilities, ChatStreamConversation } from "../../api/types";
import { AnswerConfirmation } from "./AnswerConfirmation";
import { canRecordAudio, createChatModelAdapter, createTranscribeDictationAdapter } from "./chatAdapters";

/**
 * One conversation with the assistant: the thread, its composer, and the runtime that streams each answer. The Assistant
 * page mounts it beside its conversation rail; "Explain" mounts it alone, asking its question as the first message.
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
    runQuery: capabilities.runQuery,
    composerPlaceholder,
  }), [capabilities.questionConfirmation, capabilities.runQuery, composerPlaceholder]);

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <Thread components={components} />
    </AssistantRuntimeProvider>
  );
}
