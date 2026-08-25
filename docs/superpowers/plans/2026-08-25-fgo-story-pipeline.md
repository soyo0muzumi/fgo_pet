# FGO Story Pipeline Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a tested Python pipeline that discovers, fetches, parses, filters, and structures CN-first/JP-fallback Mash story scripts, then produces reviewable evidence cards without placing full story text in Git.

**Architecture:** A CLI orchestrates small modules for Atlas access, cache safety, source resolution, stateful script parsing, Mash scene discovery, LLM evidence extraction, and persona compilation. Raw text and lossless parsed documents remain in an external data root; only schemas, compact fixtures, reviewed abstractions, and reports are eligible for the repository.

**Tech Stack:** Python 3.11+, Pydantic 2, HTTPX, Typer, pytest, respx, JSON/JSONL

**Spec:** `docs/superpowers/specs/2026-08-25-fgo-pet-content-pipeline-design.md`

## Global Constraints

- CN official text has priority over JP official text; machine translation is never labeled official.
- Full CN/JP story text must remain under an explicit external data root and must not enter Git history.
- Every evidence claim must cite a script ID, scene index, and utterance order.
- The parser must preserve unknown commands and continue parsing recognized dialogue.
- Master tables may contain JSON while having no `.json` extension.
- Source resolution must cover War, Quest, Event, and Interlude-like containers instead of relying only on Quest links.
- LLM output is a candidate until schema validation, evidence-bound checks, and human review succeed.
- Do not modify files under the raw asset/data roots.

---

### Task 1: Python project and safe path configuration

**Files:**
- Create: `pyproject.toml`
- Create: `src/fgo_pet_content/__init__.py`
- Create: `src/fgo_pet_content/config.py`
- Create: `src/fgo_pet_content/cli.py`
- Create: `tests/test_config.py`
- Modify: `.gitignore`

**Interfaces:**
- Consumes: `FGO_CONTENT_DATA_ROOT` or CLI `--data-root`.
- Produces: `ContentPaths.from_root(root: Path, repo_root: Path) -> ContentPaths`; Typer app `fgo-content`.

- [ ] **Step 1: Write failing path-safety tests**

```python
from pathlib import Path
import pytest
from fgo_pet_content.config import ContentPaths

def test_external_data_root_cannot_be_inside_repo(tmp_path: Path):
    repo = tmp_path / "repo"
    repo.mkdir()
    with pytest.raises(ValueError, match="outside the repository"):
        ContentPaths.from_root(repo / "story_cache", repo)

def test_content_paths_create_expected_external_layout(tmp_path: Path):
    repo = tmp_path / "repo"
    data = tmp_path / "fgo_assets"
    repo.mkdir()
    paths = ContentPaths.from_root(data, repo)
    assert paths.raw_scripts == data / "story_cache" / "raw"
    assert paths.parsed_scripts == data / "story_cache" / "parsed"
```

- [ ] **Step 2: Run the tests and verify the missing package failure**

Run: `python -m pytest tests/test_config.py -v`

Expected: FAIL with `ModuleNotFoundError: No module named 'fgo_pet_content'`.

- [ ] **Step 3: Add project metadata, path model, CLI entry point, and ignore rules**

```toml
[project]
name = "fgo-pet-content"
version = "0.1.0"
requires-python = ">=3.11"
dependencies = ["httpx>=0.27,<1", "pydantic>=2.8,<3", "typer>=0.12,<1"]

[project.optional-dependencies]
dev = ["pytest>=8,<9", "respx>=0.21,<1"]

[project.scripts]
fgo-content = "fgo_pet_content.cli:app"

[build-system]
requires = ["hatchling"]
build-backend = "hatchling.build"

[tool.pytest.ini_options]
pythonpath = ["src"]
testpaths = ["tests"]
```

Implement `ContentPaths` as a frozen dataclass with `raw_scripts`, `parsed_scripts`, `catalog`, `reports`, and `art_workspace`. Resolve both paths before calling `Path.is_relative_to(repo_root)` and raise before creating directories.

Add these repository-relative ignore rules:

```gitignore
.venv/
.pytest_cache/
__pycache__/
*.pyc
local_data/
story_cache/
```

- [ ] **Step 4: Run configuration tests and CLI help**

Run: `python -m pytest tests/test_config.py -v`

Expected: 2 passed.

