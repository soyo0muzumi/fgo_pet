# FGO Pet

**English** | [简体中文](README.zh-CN.md)

FGO Pet is a Windows 11 desktop companion based on Fate/Grand Order servants. It brings focus sessions, daily activity, Todo items, Codex/Agent progress, dialogue, and memory into a desktop pet and its attached panel.

The application uses WPF and .NET 8. Servant images, dialogue, persona prompts, and knowledge resources are not embedded in the program installer. They are distributed separately as data-only `.fgopetpack` role packages. The first supported servant is Mash Kyrielight.

## Release status

Phases 1–4 are implemented and accepted on the primary Windows environment: the desktop pet and role-package runtime, focus and timeline, dialogue and memory, Todo items, and Codex Agent integration are all on local `main`. Phase 5 covers backup and restore, guided configuration, the production role package, the GUI installer, and the first-release gate.

The repository does not yet provide a final installer for regular users. PowerShell, CLI, and `dotnet` commands in the source tree are for development, diagnostics, and acceptance only. They are not the end-user installation path.

The Phase 5 Release must provide:

- one Windows x64 per-user GUI installer;
- at least one reviewed, separately distributed `.fgopetpack`;
- install, upgrade, rollback, and uninstall flows;
- in-app role-package import, model configuration, and Agent configuration;
- no requirement for users to open a terminal, edit `PATH`, run scripts, or copy opaque project IDs.

The program installer and role package remain separate artifacts, but both are required for a usable first installation.

## Installation and first launch

The final Release user flow is:

1. Double-click the FGO Pet GUI installer and complete the installation wizard.
2. Start FGO Pet. First launch opens the role-package setup flow.
3. Select the `.fgopetpack` supplied on the release page and let FGO Pet validate and install it.
4. Select an appearance and set it as the current servant. The desktop-pet UI opens only after this step.
5. AI model and Agent setup may be skipped. Once a role package is active, the pet and focus features work offline.

The role-package step cannot be skipped. Without a role package, the current application can only show its package library/import UI and has no servant portrait to display. The Release onboarding flow must therefore remain on package setup instead of opening an empty desktop pet.

## Everyday use

Click the servant portrait to open or close the attached panel. The panel has four sections:

- **Focus (`专注`)** — start `25/5 × 4`, `50/10 × 2`, or custom Pomodoro sessions. Sessions can be paused, resumed, or stopped. After an unexpected exit, the latest session is restored in a paused state; offline time never advances it.
- **Today (`今日`)** — inspect today's focus time, events, and bond progress. Focus credit belongs to the servant active when the focus stage started and is capped at Lv.10.
- **Todo (`TODO`)** — create, confirm, and review tasks. An approved Agent target can receive a task, and its progress appears on the execution timeline and in history.
- **Dialogue (`对话`)** — use the active role package's persona and knowledge. A configured model enables messages; without a model, the panel points to settings while the pet and focus features keep working.

Clicking the portrait closes the panel, and `Esc` steps back through the current UI level. The system-tray menu can show the pet again, open settings, or exit the application.

## Settings and feature configuration

Open **Settings (`设置`)** from the system tray or the portrait menu. All regular-user configuration is performed through the GUI.

### User profile

Use **Settings → User Profile (`用户资料`)** to set an optional global display name. This field belongs to the application profile and does not automatically become the name used by a servant when addressing the user.

Each servant's form of address is configured separately in its role package and is stored by stable `servant_id`.

### Personalization

Use **Settings → Personalization (`个性化`)** to configure:

- portrait scale: 50%, 60%, or 75%;
- always-on-top behavior;
- automatic collapse of an inactive expanded panel;
- restoration of the default personalization values.

Window placement and valid settings are persisted automatically. Restoring personalization defaults does not change the selected theme.

### Role packages

A role package is required to display the desktop pet. In **Settings → Role Packages (`角色包`)**:

1. select **Choose file (`选择文件`)** and choose a `.fgopetpack`;
2. select **Install (`安装`)**;
3. open the installed package;
4. under **Servant and appearance (`从者与外观`)**, choose an appearance and select **Set as current servant (`设为当前从者`)**.

The package detail page also provides:

- **Form of address (`称呼设置`)** — use the package default or enter a custom name;
- **Package information (`角色包信息`)** — inspect version, compatibility, and source;
- **Package-declared settings (`角色包声明设置`)** — edit only the switches, choices, and text fields allowed by the application contract;
- **Uninstall this version (`卸载此版本`)** — remove the selected version; uninstall and failed upgrades must preserve a recovery path for the active version.

FGO Pet never executes code from a role package. The installer validates the manifest, compatibility, paths, and file hashes.

### AI model and connection

Model configuration is optional and affects only features such as dialogue. In **Settings → AI Model and Connection (`AI 模型与连接`)**:

