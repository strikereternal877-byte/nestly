"use client";

import * as signalR from "@microsoft/signalr";
import { useMutation, useQuery } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";
import { Alert, Button, Card, Skeleton, cx } from "@/components/ui";
import { API_BASE_URL, API_V1, apiFetch, describeError } from "@/lib/api";
import { getAccessToken } from "@/lib/auth";
import { ChatContextType, ChatSenderType } from "@/lib/types";
import type { ChatMessagePageResult, ChatMessageResponse, ChatThreadResponse } from "@/lib/types";

/**
 * Chat panel embedded on a booking detail or support ticket detail page
 * (task 192). REST is the real send/read path (task 190/191 - works even if
 * the socket never connects); the SignalR hub at /hubs/chat only pushes live
 * updates to a thread this client has already GETten/POSTed through once.
 */
export function ChatWidget({ contextType, contextId }: { contextType: ChatContextType; contextId: string }) {
  const [draft, setDraft] = useState("");
  const [liveMessages, setLiveMessages] = useState<ChatMessageResponse[]>([]);
  const bottomRef = useRef<HTMLDivElement>(null);

  const threadQuery = useQuery({
    queryKey: ["chat-thread", contextType, contextId],
    queryFn: () =>
      apiFetch<ChatThreadResponse>(`${API_V1}/chat/threads`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ contextType, contextId }),
      }),
  });

  const threadId = threadQuery.data?.id;

  const historyQuery = useQuery({
    queryKey: ["chat-messages", threadId],
    queryFn: () =>
      apiFetch<ChatMessagePageResult>(`${API_V1}/chat/threads/${threadId}/messages?pageSize=100`, {
        authenticated: true,
      }),
    enabled: !!threadId,
  });

  // Server history plus anything that arrived live since it loaded, deduped
  // by id - both our own just-sent message (added directly, in case the
  // socket never connects - REST is the real send path, same as the
  // backend's design) and the hub's echo of that same message can land here.
  const messages = dedupeById([...(historyQuery.data?.messages ?? []), ...liveMessages]);

  const sendMutation = useMutation({
    mutationFn: (body: string) =>
      apiFetch<ChatMessageResponse>(`${API_V1}/chat/threads/${threadId}/messages`, {
        method: "POST",
        authenticated: true,
        body: JSON.stringify({ body }),
      }),
    onSuccess: (message) => {
      setDraft("");
      setLiveMessages((prev) => [...prev, message]);
    },
  });

  const markReadMutation = useMutation({
    mutationFn: () => apiFetch(`${API_V1}/chat/threads/${threadId}/read`, { method: "POST", authenticated: true }),
  });

  useEffect(() => {
    if (!threadId) return;

    // "/hubs/chat" mirrors ChatHubRoutes.ChatPath; accessTokenFactory (rather
    // than an Authorization header, which a browser can't set on a WebSocket
    // handshake) puts the JWT on ?access_token= for the client automatically -
    // exactly what ChatHubJwtEvents.OnMessageReceived reads back server-side.
    // withCredentials: false - auth rides the query-string token above, not
    // cookies, so the negotiate request shouldn't ask for credentialed CORS
    // (the API's CORS policy doesn't grant it, and doesn't need to).
    const connection = new signalR.HubConnectionBuilder()
      .withUrl(`${API_BASE_URL}/hubs/chat`, {
        accessTokenFactory: () => getAccessToken() ?? "",
        withCredentials: false,
      })
      .withAutomaticReconnect()
      .build();

    // "MessageReceived" mirrors ChatHubBroadcastHandler.MessageReceivedMethod.
    connection.on("MessageReceived", (message: ChatMessageResponse) => {
      if (message.threadId !== threadId) return;
      setLiveMessages((prev) => [...prev, message]);
      if (message.senderType !== ChatSenderType.Customer) {
        markReadMutation.mutate();
      }
    });

    connection.onreconnected(() => {
      connection.invoke("JoinThread", threadId).catch(() => {});
    });

    const started = connection
      .start()
      .then(() => connection.invoke("JoinThread", threadId))
      .catch(() => {
        // Live delivery degrades to REST-only (a manual refetch); the send/
        // read paths above never depend on the socket being up.
      });

    return () => {
      // Stop only once start has settled: stopping mid-negotiation makes
      // SignalR log an error (dev StrictMode remounts every effect, so the
      // Next.js overlay showed one issue per hub on each visit).
      started
        .then(() => connection.invoke("LeaveThread", threadId))
        .catch(() => {})
        .finally(() => connection.stop());
    };
    // Reconnects only when the thread changes - markReadMutation is a fresh
    // object every render (useMutation) and would otherwise tear the socket
    // down and reconnect on every render.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [threadId]);

  useEffect(() => {
    if (threadId && messages.some((m) => m.senderType !== ChatSenderType.Customer && !m.readAtUtc)) {
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
            <Button
              size="sm"
              variant="secondary"
              loading={threadQuery.isRefetching}
              onClick={() => threadQuery.refetch()}
            >
              Retry
            </Button>
          }
        >
          {describeError(threadQuery.error)}
        </Alert>
      </Card>
    );
  }

  // The thread opened but its history didn't. The composer below would post
  // into a conversation the customer can't see, so this has to be a terminal
  // state with its own retry rather than an inline warning.
  if (historyQuery.isError) {
    return (
      <Card title="Chat">
        <Alert
          tone="error"
          title="Couldn't load these messages"
          action={
            <Button
              size="sm"
              variant="secondary"
              loading={historyQuery.isRefetching}
              onClick={() => historyQuery.refetch()}
            >
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
    <Card title="Chat">
      <div className="flex max-h-96 flex-col gap-3 overflow-y-auto rounded-lg border border-line p-3">
        {messages.length === 0 ? (
          <p className="text-sm text-fg-muted">No messages yet — say hello.</p>
        ) : (
          messages.map((message) => {
            const fromCustomer = message.senderType === ChatSenderType.Customer;
            return (
              <div
                key={message.id}
                className={cx(
                  "max-w-[80%] rounded-2xl px-3 py-2 text-sm",
                  fromCustomer
                    ? "self-end rounded-br-md bg-brand-600 text-fg-on-brand"
                    : "self-start rounded-bl-md bg-surface-2 text-fg",
                )}
              >
                {/* Side and colour are the only visual cue for who spoke, and
                    neither reaches a screen reader. */}
                <span className="sr-only">{fromCustomer ? "You said:" : "Support said:"}</span>
                <p>{message.body}</p>
                <p
                  className={cx(
                    "nums mt-1 text-xs",
                    fromCustomer ? "text-fg-on-brand/70" : "text-fg-subtle",
                  )}
                >
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
              // `variables` is the body of the attempt that failed — the draft
              // box was never cleared (that only happens onSuccess), but
              // resending it directly saves the customer re-finding the send
              // button after reading the error.
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
        <label htmlFor="chat-message" className="sr-only">
          Message
        </label>
        <input
          id="chat-message"
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

/**
 * Matches the real transcript: the same bordered, padded scroll box holding
 * alternating bubbles, plus the composer row underneath. Bubble widths vary so
 * it reads as a conversation rather than a stack of identical bars.
 */
function ChatSkeleton() {
  const bubbles = [
    { fromCustomer: false, width: "w-3/5" },
    { fromCustomer: true, width: "w-2/5" },
    { fromCustomer: false, width: "w-1/2" },
    { fromCustomer: true, width: "w-1/3" },
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
              bubble.fromCustomer ? "self-end rounded-br-md" : "self-start rounded-bl-md",
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