Run: `python -m fgo_pet_content.cli --help`

Expected: exit 0 and display `FGO Pet content pipeline`.

- [ ] **Step 5: Commit the safe project foundation**

```bash
git add pyproject.toml .gitignore src/fgo_pet_content tests/test_config.py
git commit -m "build: scaffold content pipeline"
```

### Task 2: Versioned story and evidence schemas

**Files:**
- Create: `src/fgo_pet_content/models/source.py`
- Create: `src/fgo_pet_content/models/story.py`
- Create: `src/fgo_pet_content/models/evidence.py`
- Create: `src/fgo_pet_content/models/__init__.py`
- Create: `tests/models/test_story_models.py`
- Create: `tests/models/test_evidence_models.py`

**Interfaces:**
- Consumes: parsed command state and Atlas/local source metadata.
- Produces: `StoryDocument`, `StoryScene`, `Utterance`, `SourceRef`, `EvidenceCard`, `EvidenceCitation` Pydantic models.

- [ ] **Step 1: Write failing model validation tests**

```python
import pytest
from pydantic import ValidationError
from fgo_pet_content.models.evidence import EvidenceCard, EvidenceCitation

def test_evidence_requires_a_precise_citation():
    with pytest.raises(ValidationError):
        EvidenceCard(
            subject="mash", category="relationship", claim="玛修信赖前辈",
            authority="core", confidence=0.9, sources=[]
        )

def test_citation_rejects_empty_utterance_orders():
    with pytest.raises(ValidationError):
        EvidenceCitation(region="CN", script_id="0200040010", scene_index=1, utterance_orders=[])
```

- [ ] **Step 2: Run the tests and verify model imports fail**

Run: `python -m pytest tests/models -v`

Expected: FAIL because `fgo_pet_content.models` does not exist.

- [ ] **Step 3: Implement strict versioned schemas**

Use string enums for `Region(CN, JP)`, `TranslationStatus(official_cn, jp_fallback, alignment_uncertain, cn_jp_divergence)`, `Authority(core, context, style, flavor, archive)`, and `ReviewStatus(pending, approved, rejected)`.

Define the key signatures exactly:

```python
class Utterance(BaseModel):
    order: int
    speaker: str
    actor_slot: str | None = None
    servant_id: int | None = None
    figure_id: str | None = None
    face_id: int | None = None
    text: str
    branch_path: list[str] = []
    raw_start_line: int
    raw_end_line: int

class EvidenceCard(BaseModel):
    schema_version: Literal[1] = 1
    evidence_id: str
    subject: str
    category: str
    claim: str
    conditions: list[str] = []
    behavior: list[str] = []
    speech_traits: list[str] = []
    timeline: str | None = None
    authority: Authority
    confidence: float = Field(ge=0, le=1)
    translation_status: TranslationStatus
    sources: Annotated[list[EvidenceCitation], Field(min_length=1)]
    review: ReviewState = ReviewState()
```

Use `Field(default_factory=list)` and `Field(default_factory=ReviewState)` in the implementation to avoid shared mutable defaults.

- [ ] **Step 4: Run all model tests and export JSON schemas**

Run: `python -m pytest tests/models -v`

Expected: all tests pass.

Run: `python -c "from fgo_pet_content.models.story import StoryDocument; print(StoryDocument.model_json_schema()['title'])"`

Expected: `StoryDocument`.

- [ ] **Step 5: Commit the schema contract**

```bash
git add src/fgo_pet_content/models tests/models
git commit -m "feat: define story evidence schemas"
```

### Task 3: Atlas client and immutable raw cache

**Files:**
- Create: `src/fgo_pet_content/atlas.py`
- Create: `src/fgo_pet_content/cache.py`
- Create: `tests/test_atlas.py`
- Create: `tests/fixtures/atlas_script_search_mash.json`

**Interfaces:**
- Consumes: `ContentPaths`, Atlas endpoints, `Region`, query strings.
- Produces: `AtlasClient.search_scripts(region, query, limit) -> list[ScriptSearchHit]`; `AtlasClient.fetch_script(region, script_id) -> CachedScript`.

- [ ] **Step 1: Write failing HTTP and cache tests with respx**

