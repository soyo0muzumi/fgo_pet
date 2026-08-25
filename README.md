# FGO Pet

FGO Pet is a Windows desktop companion that presents work, study, Codex progress, and focus sessions through an FGO servant persona.

The project is being redesigned from a clean repository. The first servant is Mash Kyrielight, with selectable ascensions and costumes, static portrait expression changes, an attached pomodoro timer, OpenAI-compatible dialogue, a local timeline, and a lightweight bond/collection system.

## Current status

The approved design and the first rendering-spike plan are available under `docs/superpowers/`.

Implementation starts with a disposable .NET 8 WPF/SkiaSharp comparison to validate transparent-edge quality at 100%, 125%, and 150% Windows scaling before the production renderer is selected.

FGO artwork and extracted Atlas assets are not stored in this repository.
