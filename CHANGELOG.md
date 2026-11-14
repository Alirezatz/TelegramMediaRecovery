## Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- Cache-usage filters for media bodies, profile pictures/icons, thumbnails/covers, stickers and unknown roles.
- Per-item classification explanations and cache-usage fields in CSV/JSON export manifests; unknown roles remain visible when excluding avatars and thumbnails.
- Desktop workflow: choose source, scan without exporting, filter and preview media, then export checked items.
- Virtualized media list, bounded thumbnail cache, local WebP decoding and animated Lottie sticker previews.
- On-demand audio/video preview, cancellation, export progress and separate output directories for each run.
- English desktop labels, resizable filter/preview panes, smaller-screen layout and search/cancel shortcuts.
- A reusable `RecoverySession` with scan, preview and selected-export interfaces, plus core and WPF regression tests.

### Changed

- Retain stream source ranges instead of decrypted buffers; write verified fragments in one pass with bounded value leases.
- Share index replay and report output between desktop and CLI; use four scan workers, a bounded value pool and batched AES-CTR.
- Split preview scheduling, thumbnail loading and typed filtering from the main view model; reuse raw animation surfaces, bound the visible-first thumbnail queue and avoid redundant sorting.

### Fixed

- Recalculate export coverage, fragment counts and partial filenames after cache eviction, retaining previously verified tail extents.
- Record selected-export failures in skipped reports and wait for preview work when closing.
- Defer the final window close until the current Closing event returns, including idle and repeated-close paths.
- Reset playback when switching previews, lock checked items during export, and update empty-result messages.
- Report scan progress across all databases and ignore stale progress callbacks.
- Support official Telegram 32/48-byte Store records alongside the original 64/80-byte variant.
- Stop index replay at an invalid bundled Store part, preserving earlier valid entries.
- Group streamed fragments by both halves of the base file key and retain their offsets when metadata slice zero is absent.
- Count overlapping parts once, retain longer overlapping data, and use 64-bit output offsets.
- Compare streamed content hashes for de-duplication instead of comparing only fragment layouts.
- Align CSV row fields with the header, and correct the synthetic local-key writer's logical length.

## [1.0.0] - 2026-10-03

First release.

### Added

- Local key derivation for both the modern (`key_datas`) and the legacy
  (`<hash>/maps`) layouts, with 256-byte and 64-byte keys.
- TDEF container reader that validates the per-file SHA-256 header, including
  the byte-range lock a running client takes on the first four bytes.
- Binlog reader for `Store`, `StoreWithTime`, `MultiStore`, `MultiRemove` and
  `MultiAccess`, including the 24-byte `EntrySize` field that a naive parser
  misreads as 3 bytes.
- Cache version discovery: the live `version` folder plus older generations,
  resolved by checksum so a stale folder can never inject the wrong bytes.
- Reconstruction of streamed media from `media_cache`: contiguous slices,
  complex part maps, and good headers with the first slice folded in.
- Assembly straight to disk, so a multi-gigabyte video never has to fit in
  memory.
- Content sniffing for 30+ container formats, including telling a gzipped
  Lottie `.tgs` sticker apart from a real gzip archive.
- Recovery of TDEF files that no live binlog entry references.
- SHA-256 de-duplication across accounts and databases.
- CSV and JSON manifests, a skipped-values report, and a self-contained
  offline HTML gallery.
- WPF desktop app and a headless CLI front end.