```python
@respx.mock
def test_fetch_script_writes_content_addressed_cache(tmp_path, paths):
    respx.get("https://static.atlasacademy.io/CN/Script/02/0200040010.txt").mock(
        return_value=httpx.Response(200, text="＄02-00\n＠玛修\n早上好。\n[k]\n")
    )
    cached = AtlasClient(paths).fetch_script(Region.CN, "0200040010")
    assert cached.sha256.startswith("sha256:")
    assert cached.raw_path.read_text(encoding="utf-8").startswith("＄02-00")
    assert cached.metadata_path.exists()
```

Also test that CN 404 returns a typed `ScriptUnavailable`, HTTP 429 preserves existing cache, and a repeated matching hash does not replace the raw file.

- [ ] **Step 2: Run the focused tests and verify failure**

Run: `python -m pytest tests/test_atlas.py -v`

Expected: FAIL because `AtlasClient` is undefined.

- [ ] **Step 3: Implement explicit endpoints, timeouts, hashes, and sidecar metadata**

Use these endpoints:

```python
SEARCH_URL = "https://api.atlasacademy.io/nice/{region}/script/search"
SCRIPT_INFO_URL = "https://api.atlasacademy.io/nice/{region}/script/{script_id}"
```

Obtain the static script URL from API responses rather than constructing it when possible. Store raw text as `<data-root>/story_cache/raw/<region>/<script-id>/<sha256>.txt` and metadata as `<sha256>.json`. Write through a same-directory temporary file followed by `Path.replace` so interrupted downloads cannot corrupt a valid cache.

- [ ] **Step 4: Run HTTP/cache tests**

Run: `python -m pytest tests/test_atlas.py -v`

Expected: all tests pass without live network access.

- [ ] **Step 5: Commit the Atlas boundary**

```bash
git add src/fgo_pet_content/atlas.py src/fgo_pet_content/cache.py tests/test_atlas.py tests/fixtures/atlas_script_search_mash.json
git commit -m "feat: fetch and cache Atlas scripts"
```

### Task 4: Local master-table catalog and source resolver

**Files:**
- Create: `src/fgo_pet_content/catalog.py`
- Create: `src/fgo_pet_content/master_tables.py`
- Create: `tests/test_catalog.py`
- Create: `tests/fixtures/master/mstWar`
- Create: `tests/fixtures/master/mstQuestScript`

**Interfaces:**
- Consumes: a directory of extensionless JSON master tables and Atlas script results.
- Produces: `MasterTableReader.read(name: str) -> list[dict]`; `SourceCatalog.resolve(script_id: str) -> list[SourceRef]`.

- [ ] **Step 1: Write failing extensionless-table and War-link tests**

```python
def test_extensionless_json_master_table_is_read(master_root):
    rows = MasterTableReader(master_root).read("mstWar")
    assert rows[0]["scriptId"] == "0200040010"

def test_war_opening_script_resolves_without_quest_link(master_root):
    refs = SourceCatalog(MasterTableReader(master_root)).resolve("0200040010")
    assert [(r.container_type, r.container_id) for r in refs] == [("war_opening", 204)]
```

- [ ] **Step 2: Run catalog tests and verify failure**

Run: `python -m pytest tests/test_catalog.py -v`

Expected: FAIL because catalog modules do not exist.

- [ ] **Step 3: Implement content-sniffed JSON loading and indexed resolution**

Read UTF-8/UTF-8-BOM JSON after verifying the first non-whitespace byte is `[` or `{`. Build indexes once in `SourceCatalog.from_master_root(root)`. Return multiple `SourceRef` values when a script is reused. Add unresolved script IDs to `reports/unresolved_sources.jsonl` through a report interface rather than silently returning fabricated metadata.

- [ ] **Step 4: Run catalog tests and the full suite**

Run: `python -m pytest tests/test_catalog.py -v`

Expected: all catalog tests pass.

Run: `python -m pytest -q`

Expected: all tests pass.

- [ ] **Step 5: Commit source resolution**

```bash
git add src/fgo_pet_content/catalog.py src/fgo_pet_content/master_tables.py tests/test_catalog.py tests/fixtures/master
git commit -m "feat: resolve story script sources"
```

### Task 5: Stateful FGO script parser

**Files:**
- Create: `src/fgo_pet_content/parser/commands.py`
- Create: `src/fgo_pet_content/parser/state.py`
- Create: `src/fgo_pet_content/parser/story_parser.py`
- Create: `src/fgo_pet_content/parser/__init__.py`
- Create: `tests/parser/test_story_parser.py`
- Create: `tests/fixtures/scripts/CN/0200040010_excerpt.txt`

