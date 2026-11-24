#!/usr/bin/env python3
"""Submit an uploaded Cuebot Companion build to App Review through the App Store Connect API.

Safe to re-run: it picks up an existing version and open submission, and stops without changes when the version is
already with Apple or live. Standard library plus the openssl command, so the job installs nothing.

Reads ASC_KEY_ID, ASC_ISSUER_ID and ASC_KEY_P8_BASE64 from the environment.
"""
import argparse
import base64
import hashlib
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
MAX_REVIEW_NOTES = 4000
# The always-on demo Cuebot (made-up data) that App Review and testers pair with.
TEST_SERVER = "https://cuebot-test.techdaddydigital.com/"
TEST_SERVER_NOTE = (f"No Cuebot is needed to review this app. On this device, open {TEST_SERVER} in Safari, tap "
                    "\"Pair this phone\", then tap Pair in Cuebot Companion. It connects to a demo Cuebot with made-up "
                    "data. To try the Mods tab, tap \"Join as a mod\" on that page, then in the app open Mods, Join, Paste "
                    "the invite link, and choose Sign in with Test account. A recording of the app is attached.")

EDITABLE = {"PREPARE_FOR_SUBMISSION", "READY_FOR_REVIEW", "DEVELOPER_REJECTED", "REJECTED", "METADATA_REJECTED",
            "INVALID_BINARY"}
WITH_APPLE = {"WAITING_FOR_REVIEW", "IN_REVIEW", "WAITING_FOR_EXPORT_COMPLIANCE", "PENDING_APPLE_RELEASE",
              "PENDING_DEVELOPER_RELEASE", "ACCEPTED", "PROCESSING_FOR_DISTRIBUTION", "PROCESSING_FOR_APP_STORE"}
LIVE = {"READY_FOR_DISTRIBUTION", "READY_FOR_SALE"}


class SubmitError(Exception):
    pass


