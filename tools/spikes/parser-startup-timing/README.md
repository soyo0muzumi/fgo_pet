# Parser startup timing probe

Non-production diagnostic executable, never registered, packaged or installed. It generates a timing variant from the current trusted PDF helper source, uses the same PdfPig 0.1.13 package, and verifies that removing its phase markers restores the original business source exactly. Production parser source and all input/output/resource limits stay unchanged.

Build explicitly with an already installed Python supplied as `ParserProbePython`; restore only from the existing local package cache. The test-only wrapper explicitly selects this helper in its own output directory. Production helpers and prior evidence binaries are preserved.

The explicit diagnostic wrapper supplies a per-run random salt. Each process emits only hashed identity, phase, UTC/monotonic time and the preceding emission cost to stderr. The test runner retains only validated phase metadata after the bounded operation, without logging raw output. No PDF bytes, paths, text, arguments, user data or exception bodies are recorded. The salt aligns hashes with the read-only process sampler and is not persisted. Missing diagnostic configuration produces no phase records.

Phases cover earliest managed entry, stdin opening/EOF, PDF opening, text extraction and result emission. Writing these records has overhead, including first-use initialization; report that overhead and retain unmodified-helper controls. These results cannot sign H1/H2/P4 or establish a unique wait object without supporting evidence. The probe does not call a model or change timeout, concurrency, process priority or affinity.
