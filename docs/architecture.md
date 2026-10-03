# Architecture

## Layers

```
┌──────────────────────────────────────────────────────────────────┐
│  TelegramMediaRecovery.Desktop   WPF, MVVM, no MVVM framework     │
│  TelegramMediaRecovery.Cli       console front end                │
└──────────────────────────────┬───────────────────────────────────┘
                               │
┌──────────────────────────────▼───────────────────────────────────┐
│  Extraction.ExtractionEngine                                     │
│    - discovers databases, parses binlogs sequentially              │
│    - hands the work to DatabaseExtractor (one per database)       │
│    - writes the manifest and the HTML report                      │
├──────────────────────────────────────────────────────────────────┤
│  Extraction.CacheValueReader      value → bytes, checksum-verified │
│  Extraction.DatabaseExtractor     cache DB and media_cache DB      │
│  Extraction.OutputNamer           paths, sequencing, categories    │
│  Extraction.OutputDeduplicator    SHA-256 de-duplication           │
├──────────────────────────────┬───────────────────────────────────┤
│  Media.StreamedFilePlanner      slice chains → part maps           │
│  Media.StreamedFileWriter       sparse, single-pass assembly       │
│  Media.PartMap                  complex serialization              │
│  Media.MediaSniffer             magic bytes → format               │
├──────────────────────────────┴───────────────────────────────────┤
│  Cache.CacheBinlogReader         record framing → live entries     │
│  Cache.CacheRecordLayout        the exact byte offsets             │
│  Cache.CacheDatabaseLocator     accounts and databases             │
│  Cache.CacheVersionStore        the `version` marker and folders   │
├──────────────────────────────────────────────────────────────────┤
│  Containers.TdefFile / TdfContainer   encrypted file containers    │
│  Containers.QtDataStreamReader        Qt serialisation primitives  │
│  Crypto.LocalKeyLoader / LocalKey     passcode → local key          │
│  Crypto.AesCtrCipher / AesIge         the two ciphers               │
│  Crypto.XxHash32                      the value checksum            │
└──────────────────────────────────────────────────────────────────┘
```

`TelegramMediaRecovery.Core` has no reference to the WPF project. Everything above the dashed line is
reusable from a console app, a service, or a test, which is why the format reader can be tested on its own.

## Why the pipeline is shaped this way

**Binlog parsing is sequential, extraction is parallel.** Reading a binlog is a single large sequential
read plus a linear scan — 10.8 MB and 106 099 records parse in about 250 ms. Doing that once per database
up front, then fanning the per-file work out over a thread pool, keeps the parallel section embarrassingly
parallel with no synchronisation beyond the de-duplication set.

**Nothing is trusted before it is verified.** `CacheValueReader` tries each known version folder, decrypts,
and compares XXH32. A hit is proof; anything else is a skip with a reason. The same is true of the TDEF
header hash, which is why the orphan scan can accept files that no index mentions at all.

**Streamed files are never fully in memory.** A 4 GB video must not become a 4 GB `byte[]`. The planner
decides what each cached value contributes, and the writer creates the file at its final length and writes
parts at their offsets. Gaps stay as sparse zeros, which is exactly what the player would see.

**Failures are data, not exceptions.** One corrupt file must not end a 20-minute run, so per-file problems
land in `ExtractionCounters.Skipped` with a `SkipReason`, and the summary reports them per reason. Only
folder-level problems (no key file, no databases at all) throw.

## Data flow for one streamed file

```
binlog entries sharing key.high
        │  sorted by key.low
        ▼
base = lowest low                          ← slice 0, the header
for each subsequent low:  sliceNumber = low - base.low - 1
        │
        ├─ value starts with a media signature, or length is part aligned
        │     → contiguous: parts at sliceNumber-1 times kInSlice
        │
        └─ otherwise try ParseComplexCachedMap
              ├─ header: parts are file-absolute, and a second map may follow
              │          (the "good header" case, merged at offset 0)
              └─ slice: parts are slice-relative, shifted by sliceNumber-1 times kInSlice
        ▼
StreamedFilePlan  (part list, reconstructed size, cached bytes, coverage)
        ▼
sparse file at its final length, parts written in ascending offset order
```

## Threading

- One `DatabaseExtractor` per database, `Parallel.ForEach` over its entries (or its key groups for
  `media_cache`).
- Shared mutable state is limited to a `ConcurrentDictionary` of content hashes and a `Lock` around the
  per-kind sequence counter, so no result ever depends on which thread finished first.
- Progress is reported through `IProgress<ExtractionProgress>`, which the WPF app marshals to the UI
  dispatcher; `ExtractionOptions.CancellationToken` is checked per item and per group.

## Adding a format

1. Add the magic and its category to `MediaSniffer`, and a test with a real header in `FormatTests`.
2. Write the byte layouts in `CacheRecordLayout` (or `StreamingLayout`) with the C++ struct they come from
   in the doc comment.
3. Add a synthetic record to `BinlogBuilder` and a test that the parser frames it.

If a test can be written against a hand-built byte array, it should be: no fixture files, no user data, and
a reviewer can see the expected layout right in the test.

## Testing strategy

| Layer | How it is tested |
|---|---|
| XxHash32 | reference vectors |
| AES-CTR | NIST SP 800-38A counter-mode vector |
| IGE and local blobs | round trip plus a wrong-key failure |
| Key derivation | determinism and passcode sensitivity, both variants |
| Binlog framing | byte-exact synthetic records built by `BinlogBuilder` |
| Place ids | the low-nibble-first rendering |
| Part maps / good headers | synthetic maps, including the rejection paths |
| Sniffing | real magic bytes per container |
| Whole engine | `EndToEndTests` builds a `tdata` folder with the *write* side of every format and checks the bytes come back |
