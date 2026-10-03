## Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses
[semantic versioning](https://semver.org/spec/v2.0.0.html).

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
