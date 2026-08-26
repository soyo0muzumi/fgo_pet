# Mash Profile and Story Retrieval Implementation Plan

> **For Codex:** REQUIRED SUB-SKILL: Use superpowers:test-driven-development for each task and superpowers:verification-before-completion before claiming completion.

**Goal:** Build a compact, source-traceable Mash profile and local story retrieval layer so plot questions are answered accurately without placing the full corpus in every prompt.

**Architecture:** Fetch lore-enabled servant data with CN-first/JP-fallback provenance, normalize it into a bounded profile, and index parsed scenes in SQLite FTS5. A deterministic composer always supplies the compact profile and adds only 2–4 retrieved scene windows for plot questions; an optional reranker may reorder candidates but must fail open to FTS ranking.

**Tech Stack:** Python 3.11, Pydantic 2, httpx, Typer, stdlib `sqlite3` FTS5, pytest, respx

**Approved spec:** `docs/superpowers/specs/2026-08-26-knowledge-and-art-readiness-design.md`

---

## Task 1: Add lore-enabled servant acquisition

**Files:**
- Modify: `src/fgo_pet_content/atlas.py`
- Test: `tests/test_atlas.py`

**Step 1: Write failing tests**

Assert `fetch_servant(Region.CN, 1, lore=True)` requests `/nice/CN/servant/1?lore=true`, caches the response, returns the cache on a second call, and returns `None` on 404 so fallback can proceed.

```python
def test_fetch_servant_requests_lore_and_caches(paths, respx_mock):
    route = respx_mock.get(
        "https://api.atlasacademy.io/nice/CN/servant/1",
        params={"lore": "true"},
    ).mock(return_value=httpx.Response(200, json={"id": 800100, "profile": {}}))
    client = AtlasClient(paths)
    assert client.fetch_servant(Region.CN, 1, lore=True)["id"] == 800100
    assert client.fetch_servant(Region.CN, 1, lore=True)["id"] == 800100
    assert route.call_count == 1
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/test_atlas.py -k fetch_servant -q`
Expected: FAIL because the method is absent.

**Step 3: Implement the smallest API**

```python
def fetch_servant(
    self, region: Region, collection_no: int, *, lore: bool = True
) -> dict | None:
    suffix = "-lore" if lore else ""
    cache = self.paths.raw / "servants" / region.value / f"{collection_no}{suffix}.json"
    return self._fetch_json_cached(
        cache,
        f"/nice/{region.value}/servant/{collection_no}",
        params={"lore": str(lore).lower()},
        missing_ok=True,
    )
```

Reuse existing HTTP, atomic cache, and error conventions rather than adding a second transport.

**Step 4: Verify and commit**

Run: `D:\environments\anaconda\python.exe -m pytest tests/test_atlas.py -q`
Expected: PASS.

```powershell
git add src/fgo_pet_content/atlas.py tests/test_atlas.py
git commit -m "feat: fetch lore-enabled servant profiles"
```

## Task 2: Normalize a bounded profile with provenance

**Files:**
- Create: `src/fgo_pet_content/profile/__init__.py`
- Create: `src/fgo_pet_content/profile/models.py`
- Create: `src/fgo_pet_content/profile/extract.py`
- Create: `tests/profile/test_extract.py`

**Step 1: Write failing tests**

Cover CN precedence, per-field JP fallback with `jp_fallback=true`, provenance, HTML/whitespace cleanup, a 1,200-Chinese-character summary limit, and `ProfileUnavailable` when neither payload has profile data.

```python
def test_cn_first_with_jp_field_fallback():
    profile = build_profile(CN_LORE, JP_LORE, servant_id=800100)
    assert profile.name == "玛修·基列莱特"
    assert profile.facts["likes"].source_region is Region.JP
    assert profile.facts["likes"].jp_fallback is True
    assert len(profile.summary) <= 1200
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/profile/test_extract.py -q`
Expected: import failure.

**Step 3: Add explicit models and deterministic extraction**

```python
class ProfileFact(BaseModel):
    value: str
    source_region: Region
    source_path: str
    jp_fallback: bool = False

class MashProfile(BaseModel):
    servant_id: Literal[800100]
    collection_no: Literal[1]
    name: str
    summary: str
    facts: dict[str, ProfileFact]
    source_hashes: dict[str, str]
```

Map only approved identity/profile paths. Prefer non-empty CN per field, otherwise JP. Build the summary from a fixed ordered list and truncate only at sentence boundaries; no LLM call belongs in this build step.

**Step 4: Verify and commit**

Run: `D:\environments\anaconda\python.exe -m pytest tests/profile/test_extract.py -q`
Expected: PASS.

```powershell
git add src/fgo_pet_content/profile tests/profile
git commit -m "feat: normalize sourced Mash profile facts"
```

## Task 3: Build a local FTS5 scene index

**Files:**
- Create: `src/fgo_pet_content/retrieval/__init__.py`
- Create: `src/fgo_pet_content/retrieval/models.py`
- Create: `src/fgo_pet_content/retrieval/index.py`
- Create: `tests/retrieval/test_index.py`

**Step 1: Write failing tests**

Using small `StoryDocument` fixtures, assert schema metadata, idempotent rebuild, speaker/alias inclusion, provenance retention, and correct top hit for a distinctive plot term.

