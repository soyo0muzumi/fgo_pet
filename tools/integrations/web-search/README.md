# Bundled Python web search

This integration provides a single-purpose Python executable for `web.search`. It uses the DuckDuckGo HTML engine from the pinned [`ddgs` 9.16.1 release](https://pypi.org/project/ddgs/9.16.1/). DDGS is MIT-licensed; its native dependencies and CPython keep their own licenses. Build outputs include their notices under `licenses/`.

The engine is imported directly instead of asking the metasearch class to choose a provider. This prevents a missing or disabled backend from silently falling back to another provider. The wrapper classifies HTTP failures and challenge pages before result parsing. A successful empty response requires known no-results markup; malformed or changed markup is reported as a failure. Source pages are not opened.

## Build on Windows

Build with 64-bit Python 3.11 or 3.12:

```powershell
pwsh -File tools/integrations/web-search/build.ps1 -Python python
```

The script creates an isolated temporary environment, installs the pinned build inputs, bundles the helper with PyInstaller in one-directory mode, collects license notices, writes a file/size/SHA-256 manifest, and runs the bundled offline self-test. It produces `artifacts/web-search/runtime` by default. An existing nonempty output is preserved; pass `-OutputDirectory` with a new directory when rebuilding.

The complete directory is required. It contains:

- `FgoPet.WebSearch.exe`
- `_internal/` with Python and native dependencies
- `licenses/` including CPython and dependency notices
- `runtime-manifest.json`

Point `FgoPetWebSearchRuntime` at this directory for MSBuild. The application stages it under `WebSearch/`. Neither application startup nor the helper downloads or installs anything. Testers need no separate Python installation or search key.

## Protocol

The host sends one UTF-8 JSON object on stdin and closes stdin:

```json
{"protocolVersion":1,"query":"Python documentation","maxResults":3}
```

The helper returns one bounded UTF-8 JSON object on stdout. Results contain `url`, `title`, `content`, `titleTruncated`, and `snippetTruncated`; the root includes `protocolVersion`, `success`, `results`, and `truncated`. Failure responses contain only `protocolVersion`, `success=false`, and a safe `errorCode`. Exit status is zero for success and nonzero for failure. Arguments other than `--self-test` are rejected; queries never appear in the command line.

The offline self-test is:

```powershell
& './artifacts/web-search/runtime/FgoPet.WebSearch.exe' --self-test
```

It reports `protocolVersion=1`, `provider=duckduckgo`, and `dependencyVersion=9.16.1` without a query or network access. It validates dependency imports and the fixed engine, not external connectivity. The settings-page connection test performs a separate fixed public query after explicit search authorization.

The parent process imposes the total deadline and process/memory/output limits. The helper uses a 15-second HTTP timeout, verified TLS, no redirects, one provider, one page, and no automatic retry. Rate limiting or network restrictions can still make a live search fail and are returned as such.

## Development checks

```powershell
python -m pip install -r tools/integrations/web-search/requirements.txt pytest
python -m pytest tests/web_search
```

The fixture tests need no internet or real configuration. An optional real-query check should use a public synthetic query and record only success/error code and result count. Do not store real user queries, source content, protected settings, or executable build outputs in the repository.
