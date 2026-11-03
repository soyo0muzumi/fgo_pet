import { Volume2 } from "lucide-react";
import type { Message } from "./domain";
import "./speech-notice.css";

export function SpeechNotice({
  message,
  disabled = false,
  onStop,
}: {
  message: Message | undefined;
  disabled?: boolean;
  onStop: () => void;
}) {
  if (!message?.speechBusy) return null;

  // Busy can include preparation or playback. Keep the owner's wording instead
  // of inferring an audio phase from text generation or a busy flag.
  const status = message.speechStatus?.trim() ? message.speechStatus : "语音处理中";
  const action = message.speechAction?.trim() ? message.speechAction : "停止语音";

  return (
    <div className="speech-notice" role="group" aria-label="语音状态">
      <Volume2 className="speech-notice-icon" size={13} aria-hidden="true" />
      <span className="speech-notice-status" role="status" aria-live="polite">
        {status}
      </span>
      {message.canReadAloud && (
        <button
          type="button"
          className="speech-notice-action"
          disabled={disabled}
          onClick={onStop}
        >
          {action}
        </button>
      )}
    </div>
  );
}
