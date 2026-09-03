# Release-candidate acceptance checklist

Run the isolated gate against a verified Task 2 candidate. Record command output and evidence; this checklist is not public-release authorization.

- [ ] Clean extraction into generated temporary space; no files are written to the repository or user profile.
- [ ] Offline executable presence: `FgoPet.App.exe`, `FgoPet.AgentRelay.exe`, and `FgoPet.CodexAdapter.exe`.
- [ ] Existing adapter MCP smoke passes with isolated `FGO_PET_STATE_ROOT` and `FGO_PET_PIPE_SUFFIX`.
- [ ] Role packages remain separate from the App archive and output root.
- [ ] Upgrade simulation installs the same candidate twice without losing the isolated marker.
- [ ] Failed verification rejects a modified archive and cleans generated temporary space.
- [ ] Uninstall removes only installer-owned files and preserves state by default.
- [ ] Manual Windows GUI install evidence: pass/fail, OS/build, runtime version, timestamp, notes, screenshot/log path.
- [ ] Manual sleep/resume evidence: pass/fail, duration, timestamp, notes.
- [ ] Manual DPI evidence: pass/fail, scale factors, screenshot/log path, notes.
- [ ] Manual multi-monitor evidence: pass/fail, monitor arrangement/scales, screenshot/log path, notes.
- [ ] Manual long-running evidence: pass/fail, duration, memory/process notes, logs.

Evidence owner: ____  Candidate/archive hash: ____  Environment: ____  Date: ____  Decision: ____
