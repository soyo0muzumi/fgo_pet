"""One bounded JSON request on stdin; one bounded JSON response on stdout.

Only the pinned DuckDuckGo engine is used. This executable never installs packages,
opens result URLs, reads application settings, or changes provider after a failure.
"""
from __future__ import annotations

import json
import logging
import sys
import unicodedata
from importlib.metadata import PackageNotFoundError, version
from urllib.parse import urlsplit

PROTOCOL_VERSION = 1
DEPENDENCY_VERSION = "9.16.1"
MAX_INPUT_BYTES = 8192
MAX_OUTPUT_BYTES = 131072
MAX_PAGE_BYTES = 1048576
SEARCH_URL = "https://html.duckduckgo.com/html/"


class SearchFailure(Exception):
    def __init__(self, code: str):
        self.code = code
        super().__init__(code)


def _unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise SearchFailure("TOOL_INVALID_ARGUMENTS")
        result[key] = value
    return result


def parse_request(data: bytes) -> tuple[str, int]:
    if len(data) > MAX_INPUT_BYTES:
        raise SearchFailure("TOOL_INVALID_ARGUMENTS")
    try:
        request = json.loads(data.decode("utf-8", errors="strict"), object_pairs_hook=_unique_object)
    except (ValueError, UnicodeError) as error:
        raise SearchFailure("TOOL_INVALID_ARGUMENTS") from error
    if not isinstance(request, dict) or set(request) != {"protocolVersion", "query", "maxResults"}:
        raise SearchFailure("TOOL_INVALID_ARGUMENTS")
    query, maximum = request["query"], request["maxResults"]
    if type(request["protocolVersion"]) is not int or request["protocolVersion"] != PROTOCOL_VERSION:
        raise SearchFailure("TOOL_INVALID_ARGUMENTS")
    if not isinstance(query, str) or not query.strip() or len(query) > 400 or not _valid_text(query):
        raise SearchFailure("TOOL_INVALID_ARGUMENTS")
    if type(maximum) is not int or not 1 <= maximum <= 5:
        raise SearchFailure("TOOL_INVALID_ARGUMENTS")
    return query, maximum


def _valid_text(text: str, multiline: bool = False) -> bool:
    return all(unicodedata.category(char) not in ("Cc", "Cs") or (multiline and char in "\r\n\t") for char in text)


def classify_page(status: int, text: str) -> str:
    if status in (202, 403, 418, 429):
        raise SearchFailure("WEB_SEARCH_RATE_LIMITED")
    if status != 200:
        raise SearchFailure("WEB_SEARCH_NETWORK_FAILED")
    if len(text.encode("utf-8")) > MAX_PAGE_BYTES:
        raise SearchFailure("WEB_SEARCH_RESPONSE_TOO_LARGE")
    if any(marker in text.lower() for marker in ('id="challenge-form"', 'anomaly-modal', '/anomaly.js')):
        raise SearchFailure("WEB_SEARCH_RATE_LIMITED")
    return text


def _dependencies():
    try:
        if version("ddgs") != DEPENDENCY_VERSION:
            raise SearchFailure("WEB_SEARCH_RUNTIME_INVALID")
        import primp
        from ddgs.engines.duckduckgo import Duckduckgo
        from ddgs.exceptions import DDGSException, TimeoutException
    except (ImportError, PackageNotFoundError) as error:
        raise SearchFailure("WEB_SEARCH_RUNTIME_MISSING") from error
    if Duckduckgo.disabled or Duckduckgo.search_url != SEARCH_URL:
        raise SearchFailure("WEB_SEARCH_RUNTIME_INVALID")
    return primp, Duckduckgo, DDGSException, TimeoutException


