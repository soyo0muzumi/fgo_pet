# Character Design Readiness Integration Plan

> **For Codex:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development for each task, superpowers:verification-before-completion before claiming completion, and superpowers:simplify-and-harden for the final bounded review.

**Goal:** Enforce one machine-checkable gate and integrate the approved knowledge/art standard into Mash's character design only after code tests, retrieval scenarios, automated art checks, and visual QA pass.

**Architecture:** A readiness validator consumes manifests and reports rather than copyrighted source content. It emits deterministic PASS/BLOCKED evidence for profile, retrieval, response policy, persona runtime, and art. Character design documents are updated only when a PASS report matches current artifact hashes.

**Tech Stack:** Python 3.11, Pydantic 2, pytest, Markdown

**Prerequisites:** Complete `2026-08-26-profile-and-story-retrieval.md` and `2026-08-26-casual-art-processing.md` first.

---

## Task 1: Define and test the readiness contract

**Files:**
- Create: `src/fgo_pet_content/readiness.py`
- Create: `tests/test_readiness.py`

**Step 1: Write failing gate tests**

Test each condition independently:

- Profile is non-empty, bounded, and every fact has provenance.
- Story FTS schema is current and contains scenes.
- Eleven scenarios ran; plot hits use 2–4 windows and at most 900 tokens.
- Persona bundle contains only approved evidence.
- Art has one full body plus 28 raw and 28 runtime expressions.
- Automated art QA has zero errors and visual QA is explicitly `approved`.
- Reported source/artifact hashes match current files.

```python
def test_visual_qa_blocks_integration(valid_inputs):
    valid_inputs.art_report["visual_qa"] = "pending"
    result = evaluate_readiness(valid_inputs)
    assert result.status == "BLOCKED"
    assert "art.visual_qa" in result.failed_checks

def test_all_checks_produce_pass(valid_inputs):
    result = evaluate_readiness(valid_inputs)
    assert result.status == "PASS"
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/test_readiness.py -q`
Expected: import failure.

**Step 3: Implement versioned models and fail-closed evaluation**

```python
class ReadinessResult(BaseModel):
    schema_version: Literal[1] = 1
    character_id: Literal[800100] = 800100
    status: Literal["PASS", "BLOCKED"]
    checks: list[ReadinessCheck]
    artifact_hashes: dict[str, str]
```

Every check contains `id`, `status`, `evidence_path`, and concise detail. Missing, stale, malformed, or pending evidence yields BLOCKED; warnings never imply a pass.

**Step 4: Verify and commit**

```powershell
D:\environments\anaconda\python.exe -m pytest tests/test_readiness.py -q
git add src/fgo_pet_content/readiness.py tests/test_readiness.py
git commit -m "feat: enforce character readiness gate"
```

Expected: PASS.

## Task 2: Add CLI report generation and stale-evidence protection

**Files:**
- Modify: `src/fgo_pet_content/cli.py`
- Create: `tests/test_readiness_cli.py`
- Modify: `README.md`

**Step 1: Write failing CLI tests**

Test `readiness check-mash` with valid and invalid roots. Assert exit code 0 only for PASS, exit code 1 for BLOCKED, atomic report writing, and detection when an artifact changes after review.

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/test_readiness_cli.py -q`
Expected: FAIL because the command is absent.

**Step 3: Implement the command**

```text
fgo-content readiness check-mash \
  --data-root D:\fgo_unpack\fgo_assets \
  --report docs/reports/2026-08-26-mash-phase0-readiness.json
