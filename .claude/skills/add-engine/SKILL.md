---
name: add-engine
description: Checklist for adding a new protocol or network engine adapter to BYDLO (parser, config builder, IEngine adapter, registry, tests, docs, credits).
---

# add-engine

1. Model: add the protocol to `ProtocolKind` and, for a new binary, to `EngineKind` (`src/Bydlo.Core/Models`).
2. Import: implement `IProfileParser` in `src/Bydlo.Core/Import/Parsers` and register it in `ProfileImporter`.
   Secrets go to `ImportedProfile.Secrets`, never to `Profile.Options`.
3. Config: extend `SingBoxConfigBuilder` / `XrayConfigBuilder` or add a dedicated builder.
4. Adapter: implement `IEngine` (usually via `ProcessEngineBase`) in `src/Bydlo.Engines` with an honest
   `EngineDescriptor` (platform flags must reflect what is really implemented).
5. Register it in `EngineRegistryFactory`. If BYDLO downloads it, pin version + SHA-256 in `engines/engines.lock.json`.
6. Tests in `tests/Bydlo.Tests` using `FakeProcessRunner` — never spawn the real binary.
7. Docs: `docs/COMPATIBILITY.md`, `THIRD_PARTY_NOTICES.md` (credit + license), `Credits` list in the app,
   localization strings (en + ru).
8. Run the `safe-check` skill. Real connectivity is tested manually by the maintainer only.