def search(query: str, maximum: int) -> list[dict[str, str]]:
    # Loading the exact engine directly avoids the metasearch class's fallback to
    # other providers. The small HTTP override prevents 429/challenge pages from
    # being mistaken for a successful search with zero results.
    primp, Duckduckgo, DDGSException, TimeoutException = _dependencies()

    class FixedDuckDuckGo(Duckduckgo):
        known_empty = False

        def request(self, method, url, **kwargs):
            if method != "POST" or url != SEARCH_URL:
                raise SearchFailure("WEB_SEARCH_RUNTIME_INVALID")
            response = self.http_client.request(method, url, **kwargs)
            text = classify_page(response.status_code, response.text)
            self.known_empty = any(marker in text.lower() for marker in (
                'class="no-results', "no results found for", "no  results.",
            ))
            return text

    try:
        engine = FixedDuckDuckGo(timeout=15, verify=True)
        engine.http_client.client = primp.Client(timeout=15, verify=True, follow_redirects=False,
            cookie_store=False, referer=False, impersonate="chrome", impersonate_os="windows")
        found = engine.search(query, region="wt-wt", safesearch="moderate", page=1)
        if not found and not engine.known_empty:
            raise SearchFailure("WEB_SEARCH_RESPONSE_INVALID")
        return [{"href": result.href, "title": result.title, "body": result.body} for result in (found or [])][:maximum]
    except TimeoutException as error:
        raise SearchFailure("WEB_SEARCH_TIMEOUT") from error
    except DDGSException as error:
        raise SearchFailure("WEB_SEARCH_NETWORK_FAILED") from error
    except SearchFailure:
        raise
    except Exception as error:
        raise SearchFailure("WEB_SEARCH_RESPONSE_INVALID") from error


def normalize_results(rows, maximum: int) -> tuple[list[dict], bool]:
    if not isinstance(rows, list):
        raise SearchFailure("WEB_SEARCH_RESPONSE_INVALID")
    results, truncated = [], len(rows) > maximum
    for row in rows:
        if len(results) >= maximum:
            truncated = True
            break
        if not isinstance(row, dict) or not all(isinstance(row.get(field), str) for field in ("href", "title", "body")):
            raise SearchFailure("WEB_SEARCH_RESPONSE_INVALID")
        url, title, snippet = row["href"], row["title"], row["body"]
        try:
            parsed = urlsplit(url)
            safe_url = (0 < len(url) <= 2048 and _valid_text(url) and parsed.scheme in ("http", "https")
                and bool(parsed.hostname) and parsed.username is None and parsed.password is None)
        except ValueError:
            safe_url = False
        if not safe_url:
            truncated = True
            continue
        if not _valid_text(title) or not _valid_text(snippet, multiline=True):
            raise SearchFailure("WEB_SEARCH_RESPONSE_INVALID")
        title_clipped, snippet_clipped = len(title) > 256, len(snippet) > 2000
        truncated |= title_clipped or snippet_clipped
        results.append({"url": url, "title": title[:256], "content": snippet[:2000],
            "titleTruncated": title_clipped, "snippetTruncated": snippet_clipped})
    return results, truncated


def handle_request(data: bytes, search_function=search) -> dict:
    query, maximum = parse_request(data)
    results, truncated = normalize_results(search_function(query, maximum), maximum)
    return {"protocolVersion": PROTOCOL_VERSION, "success": True, "results": results, "truncated": truncated}


def response_bytes(result: dict) -> bytes:
    value = json.dumps(result, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    if len(value) > MAX_OUTPUT_BYTES:
        raise SearchFailure("WEB_SEARCH_RESPONSE_TOO_LARGE")
    return value + b"\n"


def main() -> int:
    logging.disable(logging.CRITICAL)
    try:
        if sys.argv[1:] == ["--self-test"]:
            _dependencies()
            result = {"protocolVersion": PROTOCOL_VERSION, "success": True, "provider": "duckduckgo",
                "dependencyVersion": DEPENDENCY_VERSION}
        elif sys.argv[1:]:
            raise SearchFailure("TOOL_INVALID_ARGUMENTS")
        else:
            result = handle_request(sys.stdin.buffer.read(MAX_INPUT_BYTES + 1))
        output = response_bytes(result)
        exit_code = 0
    except SearchFailure as error:
        output = response_bytes({"protocolVersion": PROTOCOL_VERSION, "success": False, "errorCode": error.code})
        exit_code = 1
    except Exception:
        output = response_bytes({"protocolVersion": PROTOCOL_VERSION, "success": False, "errorCode": "WEB_SEARCH_RUNTIME_FAILED"})
        exit_code = 1
    sys.stdout.buffer.write(output)
    sys.stdout.buffer.flush()
    return exit_code


if __name__ == "__main__":
    raise SystemExit(main())