```

Resolve knowledge/persona/art manifests, recompute hashes, read subsystem readiness reports, evaluate checks, and atomically write JSON. Stdout contains only status and failed check IDs.

**Step 4: Verify and commit**

```powershell
D:\environments\anaconda\python.exe -m pytest tests/test_readiness_cli.py -q
D:\environments\anaconda\python.exe -m pytest -q
git add src/fgo_pet_content/cli.py tests/test_readiness_cli.py README.md
git commit -m "feat: report Mash Phase 0 readiness"
```

Expected: PASS.

## Task 3: Run the complete acceptance gate

**Files:**
- Create: `docs/reports/2026-08-26-mash-phase0-readiness.json`

**Step 1: Run all tests**

Run: `D:\environments\anaconda\python.exe -m pytest -q`
Expected: PASS for baseline, knowledge, art, and readiness tests.

**Step 2: Re-run knowledge and art acceptance**

Run all eleven fixed scenarios against current profile/index. Relevant evidence must avoid “不清楚”; contexts remain within 2–4 windows and 900 tokens; uncovered details are coverage gaps.

Run:

```powershell
D:\environments\anaconda\python.exe -m fgo_pet_content.cli art validate --bundle D:\fgo_unpack\fgo_assets\pet\mash\casual
```

Expected: zero errors and `visual_qa: approved` in the art report.

**Step 3: Generate the gate report**

```powershell
D:\environments\anaconda\python.exe -m fgo_pet_content.cli readiness check-mash --data-root D:\fgo_unpack\fgo_assets --report docs/reports/2026-08-26-mash-phase0-readiness.json
```

Expected: exit 0 and `status: PASS`. If BLOCKED, stop here, fix the named subsystem with a failing regression test, then repeat Tasks 3.1–3.3. Do not edit character design while blocked.

**Step 4: Commit passing evidence**

```powershell
git add docs/reports/2026-08-26-mash-phase0-readiness.json
git commit -m "test: record passing Mash readiness gate"
```

## Task 4: Integrate the verified standard into character design

**Files:**
- Modify: `docs/superpowers/specs/2026-08-25-fgo-pet-design.md`
- Modify: `docs/superpowers/specs/2026-08-25-fgo-pet-content-pipeline-design.md`
- Modify: `docs/superpowers/specs/2026-08-26-knowledge-and-art-readiness-design.md`
- Create: `tests/test_design_readiness_docs.py`

**Step 1: Write a failing documentation contract test**

Read the design docs and PASS report. Require default outfit, stable expression IDs, profile/FTS/context layers, CN-first/JP-fallback, 2–4-sentence answers, 2–4-scene/900-token context, expansion-on-request, and readiness evidence path. Phase 0 may be unblocked only when the report says PASS.

```python
def test_character_design_references_passing_readiness_report():
    report = json.loads(READINESS_REPORT.read_text(encoding="utf-8"))
    design = DESIGN.read_text(encoding="utf-8")
    assert report["status"] == "PASS"
    assert "mash_casual_98001000" in design
    assert "r01c01–r07c04" in design
    assert "2026-08-26-mash-phase0-readiness.json" in design
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/test_design_readiness_docs.py -q`
Expected: FAIL because the old design lacks the verified standard.

**Step 3: Update the designs**

Make these rules normative:

- Compact profile is always available; story context is query-dependent.
- Plot questions use local FTS and optional fail-open reranking.
- Default response is 2–4 sentences, expanding on request.
- Unsupported details are source coverage limits, not character ignorance.
- Official CN is preferred; JP fallback retains original and provenance.
- `mash_casual_98001000` is the default outfit.
- Full body and all 28 expressions exist; runtime IDs are `r01c01`–`r07c04`, while semantic labels may evolve.
- Raw art is retained; runtime art and anchors come from the validated manifest.
- The readiness JSON is the evidence for lifting the Phase 0 block.

Align the pipeline design with the same interfaces. Mark the readiness spec VERIFIED and reference hashes instead of embedding external content.

**Step 4: Verify and commit**

```powershell
D:\environments\anaconda\python.exe -m pytest tests/test_design_readiness_docs.py -q
D:\environments\anaconda\python.exe -m pytest -q
git diff --check
git add docs/superpowers/specs tests/test_design_readiness_docs.py
git commit -m "docs: integrate verified Mash character standard"
```

Expected: all tests pass and no whitespace errors.

## Task 5: Final bounded quality review

**Files:**
- Review: all files changed by the three plans

**Step 1: Run simplify-and-harden**

Check duplicate models, unsafe FTS query construction, path traversal, stale hashes, copyrighted text accidentally tracked in Git, image edge damage, and disagreements between code/documented limits.

**Step 2: Apply evidence-backed corrections**

For each correction, add or update a failing regression test first, make the smallest change, then rerun its focused test.

**Step 3: Run final verification**

```powershell
D:\environments\anaconda\python.exe -m pytest -q
git status --short
git diff --check
```

Expected: all tests pass, no unintended generated/copyrighted assets are tracked, and no whitespace errors.

**Step 4: Re-run readiness and commit review fixes**

Re-run `readiness check-mash`; it must remain PASS with current hashes. Use `git diff --name-only` to identify the exact review corrections, stage those paths individually, and commit them as `refactor: harden Mash readiness pipeline`. If review produced no correction, do not create an empty commit.
