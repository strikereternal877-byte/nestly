"use client";

import * as signalR from "@microsoft/signalr";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { Alert, Button, Card, Skeleton, cx } from "@/components/ui";
import { getChatHistory, getOrCreateChatThread, markChatThreadRead, sendChatMessage } from "@/lib/chat-api";
import { API_BASE_URL, describeError } from "@/lib/api";
import { getAccessToken } from "@/lib/auth";
import { ChatContextType, ChatSenderType } from "@/lib/chat-types";
import type { ChatMessageResponse } from "@/lib/chat-types";

/**
 * Chat panel on the job detail screen (task 193's provider reply view,
 * PRODUCT-ENHANCEMENTS.md "3. IN-APP CHAT") - the provider-app counterpart to
 * customer-web's ChatWidget, whose interaction pattern this mirrors exactly
 * (REST as the real send/read path, the /hubs/chat socket only for live
 * push once a thread has been opened through here). The only difference is
 * the "own message" side: here it's Provider, not Customer.
 */
export function ChatPanel({ bookingId }: { bookingId: string }) {
  const [draft, setDraft] = useState("");
  const [liveMessages, setLiveMessages] = useState<ChatMessageResponse[]>([]);
  const bottomRef = useRef<HTMLDivElement>(null);

  const threadQuery = useQuery({
    queryKey: ["provider-chat-thread", bookingId],
    queryFn: () => getOrCreateChatThread({ contextType: ChatContextType.Booking, contextId: bookingId }),
  });

  const threadId = threadQuery.data?.id;

  const historyQuery = useQuery({
    queryKey: ["provider-chat-messages", threadId],
    queryFn: () => getChatHistory(threadId!),
    enabled: !!threadId,
  });

  // Server history plus anything that arrived live since it loaded, deduped
  // by id - same reasoning as ChatWidget: our own just-sent message is added
  // directly in case the socket never connects, and the hub's echo of it can
  // also land here.
  const messages = dedupeById([...(historyQuery.data?.messages ?? []), ...liveMessages]);

  const sendMutation = useMutation({
    mutationFn: (body: string) => sendChatMessage(threadId!, { body }),
    onSuccess: (message) => {
      setDraft("");
      setLiveMessages((prev) => [...prev, message]);
    },
  });

  const markReadMutation = useMutation({
    mutationFn: () => markChatThreadRead(threadId!),
  });

  useEffect(() => {
    if (!threadId) return;

    // "/hubs/chat" mirrors HubRoutes.ChatPath; accessTokenFactory puts the
    // JWT on ?access_token= since a browser can't set an Authorization
    // header on the WebSocket handshake - same pattern as useJobStatusLive's
    // tracking-hub connection.
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/chat`, {
        accessTokenFactory: () => getAccessToken() ?? "",
        withCredentials: false,
      })
      .withAutomaticReconnect()
      // See useJobStatusLive's identical call for why this is here: without
      // it, @microsoft/signalr's default Information level logs the
      // fully-resolved connection URL - including ?access_token=<JWT> - to
      // the browser console on every connect/reconnect.
      .configureLogging(signalR.LogLevel.Warning)
      .build();

    // "MessageReceived" mirrors ChatHubBroadcastHandler.MessageReceivedMethod.
    connection.on("MessageReceived", (message: ChatMessageResponse) => {
      if (message.threadId !== threadId) return;
      setLiveMessages((prev) => [...prev, message]);
      if (message.senderType !== ChatSenderType.Provider) {
        markReadMutation.mutate();
      }
    });

    connection.onreconnected(() => {
      connection.invoke("JoinThread", threadId).catch(() => {});
    });

    connection
      .start()
      .then(() => connection.invoke("JoinThread", threadId))
      .catch(() => {
        // Live delivery degrades to REST-only (a manual refetch); the send/
        // read paths above never depend on the socket being up.
      });

    return () => {
      connection.invoke("LeaveThread", threadId).catch(() => {});
      connection.stop();
    };
    // Reconnects only when the thread changes - markReadMutation is a fresh
    // object every render (useMutation) and would otherwise tear the socket
    // down and reconnect on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [threadId]);

  useEffect(() => {
    if (threadId && messages.some((m) => m.senderType !== ChatSenderType.Provider && !m.readAtUtc)) {
      markReadMutation.mutate();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [threadId, messages.length]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages.length]);

  function handleSend() {
    const body = draft.trim();
    if (!body || !threadId) return;
    sendMutation.mutate(body);
  }

  if (threadQuery.isPending || historyQuery.isPending) {
    return (
      <Card title="Chat">
        <ChatSkeleton />
      </Card>
    );
  }

  if (threadQuery.isError) {
    return (
      <Card title="Chat">
        <Alert
          tone="error"
          title="Couldn't open this chat"
          action={
            <Button size="sm" variant="secondary" loading={threadQuery.isRefetching} onClick={() => threadQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(threadQuery.error)}
        </Alert>
      </Card>
    );
  }

  if (historyQuery.isError) {
    return (
      <Card title="Chat">
        <Alert
          tone="error"
          title="Couldn't load these messages"
          action={
            <Button size="sm" variant="secondary" loading={historyQuery.isRefetching} onClick={() => historyQuery.refetch()}>
              Retry
            </Button>
          }
        >
          {describeError(historyQuery.error)}
        </Alert>
      </Card>
    );
  }

  return (
    <Card title="Chat" description="Message the customer about this job.">
      <div className="flex max-h-96 flex-col gap-3 overflow-y-auto rounded-lg border border-line p-3">
        {messages.length === 0 ? (
          <p className="text-sm text-fg-muted">No messages yet — say hello.</p>
        ) : (
          messages.map((message) => {
            const fromProvider = message.senderType === ChatSenderType.Provider;
            return (
              <div
                key={message.id}
                className={cx(
                  "max-w-[80%] rounded-2xl px-3 py-2 text-sm",
                  fromProvider
                    ? "self-end rounded-br-md bg-brand-600 text-fg-on-brand"
                    : "self-start rounded-bl-md bg-surface-2 text-fg",
                )}
              >
                <span className="sr-only">{fromProvider ? "You said:" : "Customer said:"}</span>
                <p>{message.body}</p>
                <p className={cx("nums mt-1 text-xs", fromProvider ? "text-fg-on-brand/70" : "text-fg-subtle")}>
                  {new Date(message.sentAtUtc).toLocaleTimeString()}
                </p>
              </div>
            );
          })
        )}
        <div ref={bottomRef} />
      </div>

      {sendMutation.isError ? (
        <div className="mt-2">
          <Alert
            tone="error"
            title="Your message didn't send"
            action={
              sendMutation.variables ? (
                <Button
                  size="sm"
                  variant="secondary"
                  loading={sendMutation.isPending}
                  onClick={() => sendMutation.mutate(sendMutation.variables as string)}
                >
                  Try again
                </Button>
              ) : undefined
            }
          >
            {describeError(sendMutation.error)}
          </Alert>
        </div>
      ) : null}

      <form
        className="mt-3 flex gap-2"
        onSubmit={(event) => {
          event.preventDefault();
          handleSend();
        }}
      >
        <label htmlFor="job-chat-message" className="sr-only">
          Message
        </label>
        <input
          id="job-chat-message"
          type="text"
          maxLength={4000}
          value={draft}
          onChange={(e) => setDraft(e.target.value)}
          placeholder="Type a message…"
          className="min-w-0 flex-1 rounded-lg border border-line bg-surface px-3 py-2 text-sm text-fg shadow-xs outline-none transition duration-fast ease-out placeholder:text-fg-subtle hover:border-line-strong focus:border-brand-600 focus:ring-2 focus:ring-brand-600/25"
        />
        <Button type="submit" loading={sendMutation.isPending} disabled={!draft.trim()}>
          Send
        </Button>
      </form>
    </Card>
  );
}

/** Matches the real transcript shape - a bordered scroll box of alternating bubbles plus the composer row. */
function ChatSkeleton() {
  const bubbles = [
    { fromProvider: false, width: "w-3/5" },
    { fromProvider: true, width: "w-2/5" },
    { fromProvider: false, width: "w-1/2" },
  ];

  return (
    <div aria-hidden>
      <div className="flex flex-col gap-3 rounded-lg border border-line p-3">
        {bubbles.map((bubble, index) => (
          <Skeleton
            key={index}
            className={cx(
              "h-14 rounded-2xl",
              bubble.width,
              bubble.fromProvider ? "self-end rounded-br-md" : "self-start rounded-bl-md",
            )}
          />
        ))}
      </div>
      <div className="mt-3 flex gap-2">
        <Skeleton className="h-[38px] min-w-0 flex-1" />
        <Skeleton className="h-[38px] w-20 rounded-lg" />
      </div>
    </div>
  );
}

/** Later entries win (e.g. a read-receipt update arriving after the original send). */
function dedupeById(messages: ChatMessageResponse[]): ChatMessageResponse[] {
  const byId = new Map(messages.map((m) => [m.id, m]));
  return Array.from(byId.values()).sort((a, b) => a.sentAtUtc.localeCompare(b.sentAtUtc));
}
