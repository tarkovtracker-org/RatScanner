# Agent Backlog

Working notes for issues flagged by AI agents, not user-requested items. (User-requested items stay in `BACKLOG.md`.)

Items are grouped by impact. The top group contains concrete correctness/reliability problems with reachable failure modes. The lower group is cleanup or design questions that need human confirmation before acting.

Resolved items are removed once the fix is merged to `master`; the merged PR and its linked issue are the durable record. Anything that grows beyond a quick fix should be promoted to a GitHub issue and linked here until it lands.

---

## Open items

None.

---

## Template for new items

```text
### Short title

**Priority:** P0 | P1 | P2
**Status:** Not started | In progress | Blocked | Done
**Files:** path/to/file, ...

Describe the problem with evidence (file:line, observed behavior, expected behavior).
Note whether the item is a concrete correctness issue or a design/cleanup question.
Keep it scoped so it can be picked up without re-investigation.
```
