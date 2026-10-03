# Contributing

Thanks for helping. This project is small on purpose: one format reader, one engine, two front ends, and a
test suite that pins down every byte layout it depends on.

## Getting set up

```powershell
git clone https://github.com/<you>/TelegramMediaRecovery
cd TelegramMediaRecovery
dotnet build
dotnet test
```

You need the .NET 10 SDK. The WPF project only builds on Windows; `Core`, `Cli` and the tests build
anywhere.

## House rules

**A byte layout is never guessed.** Every offset in `CacheRecordLayout`, `StreamingLayout` and
`TdefFile` cites the upstream file and function it came from, in a doc comment. If you add a field, do
the same. `docs/binary-formats.md` holds the long form.

**Verify, then write.** Nothing is written to the output folder before its checksum matches. If you find a
path that writes unverified bytes, that is a bug — please report it rather than working around it.

**Tests use hand-built bytes.** `BinlogBuilder` and `EndToEndTests` construct synthetic `tdata` folders
with the *write* side of every format. Do not add fixture files, and never add data from a real tdata
folder: not binlogs, not place files, not keys, not passcodes.

**`tdata` is read-only.** The tool must never create, rename or delete anything inside it. Opening a file
is done with `FileShare.ReadWrite | FileShare.Delete` so a running client does not get in the way, and a
locked byte range is handled by skipping the magic check rather than by retrying.

## Style

- `TreatWarningsAsErrors` is on and `dotnet format --verify-no-changes` runs in CI.
- File-scoped namespaces, primary constructors where they help, collection expressions for byte literals.
- Prefer `ReadOnlySpan<byte>` to `byte[]` in hot paths; it is what makes the per-block AES code allocation
  free.
- Write binary magics as byte lists (`[(byte)0xFF, (byte)0xD8]`), not as `"..."u8` string literals. In C#
  a `\xFF` escape is the code point U+00FF, which UTF-8 encodes as two bytes, so `"\xff\xd8\xff"u8` is
  six bytes long and silently never matches. This bug cost real debugging time; there is a comment in
  `MediaSniffer` so it does not come back.

## Commit messages and pull requests

One logical change per commit, imperative subject under 72 characters, and a body that says why. Fill in
the pull-request template, in particular the checklist — the "new byte layouts cite their upstream
source" item is the one reviewers care about most.

## Adding a supported format

1. Add the magic and category to `MediaSniffer`.
2. Add a test with a real file header in `FormatTests`.
3. Update the format table in `README.md`.

## Adding a record type or field

1. Add the offset to `CacheRecordLayout` with the C++ struct in the doc comment.
2. Add a builder method to `BinlogBuilder` that reproduces the padding exactly.
3. Add a test that the reader frames it, and one that it stops safely when the field is corrupt.

## Reporting a security issue

This tool reads only local data and has no network surface, so there is little to attack. If you believe
you have found something anyway, open a private security advisory on the repository rather than a public
issue.
