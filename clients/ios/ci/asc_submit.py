#!/usr/bin/env python3
"""Submit an uploaded Cuebot Companion build to App Review through the App Store Connect API.

Safe to re-run: it picks up an existing version and open submission, and stops without changes when the version is
already with Apple or live. Standard library plus the openssl command, so the job installs nothing.

Reads ASC_KEY_ID, ASC_ISSUER_ID and ASC_KEY_P8_BASE64 from the environment.
"""
import argparse
import base64
import json
import os
import subprocess
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

BUNDLE_ID = "digital.techdaddy.cuebot.companion"
API = "https://api.appstoreconnect.apple.com"
MAX_NOTES = 4000

EDITABLE = {"PREPARE_FOR_SUBMISSION", "READY_FOR_REVIEW", "DEVELOPER_REJECTED", "REJECTED", "METADATA_REJECTED",
            "INVALID_BINARY"}
WITH_APPLE = {"WAITING_FOR_REVIEW", "IN_REVIEW", "WAITING_FOR_EXPORT_COMPLIANCE", "PENDING_APPLE_RELEASE",
              "PENDING_DEVELOPER_RELEASE", "ACCEPTED", "PROCESSING_FOR_DISTRIBUTION", "PROCESSING_FOR_APP_STORE"}
LIVE = {"READY_FOR_DISTRIBUTION", "READY_FOR_SALE"}


class SubmitError(Exception):
    pass


def b64url(data):
    return base64.urlsafe_b64encode(data).rstrip(b"=").decode()


def der_to_raw(der):
    """Turn openssl's DER ECDSA signature into the fixed 64-byte form JWT uses."""
    if der[0] != 0x30:
        raise SubmitError("unexpected signature format")
    pos = 3 if der[1] == 0x81 else 2
    parts = []
    for _ in range(2):
        if der[pos] != 0x02:
            raise SubmitError("unexpected signature format")
        length = der[pos + 1]
        parts.append(der[pos + 2 : pos + 2 + length].lstrip(b"\0").rjust(32, b"\0"))
        pos += 2 + length
    return parts[0] + parts[1]


class Client:
    def __init__(self, key_id, issuer, key_path, sleep=time.sleep):
        self.key_id, self.issuer, self.key_path, self.sleep = key_id, issuer, key_path, sleep

    def token(self):
        now = int(time.time())
        header = b64url(json.dumps({"alg": "ES256", "kid": self.key_id, "typ": "JWT"}).encode())
        claims = b64url(json.dumps({"iss": self.issuer, "iat": now, "exp": now + 900,
                                    "aud": "appstoreconnect-v1"}).encode())
        signing_input = f"{header}.{claims}".encode()
        result = subprocess.run(["openssl", "dgst", "-sha256", "-sign", self.key_path], input=signing_input,
                                capture_output=True, check=False)
        if result.returncode != 0:
            raise SubmitError("could not sign the API token with the App Store Connect key")
        return f"{header}.{claims}.{b64url(der_to_raw(result.stdout))}"

    def request(self, method, path, body=None):
        url = path if path.startswith("http") else API + path
        data = json.dumps(body).encode() if body is not None else None
        for attempt in range(4):
            request = urllib.request.Request(url, data=data, method=method)
            request.add_header("Authorization", f"Bearer {self.token()}")
            if data is not None:
                request.add_header("Content-Type", "application/json")
            try:
                with urllib.request.urlopen(request, timeout=60) as response:
                    raw = response.read()
                    return json.loads(raw) if raw else {}
            except urllib.error.HTTPError as error:
                detail = error.read().decode(errors="replace")
                # Only reads are retried: a repeated write could act twice.
                if method == "GET" and (error.code == 429 or error.code >= 500) and attempt < 3:
                    self.sleep(5 * (attempt + 1))
                    continue
                raise SubmitError(f"{method} {urllib.parse.urlsplit(url).path}: HTTP {error.code}: "
                                  f"{describe(detail)}") from None
            except urllib.error.URLError as error:
                if method == "GET" and attempt < 3:
                    self.sleep(5 * (attempt + 1))
                    continue
                raise SubmitError(f"{method} {urllib.parse.urlsplit(url).path}: {error.reason}") from None
        raise SubmitError(f"{method} {path}: no response")

    def get_all(self, path):
        items, next_url = [], path
        while next_url:
            page = self.request("GET", next_url)
            items.extend(page.get("data", []))
            next_url = page.get("links", {}).get("next")
        return items


