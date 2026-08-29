# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```sh
dotnet build                                          # build the solution
dotnet test                                           # run all tests
dotnet run -c Release --project src/IndexedLargeDiskData.Cli -- stats --root <dir>
```

Run a single test or a subset (xUnit + VSTest filter syntax):

```sh
dotnet test --filter "FullyQualifiedName~SortedIndexTests"
dotnet test --filter "FullyQualifiedName=IndexedLargeDiskData.Tests.SortedIndexTests.Maintain_MergesATierAndPreservesEveryEntry"
```

Exercise a real dataset through the CLI (`--cache-mb` defaults to 256 here; the library default is 20 GiB):

```sh
dotnet run -c Release --project src/IndexedLargeDiskData.Cli -- ingest-transactions --root d:\data --count 2000000 --keys 50000
dotnet run -c Release --project src/IndexedLargeDiskData.Cli -- find-v0 --root d:\data --key 12345
dotnet run -c Release --project src/IndexedLargeDiskData.Cli -- maintain --root d:\data
```

## What this stores

Two append-only datasets, never updated or deleted, sized for terabytes on disk:

- **Transactions** — `TripleRecord`, three `long`s, 24 bytes. Indexed on `V0` and `V1`, both
  duplicate tolerant (one key normally matches many records).
- **Addresses** — `AddressRecord`, a `long` id plus a 75-character address, 83 bytes. The address is
  text, held as a `string` and stored as 75 ASCII bytes, so a character count and a byte count are the
  same number. Navigable in both directions: id to address and address to id.

Beside them, `blockslog.txt` records the block numbers passed to `DataRoot.AddSingleTransaction`, one
line per block — the number, a space, and the UTC time it was first seen:

```
170001 2026-08-29T16:19:36.671Z
170002 2026-08-29T16:19:36.866Z
```

Consecutive repeats are dropped — ingest calls with the same block number once per transaction in it
— and the check is a comparison against a field, never a read of the file. The clock is only read on
the branch that writes, so the stamp marks the first transaction in the block rather than some later
one, and the repeat path stays a comparison. The file is read once, at open, and only its last 128
bytes, to recover the last number across a restart.

## Architecture

The layers, bottom up. Each one only knows about the layer below it.

**`Caching/BlockCache`** — one process-wide cache of fixed-size blocks in native memory
(`NativeMemory.AlignedAlloc`), shared by every store and file. The whole budget is committed at
construction. It is native rather than managed because 20 GiB of `byte[]` would sit in gen2 and the
LOH and dominate every collection. Eviction is CLOCK; blocks are reference counted, and `BlockLease`
pins a block for as long as it is alive. **Never let a `lease.Span` outlive its `using`** — the
memory is handed to another block as soon as CLOCK reclaims it.

**`Storage/SegmentedFileSet`** — presents a directory of capped `NNNNNN.dat` files as one contiguous
append-only byte stream. Appends land in a write buffer; reads below the durable length go through
the cache, reads above it come from the buffer, so an appended record is visible immediately.

**`Storage/RecordStore<T>`** — fixed-width records over a `SegmentedFileSet`, addressed by ordinal.
Fixed width is the load-bearing assumption: record `n` is at byte `n * T.Size`, so there is no offset
table and an index entry only needs an 8-byte ordinal. Segment caps are rounded down to a whole
number of records so no record straddles two files.

**`Indexing/SortedIndex`** — a duplicate-tolerant `long` key to ordinal map, built as an LSM of
immutable sorted runs. Entries buffer in a `MemTable` (a large sorted region plus a short unsorted
tail), flush to a level-0 `IndexSegment`, and merge upward in tiers of `MergeFanout`. Segment layout
is header, entries, fences, Bloom filter; only the fences are loaded eagerly into managed memory.

**`Stores/IndexedStore<T>`** — ties a `RecordStore<T>` to N `SortedIndex` instances and owns the
manifest. `TransactionStore` and `AddressStore` derive from it and just supply `GetKey`.

**`DataRoot`** — owns the `BlockCache` and both stores. One per process; keep it for the process
lifetime.

## Invariants worth knowing before changing anything

**Indexes are derived state; records are the truth.** Nothing in the index path is crash safe on
purpose. A missing tail of index entries is rebuilt on open by replaying records from the index's
`CoveredUpTo`. Delete every `.idx` file and the store still answers correctly after a reopen — there
is a test for exactly this. This is what makes an index flush a plain write with no journal in front
of it.

**An index may never claim coverage past the committed record count.** `IndexedStore.CommitRecords`
forces records to disk and writes the manifest *before* any index flushes, and the flush is stamped
with that committed count. Records found past the committed count on open are discarded rather than
exposed, because an index could never have referenced them. Breaking this ordering produces dangling
ordinals that only surface after a crash.

**A directory can only be opened with the options it was written with.** `DataRoot` writes
`options.json` into the root when it creates one and checks it on every later open, throwing
`InvalidDataException` naming the fields that moved. `BlockSize`, `SegmentSize` and the index knobs
decide the shape of what lands on disk, so reopening under different ones does not fail where the
mistake was made — it fails later as a segment that looks truncated or an index that will not parse.
The check covers every option, the runtime-only ones included, so changing the cache budget for an
existing directory means rewriting the file rather than passing a different value.

**Merges are made atomic by `pending.commit`.** A merge writes `.idx.tmp` outputs, writes the pending
file, renames, deletes inputs, then deletes the pending file. Reopening replays whatever is left. The
failure this prevents is having a merge output *and* its inputs both live, which would make every
lookup return each ordinal twice.

**The 75-character address is indexed on its leading 8 characters only.** A full-width key would make
index entries 83 bytes instead of 16. A prefix match is only a *candidate* — text addresses can share
their opening characters far more readily than a digest could — so `AddressStore.FindByAddress`
confirms each one by comparing the full 75 characters on the record. Any new lookup path over that
index must do the same confirmation. If a corpus arrives where every address opens with the same
scheme or network tag, the key to change is `AddressRecord.PrefixOf`: a 64-bit hash of the whole
string would spread the keys out, and the confirmation step already there would still hold.

## Sizing, and why the defaults are what they are

At 24 bytes a record, one TB of transactions is ~45.8 billion rows, and each 16-byte index entry
over them is ~733 GB — the two indexes together cost more disk than the data. If the ingest can be
ordered by one of the indexed fields, that index collapses to a sparse block-level index and only the
other needs full entries; that trade is not implemented and would go in `SortedIndex`.

Defaults in `StoreOptions`, with the reason each one is where it is:

- `BlockSize` 4 KiB — matches the page and keeps read amplification low for point lookups. A 20 GiB
  budget at 4 KiB is 5.2M blocks, and the per-block dictionary bookkeeping is real (hundreds of MB);
  raising the block size trades read amplification for less of it.
- `MemTableEntries` 1M — also the crash replay bound. Raise it to tens of millions during a bulk load.
- `MaxSegmentEntries` 128M — a segment's Bloom filter must be fully resident while it is written
  (the bits are scattered and cannot be streamed); at 10 bits per key that is ~160 MiB. Larger merges
  spill into several outputs.
- `BloomBitsPerKey` 10 — ~1% false positives. Lookups probe every live segment, so without a filter a
  point lookup pays a binary search per segment instead of one block read.

## Conventions

`Directory.Build.props` sets `TargetFramework`, `LangVersion`, `Nullable`, `ImplicitUsings`,
`AllowUnsafeBlocks` and `GenerateDocumentationFile` for every project — set those there, not per
csproj. Documentation generation is on, so public members need XML doc comments or the build warns.

The test project sees library internals via `InternalsVisibleTo`, so `SortedIndex`, `IndexSegment`
and the internal `RecordStore<T>` constructor can be tested directly. `TestData.SmallOptions` shrinks
blocks, segments and memtables so a few thousand records exercise segment rollover, cache eviction,
memtable flushes and tier merges — behaviour that would otherwise need terabytes to reach.

Stores are safe for one writer and many concurrent readers. `SortedIndex` guards the segment array
with a `ReaderWriterLockSlim` and the memtable with a separate lock; take them in that order
(segments, then memtable) to keep the ordering consistent with the publish path.