**Interfaces:**
- Consumes: raw script text, `SourceRef`, and optional `figure_to_servant: Mapping[str, int]`.
- Produces: `parse_story(text: str, source: SourceRef, figure_to_servant: Mapping[str, int]) -> StoryDocument`.

- [ ] **Step 1: Add a short legal fixture and failing state tests**

The fixture contains only enough synthetic/short text to cover `charaSet`, `scene`, `charaTalk`, `charaFace`, `＠speaker`, `[r]`, `[k]`, an unknown command, and a choice branch.

```python
def test_face_and_actor_state_are_attached_to_utterance(fixture_text, source):
    doc = parse_story(fixture_text, source, {"98001000": 800100})
    line = doc.scenes[0].utterances[0]
    assert line.speaker == "玛修"
    assert line.servant_id == 800100
    assert line.face_id == 13
    assert "\n" in line.text

def test_unknown_command_is_preserved_without_losing_dialogue(fixture_text, source):
    doc = parse_story(fixture_text, source, {})
    assert doc.unknown_commands[0].name == "futureCommand"
    assert doc.scenes[0].utterances
```

- [ ] **Step 2: Run parser tests and verify failure**

Run: `python -m pytest tests/parser/test_story_parser.py -v`

Expected: FAIL because `parse_story` is undefined.

- [ ] **Step 3: Implement a single-pass parser with explicit state transitions**

Parse command lines with one tokenizer, then dispatch recognized names to state handlers. Collect dialogue after an `＠` line until `[k]`; normalize `[r]` to newline and retain raw start/end lines. Never infer emotion from `face_id`. Preserve unknown commands as `UnknownCommand(name, arguments, line_number, raw)`.

- [ ] **Step 4: Verify parser behavior and snapshot stability**

Run: `python -m pytest tests/parser/test_story_parser.py -v`

Expected: all parser tests pass.

Run: `python -m pytest -q`

Expected: all tests pass.

- [ ] **Step 5: Commit the parser**

```bash
git add src/fgo_pet_content/parser tests/parser tests/fixtures/scripts
git commit -m "feat: parse FGO story scripts"
```

### Task 6: Mash candidate discovery and CN-first fallback orchestration

**Files:**
- Create: `src/fgo_pet_content/discovery.py`
- Create: `src/fgo_pet_content/pipeline.py`
- Create: `tests/test_discovery.py`
- Create: `tests/test_pipeline.py`
- Modify: `src/fgo_pet_content/cli.py`

**Interfaces:**
- Consumes: Mash identity configuration, `AtlasClient`, `SourceCatalog`, and `parse_story`.
- Produces: `MashIdentity`; `discover_candidates(identity, atlas) -> list[ScriptCandidate]`; `StoryPipeline.fetch_and_parse(candidate) -> ParsedArtifact`.

- [ ] **Step 1: Write failing discovery and fallback tests**

```python
def test_candidate_discovery_deduplicates_name_and_figure_hits(fake_atlas):
    identity = MashIdentity(servant_id=800100, names={"CN": ["玛修"], "JP": ["マシュ"]}, figure_ids={"98001000"})
    hits = discover_candidates(identity, fake_atlas)
    assert [h.script_id for h in hits].count("0200040010") == 1

def test_cn_missing_uses_jp_and_marks_translation_status(fake_pipeline):
    artifact = fake_pipeline.fetch_and_parse(ScriptCandidate(script_id="x"))
    assert artifact.document.source.region == Region.JP
    assert artifact.translation_status == TranslationStatus.JP_FALLBACK
```

- [ ] **Step 2: Run orchestration tests and verify failure**

Run: `python -m pytest tests/test_discovery.py tests/test_pipeline.py -v`

Expected: FAIL because discovery and pipeline modules are missing.

- [ ] **Step 3: Implement identity-driven search, deduplication, and fallback**

Search each configured name in its native region; merge hits by script ID and retain match reasons. After parsing, retain a script as Mash-related when at least one utterance resolves to Servant `800100`, a configured Mash name speaks, or a reviewed whitelist entry matches. Exclude mere one-off name mentions from the dialogue corpus but list them in the candidate report.

Expose:

