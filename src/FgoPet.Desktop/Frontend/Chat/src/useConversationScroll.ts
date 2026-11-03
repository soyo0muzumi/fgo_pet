import { useCallback, useLayoutEffect, useRef, useState } from "react";

type ReadingPosition = {
  messageId: string | null;
  offset: number;
  scrollTop: number;
};

const BOTTOM_TOLERANCE = 3;

function hasReadingSelection(container: HTMLElement) {
  const selection = window.getSelection();
  return !!selection && !selection.isCollapsed &&
    (!!selection.anchorNode && container.contains(selection.anchorNode) ||
      !!selection.focusNode && container.contains(selection.focusNode));
}

function messageOffset(container: HTMLElement, message: HTMLElement) {
  const bounds = container.getBoundingClientRect();
  // Motion may scale the containing window during its entrance. Store offsets
  // in scroll coordinates, independent of that visual transform.
  const scale = container.offsetHeight ? bounds.height / container.offsetHeight : 1;
  return (message.getBoundingClientRect().top - bounds.top) / (scale || 1) -
    container.clientTop;
}

/**
 * Owns only the conversation viewport. User reading pauses following; content
 * growth and width changes restore a stable message rather than a stale pixel
 * position. The host remains responsible for the actual desktop window.
 */
export function useConversationScroll({
  conversationId,
  active,
}: {
  conversationId: string;
  active: boolean;
}) {
  const scroller = useRef<HTMLDivElement>(null);
  const content = useRef<HTMLDivElement>(null);
  const [showLatest, setShowLatest] = useState(false);
  const following = useRef(true);
  const saved = useRef<ReadingPosition | null>(null);
  const previousTop = useRef(0);
  const programmaticTop = useRef<number | null>(null);
  const intent = useRef<"up" | "down" | null>(null);
  const selectionPaused = useRef(false);
  const enabled = useRef(active);
  enabled.current = active;

  const updateButton = useCallback(() => {
    const el = scroller.current;
    if (!el) return;
    setShowLatest(!following.current &&
      el.scrollHeight - el.clientHeight - el.scrollTop > BOTTOM_TOLERANCE);
  }, []);

  const capturePosition = useCallback(() => {
    const el = scroller.current;
    if (!el || !el.clientHeight) return;
    let message: HTMLElement | undefined;
    for (const candidate of el.querySelectorAll<HTMLElement>("[data-message-id]")) {
      const offset = messageOffset(el, candidate);
      if (offset + candidate.offsetHeight > 0 && offset < el.clientHeight) {
        message = candidate;
        break;
      }
    }
    saved.current = {
      messageId: message?.dataset.messageId ?? null,
      offset: message ? messageOffset(el, message) : 0,
      scrollTop: el.scrollTop,
    };
  }, []);

  const pauseFollow = useCallback(() => {
    following.current = false;
    programmaticTop.current = null;
    capturePosition();
    updateButton();
  }, [capturePosition, updateButton]);

  const setScrollTop = useCallback((top: number) => {
    const el = scroller.current;
    if (!el) return;
    const target = Math.max(0, Math.min(top, el.scrollHeight - el.clientHeight));
    if (Math.abs(el.scrollTop - target) < 0.5) return;
    programmaticTop.current = target;
    // A single deliberate jump avoids mistaking a smooth-scroll intermediate
    // position for a user's attempt to stop following.
    el.scrollTo({ top: target, behavior: "instant" });
    previousTop.current = el.scrollTop;
  }, []);

  const synchronize = useCallback(() => {
    const el = scroller.current;
    if (!enabled.current || !el || !el.clientHeight) return;
    if (hasReadingSelection(el)) {
      if (!selectionPaused.current) {
        selectionPaused.current = true;
        pauseFollow();
      }
      // Do not reposition content underneath an active drag/selection.
      updateButton();
      return;
    }
    selectionPaused.current = false;
    if (following.current) {
      setScrollTop(el.scrollHeight);
    } else if (saved.current) {
      const position = saved.current;
      const message = position.messageId
        ? Array.from(el.querySelectorAll<HTMLElement>("[data-message-id]"))
          .find((item) => item.dataset.messageId === position.messageId)
        : undefined;
      setScrollTop(message
        ? el.scrollTop + messageOffset(el, message) - position.offset
        : position.scrollTop);
    }
    updateButton();
  }, [pauseFollow, setScrollTop, updateButton]);

  const resetFollow = useCallback(() => {
    const el = scroller.current;
    if (el && hasReadingSelection(el)) window.getSelection()?.removeAllRanges();
    following.current = true;
    selectionPaused.current = false;
    saved.current = null;
    intent.current = null;
    programmaticTop.current = null;
    setShowLatest(false);
    synchronize();
  }, [synchronize]);

  useLayoutEffect(() => {
    resetFollow();
  }, [conversationId, resetFollow]);

  useLayoutEffect(() => {
    const el = scroller.current;
    if (!active || !el) return;
    let frame = 0;
    let pointerDown = false;
    let touch: { x: number; y: number } | null = null;
    const oldAnchoring = el.style.getPropertyValue("overflow-anchor");
    // Browser anchoring and our message anchor must not both adjust the view.
    el.style.setProperty("overflow-anchor", "none");
    const schedule = () => {
      if (frame) return;
      frame = requestAnimationFrame(() => {
        frame = 0;
        synchronize();
      });
    };
    const startIntent = (direction: "up" | "down") => {
      intent.current = direction;
      programmaticTop.current = null;
      if (direction === "up") pauseFollow();
      else if (!hasReadingSelection(el) &&
        el.scrollHeight - el.clientHeight - el.scrollTop <= BOTTOM_TOLERANCE) {
        // A downward gesture at the bottom may not emit a scroll event.
        following.current = true;
        saved.current = null;
        updateButton();
      }
    };
    const onWheel = (event: WheelEvent) => {
      if (event.ctrlKey || Math.abs(event.deltaY) <= Math.abs(event.deltaX)) return;
      if (event.deltaY) startIntent(event.deltaY < 0 ? "up" : "down");
    };
    const onTouchStart = (event: TouchEvent) => {
      const point = event.touches[0];
      touch = point ? { x: point.clientX, y: point.clientY } : null;
    };
    const onTouchMove = (event: TouchEvent) => {
      const point = event.touches[0];
      if (!point || !touch) return;
      const dy = point.clientY - touch.y;
      if (Math.abs(dy) > Math.abs(point.clientX - touch.x) && Math.abs(dy) > 2)
        startIntent(dy > 0 ? "up" : "down");
      touch = { x: point.clientX, y: point.clientY };
    };
    const onKeyDown = (event: KeyboardEvent) => {
      const target = event.target instanceof Element ? event.target : null;
      if (event.defaultPrevented || target?.closest("input,textarea,select,[contenteditable='true']"))
        return;
      if (["ArrowUp", "PageUp", "Home"].includes(event.key) ||
        event.key === " " && event.shiftKey) startIntent("up");
      else if (["ArrowDown", "PageDown", "End"].includes(event.key) ||
        event.key === " ") startIntent("down");
    };
    const onPointerDown = () => {
      pointerDown = true;
      intent.current = null;
      programmaticTop.current = null;
    };
    const onPointerUp = () => {
      pointerDown = false;
    };
    const onSelection = () => {
      const selected = hasReadingSelection(el);
      if (selected && !selectionPaused.current) {
        selectionPaused.current = true;
        intent.current = null;
        pauseFollow();
      } else if (!selected && selectionPaused.current) {
        selectionPaused.current = false;
        // Ending a selection does not implicitly resume following.
        capturePosition();
        updateButton();
      }
    };
    const onScroll = () => {
      const top = el.scrollTop;
      if (programmaticTop.current !== null &&
        Math.abs(top - programmaticTop.current) < 1) {
        programmaticTop.current = null;
        previousTop.current = top;
        updateButton();
        return;
      }
      if (pointerDown && top < previousTop.current - 1) {
        intent.current = "up";
        pauseFollow();
      } else if (!selectionPaused.current &&
        (intent.current === "down" || pointerDown && top > previousTop.current) &&
        el.scrollHeight - el.clientHeight - top <= BOTTOM_TOLERANCE) {
        following.current = true;
        saved.current = null;
      }
      previousTop.current = top;
      if (!following.current) capturePosition();
      updateButton();
    };

    el.addEventListener("wheel", onWheel, { passive: true });
    el.addEventListener("touchstart", onTouchStart, { passive: true });
    el.addEventListener("touchmove", onTouchMove, { passive: true });
    el.addEventListener("keydown", onKeyDown);
    el.addEventListener("pointerdown", onPointerDown);
    el.addEventListener("scroll", onScroll, { passive: true });
    document.addEventListener("pointerup", onPointerUp);
    document.addEventListener("pointercancel", onPointerUp);
    document.addEventListener("selectionchange", onSelection);
    const observer = new ResizeObserver(schedule);
    observer.observe(el);
    if (content.current) observer.observe(content.current);
    previousTop.current = el.scrollTop;
    synchronize();
    return () => {
      observer.disconnect();
      cancelAnimationFrame(frame);
      el.style.setProperty("overflow-anchor", oldAnchoring);
      el.removeEventListener("wheel", onWheel);
      el.removeEventListener("touchstart", onTouchStart);
      el.removeEventListener("touchmove", onTouchMove);
      el.removeEventListener("keydown", onKeyDown);
      el.removeEventListener("pointerdown", onPointerDown);
      el.removeEventListener("scroll", onScroll);
      document.removeEventListener("pointerup", onPointerUp);
      document.removeEventListener("pointercancel", onPointerUp);
      document.removeEventListener("selectionchange", onSelection);
    };
  }, [active, capturePosition, pauseFollow, synchronize, updateButton]);

  // React commits cover streamed text/status changes. ResizeObserver additionally
  // covers width transitions, fonts, images, and code/table layout changes.
  useLayoutEffect(() => {
    synchronize();
  });

  return {
    scroller,
    content,
    showLatest,
    capturePosition,
    resetFollow,
    latest: resetFollow,
  };
}
