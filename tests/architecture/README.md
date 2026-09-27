# Architecture boundaries

`policy.json` describes intended project classifications and allowed dependency directions. It is authored independently of the existing dependency graph.

`baseline.json` records temporary dependency exceptions. Entries may be removed as ownership moves; additions fail the gate. `baseline-origin.json` pins the original baseline, and CI also compares the mutable baseline with repository history to prevent removed exceptions from returning.

The gate evaluates effective `Compile` and `ProjectReference` items in Debug and Release, both with the default runtime and `win-x64`. It includes imported and conditional items. Missing or unclassified projects, misclassified test projects, new debt, and references outside the repository fail. Test projects are classified explicitly; rendering probes under `tools/spikes` are outside the production policy.

`src/` contains the application bootstrap, pure Kernel, Desktop shell/data-management implementations, Platform contracts/storage/Windows implementations, and UiSdk. `plugins/` contains capability implementations (including Content/Desktop, Dialogue, Focus, Memory, Speech, and Todo) and provider integrations. Cross-module behavior and architecture checks live under `tests/`; maintenance scripts and auxiliary tools live under `tools/`. The former Core, Infrastructure, UiFoundation, and Archives aggregate projects are retired, and the legacy Compile baseline is empty. Four temporary project-reference exceptions remain: Desktop Shell to Content and Dialogue, and DataManagement to Dialogue and Memory. They are recorded in `baseline.json` and must only shrink. This does not claim full V3 migration or device acceptance.

Run `pwsh -File tools/scripts/test-architecture.ps1` for the architecture gate. The repository evaluation checks the shrinking baseline against evaluated MSBuild project references. Other required build, test, and Phase 4 gates remain in force for their affected changes.
