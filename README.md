# FGO Pet

**English** | [简体中文](README.zh-CN.md)

FGO Pet is a Windows 11 desktop companion inspired by Fate/Grand Order servants. It combines a desktop pet, focus sessions, daily activity, Todo items, AI dialogue, memory, Agent tasks, and optional speech in a local-first desktop experience.

The application uses WPF and .NET 8. Servant artwork, persona data, and knowledge are installed separately through data-only `.fgopetpack` packages. Model and Agent integration are optional; the pet and focus features remain available offline when they are not configured.

## Current release

**v0.1.2 is available for external testing as a Windows x64 ZIP.** It is a test build, not a signed or auto-updating public release.

Requirements:

- Windows 11 x64;
- [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0);
- at least one compatible `.fgopetpack` role package.

See the [v0.1.2 external testing notes](docs/release/0.1.2-test-notes.md) for scope, limitations, and the feedback template. See the [roadmap](docs/roadmap.md) for current and next-version priorities.

## Feature overview

- **Desktop companion and windows** — the pet stays on the desktop; chat and settings are independent taskbar windows, and the task panel adapts to available width.
- **Focus and Today** — preset or custom Pomodoro sessions, pause and recovery, a daily timeline, and servant bond progress.
- **Dialogue and memory** — multi-turn conversations, history recovery, role persona and knowledge, with user-reviewed memory candidates.
- **Todo and Agent** — tasks require confirmation before dispatch; Agent sources and project access are deny-by-default, while stop and unknown outcomes require source confirmation or explicit review.
- **Speech** — optional synthesis, auto-read, manual playback, stop, and retry; speech failures do not block other features.
- **Data and privacy** — safe sharing exports, private backup and restore, conversation deletion, and local data controls.

## ZIP quick start

1. Compare the ZIP SHA-256 with the supplied `SHA256SUMS` entry.
2. Extract the complete ZIP into a normal user-writable directory. Do not run it from inside the archive.
3. Start `FgoPet.App.exe`.
4. On first launch, import a `.fgopetpack`, choose an appearance, and set it as the active servant.
5. Configure an AI model, Agent, or speech provider only if needed; each setup can be skipped.

Role packages cannot execute code. FGO Pet validates their manifest, compatibility, paths, and file hashes. The application ZIP and `.fgopetpack` files are separate artifacts; do not embed a role package inside the application archive.

## Everyday use

- Select the pet controls to open chat and common actions.
- The workspace sidebar provides chat, Todo, focus, and settings; chat history and new conversations are available in the chat header.
- History shows conversation titles and local times, 50 conversations per page; use Load more for older conversations. Select a title to continue chatting, or confirm deletion of one conversation. Deletion is unavailable while a reply is running and preserves approved memories. Data settings provide export, backup and restore, and bulk cleanup.
- Settings groups pages into Appearance and Roles, Capabilities, and General and Data. Capabilities contains model services, speech, and Agent connections. Returning to a page restores its own scroll position.
- The system tray can show the pet, open settings, or exit the application.
- `Esc` closes the current overlay or returns one level; primary controls expose keyboard focus and visible state feedback.

### AI model

Under **Settings → Capabilities → Model Services**, enter the provider, Base URL, model, and API key. Connection testing does not save the draft; only an explicit save activates the new configuration. API keys are stored in Windows Credential Manager, not settings files or exports.

The following context and memory changes are on the development branch, with real-provider and native Windows acceptance still pending.

Context capacity comes from a manual override, provider metadata, or known model information, in that order. An unknown model uses a clearly labelled conservative estimate. Requests reserve space for the selected output limit and count the complete prompt and tool definitions; estimates are not exact tokenizer counts.

New conversations can retrieve earlier messages from the same servant and project, with links to their source conversations. History defaults to the current project; turn off that filter to open older conversations from other projects. Unassigned history is separate from project history. Ambiguous references prompt a clarification.

Long conversations compact older complete exchanges while retaining recent raw messages and the current Todo draft. Compaction preserves the original history and survives restart and private backup restore. It also works when long-term memory is disabled. If compaction cannot safely fit the request, the input is retained and the app reports the problem.

Long-term memory candidates show their evidence and scope and require approval. General servant preferences can be recalled across projects; project memories stay within that project. Corrections select an exact memory and version, and edits, disabling and deletion take effect on subsequent requests. Deleting a source conversation preserves approved memories and marks their source unavailable. Storage permits 200 approved items / 40,000 characters per servant, including disabled items; existing data is never evicted to make room. Budgeting, compaction and memory mechanisms draw on the pinned MIT sources in [Third-party notices](THIRD-PARTY-NOTICES.md).

### Agent

Under **Settings → Capabilities → Agent Connection**, detect and approve a real source, select allowed projects, and explicitly confirm the permission scope. Unapproved sources or projects cannot receive tasks, and revocation takes effect immediately. A stop request is never presented as a confirmed stop, and an outcome-unknown dispatch is never retried automatically.

### Speech

Under **Settings → Capabilities → Speech**, select and test a speech provider. Playback supports stop, retry, and long-text splitting, with temporary audio cleaned up by the playback lifecycle.

## Security and privacy boundaries

- API keys are stored in Windows Credential Manager.
- Relay and Adapter pairing credentials use protected local state and are excluded from user backups.
- The Agent protocol excludes full prompts, reasoning, tool arguments, terminal output, credentials, and unnecessary local paths.
- Todo dispatch, memory approval, project authorization, and irreversible data operations require explicit user actions.
- A safe sharing export is not a complete backup; private backups exclude credentials, role-package assets, and Agent pairing state.
- Private restore validates and stages a copy, then closes the app. Reopen it to restore before normal services start. A failed restore rolls back; an uncertain rollback stops startup and preserves recovery materials.
- FGO artwork, Atlas files, audio, and extracted assets are not stored in this repository.

## Developer entry points

Requirements: Windows, .NET SDK 8.0.x, and PowerShell 7.

```powershell
dotnet build FgoPet.sln -c Release -warnaserror
dotnet test FgoPet.sln -c Release
```

Further documentation:

- [Developer guide](docs/guides/development.md) — complete build, test, repository, and ZIP candidate workflow;
- [Module map](modules/README.md) — ownership, dependency direction, and migration status;
- [Agent integration guide](docs/guides/agent-integration.md);
- [Codex Adapter guide](docs/guides/codex-adapter.md);
- [Release candidate workflow](docs/release/README.md);
- [Documentation index](docs/README.md).

Release scripts create local candidates only. They do not grant authorization to push, tag, upload, or create a public Release.