def describe(detail):
    try:
        errors = json.loads(detail).get("errors", [])
        return "; ".join(f"{e.get('title', '')} {e.get('detail', '')}".strip() for e in errors) or detail[:500]
    except (ValueError, AttributeError):
        return detail[:500]


def read_notes(path):
    try:
        with open(path, encoding="utf-8") as handle:
            notes = handle.read().strip()
    except OSError:
        raise SubmitError(f"no release notes at {path}") from None
    if not notes:
        raise SubmitError(f"{path} is empty")
    if len(notes) > MAX_NOTES:
        raise SubmitError(f"{path} has {len(notes)} characters, the App Store allows {MAX_NOTES}")
    return notes


def state_of(version):
    attributes = version["attributes"]
    return attributes.get("appVersionState") or attributes.get("appStoreState")


def wait_for_build(client, app_id, version, build, timeout=3600, interval=30):
    path = "/v1/builds?" + urllib.parse.urlencode({"filter[app]": app_id, "filter[version]": build,
                                                   "filter[preReleaseVersion.version]": version, "limit": 1})
    deadline = time.monotonic() + timeout
    while True:
        found = client.request("GET", path).get("data", [])
        state = found[0]["attributes"].get("processingState") if found else None
        if state == "VALID":
            return found[0]["id"]
        if state in ("FAILED", "INVALID"):
            raise SubmitError(f"Apple marked build {version} ({build}) as {state}")
        if time.monotonic() >= deadline:
            raise SubmitError(f"build {version} ({build}) was not ready in time (last state: {state or 'not found'})")
        print(f"Waiting for Apple to process {version} ({build}): {state or 'not visible yet'}", flush=True)
        client.sleep(interval)


