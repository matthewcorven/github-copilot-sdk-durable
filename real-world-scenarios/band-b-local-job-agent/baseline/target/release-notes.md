# Release notes

## Overview

This release settles the queue.

## New

- Background resume for interrupted jobs
- Per-iteration checkpoint inspection
- A `--dry-run` flag on the runner

## Fixes

### Panic on empty plan

The loop no longer panics when the plan file is empty.

### Timeout drift

#### Retries

Retries now start from the time the request was sent, not from when it was queued.

## Upgrade notes

```text
## This heading is inside a fence and must not change.
```

- Nothing else to do.
