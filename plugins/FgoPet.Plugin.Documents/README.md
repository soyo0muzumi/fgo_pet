# Documents

Contributes `document.read` for bounded TXT, Markdown, DOCX and PDF text extraction. It uses the host's shared workspace guard and source-version cursor; it never executes document content or supplies another path authority.

Input is limited to 8 MiB. DOCX parsing bounds entry count, actual decompressed bytes, XML and output size, validates ZIP CRC, prohibits DTDs and external relationships, and enforces a deadline. PDF extraction runs in the packaged `FgoPet.DocumentParser.exe` helper through the bounded process port, with time, memory, process and output limits. Encrypted, corrupt and textless PDFs return safe errors; OCR is outside this module.

The helper and its PdfPig dependency and license are copied into build and publish output. Host composition supplies the trusted helper executable path. Parser stdin contains bounded document bytes, rather than a model-controlled file path.

Validate with the Documents and Docx filters in `tests/FgoPet.Capability.Tests`, then build and publish this project in Release with warnings treated as errors. These checks do not establish product UI acceptance.
