# Architecture

## Entry points

`TelegramMediaRecovery.Core` has no UI dependencies. Its two public workflows share the same key,
container, index and media readers:

| Front end | Core interface | Workflow |
|---|---|---|
| Desktop (WPF, MVVM) | `Browsing.RecoverySession` | choose source, scan, browse and preview, export checked items |
| CLI | `Extraction.ExtractionEngine` | discover and extract in one run, with command-line options |

The desktop does not run a full extraction to populate its browser. A scan reads and validates cache
values, identifies formats and retains only `RecoveryItem` metadata and source references. It creates
neither an output directory nor a decrypted media library. The browser currently uses indexed entries;
the CLI's orphan scan is a separate capability.

## Shared readers

| Module | Responsibility |
|---|---|
| `Crypto.LocalKeyLoader` | local passcode and `key_datas` / legacy key loading |
| `Containers.TdfContainer`, `TdefFile` | bookkeeping and encrypted cache containers |
| `Cache.CacheDatabaseLocator`, `CacheVersionStore` | account, database and generation discovery |
| `Cache.CacheBinlogReader` | sequential record replay; compact and wide-entry layout detection |
| `Cache.CacheIndexReader` | shared generation merge, warnings and current-index precedence |
| `Extraction.CacheValueReader`, `CacheValueLease` | version fallback, pooled decryption and XXH32 verification |
| `Media.MediaSniffer` | media signature to format and category |
| `Media.StreamedFilePlanner`, `PartMap` | group one file's slices and resolve part extents |
| `Media.StreamedFileWriter` | assemble known parts without allocating the entire output file |
| `Reporting.RecoveryReportWriter` | shared CSV / JSON manifests, skipped entries and local HTML gallery |

Cache checksum verification proves the cached bytes match their index entry. It does not identify a
source message, establish that saving was restricted, or prove that an entire original stream is cached.
The desktop presents those limits explicitly. Cache dates are estimates of cache access, not message dates.

`Media.CacheUsageClassifier` derives a separate role from recognized Telegram key namespaces and sticker
tags. `RecoveryItem.Usage` distinguishes profile pictures, thumbnails/covers, stickers and media bodies
without decoding image contents or retaining extra media buffers. Unknown keys remain unknown. The UI
can exclude profile pictures and thumbnails while preserving unknown media; CSV and JSON manifests keep
the role for exported files. This is independent of the file's container, completeness and saving restrictions.

## Recovery session

`RecoverySession` owns the source key, one scan's candidate identities and temporary preview leases.
The caller disposes the session when changing source or closing the browser. Items from a previous
scan or another session cannot be previewed or exported through it.

- `Scan` merges indexed entries from discovered generations and verifies their available values. It
  returns candidates, unreadable/unrecognized counts and index warnings. Per-value failures do not
  stop the scan; missing keys or databases are source-level errors. A bounded four-worker scan fills
  metadata slots and publishes candidates in deterministic source order.
- `ReadPreviewBytes` reads a selected bitmap or sticker into memory, limited to 64 MiB. The desktop
  decodes JPEG, PNG, WebP and other supported bitmaps with SkiaSharp and animates TGS with Skottie.
- `CreatePreview` assembles just the requested video or audio into a disposable temporary file.
  WPF's `MediaElement` plays formats supported by the installed Windows codecs. The user can also
  open the temporary file in an external player.
- `Export` processes the checked candidate identities only. It creates a unique run directory outside
  the source, stages each file before moving it into place, and never overwrites an existing output.
  Export does not deduplicate away separately selected identities. It rechecks source values because
  a live Telegram client can evict them after scanning. Size, coverage, fragment count and partial
  filenames use the actual export result. A previously verified trailing extent is retained when its
  source slice disappears, so tail eviction also produces a visible gap.

Cancellation is checked between records/groups and during stream writing. Cancelling export preserves
completed files and produces reports for them; failed or cancelled staging files are removed. Temporary
previews are released when the preview changes or the session closes. No recovery operation writes to
the source directory.

## Streaming identity and assembly