```text
fgo-content story discover --servant 800100 --data-root D:\fgo_unpack\fgo_assets
fgo-content story fetch --servant 800100 --master-root D:\fgo_unpack\out\gamedata\unpack_master --data-root D:\fgo_unpack\fgo_assets
```

- [ ] **Step 4: Run orchestration tests and CLI help**

Run: `python -m pytest tests/test_discovery.py tests/test_pipeline.py -v`

Expected: all tests pass.

Run: `python -m fgo_pet_content.cli story --help`

Expected: discover and fetch commands are listed.

- [ ] **Step 5: Commit the story pipeline orchestration**

```bash
git add src/fgo_pet_content/discovery.py src/fgo_pet_content/pipeline.py src/fgo_pet_content/cli.py tests/test_discovery.py tests/test_pipeline.py
git commit -m "feat: discover Mash story corpus"
```

### Task 7: CN/JP scene and utterance alignment

**Files:**
- Create: `src/fgo_pet_content/alignment.py`
- Create: `tests/test_alignment.py`

**Interfaces:**
- Consumes: CN and JP `StoryDocument` values for the same script/container.
- Produces: `align_documents(cn: StoryDocument, jp: StoryDocument) -> AlignmentDocument` containing matched scene/utterance orders, status, and confidence without translating either source.

- [ ] **Step 1: Write failing exact and uncertain alignment tests**

```python
def test_same_script_actor_sequence_aligns_utterances(cn_doc, jp_doc):
    result = align_documents(cn_doc, jp_doc)
    assert result.pairs[0].cn_order == 1
    assert result.pairs[0].jp_order == 1
    assert result.pairs[0].status == TranslationStatus.OFFICIAL_CN

def test_divergent_branch_is_flagged_instead_of_forced(cn_doc, jp_doc):
    jp_doc.scenes[0].utterances.pop()
    result = align_documents(cn_doc, jp_doc)
    assert result.unmatched
    assert result.status in {
        TranslationStatus.ALIGNMENT_UNCERTAIN,
        TranslationStatus.CN_JP_DIVERGENCE,
    }
```

- [ ] **Step 2: Run alignment tests and verify failure**

Run: `python -m pytest tests/test_alignment.py -v`

Expected: FAIL because `align_documents` is undefined.

- [ ] **Step 3: Implement conservative structural alignment**

Match identical script IDs and container IDs first, then align scenes by order/background boundaries and utterances by speaker/actor sequence. Use dynamic-programming sequence alignment only inside matched scenes. Do not use machine translation to force a match. Mark low-confidence gaps as `alignment_uncertain` and meaningful added/removed sequences as `cn_jp_divergence`.

- [ ] **Step 4: Run alignment and full tests**

Run: `python -m pytest tests/test_alignment.py -v`

Expected: all alignment tests pass.

Run: `python -m pytest -q`

Expected: all tests pass.

- [ ] **Step 5: Commit bilingual alignment**

```bash
git add src/fgo_pet_content/alignment.py tests/test_alignment.py
git commit -m "feat: align CN and JP story structure"
```

### Task 8: Evidence candidate extraction with source-bound validation

**Files:**
- Create: `src/fgo_pet_content/evidence/context.py`
- Create: `src/fgo_pet_content/evidence/prompts.py`
- Create: `src/fgo_pet_content/evidence/extractor.py`
- Create: `src/fgo_pet_content/evidence/validator.py`
- Create: `src/fgo_pet_content/evidence/__init__.py`
- Create: `tests/evidence/test_context.py`
- Create: `tests/evidence/test_extractor.py`
- Create: `tests/evidence/test_validator.py`

**Interfaces:**
- Consumes: Mash-related `StoryDocument` and an OpenAI-compatible structured-output client.
- Produces: `build_evidence_windows(document, servant_id=800100) -> list[EvidenceWindow]`; `EvidenceExtractor.extract(window) -> list[EvidenceCard]`; `validate_evidence(card, window) -> ValidationResult`.

- [ ] **Step 1: Write failing window and citation-bound tests**

```python
def test_window_includes_neighbor_context_around_mash_line(story_document):
    windows = build_evidence_windows(story_document, servant_id=800100, neighbor_lines=3)
    assert windows[0].target_orders == [4]
    assert [u.order for u in windows[0].utterances] == [1, 2, 3, 4, 5, 6, 7]

def test_validator_rejects_citation_outside_window(card, evidence_window):
    card.sources[0].utterance_orders = [999]
    result = validate_evidence(card, evidence_window)
    assert not result.accepted
    assert "outside supplied evidence" in result.reasons
```

