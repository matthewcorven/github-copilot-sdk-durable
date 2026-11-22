"""The hand edit the divergence flow makes between its two processes.

Two open items are reordered and `docs/section-08.md` is ticked although its H1 is still missing.
The store is not touched: this is the user editing the input the loop reads.
"""
import sys


def main(path: str) -> int:
    text = open(path).read()
    lines = text.split("\n")
    s09 = next(line for line in lines if "section-09" in line)
    s08 = next(line for line in lines if "section-08" in line)
    s03 = next(line for line in lines if "section-03" in line)
    s04 = next(line for line in lines if "section-04" in line)
    rest = [line for line in lines if line not in (s09, s08, s03)]
    at = rest.index(s04)
    rest[at:at] = [s09, s08.replace("- [ ]", "- [x]", 1)]
    rest.append(s03)
    open(path, "w").write("\n".join(rest))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1]))
