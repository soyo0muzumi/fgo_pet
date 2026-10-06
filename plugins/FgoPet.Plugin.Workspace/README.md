# Workspace Plugin

`WorkspacePlugin` contributes bounded `workspace.read`, `workspace.list`, `workspace.glob`, `workspace.grep`, `workspace.write`, and `workspace.edit` tools to the native plugin catalog. It receives the host's shared `IWorkspaceAccessGuard` and gives that same guard to both the read and write providers; it does not create another authorization source or workspace authority. `PluginRuntime` controls whether the catalogued tools are active.

The host guard supplies the resource authorization. Its frozen fingerprint covers scope, canonical root and current-directory paths, and their physical directory identities. The guard rejects unsafe and reparse paths, pins parent directories while a lease is held, and revalidates file identity around access. Read and scan operations impose file, entry, depth, time, and output limits. Text writes use a same-directory staged replacement; `workspace.edit` changes exactly one matching fragment, while `workspace.write` requires an explicit create-or-replace choice.

`expectedVersion` is cooperative optimistic concurrency. The final replacement is not a cross-process compare-and-swap: another writer can change a file after the last version check and before replacement.

`WorkspaceAuthorizationRegistry` accepts explicit in-process host grants for a current conversation/role/project only. Revocation or replacement changes the authority identity; no model argument or package can grant a root. Host composition registers an empty registry until a trusted grant is supplied, so workspace, documents and shell access fail closed without one. The registry is bounded to 64 scopes and does not evict existing authority when full.

When paired with `shell.exec`, the workspace constrains the shell's working directory only. Shell commands still run with host-user permissions and may access other paths, use the network, or start child processes; the workspace guard is not a process sandbox.

Build the plugin and run its focused capability tests from the repository root:

```powershell
dotnet build plugins/FgoPet.Plugin.Workspace/FgoPet.Plugin.Workspace.csproj -c Release -warnaserror
dotnet test tests/FgoPet.Capability.Tests/FgoPet.Capability.Tests.csproj -c Release --filter FullyQualifiedName~FgoPet.Capability.Tests.Workspace
```
