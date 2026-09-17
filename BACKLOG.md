# Backlog

Working notes for issues found but not yet fixed. Append new items under the matching category. Remove items when done; the merged PR is the durable record.

## UI/UX

None.

## Versioning

None.

## Release readiness / data integration

### Complete the manual beta scan and clean-machine gates

**Status:** Blocked (needs a clean Windows x64 machine and current-game screenshot fixtures)
**Files:** `tests/RatScanner.Tests/ScanPipelineImageHarnessTests.cs`, `src/ScanEngine/RatEye.Benchmarks`, `docs/agent-context/build-and-validation.md`

Hermetic CI intentionally skips private/current-game screenshot fixtures, so green unit tests do not prove current EFT recognition accuracy. Before beta promotion, replay the available diagnostic fixtures and exercise the exact packaged ZIP on clean Windows x64 for startup, RatEye native dependency loading, one name scan, highlighted and normal inventory scans, and at least one non-English OCR scan.

The reproducible benchmark/fixture harness from [RatScanner issue #4](https://github.com/tarkovtracker-org/RatScanner/issues/4) has landed; what remains is running it against current-game fixtures and the packaged ZIP on a clean machine, which cannot be automated from CI.

## Other

None.

## Template for new items

```text
### Short title

**Status:** Not started | In progress | Blocked
**Files:** path/to/file, ...

Describe the problem with evidence (file:line, observed behavior, expected behavior).
Note edge cases and suggested fix shape. Keep it scoped so it can be picked up
without re-investigation.
```
