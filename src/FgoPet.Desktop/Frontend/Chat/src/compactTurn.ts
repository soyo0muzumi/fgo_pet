import type { Message, UiState } from "./domain.ts";

type CompactTurnState = Pick<UiState, "ownerKey" | "conversationId" | "messages">;

/** A local reading anchor, never part of the host protocol or persisted history. */
export type CompactTurnAnchor = Readonly<{
  ownerKey: string | null;
  conversationId: string;
  baselineMessageIds: readonly string[];
  userId: string | null;
  canAdmitConversation: boolean;
}>;

/** Capture immediately before submitting, while every existing message belongs
 * to an earlier turn. The host's new user message establishes the boundary. */
export function startCompactTurn(state: CompactTurnState): CompactTurnAnchor {
  return {
    ownerKey: state.ownerKey ?? null,
    conversationId: state.conversationId,
    baselineMessageIds: state.messages.map(message => message.id),
    userId: null,
    canAdmitConversation: state.conversationId === "",
  };
}

/** Return from full reading to the most recent user turn without submitting a
 * message or changing the conversation. Reuse the same scope/admission rules. */
export function recentCompactTurn(state: CompactTurnState): CompactTurnAnchor | null {
  let index = state.messages.length - 1;
  while (index >= 0 && state.messages[index].role !== "user") index--;
  if (index < 0) return null;
  return {
    ...startCompactTurn({ ...state, messages: state.messages.slice(0, index) }),
    userId: state.messages[index].id,
  };
}

/** Preserve the same object until ownership, first-send admission, or the new
 * user message actually changes. Callers can safely use this in an effect. */
export function reconcileCompactTurn(
  anchor: CompactTurnAnchor | null,
  state: CompactTurnState,
): CompactTurnAnchor | null {
  if (!anchor || anchor.ownerKey !== (state.ownerKey ?? null)) return null;

  let conversationId = anchor.conversationId;
  let canAdmitConversation = anchor.canAdmitConversation;
  if (state.conversationId !== conversationId) {
    // Only this first blank-ID admission may carry the reading anchor forward.
    // History navigation and later identity changes must start a new anchor.
    if (conversationId !== "" || !canAdmitConversation || !state.conversationId)
      return null;
    conversationId = state.conversationId;
    canAdmitConversation = false;
  }

  let userId = anchor.userId;
  if (userId === null) {
    const before = new Set(anchor.baselineMessageIds);
    userId = state.messages.find(message => message.role === "user" && !before.has(message.id))?.id ?? null;
  }
  if (conversationId === anchor.conversationId && userId === anchor.userId &&
      canAdmitConversation === anchor.canAdmitConversation) return anchor;
  return { ...anchor, conversationId, canAdmitConversation, userId };
}

/** Never borrow a previous assistant reply while the new user message is still
 * unacknowledged. Keep the whole current turn, including multiple replies and
 * intact Markdown, and let the viewport own overflow rather than slicing text. */
export function compactTurnMessages(
  anchor: CompactTurnAnchor | null,
  state: CompactTurnState,
): Message[] {
  const current = reconcileCompactTurn(anchor, state);
  if (current?.userId == null) return [];
  const start = state.messages.findIndex(message => message.id === current.userId && message.role === "user");
  return start < 0 ? [] : state.messages.slice(start);
}
