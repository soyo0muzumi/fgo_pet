#!/usr/bin/env python3
"""Verify a running IndexTTS WebUI against the contract FgoPet's adapter requires.

Mirrors the protocol used by
modules/speech/src/FgoPet.Speech.Infrastructure/IndexTtsSpeechSynthesizer.cs:

  1. GET  /config                          -> exactly one ``gen_single`` with 26 inputs
  2. POST /gradio_api/upload               -> upload the reference WAV
  3. POST /gradio_api/call/gen_single      -> start inference
  4. GET  /gradio_api/call/gen_single/<id> -> read the SSE stream until ``complete``
  5. GET  /gradio_api/file=<path>          -> download the generated WAV

It also reports wall-clock timings for each stage so the acceptance matrix can
record real cold/warm numbers instead of guesses.

Usage:
    python verify_indextts_service.py --reference voice.wav
    python verify_indextts_service.py --base-url http://127.0.0.1:7860 --reference voice.wav --text "测试文本"
"""

from __future__ import annotations

import argparse
import json
import struct
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid

EXPECTED_INPUTS = 26
EXPECTED_TYPES = ("radio", "audio", "textbox", "dropdown", "audio", "slider")
# Indexes the adapter overrides, matching the v2.5 gen_single signature.
PROMPT_INDEX = 1
TEXT_INDEX = 2
EMOTION_REFERENCE_INDEX = 4
EMOTION_TEXT_INDEX = 14
EMOTION_RANDOM_INDEX = 15


class VerificationError(RuntimeError):
    """Raised when the service does not satisfy the adapter contract."""


def request(
    url: str,
    *,
    data: bytes | None = None,
    headers: dict[str, str] | None = None,
    timeout: int = 120,
):
    return urllib.request.urlopen(
        urllib.request.Request(url, data=data, headers=headers or {}), timeout=timeout
    )


def get_json(url: str):
    with request(url) as response:
        return json.loads(response.read().decode("utf-8"))


def describe_wav(payload: bytes) -> str:
    if len(payload) < 44 or payload[0:4] != b"RIFF" or payload[8:12] != b"WAVE":
        return f"{len(payload)} bytes (not a RIFF/WAVE header)"
    offset = 12
    sample_rate = byte_rate = 0
    data_size = 0
    while offset + 8 <= len(payload):
        chunk_id = payload[offset : offset + 4]
        chunk_size = struct.unpack_from("<I", payload, offset + 4)[0]
        if chunk_id == b"fmt " and chunk_size >= 16:
            channels = struct.unpack_from("<H", payload, offset + 10)[0]
            sample_rate, byte_rate = struct.unpack_from("<II", payload, offset + 12)
        elif chunk_id == b"data":
            data_size = chunk_size
            break
        offset += 8 + chunk_size + (chunk_size % 2)
    if byte_rate:
        seconds = data_size / byte_rate if data_size else 0.0
        return (
            f"{len(payload)} bytes, {sample_rate} Hz, {channels} ch, "
            f"{data_size} bytes of audio (~{seconds:.2f}s)"
        )
    return f"{len(payload)} bytes (no fmt chunk found)"


def check_config(base_url: str):
    config = get_json(f"{base_url}/config")
    dependencies = [
        entry
        for entry in config.get("dependencies", [])
        if entry.get("api_name") == "gen_single"
    ]
    if len(dependencies) != 1:
        raise VerificationError(
            f"expected exactly one gen_single api, found {len(dependencies)}; "
            "the WebUI version does not match the one FgoPet was written against"
        )

    inputs = dependencies[0].get("inputs", [])
    if len(inputs) != EXPECTED_INPUTS:
        raise VerificationError(
            f"gen_single exposes {len(inputs)} inputs, expected {EXPECTED_INPUTS}"
        )

    components = {component["id"]: component for component in config.get("components", [])}
    for index, expected in enumerate(EXPECTED_TYPES):
        actual = components[inputs[index]].get("type")
        if actual != expected:
            raise VerificationError(
                f"input {index} is '{actual}', expected '{expected}'"
            )

    choices = components[inputs[0]].get("props", {}).get("choices") or []
    if not choices:
        raise VerificationError("the emotion control radio exposes no choices")
    first = choices[0]
    resolved = first[1] if isinstance(first, list) and len(first) > 1 else first
    label = first[0] if isinstance(first, list) and len(first) > 0 else first
    if resolved != 0:
        raise VerificationError(
            f"first emotion choice resolves to {resolved!r}, expected the speaker-reference method 0"
        )

    return config, inputs, components, label, resolved


def build_payload(inputs, components, uploaded_path: str, text: str):
    data = []
    for component_id in inputs:
        props = components[component_id].get("props", {})
        data.append(props["value"] if "value" in props else None)

    choices = components[inputs[0]]["props"]["choices"]
    first = choices[0]
    data[0] = first[1] if isinstance(first, list) and len(first) > 1 else first
    data[PROMPT_INDEX] = {"path": uploaded_path, "meta": {"_type": "gradio.FileData"}}
    data[TEXT_INDEX] = text
    data[EMOTION_REFERENCE_INDEX] = None
    data[EMOTION_TEXT_INDEX] = ""
    data[EMOTION_RANDOM_INDEX] = False
    return data