1. select a provider;
2. enter the API key;
3. review or enter the Base URL;
4. refresh the available-model list and choose a model, or enter the model ID directly;
5. select **Test connection (`测试连接`)**;
6. after a successful test, select **Save connection (`保存连接`)**.

API keys are stored only in Windows Credential Manager. They are not written to `settings.json`, exports, or role packages. Select **Skip and use offline (`跳过，离线使用`)** to leave model setup for later.

### Agent connection

Agent integration is optional. The Phase 5 Release includes the Relay, Adapter, and Codex plugin payload in the GUI installer. Installation, repair, and registration must be handled by the installer or FGO Pet settings; regular users do not run commands.

In **Settings → Agent Connection (`Agent 连接`)**:

1. enable **Agent integration (`启用 Agent 集成`)**;
2. start Codex and inspect the name, instance, and version under **Pending sources (`待批准来源`)**;
3. select **Approve (`批准`)**;
4. select allowed project targets through the GUI;
5. enable the source and save its permissions;
6. select **Test connection (`测试连接`)** and confirm that Relay, App, and Adapter are online.

Permissions are deny-by-default. Tasks cannot be dispatched until both the source and target are explicitly approved. Revocation takes effect immediately and remains revoked after restart. The final Release must not ask users to copy project IDs, modify `PATH`, or run commands such as `target add`; commands currently present in the repository are development and acceptance tools only.

### Dialogue and memory

In **Settings → Dialogue and Memory (`对话与记忆`)** you can:

- enable or disable memory;
- inspect pending memory candidates;
- edit, approve, or reject a candidate;
- edit, disable, or delete an approved memory.

Candidates do not enter later conversations automatically. Only memories explicitly approved by the user are stored. Memory belongs to the servant, so switching appearances for the same servant does not move it.

### Data and privacy

The current **Settings → Data and Privacy (`数据与隐私`)** page provides a safe sharing export, conversation deletion, and local user-data controls. The safe export contains allowed dialogue, summaries, memories, and bounded metadata. It excludes API keys, full prompts, raw story data, and role-package assets.

The safe export is not a complete backup and cannot be restored. Phase 5.2 adds a separate private backup and restore flow covering focus, timeline, bond, dialogue, memory, Todo, work archives, and settings while excluding model credentials and Agent pairing secrets.

Deletion actions have different scopes; review the confirmation text before continuing. Uninstall preserves user data by default, and a complete data removal requires separate confirmation.

### Theme

Use **Settings → Theme (`主题`)** to choose:

- **Modern Gray** — neutral gray surfaces with restrained Windows-style blue accents;
- **FGO Light** — navy navigation with soft gold accents.

Themes currently affect only the settings window. The desktop pet and dialogue panel retain the existing dark terminal-inspired appearance.

## Data and privacy boundaries

- Runtime data is stored under the current Windows user's local application-data directory.
- API keys are stored in Windows Credential Manager.
- Relay and Adapter pairing credentials use protected machine-local state and are excluded from user backups.
- Role packages contain data and assets only; executable code is rejected.
- The Agent protocol rejects prompts, reasoning, tool calls, terminal output, credentials, and file paths.
- Memory candidates, Todo dispatches, and work archives require explicit user confirmation.

## Current phase status

- **Phase 1** — desktop pet, role-package installation, portrait rendering, tray, and attached panel are implemented.
- **Phase 2** — focus, recovery, today's timeline, and bond progression are accepted in the primary Release environment.
- **Phase 3** — model setup, dialogue, memory, settings, and privacy controls are accepted.
- **Phase 4** — Todo, Agent Relay/Adapter, Codex integration, restart recovery, and revocation are accepted and merged into local `main`.
- **Phase 5** — the approved direction covers task-operation safety, backup and restore, GUI configuration, the production role package, and Release packaging; feature implementation has not started.

See the [Phase 4 closeout](docs/reports/2026-09-01-phase4-closeout.md) and the [Phase 5 productization design](docs/superpowers/specs/2026-09-01-phase5-productization-design.md).

## Developer build and test

The following commands are for source development and verification only. They are not end-user installation steps.

Requirements: Windows and .NET SDK 8.0.x.

```powershell
dotnet build FgoPet.sln -c Release -warnaserror
dotnet test FgoPet.sln -c Release
pwsh -File scripts/test-phase1.ps1
pwsh -File scripts/test-phase2.ps1
pwsh -File scripts/test-phase3-settings.ps1
pwsh -File scripts/test-phase4.ps1
```

Developers may use the packless smoke test to verify the application shell. This does not mean that end users may skip the required role-package step:

```powershell
dotnet run --project src/FgoPet.App/FgoPet.App.csproj -c Release -- --smoke-test
```

FGO artwork and extracted Atlas assets are not stored in this repository.
