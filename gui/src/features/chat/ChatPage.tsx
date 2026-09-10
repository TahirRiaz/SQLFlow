import { useCallback, useMemo, useRef, useState } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import {
  AssistantRuntimeProvider,
  CompositeAttachmentAdapter,
  SimpleImageAttachmentAdapter,
  useLocalRuntime,
  WebSpeechDictationAdapter,
  type DictationAdapter,
  type ThreadMessageLike,
} from "@assistant-ui/react";
import { formatDistanceToNow } from "date-fns";
import { Bot, Check, MessageSquarePlus, Pencil, Trash2, X } from "lucide-react";
import { toast } from "sonner";
import { Thread } from "@/components/assistant-ui/thread";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import { Tooltip, TooltipContent, TooltipTrigger } from "@/components/ui/tooltip";
import { cn } from "@/lib/utils";
import { isApiError } from "../../api/client";
import { chatApi } from "../../api/endpoints";
import type { ChatCapabilities, ChatConversation, ChatStreamConversation } from "../../api/types";
import { ConfirmDialog } from "../../components/ConfirmDialog";
import { CorrelationError } from "../../components/CorrelationError";
import { EmptyState } from "../../components/EmptyState";
import { parseUtc } from "../../lib/time";
import {
  canRecordAudio,
  createChatModelAdapter,
  createTranscribeDictationAdapter,
  toThreadMessages,
} from "./chatAdapters";

/**
 * The SQLFlow assistant chat (DESIGN.md 6: a full-bleed editor surface like the lineage graph):
 * a conversation rail on the left and a ChatGPT-style thread on the right. Answers stream over
 * the authenticated SSE channel; every agent run carries the signed-in user's own bearer to the
 * SQLFlow MCP server, so the assistant sees exactly what this user may see. Conversations persist
 * in the catalog and re-open with their full transcript, including tool activity.
 */
export default function ChatPage() {
  const capabilities = useQuery({ queryKey: ["chat", "capabilities"], queryFn: () => chatApi.capabilities() });

  if (capabilities.isLoading) {
    return (
      <div className="flex flex-col gap-3" data-testid="page-chat">
        <Skeleton className="h-8 w-64" />
        <Skeleton className="h-[420px] w-full rounded-lg" />
      </div>
    );
  }
  if (capabilities.isError) {
    return (
      <div data-testid="page-chat">
        {isApiError(capabilities.error)
          ? <CorrelationError error={capabilities.error} />
          : <EmptyState title="The chat assistant is unavailable" description={String(capabilities.error)} />}
      </div>
    );
  }
  if (!capabilities.data!.enabled) {
    return (
      <div data-testid="page-chat">
        <EmptyState
          icon={<Bot />}
          title="The chat assistant is not configured"
          description={
            "Ask an administrator to enable ControlPlane:Assistant on the control plane "
            + "(a model provider plus the SQLFlow MCP server URL); the chat lights up on the next restart."
          }
          data-testid="chat-not-configured"
        />
      </div>
    );
  }

  return <ChatWorkbench capabilities={capabilities.data!} />;
}

