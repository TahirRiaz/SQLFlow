import { useCallback } from "react";
import type { ThreadMessageLike } from "@assistant-ui/react";
import { useQuery, useQueryClient } from "@tanstack/react-query";
import { Skeleton } from "@/components/ui/skeleton";
import { chatApi } from "../../api/endpoints";
import { ChatThread } from "./ChatThread";

const NO_MESSAGES: ThreadMessageLike[] = [];

/**
 * An explanation's conversation: a fresh one, asking the explanation's question as its first message, and followed up
 * in the same thread. The Assistant page's conversations list it with the others once its first answer is in.
 */
export default function ExplainConversation({ question }: { question: string }) {
  const queryClient = useQueryClient();
  const capabilities = useQuery({ queryKey: ["chat", "capabilities"], queryFn: () => chatApi.capabilities() });

  const onConversationMinted = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ["chat", "conversations"] });
  }, [queryClient]);

  const onTurnCompleted = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ["chat", "conversations"] });
    void queryClient.invalidateQueries({ queryKey: ["chat", "messages"] });
  }, [queryClient]);

  if (capabilities.data === undefined) {
    return <Skeleton className="m-4 h-24 w-3/4" />;
  }

  return (
    <div className="h-full" data-testid="explain-conversation">
      <ChatThread
        conversationId={null}
        initialMessages={NO_MESSAGES}
        ask={question}
        capabilities={capabilities.data}
        onConversationMinted={onConversationMinted}
        onTurnCompleted={onTurnCompleted}
        composerPlaceholder="Ask a follow-up question..."
      />
    </div>
  );
}
