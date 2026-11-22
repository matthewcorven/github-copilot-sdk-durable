"""Reads the two connection windows out of a gate log and prints their overlap.

Each gate writes `<wall-clock seconds> [gate <pid>] ...` per frame, so a log that two applications
shared has two pid-tagged windows in it. A window is the earliest and latest line its gate wrote; the
overlap is the intersection of the two. That is what makes "the two loops ran at the same time" a
measurement: a run whose windows do not intersect cannot claim to have been concurrent, and the fault
script refuses to record it.

    overlap.py <gate-log>

Prints `<shape> <connections> <overlap-seconds>`; shape is `no-log`, `one-connection` or
`two-connections`.

A second client that is refused before it ever starts a runtime leaves one connection, not two. That is
a fact about *where* the refusal happens — the Durable variant refuses at the store, before a model is
asked for anything — and it is reported as one connection rather than as a missing measurement. The
concurrency of the two applications is established separately, by the fault script, which records that
the first process was alive and blocked at its gate at the instant the second started.
"""

import collections
import sys


def main():
    windows = collections.OrderedDict()
    try:
        handle = open(sys.argv[1], encoding="utf-8")
    except OSError:
        print("no-log 0 0.000")
        return 0
    for line in handle:
        parts = line.split()
        if len(parts) < 3:
            continue
        try:
            stamp = float(parts[0])
        except ValueError:
            continue
        if not parts[1] == "[gate":
            continue
        gate = parts[2].rstrip("]")
        low, high = windows.get(gate, (stamp, stamp))
        windows[gate] = (min(low, stamp), max(high, stamp))
    if len(windows) < 2:
        print("one-connection %d 0.000" % len(windows))
        return 0
    (_, (a0, a1)), (_, (b0, b1)) = list(windows.items())[:2]
    overlap = max(0.0, min(a1, b1) - max(a0, b0))
    print("two-connections %d %.3f" % (len(windows), overlap))
    return 0


if __name__ == "__main__":
    sys.exit(main())
