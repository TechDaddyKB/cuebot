#!/usr/bin/env python3
"""Prints the PinnedPython table for a python-build-standalone release.

Usage: tools/update-python-pin.py [release-tag] [python-minor]
  release-tag   defaults to the latest release, e.g. 20261026
  python-minor  defaults to 3.13

Paste the printed Release, Version and Builds into src/Cuebot.Scripting/Python/PinnedPython.cs. The SHA-256 values
come from the release's SHA256SUMS file; the sizes from the GitHub release API.
"""
import json
import re
import sys
import urllib.request

REPO = "astral-sh/python-build-standalone"
TARGETS = [
    "x86_64-pc-windows-msvc",
    "aarch64-pc-windows-msvc",
    "x86_64-unknown-linux-gnu",
    "aarch64-unknown-linux-gnu",
]


def fetch(url):
    request = urllib.request.Request(url, headers={"User-Agent": "Cuebot pin updater"})
    with urllib.request.urlopen(request) as response:
        return response.read()


def main():
    tag = sys.argv[1] if len(sys.argv) > 1 else None
    minor = sys.argv[2] if len(sys.argv) > 2 else "3.13"
    api = f"https://api.github.com/repos/{REPO}/releases/" + (f"tags/{tag}" if tag else "latest")
    release = json.loads(fetch(api))
    tag = release["tag_name"]
    sizes = {a["name"]: a["size"] for a in release["assets"]}
    sums = {}
    for line in fetch(f"https://github.com/{REPO}/releases/download/{tag}/SHA256SUMS").decode().splitlines():
        parts = line.split()
        if len(parts) == 2:
            sums[parts[1]] = parts[0]

    pattern = re.compile(rf"^cpython-({re.escape(minor)}\.\d+)\+{tag}-(.+)-install_only_stripped\.tar\.gz$")
    builds, versions = [], set()
    for name in sorted(sizes):
        match = pattern.match(name)
        if match and match.group(2) in TARGETS:
            versions.add(match.group(1))
            builds.append((TARGETS.index(match.group(2)), match.group(2), name))
    missing = set(TARGETS) - {b[1] for b in builds}
    if missing or len(versions) != 1:
        sys.exit(f"Release {tag} lacks a single Python {minor} build for: {', '.join(sorted(missing)) or 'one version'}")

    print(f'    public const string Release = "{tag}";\n')
    print(f'    public const string Version = "{versions.pop()}";\n')
    print("    private static readonly PythonBuild[] Builds =\n    [")
    for _, target, name in sorted(builds):
        print(f'        new("{target}", "{name}",\n            "{sums[name]}", {sizes[name]}),')
    print("    ];")


if __name__ == "__main__":
    main()
