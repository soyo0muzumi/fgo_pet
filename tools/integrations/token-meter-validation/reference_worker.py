"""Non-production pinned local reference counter. Requests exist only in anonymous stdio pipes."""
import copy
import hashlib
import importlib.metadata
import importlib.util
import json
import os
from pathlib import Path
import socket
import sys
import time

MANIFEST = "13D810E32F9EB1A5C7010E66926E52A54683F921B22E286A80141D64AFF072C7"
MAX_LINE = 1024 * 1024
network_attempts = 0
os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1", TOKENIZERS_PARALLELISM="false")

def deny_network(*args, **kwargs):
    global network_attempts
    network_attempts += 1
    raise RuntimeError("LOCAL_REFERENCE_NETWORK_FORBIDDEN")

socket.create_connection = socket.getaddrinfo = deny_network
socket.socket.connect = socket.socket.connect_ex = deny_network

def sha(data):
    return hashlib.sha256(data).hexdigest().upper()

class Counter:
    def __init__(self, root):
        from tokenizers import Tokenizer
        started = time.perf_counter()
        root = Path(root).resolve()
        manifest_file = root / "assets-manifest.json"
        assert sha(manifest_file.read_bytes()) == MANIFEST
        assert importlib.metadata.version("tokenizers") == "0.22.2"
        manifest = json.loads(manifest_file.read_text(encoding="utf-8"))
        hashes = {(item["label"], item["file"]): item["sha256"].upper() for item in manifest["files"]}
        def checked(label, name):
            path = (root / "assets" / label / name).resolve()
            assert path.is_relative_to(root) and sha(path.read_bytes()) == hashes[label, name]
            return path
        for label in ("base", "1017"):
            for name in ("LICENSE", "tokenizer.json", "tokenizer_config.json", "encoding/encoding.py"):
                checked(label, name)
            assert json.loads(checked(label, "tokenizer_config.json").read_text(encoding="utf-8"))["add_bos_token"] is False
        assert all(hashes["base", name] == hashes["1017", name] for name in ("tokenizer.json", "tokenizer_config.json", "encoding/encoding.py"))
        self.tokenizer = Tokenizer.from_file(str(checked("base", "tokenizer.json")))
        spec = importlib.util.spec_from_file_location("pinned_reference_encoder", checked("base", "encoding/encoding.py"))
        self.encoder = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.encoder)
        for text in (self.encoder.bos_token, self.encoder.eos_token, self.encoder.USER_SP_TOKEN,
                     self.encoder.ASSISTANT_SP_TOKEN, self.encoder.SYSTEM_SP_TOKEN,
                     self.encoder.thinking_start_token, self.encoder.thinking_end_token, self.encoder.dsml_token):
            assert self.tokenizer.encode(text, add_special_tokens=False).ids == [self.tokenizer.token_to_id(text)]
        golden_passed = 0
        for label in ("base", "1017"):
            for case_id in range(1, 6):
                case = self.encoder.load_cases(str(checked(label, f"encoding/tests/test_input_{case_id}.json")))[0]
                rendered, _ = self.encoder.encode_case(case, thinking_mode="chat")
                assert rendered == checked(label, f"encoding/tests/test_output_{case_id}.txt").read_text(encoding="utf-8")
                golden_passed += 1
        self.policy_hash = sha(json.dumps({"manifest": MANIFEST, "worker": sha(Path(__file__).read_bytes()),
            "tokenizers": "0.22.2", "policy": "max_chat_thinking_50_75_100_drop_thinking_false_v1",
            "wire": "deepseek-flash_primary_text_full_input_max_tokens_2048_v1"}, sort_keys=True).encode())
        self.startup = {"ready": True, "policyHash": self.policy_hash, "assetManifestHash": MANIFEST,
            "workerHash": sha(Path(__file__).read_bytes()), "tokenizersVersion": "0.22.2", "officialGoldenPassed": golden_passed,
            "identicalPinnedVariants": True, "startupMs": (time.perf_counter() - started) * 1000,
            "providerUsageVerified": False, "providerSnapshotBound": False, "networkApisBlocked": True,
            "networkAttempts": network_attempts, "productionCompositionChanged": False}

    def count(self, request):
        started = time.perf_counter()
        raw, wire = request["input"], request["wire"]
        data, transmitted = json.loads(raw), json.loads(wire)
        assert data["model"] == "deepseek-flash"
        assert not (set(data) - {"model", "messages", "tools", "tool_choice"})
        assert {k: v for k, v in transmitted.items() if k not in {"stream", "max_tokens"}} == data
        assert transmitted["stream"] is True and type(transmitted["max_tokens"]) is int and transmitted["max_tokens"] == 2048
        messages = data["messages"]
        tools = data.get("tools") or []
        assert messages and all(m.get("role") in {"system", "user", "assistant", "tool"} for m in messages)
        for message in messages:
            assert not (set(message) - {"role", "content", "tool_calls", "tool_call_id", "reasoning_content"})
            assert message.get("content") is None or isinstance(message["content"], str)
            assert "reasoning_content" not in message or isinstance(message["reasoning_content"], str)
        assert not tools or (messages[0]["role"] == "system" and data.get("tool_choice") == "auto")
        assert all(t.get("type") == "function" and isinstance(t.get("function", {}).get("parameters"), dict) for t in tools)
        modes = []
        for mode, effort in (("chat", None), ("thinking", 50), ("thinking", 75), ("thinking", 100)):
            attached = copy.deepcopy(messages)
            if tools:
                attached[0]["tools"] = copy.deepcopy(tools)
            before = copy.deepcopy(attached)
            prompt = self.encoder.encode_messages(attached, thinking_mode=mode, reasoning_effort=effort, drop_thinking=False)
            assert attached == before
            modes.append(len(self.tokenizer.encode(prompt, add_special_tokens=False).ids))
        assert network_attempts == 0
        return {"verified": True, "inputTokens": max(modes), "inputHash": sha(raw.encode("utf-8")),
            "wireHash": sha(wire.encode("utf-8")), "policyHash": self.policy_hash, "variantCounts": modes,
            "elapsedMs": (time.perf_counter() - started) * 1000, "countKind": "LocalReferenceEstimate",
            "providerUsageVerified": False, "providerSnapshotBound": False, "networkAttempts": network_attempts}

def main():
    try:
        counter = Counter(sys.argv[1])
        print(json.dumps(counter.startup), flush=True)
    except Exception:
        print(json.dumps({"ready": False, "errorCode": "LOCAL_REFERENCE_STARTUP_UNVERIFIED"}), flush=True)
        return 2
    while True:
        line = sys.stdin.readline(MAX_LINE + 1)
        if not line:
            return 0
        if len(line) > MAX_LINE:
            print(json.dumps({"verified": False, "errorCode": "LOCAL_REFERENCE_INPUT_TOO_LARGE"}), flush=True)
            return 2
        try:
            result = counter.count(json.loads(line))
        except Exception:
            result = {"verified": False, "errorCode": "LOCAL_REFERENCE_INPUT_UNVERIFIED"}
        print(json.dumps(result), flush=True)

if __name__ == "__main__":
    raise SystemExit(main())