```python
def test_fts_returns_traceable_scene(tmp_path, story_document):
    db = tmp_path / "story.sqlite3"
    build_story_index(db, [story_document])
    hits = search_story_index(db, "黑色枪管", limit=8)
    assert hits[0].scene_id == story_document.scenes[1].scene_id
    assert hits[0].source.region is Region.CN
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/retrieval/test_index.py -q`
Expected: import failure.

**Step 3: Implement atomic FTS index construction**

Create a normal `scenes` table and an external-content FTS5 table:

```sql
CREATE VIRTUAL TABLE scene_fts USING fts5(
  text, speakers, aliases,
  content='scenes', content_rowid='rowid', tokenize='unicode61'
);
```

Store document/scene IDs, region, container IDs, order, speaker list, normalized text, and source hash. Build to `*.tmp`, close, then replace the live DB. Quote normalized tokens before `MATCH`; never concatenate raw FTS syntax. Return BM25 score and typed provenance.

**Step 4: Verify and commit**

Run: `D:\environments\anaconda\python.exe -m pytest tests/retrieval/test_index.py -q`
Expected: PASS.

```powershell
git add src/fgo_pet_content/retrieval tests/retrieval/test_index.py
git commit -m "feat: index story scenes with local FTS5"
```

## Task 4: Route queries and compose bounded contexts

**Files:**
- Create: `src/fgo_pet_content/retrieval/query.py`
- Create: `src/fgo_pet_content/retrieval/context.py`
- Create: `tests/retrieval/test_query.py`
- Create: `tests/retrieval/test_context.py`

**Step 1: Write failing behavior tests**

Cover ordinary conversation (profile only), explicit plot questions (story retrieval), and ambiguous knowledge questions (retrieve only if profile coverage is insufficient). Assert 2–4 windows, at most 900 estimated tokens, stable order, deduplication, `coverage_gap=true` below threshold, and FTS fallback when a reranker raises.

```python
def test_plot_question_uses_bounded_story_context(index, profile):
    context = compose_context("在奥尔良发生了什么？", profile, index)
    assert context.route == "story"
    assert 2 <= len(context.story_windows) <= 4
    assert context.estimated_tokens <= 900
```

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/retrieval/test_query.py tests/retrieval/test_context.py -q`
Expected: import failure.

**Step 3: Implement orchestration**

Define a `StoryReranker` protocol. Retrieve eight FTS candidates, optionally rerank, then select non-overlapping results until 2–4 windows or 900 tokens. Keep citations internally and expose `route_reasons`.

Encode response policy in the context: answer in 2–4 sentences by default; give the supported conclusion first; when coverage is incomplete, describe a source coverage limit rather than claiming Mash herself “doesn't know”; offer expansion instead of injecting the corpus.

**Step 4: Verify and commit**

Run: `D:\environments\anaconda\python.exe -m pytest tests/retrieval -q`
Expected: PASS.

```powershell
git add src/fgo_pet_content/retrieval tests/retrieval
git commit -m "feat: compose bounded story-aware contexts"
```

## Task 5: Add knowledge CLI and build real artifacts

**Files:**
- Modify: `src/fgo_pet_content/cli.py`
- Create: `tests/test_knowledge_cli.py`
- Modify: `README.md`
- Create: `docs/reports/2026-08-26-mash-knowledge-readiness.md`
- External outputs: `D:\fgo_unpack\fgo_assets\story_cache\persona\mash\`

**Step 1: Write failing CLI tests**

Test `knowledge build-profile`, `knowledge build-index`, and `knowledge search`. Assert JSON output paths and query metadata; complete copyrighted story text and secrets must not be printed.

**Step 2: Verify failure**

Run: `D:\environments\anaconda\python.exe -m pytest tests/test_knowledge_cli.py -q`
Expected: FAIL because the group is absent.

**Step 3: Implement commands and manifests**

Write `profile.json`, `story.sqlite3`, and `knowledge-manifest.json` below the supplied data root. The manifest records schema versions, source hashes, language-fallback count, scene count, and build timestamp. Search output contains IDs, scores, and short excerpts only.

**Step 4: Build and verify real artifacts**

```powershell
D:\environments\anaconda\python.exe -m fgo_pet_content.cli knowledge build-profile --data-root D:\fgo_unpack\fgo_assets --servant 800100 --collection-no 1
D:\environments\anaconda\python.exe -m fgo_pet_content.cli knowledge build-index --data-root D:\fgo_unpack\fgo_assets
D:\environments\anaconda\python.exe -m pytest -q
```

Expected: non-empty sourced profile, non-zero scene count, valid manifest, and the full suite passes beyond the existing 60-test baseline.

**Step 5: Run the eleven fixed scenarios**

Record route, selected scene IDs, token estimate, fallback, and support status. Relevant indexed evidence must prevent an “不清楚” answer; gaps must be labeled as material coverage gaps. Write exact commands, hashes, outcomes, and remaining gaps to the readiness report without copying long passages.

**Step 6: Commit**

```powershell
git add src/fgo_pet_content/cli.py tests/test_knowledge_cli.py README.md docs/reports/2026-08-26-mash-knowledge-readiness.md
git commit -m "feat: deliver Mash knowledge retrieval readiness"
```

