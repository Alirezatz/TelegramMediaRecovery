# Binary formats

Every layout in this document was derived from the published source of Telegram Desktop and its
`lib_storage` library. Each section names the file and function it comes from so the derivation can be
checked. Offsets are byte offsets, all integers are little-endian unless stated otherwise, and struct sizes
include the padding a 64-bit MSVC/Itanium build inserts.

Reference repositories:

- `Telegram/SourceFiles/...` — [TDesktop-x64/tdesktop](https://github.com/TDesktop-x64/tdesktop) (64Gram), branch `dev`
- `Telegram/lib_storage` — [TDesktop-x64/lib_storage](https://github.com/TDesktop-x64/lib_storage), branch `master`

---

## 1. Overview

```
tdata/
├─ key_datas                     TDF$ container: passcode salt + wrapped local key + wrapped info
├─ <16 hex chars>/maps           legacy (pre-multi-account) variant of the same
└─ user_data#N/
   ├─ cache/0/                   one cache database generation
   │  ├─ binlog                  the index: which key lives in which file, with what size and checksum
   │  └─ XX/XXXXXXXXXXXX         the values, two hex digits of directory then twelve of file name
   └─ media_cache/0/             the same, for streamed video and audio
```

Read path for one value:

```
key_datas ──passcode key──▶ local key ──AES-CTR──▶ TDEF payload ──XXH32 check──▶ media bytes
                                                          ▲
binlog ──AES-CTR─────────────────────────────────────────┘
```

---

## 2. The `TDF$` container

Used by every small bookkeeping file: `key_datas`, `settingss`, `maps`, `configs`, the legacy `<hash>/*s`
files.

> Source: `storage/details/storage_file_utilities.cpp` → `ReadFile()`, `EncryptedDescriptor`,
> `FileWriteDescriptor::finish()`

```
[0:4]    "TDF$"
[4:8]    int32 LE   AppVersion
[8:E-16] payload    QDataStream-serialised, big-endian lengths
[E-16:]  MD5( payload ‖ int32le(payloadLength) ‖ int32le(version) ‖ "TDF$" )
```

`QtDataStreamReader` implements the only primitive these payloads need: a length-prefixed `QByteArray`,
where the length is a **big-endian** `quint32` and `0xFFFFFFFF` encodes a null (empty) array. Qt's
`QDataStream` default byte order is big-endian and Telegram Desktop never changes it.

`key_datas` holds three arrays in this order:

```
salt (32) ‖ keyEncrypted ‖ infoEncrypted
```

---

## 3. Local key derivation

> Source: `storage/details/storage_file_utilities.cpp` → `CreateLocalKey()`, `CreateLegacyLocalKey()`
> Constants: `LocalEncryptSaltSize = 32`, `kStrongIterationsCount = 100000`,
> `LocalEncryptIterCount = 4000`, `LocalEncryptNoPwdIterCount = 4`

### 3.1 Modern (`key_datas`)

```
inner  = SHA512( salt ‖ passcode ‖ salt )
key    = PBKDF2-HMAC-SHA512( inner, salt, iterations, dkLen = 256 )
iterations = 100000 when a passcode is set, otherwise 1
```

### 3.2 Legacy (`<hash>/maps`)

```
key = PBKDF2-HMAC-SHA1( passcode, salt, iterations, dkLen = 256 )
iterations = 4000 with a passcode, otherwise 4
```

The result is the **local key**: 256 bytes (`EncryptionKey::kSize`) or, for folders written by a newer
build, 64 bytes (`kSize_v2`). Both are accepted by `LocalKey`.

### 3.3 Unwrapping a local blob

Every encrypted blob is `[msgKey(16)] [AES-256-IGE ciphertext]`:

```
aesKey, aesIv = prepareAES_oldmtp(localKey, msgKey)

sha1_a = SHA1( msgKey  | K[  8: 40] )
sha1_b = SHA1( K[40: 56] | msgKey | K[ 56: 72] )
sha1_c = SHA1( K[ 72:104] | msgKey )
sha1_d = SHA1( msgKey | K[104:136] )
aesKey = sha1_a[0:8] ‖ sha1_b[8:20] ‖ sha1_c[4:16]
aesIv  = sha1_a[8:20] ‖ sha1_b[0:8] ‖ sha1_c[16:20] ‖ sha1_d[0:8]
```

> Source: `Telegram/SourceFiles/mtproto/mtproto_auth_key.cpp` → `MTP::AuthKey::prepareAES_oldmtp()`

`aesIv` is 32 bytes: the two IGE registers. The mode is

```
decrypt: out[i] = D( in[i] ^ iv1 ) ^ iv0 ;  iv0 = in[i]  ; iv1 = out[i]
encrypt: out[i] = E( in[i] ^ iv0 ) ^ iv1 ;  iv0 = out[i] ; iv1 = in[i]
```

After decryption the plaintext is:

```
[0:4]   uint32 LE   total length, including the trailing padding
[4:n]   payload
[n:]    random padding out to a multiple of 16
```

and it is only accepted when `SHA1(plaintext)[0:16] == msgKey`. That check is what turns "wrong passcode"
into a clear error instead of 256 bytes of noise.

> Source: `storage/details/storage_file_utilities.cpp` → `DecryptLocal()`

---

## 4. The `TDEF$` container

Every cache value and every binlog lives in one.

> Source: `lib_storage/storage/storage_encrypted_file.cpp` → `File::readHeader()`

```
[0:4]     "TDEF"                       also the base::FileLock::kSkipBytes region
[4:68]    salt, 64 bytes, in clear
[68:116]  encrypted header (48 bytes = 3 CTR blocks):
            uint32 format | uint32 reserved | uint64 applicationVersion | byte checksum[32]
[116:]    encrypted payload; the CTR counter continues from block 3
```

The header checksum is

```
SHA256( localKey ‖ salt ‖ format ‖ reserved ‖ applicationVersion )
```

so opening a file both proves the key is right and proves the bytes are a real container.

### 4.1 Key and counter derivation

```
ctrKey = SHA256( localKey[0 : len/2] ‖ salt[0 : 32] )
ctrIV  = SHA256( localKey[len/2:]  ‖ salt[32:64] )[0 : 16]
```

> Source: `lib_storage/storage/storage_encryption.cpp` → `EncryptionKey::prepareCtrState()`

The counter is 128-bit and **big-endian**: `CtrState::incrementedIv(blockIndex)` adds the block index with
the carry running from byte 15 towards byte 0, exactly like `CRYPTO_ctr128_encrypt`. The payload starts at
block 3, the header occupied blocks 0..2.

> Note: .NET has no CTR mode — `CipherMode` only offers CBC, ECB, OFB, CFB and CTS. `AesCtrCipher` gets
> there by noting that an ECB transform ignores its IV and therefore *is* the raw AES block function;
> encrypting an incrementing counter block yields the keystream. Note also that CTR needs the **forward**
> permutation: constructing the block transform with `CreateDecryptor()` silently produces garbage.

### 4.2 Padding

`File::writeWithPadding()` rounds the payload up to a multiple of 16 and fills the tail with random bytes.
So a value whose logical size is 1481 occupies 1488 bytes on disk, and only the first 1481 are real.

### 4.3 File locking

`base::FileLock` takes a byte-range lock on `[0, 4)` for as long as a database is open.

> Source: `Telegram/SourceFiles/storage/storage_file_lock.h` → `kLockOffset = 0`, `kLockLimit = kSkipBytes = 4`
> and `storage_file_lock_win.cpp` → `LockFile(handle, 0, 0, 4, 0)`

Only the `TDEF` magic lives in that range, so a reader can start at offset 4 and read everything else. A
running Telegram Desktop therefore does not block a recovery run; `TdefFile.IsLocked` reports whether the
magic check had to be skipped.

---

## 5. Binlog records

The binlog is an append-only log: a 16-byte header followed by records, replayed from the start to
rebuild the set of live entries.

> Source: `lib_storage/storage/cache/storage_cache_types.h`,
> `lib_storage/storage/cache/storage_cache_binlog_reader.h`,
> `lib_storage/storage/cache/storage_cache_database_object.cpp` → `readBinlog()`

### 5.1 BasicHeader (16 bytes)

```cpp
struct BasicHeader {
    uint32 format : 8;    // +0  byte 0
    uint32 flags  : 24;   // +1  bytes 1..3, little-endian
    uint32 systemTime;    // +4  unix time the log was created
    uint32 reserved1;     // +8
    uint32 reserved2;     // +12
};
```

`flags & 0x01` (`kTrackEstimatedTime`) selects the timestamped record size. **Getting this wrong is the
single most common reason a binlog parser finds nothing at all:** every record then looks 16 bytes too
short and the framing collapses immediately.

### 5.2 Store / StoreWithTime

```cpp
struct Store {                     // sizeof == 64
    uint8     type;                // +0   0x01
    uint8     tag;                 // +1   opaque category, see 5.6
    // 6 bytes padding
    EntrySize size;                // +8   std::array<uint64,3>  == 24 bytes
    PlaceId   place;               // +32  std::array<uint8,7>
    // 1 byte padding
    uint32    checksum;            // +40  XXH32(value, 0)
    // 4 bytes padding
    Key       key;                 // +48  { uint64 high; uint64 low; }
};

struct StoreWithTime : Store {     // sizeof == 80
    EstimatedTimePoint time;        // +64  { uint32 relative1, relative2, system; }
    uint32 reserved;                // +76
};
```

Both sizes are multiples of 16 because every record has to be encryptable as whole CTR blocks
(`GoodForEncryption<Record>` in `storage_cache_binlog_reader.h`).

### 5.3 `EntrySize` is 24 bytes, not 3

```cpp
using EntrySize = std::array<uint64, 3>;

size_type Store::getSize() const {          // storage_cache_types.cpp
    auto result = size_type();
    for (auto &element : (size | ranges::views::reverse)) {
        result <<= 8;
        result |= size_type(element);
    }
    return result;
}
```

`ReadTo()` writes the value's successive bytes into `element[0]`, `element[1]`, `element[2]`, so only the
**first byte of each `uint64`** carries information and the remaining 21 bytes are zero. Folding them back
together most-significant-last gives

```
size = (raw[24] << 16) | (raw[16] << 8) | raw[8]
```

A parser that assumes the old 3-byte field reads `0` for every entry and rejects the file. The upper bound is
`kDataSizeLimit = 8 << 24` = 128 MiB; the client itself refuses anything over its `maxDataSize` (16 MiB by
default), but an install with the cache limits removed can legitimately hold more.

### 5.4 Multi* records

```cpp
struct MultiStore  { uint8 type; uint8 count[3]; uint32 r1, r2, r3; };  // 16-byte head, then count * Store(WithTime)
struct MultiRemove { uint8 type; uint8 count[3]; uint32 r1, r2, r3; };  // 16-byte head, then count * Key (16 bytes)
struct MultiAccess { uint8 type; uint8 count[3]; EstimatedTimePoint t;}; // 16-byte head, then count * Key
```

`count` is a `std::array<uint8,3>` (`RecordsCount`), bounded by `kBundledRecordsLimit = 1 << 24`.

### 5.5 Replay semantics

`processRecordStoreGeneric()`, `processRecordMultiStore()`, `processRecordMultiRemove()`,
`processRecordMultiAccess()`:

| Record | Effect |
|---|---|
| `Store` / `MultiStore` | insert or **replace** the entry for the key |
| `MultiRemove` | erase the key |
| `MultiAccess` | refresh only the access clock; the entry itself is untouched |

Framing stops at the first record that does not make sense (`BinlogWrapper::readRecord()` returns an empty
span for an unknown or oversized record), and a `Store` whose size is impossible is a **hard stop**, because
`processRecordStoreGeneric()` returns `false` and that ends `readTillEnd`. A binlog truncated by a killed
process therefore costs the tail, never the whole file.

### 5.6 The `tag` byte

Observed across Telegram Desktop 6–10 and 64Gram: `1` = still image, `2` = animation (GIF / Lottie / WebM),
`5` = video, `0` = unclassified. It is only used for cache statistics, so the sniffer does not depend on it.

### 5.7 Timestamps

`EstimatedTimePoint` is a monotonic counter (`relative1 | relative2 << 32`) plus the wall-clock reading at
the moment the counter had that value (`system`). The client keeps a running maximum of `relative`, so

```
absolute(entry) = entry.time.system + (maxRelative - entry.relative)
```

is a good estimate, and it is what `CacheEntry.EstimatedLastUse` holds. `MultiAccess` updates the entry's
`relative` but not its `system`, so for an old value the estimate stays near the original write time.

### 5.8 Place ids

```cpp
using PlaceId = std::array<uint8, 7>;

QString PlaceFromId(PlaceId place) {          // storage_cache_database_object.cpp
    for (auto i = 0; i != place.size(); ++i) {
        pushDigit(place[i] & 0x0F);           // LOW nibble first
        pushDigit(place[i] >> 4);
        if (!i) result.push_back('/');
    }
}
```

Each byte is written **low nibble first**, and a directory separator follows the first byte — which is where
the two-character bucket directories (`00` … `FF`) come from. Example: the raw bytes
`12 DE DB 9A 6F F5 19` render as `21/EDBDA9F65F91`.

### 5.9 Cache versions

`DatabaseObject::computePath()` returns `base + std::to_string(version) + '/'`, and a little-endian
`int32` in a file called `version` names the live generation. The marker can be missing or stale, so this
reader enumerates every numeric folder that actually contains a binlog and treats the marked one as
authoritative; older folders are only consulted when a value fails its checksum in the live one.

### 5.10 Reading while the app is running

A live client holds `LockFile` on `[0, 4)` of the binlog. That is four bytes of magic, so the reader catches
the resulting `IOException`, sets `IsLocked`, and continues from offset 4. Nothing else is affected.

---

## 6. Streamed media (`media_cache`)

> Source: `Telegram/SourceFiles/media/streaming/media_streaming_reader.cpp`

```cpp
constexpr auto kPartSize          = Loader::kPartSize;          // 128 KiB
constexpr auto kPartsInSlice     = 64;
constexpr auto kInSlice          = kPartsInSlice * kPartSize;  // 8 MiB
constexpr auto kMaxPartsInHeader = 64;
constexpr auto kMaxOnlyInHeader  = 80 * kPartSize;             // 10 MiB
constexpr auto kPartsOutsideFirstSliceGood = 8;

Storage::Cache::Key Reader::CacheHelper::key(int sliceNumber) const {
    return Storage::Cache::Key{ baseKey.high, baseKey.low + sliceNumber };
}
```

A streamed file therefore lives at keys `baseKey + n`: slice 0 is the *header*, slice `n` holds file bytes
`[(n-1)·kInSlice, n·kInSlice)`. Values sharing a key high with consecutive lows belong to one file; the
lowest is the header and everything after it is offset by `(low - baseLow - 1) · kInSlice`.

### 6.1 Two serializations

**Contiguous** — the raw bytes of the slice. `IsContiguousSerialization(size, maxSliceSize)` is
`size % kPartSize == 0 || size == maxSliceSize`; the reader then splits the value into part-aligned chunks.

**Complex** — an explicit list of the parts that did arrive:

```cpp
uint32 count;                  // 1 .. 80   (kMaxOnlyInHeader / kPartSize)
repeat count:
    uint32 offset;             // part-aligned, relative to the slice
    uint32 size;
    byte   data[size];         // offset + size must fit inside maxSize
```

### 6.2 Headers

The header slice always uses the complex serialization. Its part offsets are **file-absolute** — typically
the `moov` index at the end of an MP4. When the header qualifies as *good*
(`ComputeIsGoodHeader`: the file is larger than `kMaxOnlyInHeader` and at most
`kPartsOutsideFirstSliceGood` parts sit at or beyond `kInSlice`) the first slice's own map is appended, and
that second map's offsets already start at 0, so it merges in as is:

```
header map (offsets at/after 8 MiB)  ‖  first-slice map (offsets from 0)
```

### 6.3 Reassembly

`StreamedFilePlanner` classifies each value and produces a list of `(offset, bytes)` parts;
`StreamedFileWriter` creates the file at its final length (sparse) and writes each part at its offset, so
missing ranges stay as zeros and a 4 GB video never becomes a 4 GB array. Coverage is
`cached bytes / reconstructed size`, which is what `--min-coverage` is compared against.

---

## 7. Integrity checks

| Check | Where | Protects against |
|---|---|---|
| `SHA256(localKey ‖ salt ‖ header)` | TDEF header | wrong local key, non-TDEF file |
| `SHA1(plaintext)[0:16] == msgKey` | local blob | wrong passcode |
| `MD5(payload ‖ len ‖ version ‖ magic)` | TDF$ container | truncated or edited bookkeeping file |
| `XXH32(value, 0)` | every cache value | stale version folder, corrupt place file |

None of these are authentication — they are integrity checks against noise, and that is exactly what is
needed to avoid writing 80 000 files of garbage when something is off.

> Source for the checksum: `lib_storage/storage/cache/storage_cache_database_object.cpp` →
> `uint32 CountChecksum(bytes::const_span data) { return XXH32(data.data(), data.size(), 0); }`
