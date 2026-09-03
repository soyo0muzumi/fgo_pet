# Release candidate preparation

This workflow prepares and locally accepts a Windows x64 candidate. It does not authorize a public release, signing, upload, remote publication, or automatic update.

```powershell
pwsh -NoProfile -File scripts/publish-release.ps1 -OutputRoot C:\fgo-pet-candidates
pwsh -NoProfile -File scripts/verify-release.ps1 -CandidateRoot C:\fgo-pet-candidates\<candidate>
pwsh -NoProfile -File scripts/test-release-candidate.ps1 -CandidateRoot C:\fgo-pet-candidates\<candidate> -TempRoot C:\fgo-pet-acceptance
```

Expected candidate artifacts are `manifest.json`, `SHA256SUMS`, `app\FgoPet-win-x64-<version>.zip`, and a separate role-package output when role resources are built. The App archive is framework-dependent `win-x64`, targets `net8.0-windows`, and requires the Windows x64 .NET 8 Desktop Runtime; it is not self-contained.

Acceptance uses the existing release verifier and adapter install/uninstall smoke boundary. It creates generated temporary extraction, install, Codex-home, and state directories, and preserves caller PATH, Codex home, pairing state, and business data.

For every manual evidence item record: owner, candidate version/hash, Windows build, .NET Desktop Runtime version, hardware/display configuration, timestamp, pass/fail, notes, and screenshot or log path. Required manual coverage is GUI install, sleep/resume, DPI, multi-monitor, and long-running operation. Missing manual evidence means the candidate is not publicly release-authorized.
