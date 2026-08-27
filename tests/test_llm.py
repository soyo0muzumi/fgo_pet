import json

import httpx
import respx

from fgo_pet_content.llm import OpenAICompatibleStructuredClient


@respx.mock
def test_openai_compatible_client_returns_structured_payload() -> None:
    route = respx.post("https://llm.example/v1/chat/completions").mock(
        return_value=httpx.Response(
            200,
            json={
                "choices": [
                    {"message": {"content": json.dumps({"cards": []})}}
                ]
            },
        )
    )
    client = OpenAICompatibleStructuredClient(
        base_url="https://llm.example/v1",
        api_key="secret",
        model="test-model",
    )

    result = client.generate(system="system", user="user", schema={"type": "object"})

    assert result == {"cards": []}
    request = route.calls[0].request
    assert request.headers["Authorization"] == "Bearer secret"
    assert json.loads(request.content)["model"] == "test-model"
