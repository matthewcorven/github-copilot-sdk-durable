"""Merges a driver spec with the machine's Playwright package and Chromium executable.

The driver is Playwright, but Playwright is not a dependency of this repository. The package that
*happens* to be installed and the browser build that happens to be cached are different versions on
a developer machine, and a driver that refuses to start because of that would report a harness error
where the fault script wanted an observation. So the merge resolves both, and says which it used in
the merged spec, which the result file keeps.
"""

import json
import os
import sys


def newest_chromium():
    override = os.environ.get("BAND_A_CHROMIUM")
    if override:
        return override
    cache = os.environ.get("PLAYWRIGHT_BROWSERS_PATH") or os.path.join(
        os.path.expanduser("~"), "Library", "Caches", "ms-playwright"
    )
    if not os.path.isdir(cache):
        return None
    suffixes = [
        ("chrome-mac-arm64", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"),
        ("chrome-mac", "Google Chrome for Testing.app", "Contents", "MacOS", "Google Chrome for Testing"),
        ("chrome-linux", "chrome"),
        ("chrome-win", "chrome.exe"),
    ]
    for entry in sorted(os.listdir(cache), reverse=True):
        if not entry.startswith("chromium-"):
            continue
        for suffix in suffixes:
            candidate = os.path.join(cache, entry, *suffix)
            if os.path.isfile(candidate):
                return candidate
    return None


def main():
    package, port, spec_path, out_path = sys.argv[1:5]
    spec = json.load(open(spec_path))
    spec["playwright"] = package
    spec.setdefault("url", "http://127.0.0.1:%s/" % port)
    executable = newest_chromium()
    if executable:
        spec["executablePath"] = executable
    json.dump(spec, open(out_path, "w"), indent=2)


if __name__ == "__main__":
    main()
