# Codex adapter guide

The first Phase 4 adapter lives at
`integrations/codex/fgo-pet-agent`. It is intentionally a local plugin boundary:

- `.codex-plugin/plugin.json` declares the plugin and its MCP server.
- `.mcp.json` starts `fgo-pet-codex-adapter mcp`.
- `hooks/hooks.json` maps deterministic session/task lifecycle hooks to
  `fgo-pet-codex-adapter hook <kind>`.
- `skills/fgo-pet-agent/SKILL.md` keeps completion and goal reporting behind
  explicit user confirmation.

Build the adapter from the repository root with:

```powershell
dotnet build src/FgoPet.CodexAdapter/FgoPet.CodexAdapter.csproj
```

The adapter process receives these environment variables from its launcher:

| Variable | Meaning |
| --- | --- |
| `FGO_PET_ADAPTER_PIPE` | local relay adapter pipe name |
| `FGO_PET_ADAPTER_CREDENTIAL` | paired relay credential |
| `FGO_PET_AGENT_INSTANCE` | opaque paired source instance |
| `FGO_PET_AGENT_TASK` | opaque current task id |
| `FGO_PET_AGENT_SEQUENCE` | positive event sequence supplied by the hook host |

The App Server client uses `thread/start` and `turn/start` through an injected JSON
RPC transport. The target passed to it is opaque; the adapter resolves it internally
and never sends a local path through the private protocol. The app only enables an
exact desktop-navigation capability when the runtime proves it. Otherwise opening a
task uses the safe in-app fallback and does not construct a guessed `codex://` URI.

## Validation checklist

Run the plugin manifest validator:

```powershell
python C:\Users\24139\.codex\skills\.system\plugin-creator\scripts\validate_plugin.py integrations/codex/fgo-pet-agent
```

Before enabling a real adapter, pair it in the app, verify the source allowlist,
test revoke, and confirm that private events contain no title or summary after the
relay boundary. The real Windows manual matrix is recorded in the Phase 4 handoff
report; the automated tests do not claim to replace that installation test.
