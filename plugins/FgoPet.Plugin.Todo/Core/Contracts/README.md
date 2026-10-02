# Todo conversation contract boundary

Todo proposal parsing, pending drafts, confirmation state, and idempotent creation belong to the Todo plugin. Dialogue owns model calls and conversation orchestration, not Todo persistence.

`ITodoConversationPort` is the composition boundary for proposal reading and the shared draft workflow. Read-only interfaces may parse proposals and expose sanitized state; they cannot create Todo items or dispatch Agent tasks. Conversation and confirmation UI must share the same draft workflow instance for its lifetime.

Successful parsing is not user authorization. Tool and text proposals first create drafts; writes require explicit confirmation with role/session scope, draft ID, version, and idempotency key. Ambiguous agreement, quoted text, negation, questions, and edit requests are not confirmation. Stale confirmation must not write, and retries must not duplicate records. The parser rejects execution fields, unknown fields, and unsafe content.

These contracts belong to the Todo plugin's published contracts and must be compiled by their owning project only. Preserve public namespaces and signatures; consumers should use published contracts rather than implementation references. Retired proposal/archive cards and view models are not part of the current UI.

Run the relevant Todo and Dialogue test projects under `tests/`, plus the Release solution build and architecture gate for boundary changes. Preserve explicit confirmation, idempotency, stale-version rejection, and dangerous-field rejection coverage.
