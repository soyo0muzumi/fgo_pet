import importlib.util
import json
from pathlib import Path

import pytest

PATH = Path(__file__).resolve().parents[2] / 'tools/integrations/web-search/search_worker.py'
spec = importlib.util.spec_from_file_location('fgopet_search_worker', PATH)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


def request(query='fixture', maximum=5):
    return json.dumps({'protocolVersion': 1, 'query': query, 'maxResults': maximum}).encode()


@pytest.mark.parametrize('body', [b'{}', b'[]', b'not json', b'\xff', b' ' * 8193,
    request(''), request('  '), request('x\nunsafe'), request('x' * 401), request('fixture', True),
    request('fixture', 0), request('fixture', 6),
    b'{"protocolVersion":1,"query":"first","query":"second","maxResults":1}'])
def test_invalid_input_never_invokes_search(body):
    def forbidden(*_):
        pytest.fail('Invalid input reached search')
    with pytest.raises(worker.SearchFailure) as caught:
        worker.handle_request(body, forbidden)
    assert caught.value.code == 'TOOL_INVALID_ARGUMENTS'


def test_supplementary_characters_count_as_scalars():
    assert worker.parse_request(request('😀' * 400))[0] == '😀' * 400


def test_normalized_sources_are_bounded_without_reading_source_urls():
    seen = []
    def search(query, maximum):
        seen.append((query, maximum))
        return [
            {'href': 'file:///private', 'title': 'file', 'body': 'private'},
            {'href': 'https://user:secret@example.org/', 'title': 'credentials', 'body': 'private'},
            {'href': 'https://example.org/', 'title': '😀' * 300, 'body': 'x' * 2500},
            {'href': 'https://other.org/', 'title': 'extra', 'body': 'extra'},
        ]
    result = worker.handle_request(request(maximum=1), search)
    assert seen == [('fixture', 1)]
    assert result['success'] is True
    assert result['truncated'] is True
    [source] = result['results']
    assert source['url'] == 'https://example.org/'
    assert len(source['title']) == 256
    assert len(source['content']) == 2000
    assert source['titleTruncated'] and source['snippetTruncated']
    assert 'secret' not in json.dumps(result)


@pytest.mark.parametrize('status,body,code', [
    (429, 'private upstream body', 'WEB_SEARCH_RATE_LIMITED'),
    (403, 'private upstream body', 'WEB_SEARCH_RATE_LIMITED'),
    (202, 'challenge', 'WEB_SEARCH_RATE_LIMITED'),
    (200, '<form id="challenge-form">', 'WEB_SEARCH_RATE_LIMITED'),
    (200, '<script src="/anomaly.js">', 'WEB_SEARCH_RATE_LIMITED'),
    (302, 'redirect', 'WEB_SEARCH_NETWORK_FAILED'),
    (500, 'upstream', 'WEB_SEARCH_NETWORK_FAILED'),
    (200, 'x' * 1048577, 'WEB_SEARCH_RESPONSE_TOO_LARGE'),
], ids=['rate-limited', 'forbidden', 'accepted-challenge', 'challenge-form', 'challenge-script', 'redirect', 'upstream', 'oversized'])
def test_http_challenges_and_errors_never_become_empty_success(status, body, code):
    with pytest.raises(worker.SearchFailure) as caught:
        worker.classify_page(status, body)
    assert caught.value.code == code
    assert str(caught.value) == code


def test_empty_result_and_network_failure_are_distinct():
    result = worker.handle_request(request(), lambda *_: [])
    assert result['success'] is True and result['results'] == []
    def failed(*_):
        raise worker.SearchFailure('WEB_SEARCH_NETWORK_FAILED')
    with pytest.raises(worker.SearchFailure):
        worker.handle_request(request(), failed)


@pytest.mark.parametrize('rows', [None, [None], [{'href': 'https://example.org', 'title': 5, 'body': 'x'}],
    [{'href': 'https://example.org', 'title': 'x', 'body': 'bad\x00text'}]])
def test_malformed_results_do_not_escape(rows):
    with pytest.raises(worker.SearchFailure) as caught:
        worker.handle_request(request(), lambda *_: rows)
    assert caught.value.code == 'WEB_SEARCH_RESPONSE_INVALID'


def test_serialized_result_has_a_hard_byte_bound():
    with pytest.raises(worker.SearchFailure) as caught:
        worker.response_bytes({'payload': 'x' * 131072})
    assert caught.value.code == 'WEB_SEARCH_RESPONSE_TOO_LARGE'


@pytest.mark.parametrize('status,html,expected', [
    (200, '<html><div class="result__body"><h2><a>Primary source</a></h2><a href="https://example.org/">A <b>bounded</b> snippet.</a></div></html>', 'result'),
    (200, '<html><div class="no-results">No results found for fixture.</div></html>', 'empty'),
    (200, '<html><h1>Unexpected service response</h1></html>', 'WEB_SEARCH_RESPONSE_INVALID'),
    (429, '<html>Rate limited</html>', 'WEB_SEARCH_RATE_LIMITED'),
    (200, '<html><form id="challenge-form"></form></html>', 'WEB_SEARCH_RATE_LIMITED'),
])
def test_pinned_engine_parsing_and_http_boundary_with_fixture_transport(monkeypatch, status, html, expected):
    primp = pytest.importorskip('primp')
    pytest.importorskip('ddgs')
    calls, clients = [], []

    class Response:
        status_code = status
        text = html
        content = html.encode()

    class Client:
        def __init__(self, **kwargs):
            clients.append(kwargs)
        def headers_update(self, _):
            pass
        def request(self, method, url, **kwargs):
            calls.append((method, url, kwargs))
            return Response()

    monkeypatch.setattr(primp, 'Client', Client)
    if expected.startswith('WEB_SEARCH_'):
        with pytest.raises(worker.SearchFailure) as caught:
            worker.search('fixture', 1)
        assert caught.value.code == expected
    else:
        results = worker.search('fixture', 1)
        if expected == 'empty':
            assert results == []
        else:
            assert results == [{'href': 'https://example.org/', 'title': 'Primary source', 'body': 'A bounded snippet.'}]
    assert len(calls) == 1
    method, url, arguments = calls[0]
    assert method == 'POST' and url == worker.SEARCH_URL
    assert arguments['data']['q'] == 'fixture'
    assert clients[-1]['verify'] is True
    assert clients[-1]['follow_redirects'] is False
    assert clients[-1]['cookie_store'] is False
