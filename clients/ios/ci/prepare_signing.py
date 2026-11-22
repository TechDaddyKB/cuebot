#!/usr/bin/env python3
"""Switch the app target's Release configuration to manual signing, in the CI checkout only.

The settings cannot go on the xcodebuild command line: they would also apply to the CuebotKit package target, which
does not support provisioning profiles. The committed project keeps automatic signing for working in Xcode.
"""
import argparse
import re
import sys

RELEASE_BLOCK = re.compile(
    r"\t\t[0-9A-F]{24} /\* Release \*/ = \{\n\t\t\tisa = XCBuildConfiguration;.*?\n\t\t\};", re.DOTALL
)


def patch(text, bundle_id, version, build, profile):
    marker = f"PRODUCT_BUNDLE_IDENTIFIER = {bundle_id};"
    matches = [m for m in RELEASE_BLOCK.finditer(text) if marker in m.group(0)]
    if len(matches) != 1:
        raise ValueError(f"expected one Release configuration for {bundle_id}, found {len(matches)}")
    match = matches[0]
    block = match.group(0)
    quoted = profile.replace("\\", "\\\\").replace('"', '\\"')
    for pattern, replacement, label in (
        (
            r"CODE_SIGN_STYLE = Automatic;",
            "CODE_SIGN_IDENTITY = \"Apple Distribution\";\n\t\t\t\tCODE_SIGN_STYLE = Manual;\n"
            f'\t\t\t\tPROVISIONING_PROFILE_SPECIFIER = "{quoted}";',
            "CODE_SIGN_STYLE",
        ),
        (r"CURRENT_PROJECT_VERSION = [^;]+;", f"CURRENT_PROJECT_VERSION = {build};", "CURRENT_PROJECT_VERSION"),
        (r"MARKETING_VERSION = [^;]+;", f"MARKETING_VERSION = {version};", "MARKETING_VERSION"),
    ):
        block, count = re.subn(pattern, lambda _: replacement, block)
        if count != 1:
            raise ValueError(f"expected one {label} in the Release configuration, found {count}")
    return text[: match.start()] + block + text[match.end() :]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    for name in ("project", "bundle_id", "version", "build", "profile"):
        parser.add_argument(name)
    args = parser.parse_args()
    with open(args.project, encoding="utf-8") as handle:
        text = handle.read()
    try:
        patched = patch(text, args.bundle_id, args.version, args.build, args.profile)
    except ValueError as error:
        sys.exit(f"prepare_signing: {error}")
    with open(args.project, "w", encoding="utf-8") as handle:
        handle.write(patched)


if __name__ == "__main__":
    main()
