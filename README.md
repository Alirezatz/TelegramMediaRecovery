# Telegram Media Recovery

Recover media cached by **Telegram Desktop** and **[64Gram](https://github.com/TDesktop-x64/tdesktop)** from a
`tdata` folder — after reinstalling Windows, after clearing the cache, or because the app no longer shows
something you know you once received.

The tool only ever *reads* `tdata`. Nothing inside it is created, renamed or deleted. There is no network
access anywhere in the code.

```
  tdata/user_data/cache         -> 82 484 images, 1 230 animations, 66 audio files
  tdata/user_data/media_cache   -> 934 streamed videos (9.15 GB), reassembled slice by slice
```

---

## Contents

- [Why the cache is worth reading](#why-the-cache-is-worth-reading)
- [Screenshots](#screenshots)
- [Getting started](#getting-started)
  - [Desktop app](#desktop-app)
  - [Command line](#command-line)
- [Output layout](#output-layout)
- [What it understands](#what-it-understands)
- [Options](#options)
- [How it works](#how-it-works)
- [Project layout](#project-layout)
- [Building and testing](#building-and-testing)
- [Troubleshooting](#troubleshooting)
- [Legal](#legal)

---

## Why the cache is worth reading

Telegram Desktop does not keep a permanent download folder. It keeps a **cache database** next to the app
data and evicts it under a size limit, and it prunes by age. When you clear the cache, uninstall, or reinstall
Windows, everything you ever viewed goes away — even though the bytes were on your disk the whole time.

`tdata` keeps that cache in two places per account:

| Folder | Contents |
|---|---|
| `tdata/user_data#N/cache` | Self-contained values: photos, GIFs, Lottie stickers, small videos, thumbnails |
| `tdata/user_data#N/media_cache` | Streamed video and audio, split into 8 MiB slices across many entries |

Both are encrypted and indexed by a small append-only log. This project reads that format, verifies every
value cryptographically, and writes the media out.

---

## Screenshots

> Add your own screenshots under `docs/images/` when you open a pull request.

| Main window | HTML report |
|---|---|
| Settings on the left, activity log on the right, cancellable at any point | `index.html` — a self-contained offline gallery |

---

## Getting started

Requirements: **Windows 10/11** and the **.NET 10 SDK** to build from source. The desktop app is WPF;
the command line tool and the library run anywhere .NET 10 runs.

### Desktop app

```powershell
git clone https://github.com/<you>/TelegramMediaRecovery
cd TelegramMediaRecovery
dotnet run --project src/TelegramMediaRecovery.Desktop
```

1. Pick your `tdata` folder (the one containing `key_datas`).
2. Enter the local passcode if you set one — otherwise leave it empty.
3. Choose an output folder.
4. Press **Start recovery**.

**Inspect folder** prints what each database contains (live entry counts, record counts, whether timestamps
are present) without writing anything, which is the fastest way to check that the folder is readable.

### Command line

```powershell
dotnet run --project src/TelegramMediaRecovery.Cli -- --tdata "D:\Telegram Desktop\tdata" --out "D:\recovered" --html
```

Or publish a single self-contained executable:

```powershell
dotnet publish src/TelegramMediaRecovery.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true
```

```
tmr --tdata <path> [options]

  --tdata <path>        tdata folder (default: ./tdata when it holds key_datas)
  --passcode <text>     local passcode (default: empty)
  --out <dir>           output folder (default: ./recovered)
  --account <name>      only this account; repeatable (user_data, user_data#2, ...)
  --db <name>           cache | media_cache; repeatable
  --kind <list>         image,animation,video,audio,document
  --min-size <size>     skip smaller values, e.g. 4KB, 512K, 1MB
  --max-size <size>     skip larger values
  --min-coverage <0..1> minimum cached share of a streamed file (default 0.05)
  --require-header      only write streamed files whose header slice survived
  --keep-unknown        also write unrecognised content as .bin
  --no-orphans          skip the unreferenced-file scan
  --no-hash             do not compute SHA-256 (faster, no de-duplication)
  --no-partial-suffix   do not mark incomplete files with .partial
  --dated-names         prefix file names with the estimated date
  --live-version-only   ignore older cache version folders
  --limit <n>           stop after n files per database
  --threads <n>         worker threads (default: one per CPU)
  --json                also write manifest.json
  --html                also write index.html
  --list                print an inventory and exit
  -h, --help            usage
```

The passcode can also come from the `TMR_PASSCODE` environment variable, which is the convenient option for
scripts and CI. Set `TMR_VERBOSE=1` for per-file logging.

---

## Output layout

```
recovered/
├─ manifest.csv                 one row per recovered file, with the exact cache key it came from
├─ manifest.json                the same data, diffable and scriptable
├─ skipped.csv                  every value that was considered but not written, with the reason
├─ index.html                   offline gallery (with --html / in the app)
└─ <account>/<database>/<kind>/
   ├─ user_data/cache/images/000001.jpg
   ├─ user_data/cache/animations/000001.tgs
   └─ user_data/media_cache/videos/000001.mp4
```

Streams that were only partially cached are written with a `.partial` infix (`000001.mp4.partial`), because
their gaps are filled with zeros exactly the way the player would see them.

`manifest.csv` columns:

```
account, database, source, key_high, key_low, place, version, stored_size,
size, coverage, kind, extension, mime, sha256, fragments, partial, date, output
```

`source` is `binlog` for values found through the index, or `orphan` for files recovered by the
unreferenced-file scan. `key_high`/`key_low`/`place` are enough to find the exact cache entry again.

---

## What it understands

| | |
|---|---|
| **Layouts** | Modern multi-account (`key_datas`) and the legacy pre-multi-account (`<hash>/maps`) key file |
| **Local key sizes** | 256-byte and 64-byte (`EncryptionKey::kSize` and `kSize_v2`) |
| **Databases** | `cache` and `media_cache` for every `user_data#N` account |
| **Binlog records** | `Store`, `StoreWithTime`, `MultiStore`, `MultiRemove`, `MultiAccess` |
| **Cache versions** | the live `version` folder plus any older folders that still hold a binlog |
| **Streamed media** | contiguous slices, complex part maps, and "good headers" with the first slice folded in |
| **Formats** | JPEG, PNG, GIF, WebP, BMP, TIFF, ICO, AVIF, HEIC, SVG, MP4/MOV/3GP/M4V, Matroska, WebM, MPEG, MP3, AAC, OGG, Opus, FLAC, WAV, Lottie `.tgs`, gzip, PDF, ZIP, 7z, RAR, TTF/OTF/WOFF/WOFF2 |
| **Also** | files that no binlog entry references any more (the orphan scan) |

---

## Options worth knowing

**Recover files the index forgot about.** The client removes a place file lazily and the log record
immediately, and can be killed in between, so an account accumulates readable files that no log mentions.
The orphan scan (on by default) finds them; the TDEF header hash proves each one is genuinely yours.

**Cut down on mostly-missing videos.** A video whose body was evicted comes back as a multi-gigabyte file
of zeros. `--min-coverage` (or the slider in the app) sets the share that has to be cached before a stream
is written at all; `--require-header` additionally demands the slice that holds the container index.

**Do it faster.** `--no-hash` skips SHA-256 and de-duplication, which is the single biggest cost on a large
account. `--no-orphans` skips the directory walk.

**Run it while Telegram is open.** A live client locks exactly the first four bytes of every database file
with `LockFile` (see `Storage::FileLock`, `kLockOffset = 0`, `kLockLimit = 4`). Those bytes are only the
`TDEF` magic, so the reader skips the magic check and reads from offset 4 — everything else, including the
whole binlog, stays readable. Closing Telegram Desktop first still gives a more consistent snapshot.

---

## How it works

```
passcode ──SHA512(salt|p|salt)+PBKDF2-HMAC-SHA512×100000──▶ passcode key
passcode key ──AES-256-IGE, SHA1 msgKey check──▶ 256-byte local key
local key ──AES-256-CTR, per-file 64-byte salt──▶ TDEF payloads
                                             ├─ binlog  → key → place → size → XXH32
                                             └─ place files → media bytes
```

Every value is verified against the XXH32 checksum the binlog stores before it is used, and every TDEF file
is verified against the SHA256 in its header before it is opened. A wrong passcode, a wrong key, a stale
version folder or a truncated file all end up as a reported skip, never as a file full of noise.

Full byte-level documentation, including the exact struct layouts and the C++ source each one comes from,
is in [`docs/binary-formats.md`](docs/binary-formats.md). The architecture is described in
[`docs/architecture.md`](docs/architecture.md).

---

## Project layout

```
src/
  TelegramMediaRecovery.Core/        net10.0     — the whole format reader, no UI dependencies
    Crypto/                                        key derivation, IGE, CTR, XXH32
    Containers/                                     TDF$ and TDEF containers
    Cache/                                          binlog framing and database discovery
    Media/                                          sniffing, part maps, stream reassembly
    Extraction/                                     the engine and its options
    Reporting/                                      CSV / JSON manifests and the HTML gallery
  TelegramMediaRecovery.Desktop/     net10.0-windows — WPF app (MVVM, no MVVM framework)
  TelegramMediaRecovery.Cli/         net10.0     — headless front end
tests/
  TelegramMediaRecovery.Core.Tests/  net10.0     — 73 tests over the byte layouts and the crypto
```

`Core` has no dependency on the WPF project, which is what makes the format reader testable and reusable.

---

## Building and testing

```powershell
dotnet build                 # whole solution
dotnet test                  # unit + end-to-end tests
dotnet format --verify-no-changes
```

The tests cover the parts that are easy to get silently wrong:

- XXH32 against the reference vectors
- AES-256-CTR against the NIST SP 800-38A counter-mode vector
- the local blob (IGE + msgkey) round trip and its wrong-passcode failure
- the binlog record layout: the 24-byte `EntrySize`, the 64/80-byte record sizes, every record type, the
  `MultiRemove`/`MultiAccess` semantics, and where the reader must stop
- place-id rendering, including the low-nibble-first twist
- complex part maps and the "good header" merge
- an end-to-end run over a synthetic `tdata` folder the test builds itself

---

## Troubleshooting

**"No readable key file was found."** The folder does not contain `key_datas` or a `<16-hex>/maps` file. Point
the tool at the `tdata` folder itself, not at `Telegram Desktop`.

**"Wrong passcode."** The passcode is the *local* one from Telegram Desktop's settings, not the account
two-step password. It is easy to forget; the app cannot tell an empty passcode from a wrong one, but it does
tell you which key file failed.

**"Unsupported cache binlog format N."** A newer Telegram Desktop introduced a container format this reader
does not know. The version number is printed; add support by extending `CacheRecordLayout`.

**A file came out as `.bin`.** The cache stores raw bytes with no metadata, so a value with no recognised
signature is genuinely unidentifiable. `--keep-unknown` keeps such values instead of skipping them, and
`skipped.csv` records the first bytes of everything that was dropped.

**Everything is `.partial`.** The cache only holds slices that were actually downloaded. Raise
`--min-coverage` to skip them, or lower it to keep whatever is there.

---

## Legal

This project reads files that already exist on **your** disk, in a folder that belongs to **you**, to recover
**your own** data. It contains no Telegram code. The file-format details it relies on were derived from the
publicly documented and published structure of the storage layer; each source file in this repository names
the exact upstream file and function it corresponds to so the derivation can be checked.

This project is not affiliated with, endorsed by, or connected to Telegram FZ-LLC or 64Gram.

Licensed under the MIT licence — see [LICENSE](LICENSE).
