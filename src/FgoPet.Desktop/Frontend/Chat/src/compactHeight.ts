export const MIN_COMPACT_HEIGHT = 160;
export const MAX_COMPACT_HEIGHT = 360;

export function compactHeight(value: number) {
  return Math.max(MIN_COMPACT_HEIGHT, Math.min(MAX_COMPACT_HEIGHT,
    Number.isFinite(value) ? Math.ceil(value) : MIN_COMPACT_HEIGHT));
}

/** A bounded, single-flight throttle. New measurements replace the pending
 * value; they never reset the timer or build a queue of stale bridge calls. */
export function createCompactHeightReporter(
  send: (height: number) => Promise<unknown>,
  onError: (error: unknown) => void,
  interval = 60,
) {
  let disposed = false;
  let inFlight = false;
  let pending: number | undefined;
  let attempted: number | undefined;
  let lastStarted = Number.NEGATIVE_INFINITY;
  let timer: ReturnType<typeof setTimeout> | undefined;

  function schedule() {
    if (disposed || inFlight || timer !== undefined || pending === undefined) return;
    const delay = Math.max(0, interval - (Date.now() - lastStarted));
    if (!delay) flush();
    else timer = setTimeout(() => { timer = undefined; flush(); }, delay);
  }

  function flush() {
    if (disposed || inFlight || pending === undefined) return;
    const height = pending;
    pending = undefined;
    if (attempted !== undefined && Math.abs(attempted - height) < 2) return;
    attempted = height;
    lastStarted = Date.now();
    inFlight = true;
    // Keep the bridge call in this turn so disposal cannot leave a not-yet-sent
    // microtask behind an explicit Expand/Collapse action.
    let task: Promise<unknown>;
    try { task = send(height); }
    catch (error) { task = Promise.reject(error); }
    let failed = false;
    void task.catch(error => {
      failed = true;
      attempted = undefined;
      // A rejected host request must not permanently suppress this size. Drop
      // duplicate measurements made during that failed call, then let a later
      // real measurement retry; do not create an automatic retry loop.
      if (pending !== undefined && Math.abs(pending - height) < 2) pending = undefined;
      if (!disposed) onError(error);
    }).finally(() => {
      inFlight = false;
      if (disposed) return;
      if (!failed && pending !== undefined && Math.abs(pending - height) < 2) pending = undefined;
      schedule();
    });
  }

  return {
    measure(value: number) {
      if (disposed) return;
      pending = compactHeight(value);
      schedule();
    },
    dispose() {
      disposed = true;
      pending = undefined;
      if (timer !== undefined) clearTimeout(timer);
      timer = undefined;
    },
  };
}

function vertical(style: CSSStyleDeclaration, ...names: string[]) {
  return names.reduce((sum, name) => sum + (Number.parseFloat(style.getPropertyValue(name)) || 0), 0);
}

/** Measure the turn's natural height, independently of the current native
 * viewport. The visible message scroller can therefore be capped to 100dvh
 * without trapping the next size request at the old window height. */
export function measureCompactContent(surface: HTMLElement) {
  const surfaceStyle = getComputedStyle(surface);
  let height = vertical(surfaceStyle, "padding-top", "padding-bottom", "border-top-width", "border-bottom-width");
  for (const child of Array.from(surface.children)) {
    if (!(child instanceof HTMLElement)) continue;
    const style = getComputedStyle(child);
    if (style.display === "none" || style.position === "absolute" || style.position === "fixed") continue;
    let childHeight = child.offsetHeight;
    if (child.classList.contains("compact-conversation")) {
      const heading = child.querySelector<HTMLElement>(".compact-reading-heading");
      const scroll = child.querySelector<HTMLElement>(".message-scroll");
      const messages = child.querySelector<HTMLElement>(".messages");
      if (scroll && messages) {
        childHeight = (heading?.offsetHeight || 0) + messages.scrollHeight +
          vertical(getComputedStyle(scroll), "padding-top", "padding-bottom", "border-top-width", "border-bottom-width") +
          vertical(style, "padding-top", "padding-bottom", "border-top-width", "border-bottom-width");
      }
    }
    height += childHeight + vertical(style, "margin-top", "margin-bottom");
  }
  return compactHeight(height);
}