- [ ] **Step 2: Run evidence tests and verify failure**

Run: `python -m pytest tests/evidence -v`

Expected: FAIL because evidence modules are missing.

- [ ] **Step 3: Implement deterministic windows, strict prompt, and validation**

The system instruction must state that the model may only infer from supplied utterances, must return Chinese abstract claims rather than story retellings, and must cite exact utterance orders. Pass Pydantic's `EvidenceCard.model_json_schema()` to the configured structured-output adapter. Reject cards with missing/out-of-window citations, unknown authority values, confidence outside `[0,1]`, or claims substantially duplicating a supplied raw line.

Keep the model adapter injectable so tests use a fake response and never need a key.

- [ ] **Step 4: Run evidence tests and full suite**

Run: `python -m pytest tests/evidence -v`

Expected: all evidence tests pass.

Run: `python -m pytest -q`

Expected: all tests pass.

- [ ] **Step 5: Commit evidence extraction**

```bash
git add src/fgo_pet_content/evidence tests/evidence
git commit -m "feat: extract source-bound persona evidence"
```

### Task 9: Review queue, persona compiler, and one-script live acceptance probe

**Files:**
- Create: `src/fgo_pet_content/review.py`
- Create: `src/fgo_pet_content/compiler.py`
- Create: `src/fgo_pet_content/reporting.py`
- Create: `tests/test_review.py`
- Create: `tests/test_compiler.py`
- Create: `tests/test_reporting.py`
- Modify: `src/fgo_pet_content/cli.py`
- Create: `docs/content-pipeline.md`

**Interfaces:**
- Consumes: candidate evidence JSONL and explicit review decisions.
- Produces: `compile_persona(cards: Sequence[EvidenceCard]) -> PersonaBundle`; review/conflict/unresolved reports; CLI commands `evidence extract`, `evidence review`, and `persona compile`.

- [ ] **Step 1: Write failing review and compile tests**

```python
def test_only_approved_core_cards_enter_core_persona(cards):
    bundle = compile_persona(cards)
    assert all(item.review.status == ReviewStatus.APPROVED for item in bundle.core_evidence)
    assert all(item.authority == Authority.CORE for item in bundle.core_evidence)

def test_same_script_does_not_count_as_independent_support(cards):
    merged = merge_support(cards)
    assert merged[0].independent_source_count == 1
```

Also test conflict reporting for different timeline conditions and output redaction that prevents raw utterance text from entering repository-facing reports.

- [ ] **Step 2: Run compiler/report tests and verify failure**

Run: `python -m pytest tests/test_review.py tests/test_compiler.py tests/test_reporting.py -v`

Expected: FAIL because compiler modules are missing.

- [ ] **Step 3: Implement explicit review transitions and deterministic compilation**

Allow only `pending -> approved|rejected` and require reviewer notes for a manual authority change. Compile approved Core cards to `core_persona.json`, approved Style cards to `speech_style.json`, and other approved cards to `knowledge/topics.jsonl`. Reports contain source IDs and abstract claims, not raw dialogue.

Document exact setup, external data root requirements, commands, outputs, and deletion/rebuild procedures in `docs/content-pipeline.md`.

- [ ] **Step 4: Run automated checks and the approved live one-script probe**

Run: `python -m pytest -q`

Expected: all tests pass.

Run with network access:

```text
fgo-content story fetch-script --region CN --script-id 0200040010 --master-root D:\fgo_unpack\out\gamedata\unpack_master --data-root D:\fgo_unpack\fgo_assets
```

Expected:

- Raw text exists only under `D:\fgo_unpack\fgo_assets\story_cache\raw\CN\0200040010`.
- Parsed document identifies War `204` and at least one Mash utterance with `figure_id` and `face_id`.
- `git status --short` shows no raw or parsed story files.
- A redacted report records script ID, counts, unknown commands, and unresolved mappings.

- [ ] **Step 5: Commit the reviewable story pipeline**

```bash
git add src/fgo_pet_content tests docs/content-pipeline.md
git commit -m "feat: compile reviewed Mash persona data"
```
