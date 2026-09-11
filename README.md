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
- The chat window provides dialogue, history, Todo, focus, and current Agent-task entry points.
- The settings window manages models, Agent access, speech, roles and appearances, personalization, themes, and data/privacy.
- The system tray can show the pet, open settings, or exit the application.
- `Esc` closes the current overlay or returns one level; primary controls expose keyboard focus and visible state feedback.

### AI model

Under **Settings → AI Model and Connection**, enter the provider, Base URL, model, and API key. Connection testing does not save the draft; only an explicit save activates the new configuration. API keys are stored in Windows Credential Manager, not settings files or exports.

### Agent

Under **Settings → Agent Connection**, detect and approve a real source, select allowed projects, and explicitly confirm the permission scope. Unapproved sources or projects cannot receive tasks, and revocation takes effect immediately. A stop request is never presented as a confirmed stop, and an outcome-unknown dispatch is never retried automatically.

### Speech

Under **Settings → Speech**, select and test a speech provider. Playback supports stop, retry, and long-text splitting, with temporary audio cleaned up by the playback lifecycle.

## Security and privacy boundaries

- API keys are stored in Windows Credential Manager.
- Relay and Adapter pairing credentials use protected local state and are excluded from user backups.
- The Agent protocol excludes full prompts, reasoning, tool arguments, terminal output, credentials, and unnecessary local paths.
- Todo dispatch, memory approval, project authorization, and irreversible data operations require explicit user actions.
- A safe sharing export is not a complete backup; private backups exclude credentials, role-package assets, and Agent pairing state.
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
