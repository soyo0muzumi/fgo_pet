# Built-in Skills

This plugin provides six static instruction packs through the native `ISkillProvider` contract: `daily-planning`, `todo-maintenance`, `task-capture`, `focus-session`, `document-review`, and `daily-report`. Each descriptor has a fixed version and a SHA-256 digest computed from its UTF-8 instruction body. The bodies are embedded in the plugin; loading a skill does not read external files, execute code, or contact a service.

The skill instructions explain how to use existing capabilities. They do not perform tool calls, grant permission, or replace the capability owner's validation. `SkillRegistry` requires each declared required tool to be active before exposing or loading that skill. Optional tools can be absent; the body tells the agent to describe the resulting limits. `daily-report` intentionally has no required tools so it can draft a report from user-provided information when Todo, Memory, or Workspace tools are unavailable.

Todo changes and Focus commands remain under their existing owners and require applicable policy approval and business confirmation. Document review must cite extracted source evidence and disclose truncation or unread portions. Reports must distinguish drafts from confirmed tool results, and must not claim a task changed, a session ran, or a document was saved until the owning tool confirms that outcome.

The host registers this plugin through the standard `IFgoPetPlugin` catalog. Tests for the descriptors, content hashes, required-tool gate, and optional-tool behavior live in `tests/FgoPet.Capability.Tests/Skills`.
