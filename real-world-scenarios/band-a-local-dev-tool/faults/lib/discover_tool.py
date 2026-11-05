"""Finds a Playwright install without adding a dependency to this repository.

The fault harness drives the baseline's Blazor page in a real browser, because the baseline has no
other entry point: its `Program.cs` starts a web host and nothing else, so an enquiry exists only
after a click creates it. Playwright is not a dependency of this repository, so the driver runs when a
machine happens to have it and the fault script records a reason when it does not. That distinction is
the point: a capability the machine lacks is recorded, never worked around by pretending the baseline
is drivable.
"""

import json
import os
import sys


def _is_package(path):
    return os.path.isdir(path) and (
        os.path.isfile(os.path.join(path, "index.js")) or os.path.isfile(os.path.join(path, "package.json"))
    )


def candidates():
    seen = []
    override = os.environ.get("BAND_A_PLAYWRIGHT")
    if override:
        seen.append(override)
    seen.append(os.path.join(os.getcwd(), "node_modules", "playwright"))
    for root in (
        os.path.join(os.path.expanduser("~"), ".local", "lib", "node_modules"),
        "/opt/homebrew/lib/node_modules",
        "/usr/local/lib/node_modules",
        "/usr/lib/node_modules",
    ):
        seen.append(os.path.join(root, "playwright"))
        seen.append(os.path.join(root, "@playwright", "cli", "node_modules", "playwright"))
        seen.append(os.path.join(root, "@playwright", "test", "node_modules", "playwright"))
    return seen


def find():
    for path in candidates():
        if _is_package(path):
            return os.path.abspath(path)
    return None


def browser_cache():
    return os.environ.get("PLAYWRIGHT_BROWSERS_PATH") or os.path.join(
        os.path.expanduser("~"), "Library", "Caches", "ms-playwright"
    )


if __name__ == "__main__":
    package = find()
    cache = browser_cache()
    print(
        json.dumps(
            {
                "playwright": package,
                "cache": cache,
                "browsers": sorted(os.listdir(cache)) if os.path.isdir(cache) else [],
            },
            indent=2,
        )
    )
    sys.exit(0 if package else 1)