function ChatWorkbench({ capabilities }: { capabilities: ChatCapabilities }) {
  const queryClient = useQueryClient();
  const conversations = useQuery({ queryKey: ["chat", "conversations"], queryFn: () => chatApi.listConversations() });

  // What the thread is MOUNTED on: the conversation the user explicitly opened (null = a fresh
  // chat) plus a mount key that changes only on an explicit open/new action. This never follows a
  // conversation minted mid-stream: the thread owns the running answer, and re-deriving its mount
  // state from a freshly minted id would swap it for a transcript-loading skeleton and tear the
  // stream down before its first answer arrived.
  const [opened, setOpened] = useState<{ id: string | null; key: number }>({ id: null, key: 0 });
  // Which conversation the rail highlights: the opened one, or the one the running thread minted.
  const [selected, setSelected] = useState<string | null>(null);

  const openConversation = useCallback((id: string | null) => {
    setSelected(id);
    setOpened((previous) => ({ id, key: previous.key + 1 }));
  }, []);

  const messages = useQuery({
    queryKey: ["chat", "messages", opened.id],
    queryFn: () => chatApi.messages(opened.id!),
    enabled: opened.id !== null,
  });

  const onConversationMinted = useCallback((conversation: ChatStreamConversation) => {
    setSelected(conversation.id);
    void queryClient.invalidateQueries({ queryKey: ["chat", "conversations"] });
  }, [queryClient]);

  const onTurnCompleted = useCallback(() => {
    void queryClient.invalidateQueries({ queryKey: ["chat", "conversations"] });
    void queryClient.invalidateQueries({ queryKey: ["chat", "messages"] });
  }, [queryClient]);

  // The thread mounts once its transcript is known: immediately for a fresh chat, after the
  // messages load for a re-opened conversation (initial messages only apply at mount).
  const initialMessages: ThreadMessageLike[] | null = opened.id === null
    ? []
    : messages.data !== undefined
      ? toThreadMessages(messages.data)
      : null;

  return (
    <div
      className="relative -m-4 flex h-[calc(100vh-93px)] min-w-0 overflow-hidden md:-m-6"
      data-testid="page-chat"
    >
      <ConversationRail
        conversations={conversations}
        selected={selected}
        onOpen={openConversation}
        onNew={() => openConversation(null)}
      />
      <div className="min-w-0 flex-1" data-testid="chat-thread">
        {initialMessages === null
          ? (
            <div className="flex h-full flex-col gap-3 p-6">
              <Skeleton className="h-16 w-2/3" />
              <Skeleton className="h-16 w-3/4 self-end" />
              <Skeleton className="h-16 w-2/3" />
            </div>
          )
          : (
            <ChatThread
              key={`${opened.key}`}
              conversationId={opened.id}
              initialMessages={initialMessages}
              capabilities={capabilities}
              onConversationMinted={onConversationMinted}
              onTurnCompleted={onTurnCompleted}
            />
          )}
      </div>
    </div>
  );
}