class NotFound(SubmitError):
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
                kind = NotFound if error.code == 404 else SubmitError
                raise kind(f"{method} {urllib.parse.urlsplit(url).path}: HTTP {error.code}: "
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


    def put_part(self, operation, data):
        """Send one part to Apple's upload address. It is pre-signed, so the API token is not sent."""
        url = operation["url"]
        if not (url.startswith("https://") or url.startswith(API + "/")):
            raise SubmitError("Apple returned an upload address that is not HTTPS")
        part = data[operation["offset"]:operation["offset"] + operation["length"]]
        for attempt in range(4):
            request = urllib.request.Request(url, data=part, method=operation.get("method", "PUT"))
            for header in operation.get("requestHeaders", []):
                request.add_header(header["name"], header["value"])
            try:
                with urllib.request.urlopen(request, timeout=300) as response:
                    response.read()
                return
            except urllib.error.URLError as error:
                if attempt == 3:
                    raise SubmitError(f"could not upload the review video: {error}") from None
                self.sleep(5 * (attempt + 1))


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


def read_video(path):
    try:
        with open(path, "rb") as handle:
            data = handle.read()
    except OSError:
        raise SubmitError(f"no review video at {path}") from None
    if not data:
        raise SubmitError(f"{path} is empty")
    return data


def check_test_server(url=TEST_SERVER):
    """A warning only: the attached video still lets App Review see the app if the demo is down."""
    # Cloudflare rejects Python's default User-Agent with a 403, which looks like an outage.
    request = urllib.request.Request(url, headers={"User-Agent": "cuebot-release-check"})
    try:
        with urllib.request.urlopen(request, timeout=20) as response:
            if response.status == 200:
                print(f"Test server is up: {url}")
                return True
    except OSError:
        pass
    print(f"::warning::The test server {url} is not answering. App Review is told to pair with it.")
    return False


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


def review_detail(client, version_id, versions):
    """The version's App Review Information, copied from an earlier version when it has none."""
    try:
        detail = client.request("GET", f"/v1/appStoreVersions/{version_id}/appStoreReviewDetail").get("data")
    except NotFound:
        detail = None
    if detail:
        return detail
    earlier = sorted((v for v in versions if v["id"] != version_id), key=lambda v: state_of(v) not in LIVE)
    for other in earlier:
        try:
            source = client.request("GET", f"/v1/appStoreVersions/{other['id']}/appStoreReviewDetail").get("data")
        except NotFound:
            continue
        if not source:
            continue
        attributes = source.get("attributes", {})
        if attributes.get("demoAccountRequired"):
            raise SubmitError("App Review Information needs a demo account, which this script does not copy")
        copied = {k: attributes[k] for k in ("contactFirstName", "contactLastName", "contactPhone", "contactEmail",
                                             "notes") if attributes.get(k) is not None}
        created = client.request("POST", "/v1/appStoreReviewDetails", {"data": {
            "type": "appStoreReviewDetails", "attributes": {**copied, "demoAccountRequired": False},
            "relationships": {"appStoreVersion": {"data": {"type": "appStoreVersions", "id": version_id}}},
        }})["data"]
        created.setdefault("attributes", {}).update(copied)
        print(f"Copied App Review Information from {other['attributes']['versionString']}")
        return created
    raise SubmitError("no App Review Information to use. Fill it in on the version in App Store Connect")


def add_test_server_note(client, detail):
    """Adds the pairing instructions once and keeps everything else in the notes."""
    current = (detail.get("attributes") or {}).get("notes") or ""
    if TEST_SERVER in current:
        return
    notes = f"{current.rstrip()}\n\n{TEST_SERVER_NOTE}" if current.strip() else TEST_SERVER_NOTE
    if len(notes) > MAX_REVIEW_NOTES:
        print("::warning::The App Review notes have no room for the test server instructions.")
        return
    client.request("PATCH", f"/v1/appStoreReviewDetails/{detail['id']}", {"data": {
        "type": "appStoreReviewDetails", "id": detail["id"], "attributes": {"notes": notes},
    }})
    print("Added the test server to the App Review notes")


def attachment_state(attachment):
    return ((attachment.get("attributes") or {}).get("assetDeliveryState") or {}).get("state")


def wait_for_attachment(client, attachment_id, timeout=900, interval=10):
    waited = 0
    while True:
        attachment = client.request("GET", f"/v1/appStoreReviewAttachments/{attachment_id}")["data"]
        state = attachment_state(attachment)
        if state == "COMPLETE":
            return
        if state == "FAILED":
            raise SubmitError("Apple could not process the review video")
        if waited >= timeout:
            raise SubmitError(f"the review video was not ready in time (last state: {state})")
        client.sleep(interval)
        waited += interval


def attach_review_video(client, detail, video_path, data):
    """Uploads the video unless the version already has a finished attachment, which is kept."""
    detail_id = detail["id"]
    attachments = client.get_all(f"/v1/appStoreReviewDetails/{detail_id}/appStoreReviewAttachments?limit=50")
    if any(attachment_state(a) == "COMPLETE" for a in attachments):
        print("The version already has a review attachment: keeping it.")
        return
    processing = next((a for a in attachments if attachment_state(a) == "UPLOAD_COMPLETE"), None)
    if processing:
        wait_for_attachment(client, processing["id"])
        return
    for attachment in attachments:
        client.request("DELETE", f"/v1/appStoreReviewAttachments/{attachment['id']}")
    created = client.request("POST", "/v1/appStoreReviewAttachments", {"data": {
        "type": "appStoreReviewAttachments",
        "attributes": {"fileName": os.path.basename(video_path), "fileSize": len(data)},
        "relationships": {"appStoreReviewDetail": {"data": {"type": "appStoreReviewDetails", "id": detail_id}}},
    }})["data"]
    operations = created["attributes"].get("uploadOperations") or []
    if not operations:
        raise SubmitError("Apple returned no upload address for the review video")
    for operation in operations:
        client.put_part(operation, data)
    client.request("PATCH", f"/v1/appStoreReviewAttachments/{created['id']}", {"data": {
        "type": "appStoreReviewAttachments", "id": created["id"],
        "attributes": {"uploaded": True,
                       "sourceFileChecksum": hashlib.md5(data, usedforsecurity=False).hexdigest()},
    }})
    wait_for_attachment(client, created["id"])
    print("Attached the review video")


def submit(client, version, build, notes, video_path=None):
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

    if video_path:
        data = read_video(video_path)
        detail = review_detail(client, version_id, versions)
        add_test_server_note(client, detail)
        attach_review_video(client, detail, video_path, data)

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
    parser.add_argument("--check-review-video", metavar="FILE", help="only check the App Review video")
    parser.add_argument("--review-video", metavar="FILE", help="attach this video to App Review Information")
    parser.add_argument("--version")
    parser.add_argument("--build")
    parser.add_argument("--notes")
    args = parser.parse_args()
    try:
        if args.check_notes or args.check_review_video:
            if args.check_notes:
                read_notes(args.check_notes)
            if args.check_review_video:
                read_video(args.check_review_video)
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
            check_test_server()
            submit(Client(os.environ["ASC_KEY_ID"], os.environ["ASC_ISSUER_ID"], key_path),
                   args.version, args.build, notes, args.review_video)
    except SubmitError as error:
        print(f"::error::{error}", file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