def submit(client, version, build, notes):
    apps = client.request("GET", "/v1/apps?" + urllib.parse.urlencode({"filter[bundleId]": BUNDLE_ID})).get("data", [])
    apps = [a for a in apps if a["attributes"].get("bundleId") == BUNDLE_ID]
    if len(apps) != 1:
        raise SubmitError(f"expected one app for {BUNDLE_ID}, found {len(apps)}")
    app_id = apps[0]["id"]

    versions = client.get_all(f"/v1/apps/{app_id}/appStoreVersions?" +
                              urllib.parse.urlencode({"filter[platform]": "IOS", "limit": 200}))
    target = next((v for v in versions if v["attributes"]["versionString"] == version), None)
    if target is not None:
        state = state_of(target)
        if state in WITH_APPLE or state in LIVE:
            print(f"{version} is already {state}: nothing to submit.")
            return "unchanged"
        if state not in EDITABLE:
            raise SubmitError(f"{version} is in state {state}, which this script does not handle")
    for other in versions:
        if other is target:
            continue
        state = state_of(other)
        if state in WITH_APPLE or state in EDITABLE - {"DEVELOPER_REJECTED"}:
            raise SubmitError(f"{other['attributes']['versionString']} is still {state}. "
                              "It must be released or withdrawn before another version can be submitted")
    has_live_version = any(state_of(v) in LIVE for v in versions)

    build_id = wait_for_build(client, app_id, version, build)

    if target is None:
        target = client.request("POST", "/v1/appStoreVersions", {"data": {
            "type": "appStoreVersions",
            "attributes": {"platform": "IOS", "versionString": version, "releaseType": "AFTER_APPROVAL"},
            "relationships": {"app": {"data": {"type": "apps", "id": app_id}}},
        }})["data"]
        print(f"Created App Store version {version}")
    version_id = target["id"]

    client.request("PATCH", f"/v1/appStoreVersions/{version_id}/relationships/build",
                   {"data": {"type": "builds", "id": build_id}})
    print(f"Attached build {build}")

    # Apple does not accept "What's New" on an app's first version.
    if has_live_version:
        localizations = client.get_all(f"/v1/appStoreVersions/{version_id}/appStoreVersionLocalizations?limit=50")
        if not localizations:
            raise SubmitError(f"{version} has no localizations to put the release notes on")
        for localization in localizations:
            client.request("PATCH", f"/v1/appStoreVersionLocalizations/{localization['id']}", {"data": {
                "type": "appStoreVersionLocalizations", "id": localization["id"],
                "attributes": {"whatsNew": notes},
            }})
        print(f"Set the release notes on {len(localizations)} localization(s)")
    else:
        print("No version is live yet, so Apple takes no release notes for this one.")

    open_submissions = client.request("GET", "/v1/reviewSubmissions?" + urllib.parse.urlencode({
        "filter[app]": app_id, "filter[platform]": "IOS", "filter[state]": "READY_FOR_REVIEW"})).get("data", [])
    if open_submissions:
        submission_id = open_submissions[0]["id"]
    else:
        submission_id = client.request("POST", "/v1/reviewSubmissions", {"data": {
            "type": "reviewSubmissions", "attributes": {"platform": "IOS"},
            "relationships": {"app": {"data": {"type": "apps", "id": app_id}}},
        }})["data"]["id"]

    items = client.get_all(f"/v1/reviewSubmissions/{submission_id}/items?include=appStoreVersion&limit=50")
    item_versions = [(i.get("relationships", {}).get("appStoreVersion", {}).get("data") or {}).get("id") for i in items]
    if any(v != version_id for v in item_versions):
        raise SubmitError("the open review submission holds other items. Review it in App Store Connect first")
    if version_id not in item_versions:
        client.request("POST", "/v1/reviewSubmissionItems", {"data": {
            "type": "reviewSubmissionItems",
            "relationships": {
                "reviewSubmission": {"data": {"type": "reviewSubmissions", "id": submission_id}},
                "appStoreVersion": {"data": {"type": "appStoreVersions", "id": version_id}},
            },
        }})

    client.request("PATCH", f"/v1/reviewSubmissions/{submission_id}", {"data": {
        "type": "reviewSubmissions", "id": submission_id, "attributes": {"submitted": True},
    }})
    print(f"Submitted {version} ({build}) to App Review. It releases automatically once approved.")
    return "submitted"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check-notes", metavar="FILE", help="only check a release notes file")
    parser.add_argument("--version")
    parser.add_argument("--build")
    parser.add_argument("--notes")
    args = parser.parse_args()
    try:
        if args.check_notes:
            read_notes(args.check_notes)
            return
        if not (args.version and args.build and args.notes):
            parser.error("--version, --build and --notes are required")
        notes = read_notes(args.notes)
        missing = [n for n in ("ASC_KEY_ID", "ASC_ISSUER_ID", "ASC_KEY_P8_BASE64") if not os.environ.get(n)]
        if missing:
            raise SubmitError("missing " + ", ".join(missing))
        with tempfile.TemporaryDirectory() as folder:
            key_path = os.path.join(folder, "key.p8")
            descriptor = os.open(key_path, os.O_WRONLY | os.O_CREAT, 0o600)
            with os.fdopen(descriptor, "wb") as handle:
                handle.write(base64.b64decode(os.environ["ASC_KEY_P8_BASE64"]))
            submit(Client(os.environ["ASC_KEY_ID"], os.environ["ASC_ISSUER_ID"], key_path),
                   args.version, args.build, notes)
    except SubmitError as error:
        print(f"::error::{error}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
