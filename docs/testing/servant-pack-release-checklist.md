# Role-package release checklist

This checklist is for a local candidate dry run. It does not authorize upload,
signing, or public release.

## Contract and source boundaries

- [ ] `package.json` uses pack schema v1, valid SemVer, a stable package/servant
      identity, an application version floor, and only known capabilities.
- [ ] Every non-`package.json` archive member is listed in `files` and uses a
      safe relative POSIX path.
- [ ] The project contains no raw source tree, absolute source path, prompt,
      log, script, executable, XAML, HTML, shader, or generated release folder.
- [ ] `art propose-layout` was reviewed; ambiguous sheets have an explicit
      human confirmation file.

## Art and QA

- [ ] Each appearance is art schema v3 with a visible body and expressions.
- [ ] Every declared hash, image, dimension, overlay bound, panel anchor, and
      fallback chain passes validation.
- [ ] All eight core expression semantics resolve to expression assets and the
      foreground does not touch crop edges.
- [ ] Preview contact sheet and semantic composites were inspected by a human.
- [ ] Real Mash art/persona/knowledge approval is recorded separately from
      synthetic fixture tests.

## Deterministic build

- [ ] Two builds from identical logical input have identical archive, checksum,
      QA report, and release-notes bytes.
- [ ] ZIP members are sorted, timestamped deterministically, and use only the
      production allowlist and size limits.
- [ ] The archive opens cleanly, extracts into a temporary directory, and its
      external SHA-256 matches the final archive.
- [ ] The output filename is `<package-id>-<package-version>.fgopetpack`.

## Local installation dry run

- [ ] `scripts/test-packaging.ps1` passes, including Core and Infrastructure
      pack tests and the archive allowlist scan.
- [ ] The candidate installs into a temporary Phase 1 package root without
      changing the current installed selection.
- [ ] Upgrade/rollback, signing, upload, and public distribution remain
      deferred until separately authorized.