def upload_reference(base_url: str, path: str) -> str:
    with open(path, "rb") as handle:
        payload = handle.read()
    if not (payload[0:4] == b"RIFF" and payload[8:12] == b"WAVE"):
        raise VerificationError(f"{path} is not a WAV file")
    if len(payload) > 20 * 1024 * 1024:
        raise VerificationError(f"{path} is larger than the 20 MB limit")

    boundary = uuid.uuid4().hex
    filename = "reference.wav"
    body = b"".join(
        [
            f"--{boundary}\r\n".encode(),
            f'Content-Disposition: form-data; name="files"; filename="{filename}"\r\n'.encode(),
            b"Content-Type: audio/wav\r\n\r\n",
            payload,
            f"\r\n--{boundary}--\r\n".encode(),
        ]
    )
    with request(
        f"{base_url}/gradio_api/upload",
        data=body,
        headers={"Content-Type": f"multipart/form-data; boundary={boundary}"},
    ) as response:
        uploaded = json.loads(response.read().decode("utf-8"))
    if not uploaded or not uploaded[0]:
        raise VerificationError("the upload endpoint returned no path")
    return uploaded[0]


def run_generation(base_url: str, data, timeout: int) -> tuple[bytes, dict]:
    started = time.monotonic()
    body = json.dumps({"data": data}).encode("utf-8")
    with request(
        f"{base_url}/gradio_api/call/gen_single",
        data=body,
        headers={"Content-Type": "application/json"},
    ) as response:
        event_id = json.loads(response.read().decode("utf-8"))["event_id"]

    first_event_at = None
    completed_at = None
    result_path = None
    event_type = ""

    with request(f"{base_url}/gradio_api/call/gen_single/{event_id}", timeout=timeout) as response:
        for raw in response:
            line = raw.decode("utf-8").rstrip("\n")
            if line.startswith("event:"):
                event_type = line[6:].strip()
                continue
            if not line.startswith("data:"):
                continue
            if first_event_at is None:
                first_event_at = time.monotonic() - started
            if event_type == "error":
                raise VerificationError(f"inference failed: {line[5:].strip()[:400]}")
            if event_type != "complete":
                continue
            completed_at = time.monotonic() - started
            payload = json.loads(line[5:])
            file_info = payload[0]
            if isinstance(file_info, dict) and "value" in file_info:
                file_info = file_info["value"]
            result_path = file_info.get("path")
            break

    if not result_path:
        raise VerificationError("the SSE stream ended without a complete event")

    with request(f"{base_url}/gradio_api/file=" + urllib.parse.quote(result_path, safe="")) as response:
        audio = response.read()

    timings = {
        "first_event_s": round(first_event_at or 0.0, 2),
        "inference_s": round(completed_at or 0.0, 2),
        "total_s": round(time.monotonic() - started, 2),
    }
    return audio, timings


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--base-url", default="http://127.0.0.1:7860")
    parser.add_argument("--reference", required=True, help="WAV file used as the voice reference")
    parser.add_argument("--text", default="今天也要按自己的节奏来。")
    parser.add_argument("--out", default="indextts-verify-output.wav")
    parser.add_argument("--timeout", type=int, default=600)
    args = parser.parse_args()

    base_url = args.base_url.rstrip("/")
    print(f"Base URL : {base_url}")
    print(f"Reference: {args.reference}")
    print(f"Text     : {args.text}")
    print()

    try:
        print("[1/4] checking /config ...")
        _, inputs, components, label, resolved = check_config(base_url)
        print(f"      gen_single inputs = {len(inputs)} (expected {EXPECTED_INPUTS})  OK")
        print(f"      first 6 types     = {', '.join(EXPECTED_TYPES)}  OK")
        print(f"      emotion method 0  = {label!r} -> {resolved!r}  OK")

        print("[2/4] uploading the reference WAV ...")
        started = time.monotonic()
        uploaded_path = upload_reference(base_url, args.reference)
        upload_s = time.monotonic() - started
        print(f"      uploaded as {uploaded_path} in {upload_s:.2f}s")

        print("[3/4] running gen_single ...")
        data = build_payload(inputs, components, uploaded_path, args.text)
        audio, timings = run_generation(base_url, data, args.timeout)
        print(f"      first event {timings['first_event_s']}s, complete {timings['inference_s']}s")

        print("[4/4] downloading the result ...")
        with open(args.out, "wb") as handle:
            handle.write(audio)
        print(f"      saved {args.out}: {describe_wav(audio)}")
    except (VerificationError, urllib.error.URLError, OSError) as error:
        print()
        print(f"FAILED: {type(error).__name__}: {error}")
        return 1

    print()
    print("RESULT: the service satisfies the FgoPet IndexTTS adapter contract.")
    print(f"        upload {upload_s:.2f}s | first event {timings['first_event_s']}s | "
          f"complete {timings['inference_s']}s | total {upload_s + timings['total_s']:.2f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