function ChatThread({
  conversationId,
  initialMessages,
  capabilities,
  onConversationMinted,
  onTurnCompleted,
}: {
  conversationId: string | null;
  initialMessages: ThreadMessageLike[];
  capabilities: ChatCapabilities;
  onConversationMinted: (conversation: ChatStreamConversation) => void;
  onTurnCompleted: () => void;
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

  return (
    <AssistantRuntimeProvider runtime={runtime}>
      <Thread />
    </AssistantRuntimeProvider>
  );
}

function ConversationRail({
  conversations,
  selected,
  onOpen,
  onNew,
}: {
  conversations: ReturnType<typeof useQuery<ChatConversation[]>>;
  selected: string | null;
  onOpen: (id: string) => void;
  onNew: () => void;
}) {
  const queryClient = useQueryClient();
  const [renamingId, setRenamingId] = useState<string | null>(null);
  const [renameValue, setRenameValue] = useState("");
  const [deleting, setDeleting] = useState<ChatConversation | null>(null);

  const rename = useMutation({
    mutationFn: ({ id, title }: { id: string; title: string }) => chatApi.renameConversation(id, title),
    onSuccess: () => {
      setRenamingId(null);
      void queryClient.invalidateQueries({ queryKey: ["chat", "conversations"] });
    },
    onError: (error) => toast.error("Could not rename the conversation", {
      description: isApiError(error) ? error.message : undefined,
    }),
  });

  const remove = useMutation({
    mutationFn: (id: string) => chatApi.deleteConversation(id),
    onSuccess: (_, id) => {
      setDeleting(null);
      if (selected === id) {
        onNew();
      }
      void queryClient.invalidateQueries({ queryKey: ["chat", "conversations"] });
      toast.success("Conversation deleted");
    },
    onError: (error) => toast.error("Could not delete the conversation", {
      description: isApiError(error) ? error.message : undefined,
    }),
  });

  return (
    <div className="flex w-60 shrink-0 flex-col border-r border-border bg-side-bar/50">
      <div className="flex h-9 shrink-0 items-center justify-between border-b border-border pr-1 pl-3">
        <span className="text-[11px] font-medium tracking-wider text-muted-foreground uppercase">Conversations</span>
        <Tooltip>
          <TooltipTrigger asChild>
            <Button variant="ghost" size="xs" aria-label="New chat" onClick={onNew} data-testid="chat-new">
              <MessageSquarePlus />
            </Button>
          </TooltipTrigger>
          <TooltipContent>New chat</TooltipContent>
        </Tooltip>
      </div>
      <div className="min-h-0 flex-1 overflow-y-auto py-1">
        {conversations.isLoading && (
          <div className="flex flex-col gap-1 px-2 py-1">
            <Skeleton className="h-7 w-full" />
            <Skeleton className="h-7 w-full" />
            <Skeleton className="h-7 w-full" />
          </div>
        )}
        {conversations.isError && (
          <div className="px-2 py-1 text-xs text-destructive">
            {isApiError(conversations.error) ? conversations.error.title : "Could not load conversations."}
          </div>
        )}
        {conversations.data?.length === 0 && (
          <div className="px-3 py-2 text-xs text-muted-foreground">
            No conversations yet; ask your first question.
          </div>
        )}
        {conversations.data?.map((conversation) => (
          <div
            key={conversation.id}
            className={cn(
              "group relative mx-1 flex h-auto flex-col rounded-md",
              selected === conversation.id ? "bg-sidebar-accent" : "hover:bg-sidebar-accent/60",
            )}
          >
            {renamingId === conversation.id
              ? (
                <form
                  className="flex items-center gap-1 px-1 py-0.5"
                  onSubmit={(event) => {
                    event.preventDefault();
                    const title = renameValue.trim();
                    if (title.length > 0) {
                      rename.mutate({ id: conversation.id, title });
                    }
                  }}
                >
                  <Input
                    autoFocus
                    value={renameValue}
                    onChange={(event) => setRenameValue(event.target.value)}
                    onKeyDown={(event) => {
                      if (event.key === "Escape") {
                        setRenamingId(null);
                      }
                    }}
                    className="h-6 text-[12px]"
                    aria-label="Conversation title"
                    data-testid="chat-rename-input"
                  />
                  <Button type="submit" variant="ghost" size="xs" aria-label="Save title" disabled={rename.isPending}>
                    <Check />
                  </Button>
                  <Button
                    type="button"
                    variant="ghost"
                    size="xs"
                    aria-label="Cancel rename"
                    onClick={() => setRenamingId(null)}
                  >
                    <X />
                  </Button>
                </form>
              )
              : (
                <button
                  type="button"
                  className="flex flex-col items-start gap-0 px-2 py-1.5 text-start"
                  onClick={() => onOpen(conversation.id)}
                  data-testid="chat-conversation-item"
                >
                  <span className="w-full truncate pr-10 text-[13px] leading-5">{conversation.title}</span>
                  <span className="font-mono text-[11px] text-muted-foreground">
                    {formatDistanceToNow(parseUtc(conversation.updatedUtc), { addSuffix: true })}
                  </span>
                </button>
              )}
            {renamingId !== conversation.id && (
              <div className="absolute top-1 right-1 hidden items-center group-hover:flex">
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Button
                      variant="ghost"
                      size="xs"
                      aria-label="Rename conversation"
                      onClick={() => {
                        setRenamingId(conversation.id);
                        setRenameValue(conversation.title);
                      }}
                      data-testid="chat-rename"
                    >
                      <Pencil />
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent>Rename</TooltipContent>
                </Tooltip>
                <Tooltip>
                  <TooltipTrigger asChild>
                    <Button
                      variant="ghost"
                      size="xs"
                      aria-label="Delete conversation"
                      onClick={() => setDeleting(conversation)}
                      data-testid="chat-delete"
                    >
                      <Trash2 />
                    </Button>
                  </TooltipTrigger>
                  <TooltipContent>Delete</TooltipContent>
                </Tooltip>
              </div>
            )}
          </div>
        ))}
      </div>
      <ConfirmDialog
        open={deleting !== null}
        title="Delete conversation"
        message={`Delete "${deleting?.title ?? ""}" and its whole transcript? This cannot be undone.`}
        confirmLabel="Delete"
        danger
        busy={remove.isPending}
        onConfirm={() => {
          if (deleting !== null) {
            remove.mutate(deleting.id);
          }
        }}
        onClose={() => setDeleting(null)}
      />
    </div>
  );
}