`StorageFileLocation::bigFileBaseCacheKey()` reserves the bottom nine bits of `key.low` for slice
numbers, and `Reader::CacheHelper::key(n)` adds the slice number. The complete group key is
`(key.high, key.low & ~0x1FF)`. Grouping by `high` alone merges unrelated documents. Inferring a header
from the lowest surviving entry shifts a stream when its actual header was evicted.

For each file, slice 0 contains header data; body slice `n` starts at `(n - 1) * 8 MiB`. A value is
either contiguous bytes or an explicit part map. Header map offsets are file-absolute; body map offsets
are relative to their slice. A good header can also contain the first body's map.

The planner retains source entries, byte offsets and a small format prefix, without retaining decrypted
media buffers. An extent map normalizes overlaps while fragments arrive; an earlier part offset wins,
with the longer part winning at an equal offset. The writer consumes newly winning ranges before their
source lease is released. Export reads each source value once, then hashes the completed output, including
zero-filled gaps. Planning and writing therefore do not require all of a video's cached bytes in memory.

`CacheValueReader.Open` lends checksum-verified memory until its lease is disposed. Its dedicated pool
keeps at most two returned arrays per size bucket, up to 16 MiB per array, and clears returned buffers.
AES counter blocks are encrypted in bounded batches; existing NIST vectors and independent counter
encryption tests verify the result. `AllParts()` / `SliceFragment.Map` remain explicit materialization
accessors for library callers; production scan and export use metadata ranges and short-lived leases.

The writer sets the output's known extent; missing ranges read as zeros. Part offsets use `long`
so assembly can address files beyond 2 GiB. `FileSize` is the end of the last known part, and `Coverage`
is the covered share of that extent. Neither describes an unknown trailing portion of the original file.

## Desktop state and threading

`MainViewModel` connects catalog, checked items and operation commands on the WPF dispatcher.
`MediaFilter` parses display choices into typed criteria and evaluates cached row search text. Sorting
is rebuilt only when its choice changes, with a single view refresh per filter update.
Scan and export run in background tasks with explicit dispatcher progress updates. Async
commands reject re-entry; source changes and another operation are disabled while an operation runs.
Closing cancels and drains operations, preview rendering and thumbnail work before disposing the session.
The final close is queued on the dispatcher: cleanup may complete synchronously, so calling `Close()`
inside the initial `Closing` event would re-enter WPF's window lifecycle. Scan progress uses the total
group count across all database indexes; the dispatcher discards stale or out-of-order progress updates.

The media grid virtualizes rows. `ThumbnailLoader` uses two workers, a visible-first queue limited to
64 pending requests and a 180-image LRU. Recycled or unloaded rows release their requests; displaced
waiters also complete. Rows hold weak thumbnail references, and decoded output is capped to the requested
thumbnail dimensions even when a codec cannot downsample directly.

`PreviewController` owns cancellation, temporary media leases and animation lifetime, with one active
preview worker so rapid selection cannot start unbounded native decodes. Images transfer
raw premultiplied pixels into frozen WPF sources. TGS runs on a background renderer that reuses its Skia
bitmap/canvas and copies into one `WriteableBitmap`; it performs no per-frame PNG encoding or decoding.
The UI thread only receives current frames. Filter changes preserve checked items and show hidden checks.

The CLI retains its parallel per-database extraction path, `OutputNamer` and optional content-hash
deduplication. Its extraction options do not silently alter the desktop's checked-item export.

## Validation and extension

Core tests use hand-built synthetic containers and binlogs, without user data. They cover crypto,
both record layouts, index replay, part maps, streaming identity, scan/export behavior, cancellation,
source protection and preview cleanup. Regression cases cover middle/tail eviction after scan,
overlap precedence during single-pass export, bounded retained planning memory and safe cancellation
cleanup. Desktop tests use an STA dispatcher to exercise filtering, selection, image/sticker preview,
rapid switching, bounded scrolling queues, export and rendered WPF bindings. The documentation screenshot also
uses generated media rather than private cache contents.

To add a format, add its signature and category to `MediaSniffer` and a synthetic-header test. If it
needs a new preview decoder, keep that dependency in the desktop project. To add an index layout,
record its upstream struct and byte offsets in [binary-formats.md](binary-formats.md), then construct
byte-exact records in `BinlogBuilder` and test the parser's framing and stop conditions.
