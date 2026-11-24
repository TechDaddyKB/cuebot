#!/usr/bin/env python3
"""Tests for the iOS release scripts, against a fake App Store Connect API. Standard library and openssl only."""
import base64
import json
import os
import subprocess
import sys
import tempfile
import threading
import unittest
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlsplit

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))

import asc_submit  # noqa: E402
import prepare_signing  # noqa: E402

BUNDLE = asc_submit.BUNDLE_ID

PROJECT = """\t\tAAAAAAAAAAAAAAAAAAAAAA01 /* Release */ = {
\t\t\tisa = XCBuildConfiguration;
\t\t\tbuildSettings = {
\t\t\t\tCODE_SIGN_STYLE = Automatic;
\t\t\t\tCURRENT_PROJECT_VERSION = 1;
\t\t\t\tMARKETING_VERSION = 1.0.4;
\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = %s;
\t\t\t};
\t\t\tname = Release;
\t\t};
\t\tAAAAAAAAAAAAAAAAAAAAAA02 /* Debug */ = {
\t\t\tisa = XCBuildConfiguration;
\t\t\tbuildSettings = {
\t\t\t\tCODE_SIGN_STYLE = Automatic;
\t\t\t\tMARKETING_VERSION = 1.0.4;
\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = %s;
\t\t\t};
\t\t\tname = Debug;
\t\t};
\t\tAAAAAAAAAAAAAAAAAAAAAA03 /* Release */ = {
\t\t\tisa = XCBuildConfiguration;
\t\t\tbuildSettings = {
\t\t\t\tCODE_SIGN_STYLE = Automatic;
\t\t\t\tMARKETING_VERSION = 1.0.4;
\t\t\t\tPRODUCT_BUNDLE_IDENTIFIER = %s.uitests;
\t\t\t};
\t\t\tname = Release;
\t\t};
""" % (BUNDLE, BUNDLE, BUNDLE)


class PrepareSigningTests(unittest.TestCase):
    def test_only_the_app_release_configuration_changes(self):
        out = prepare_signing.patch(PROJECT, BUNDLE, "1.0.5", "150.1", "Cuebot App Store")
        release, rest = out.split("AAAAAAAAAAAAAAAAAAAAAA02", 1)
        self.assertIn("CODE_SIGN_STYLE = Manual;", release)
        self.assertIn('PROVISIONING_PROFILE_SPECIFIER = "Cuebot App Store";', release)
        self.assertIn("CURRENT_PROJECT_VERSION = 150.1;", release)
        self.assertIn("MARKETING_VERSION = 1.0.5;", release)
        self.assertNotIn("Manual", rest)
        self.assertNotIn("1.0.5", rest)

    def test_a_missing_target_is_an_error(self):
        with self.assertRaises(ValueError):
            prepare_signing.patch(PROJECT, "com.example.other", "1.0.5", "1", "x")


class FakeAsc:
    def __init__(self, versions=(), build_states=("VALID",), open_submission=None, items=(),
                 review_detail=None, earlier_detail=None, attachments=(), attachment_states=("COMPLETE",)):
        self.versions = [dict(v) for v in versions]
        self.build_states = list(build_states)
        self.open_submission = open_submission
        self.items = list(items)
        self.writes = []
        self.tokens = []
        self.review_detail = review_detail
        self.earlier_detail = earlier_detail
        self.attachments = list(attachments)
        self.attachment_states = list(attachment_states)
        self.uploaded = b""
        self.upload_tokens = []

    def handle(self, method, path, query, body):
        if method != "GET":
            self.writes.append((method, path, body))
        if path.endswith("/appStoreReviewDetail"):
            version_id = path.split("/")[3]
            detail = self.review_detail if version_id in ("v", "newv") else self.earlier_detail
            return {"data": detail}
        if path == "/v1/appStoreReviewDetails" and method == "POST":
            return {"data": {"id": "detail-new", "attributes": {}}}
        if path.endswith("/appStoreReviewAttachments") and method == "GET":
            return {"data": [{"id": i, "attributes": {"assetDeliveryState": {"state": state}}}
                             for i, state in self.attachments]}
        if path == "/v1/appStoreReviewAttachments" and method == "POST":
            return {"data": {"id": "att1", "attributes": {"uploadOperations": [
                {"method": "PUT", "url": f"{asc_submit.API}/upload/1", "offset": 0, "length": 4, "requestHeaders": []},
                {"method": "PUT", "url": f"{asc_submit.API}/upload/2", "offset": 4, "length": 3, "requestHeaders": []},
            ]}}}
        if path.startswith("/v1/appStoreReviewAttachments/") and method == "GET":
            state = self.attachment_states.pop(0) if len(self.attachment_states) > 1 else self.attachment_states[0]
            return {"data": {"id": path.rsplit("/", 1)[1], "attributes": {"assetDeliveryState": {"state": state}}}}
        if path == "/v1/apps":
            return {"data": [{"id": "app1", "attributes": {"bundleId": query["filter[bundleId]"][0]}}]}
        if path == "/v1/apps/app1/appStoreVersions":
            return {"data": [{"id": v["id"], "attributes": {"versionString": v["version"], "appVersionState": v["state"]}}
                             for v in self.versions]}
        if path == "/v1/builds":
            state = self.build_states.pop(0) if len(self.build_states) > 1 else self.build_states[0]
            if state is None:
                return {"data": []}
            return {"data": [{"id": "build1", "attributes": {"processingState": state}}]}
        if path == "/v1/appStoreVersions" and method == "POST":
            return {"data": {"id": "newv", "attributes": {}}}
        if path.endswith("/appStoreVersionLocalizations"):
            return {"data": [{"id": "loc1"}, {"id": "loc2"}]}
        if path == "/v1/reviewSubmissions" and method == "GET":
            return {"data": [{"id": self.open_submission}] if self.open_submission else []}
        if path == "/v1/reviewSubmissions" and method == "POST":
            return {"data": {"id": "sub1"}}
        if path.endswith("/items"):
            return {"data": [{"id": f"i{n}", "relationships": {"appStoreVersion": {"data": {"id": v}}}}
                             for n, v in enumerate(self.items)]}
        return {}


class SubmitTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.folder = tempfile.TemporaryDirectory()
        cls.key = os.path.join(cls.folder.name, "key.p8")
        subprocess.run(["openssl", "genpkey", "-algorithm", "EC", "-pkeyopt", "ec_paramgen_curve:P-256",
                        "-out", cls.key], check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls):
        cls.folder.cleanup()

    def run_submit(self, fake, video=None):
        class Handler(BaseHTTPRequestHandler):
            def _serve(self):
                parts = urlsplit(self.path)
                fake.tokens.append(self.headers.get("Authorization", ""))
                length = int(self.headers.get("Content-Length") or 0)
                raw = self.rfile.read(length) if length else b""
                if parts.path.startswith("/upload/"):
                    fake.upload_tokens.append(self.headers.get("Authorization"))
                    fake.uploaded += raw
                    self.send_response(200)
                    self.send_header("Content-Length", "0")
                    self.end_headers()
                    return
                body = json.loads(raw) if raw else None
                payload = json.dumps(fake.handle(self.command, parts.path, parse_qs(parts.query), body)).encode()
                self.send_response(200)
                self.send_header("Content-Length", str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)

            do_GET = do_POST = do_PATCH = do_PUT = do_DELETE = _serve

            def log_message(self, *args):
                pass

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        threading.Thread(target=server.serve_forever, daemon=True).start()
        old = asc_submit.API
        asc_submit.API = f"http://127.0.0.1:{server.server_port}"
        try:
            client = asc_submit.Client("KEYID12345", "issuer", self.key, sleep=lambda _: None)
            return asc_submit.submit(client, "1.0.5", "150.1", "Notes", video)
        finally:
            asc_submit.API = old
            server.shutdown()
            server.server_close()

    def paths(self, fake):
        return [(m, p) for m, p, _ in fake.writes]

    def test_a_new_version_goes_through_every_step(self):
        fake = FakeAsc(versions=[{"id": "old", "version": "1.0.4", "state": "READY_FOR_SALE"}],
                       build_states=(None, "PROCESSING", "VALID"))
        self.assertEqual(self.run_submit(fake), "submitted")
        self.assertEqual(self.paths(fake), [
            ("POST", "/v1/appStoreVersions"),
            ("PATCH", "/v1/appStoreVersions/newv/relationships/build"),
            ("PATCH", "/v1/appStoreVersionLocalizations/loc1"),
            ("PATCH", "/v1/appStoreVersionLocalizations/loc2"),
            ("POST", "/v1/reviewSubmissions"),
            ("POST", "/v1/reviewSubmissionItems"),
            ("PATCH", "/v1/reviewSubmissions/sub1"),
        ])
        self.assertEqual(fake.writes[0][2]["data"]["attributes"]["releaseType"], "AFTER_APPROVAL")
        self.assertEqual(fake.writes[2][2]["data"]["attributes"]["whatsNew"], "Notes")
        self.assertIs(fake.writes[-1][2]["data"]["attributes"]["submitted"], True)

    def test_the_token_is_a_well_formed_es256_jwt(self):
        fake = FakeAsc(versions=[{"id": "v", "version": "1.0.5", "state": "IN_REVIEW"}])
        self.run_submit(fake)
        header, claims, signature = fake.tokens[0].removeprefix("Bearer ").split(".")
        decode = lambda part: base64.urlsafe_b64decode(part + "=" * (-len(part) % 4))
        self.assertEqual(json.loads(decode(header))["alg"], "ES256")
        self.assertEqual(json.loads(decode(claims))["aud"], "appstoreconnect-v1")
        self.assertEqual(len(decode(signature)), 64)

    def test_no_release_notes_on_the_first_version(self):
        fake = FakeAsc(versions=[{"id": "v", "version": "1.0.5", "state": "PREPARE_FOR_SUBMISSION"}])
        self.assertEqual(self.run_submit(fake), "submitted")
        self.assertFalse([p for _, p in self.paths(fake) if "Localizations" in p])

    def test_a_version_already_with_apple_is_left_alone(self):
        for state in ("WAITING_FOR_REVIEW", "IN_REVIEW", "READY_FOR_SALE"):
            fake = FakeAsc(versions=[{"id": "v", "version": "1.0.5", "state": state}])
            self.assertEqual(self.run_submit(fake), "unchanged")
            self.assertEqual(fake.writes, [])

    def test_another_version_in_review_blocks_without_writing(self):
        fake = FakeAsc(versions=[{"id": "old", "version": "1.0.4", "state": "WAITING_FOR_REVIEW"}])
        with self.assertRaisesRegex(asc_submit.SubmitError, "1.0.4 is still WAITING_FOR_REVIEW"):
            self.run_submit(fake)
        self.assertEqual(fake.writes, [])

    def test_a_failed_build_stops_before_any_write(self):
        fake = FakeAsc(versions=[{"id": "old", "version": "1.0.4", "state": "READY_FOR_SALE"}], build_states=("INVALID",))
        with self.assertRaisesRegex(asc_submit.SubmitError, "INVALID"):
            self.run_submit(fake)
        self.assertEqual(fake.writes, [])

    def test_a_retry_reuses_the_open_submission_and_item(self):
        fake = FakeAsc(versions=[{"id": "old", "version": "1.0.4", "state": "READY_FOR_SALE"},
                                 {"id": "v", "version": "1.0.5", "state": "PREPARE_FOR_SUBMISSION"}],
                       open_submission="sub9", items=["v"])
        self.assertEqual(self.run_submit(fake), "submitted")
        paths = self.paths(fake)
        self.assertNotIn(("POST", "/v1/reviewSubmissions"), paths)
        self.assertNotIn(("POST", "/v1/reviewSubmissionItems"), paths)
        self.assertEqual(paths[-1], ("PATCH", "/v1/reviewSubmissions/sub9"))

    def test_an_open_submission_with_other_items_is_not_submitted(self):
        fake = FakeAsc(versions=[{"id": "v", "version": "1.0.5", "state": "PREPARE_FOR_SUBMISSION"}],
                       open_submission="sub9", items=["something-else"])
        with self.assertRaisesRegex(asc_submit.SubmitError, "other items"):
            self.run_submit(fake)
        self.assertNotIn(("PATCH", "/v1/reviewSubmissions/sub9"), self.paths(fake))


class ReviewVideoTests(SubmitTests):
    DETAIL = {"id": "detail1", "attributes": {"notes": "Existing notes", "contactEmail": "review@example.test"}}
    OLD = {"id": "old", "version": "1.0.4", "state": "READY_FOR_SALE"}

    def setUp(self):
        self.video_folder = tempfile.TemporaryDirectory()
        self.video = os.path.join(self.video_folder.name, "review.mp4")
        with open(self.video, "wb") as handle:
            handle.write(b"1234567")

    def tearDown(self):
        self.video_folder.cleanup()

    def fake(self, **extra):
        extra.setdefault("review_detail", self.DETAIL)
        return FakeAsc(versions=[self.OLD], **extra)

    def body(self, fake, method, path):
        return next(b for m, p, b in fake.writes if (m, p) == (method, path))

    def test_uploads_the_video_before_submitting(self):
        fake = self.fake(attachment_states=("UPLOAD_COMPLETE", "COMPLETE"))
        self.assertEqual(self.run_submit(fake, self.video), "submitted")
        self.assertEqual(fake.uploaded, b"1234567")
        self.assertEqual(fake.upload_tokens, [None, None])
        finished = self.body(fake, "PATCH", "/v1/appStoreReviewAttachments/att1")["data"]["attributes"]
        self.assertIs(finished["uploaded"], True)
        self.assertEqual(finished["sourceFileChecksum"], "fcea920f7412b5da7be0cf42b8c93759")
        paths = self.paths(fake)
        self.assertLess(paths.index(("PATCH", "/v1/appStoreReviewAttachments/att1")),
                        paths.index(("POST", "/v1/reviewSubmissions")))

    def test_keeps_the_notes_and_adds_the_test_server_once(self):
        fake = self.fake()
        self.run_submit(fake, self.video)
        notes = self.body(fake, "PATCH", "/v1/appStoreReviewDetails/detail1")["data"]["attributes"]["notes"]
        self.assertTrue(notes.startswith("Existing notes\n\n"))
        self.assertIn(asc_submit.TEST_SERVER, notes)
        again = self.fake(review_detail={"id": "detail1", "attributes": {"notes": notes}})
        self.run_submit(again, self.video)
        self.assertNotIn(("PATCH", "/v1/appStoreReviewDetails/detail1"), self.paths(again))

    def test_notes_with_no_room_are_left_alone(self):
        fake = self.fake(review_detail={"id": "detail1", "attributes": {"notes": "x" * 3990}})
        self.assertEqual(self.run_submit(fake, self.video), "submitted")
        self.assertNotIn(("PATCH", "/v1/appStoreReviewDetails/detail1"), self.paths(fake))

    def test_a_finished_attachment_is_kept(self):
        fake = self.fake(attachments=[("a1", "COMPLETE")])
        self.assertEqual(self.run_submit(fake, self.video), "submitted")
        self.assertEqual(fake.uploaded, b"")
        self.assertNotIn(("POST", "/v1/appStoreReviewAttachments"), self.paths(fake))

    def test_a_failed_attachment_is_replaced(self):
        fake = self.fake(attachments=[("a1", "FAILED")])
        self.run_submit(fake, self.video)
        self.assertIn(("DELETE", "/v1/appStoreReviewAttachments/a1"), self.paths(fake))
        self.assertEqual(fake.uploaded, b"1234567")

    def test_a_video_apple_rejects_stops_the_submission(self):
        fake = self.fake(attachment_states=("FAILED",))
        with self.assertRaisesRegex(asc_submit.SubmitError, "could not process"):
            self.run_submit(fake, self.video)
        self.assertNotIn(("POST", "/v1/reviewSubmissions"), self.paths(fake))

    def test_a_new_version_copies_the_review_information(self):
        fake = self.fake(review_detail=None, earlier_detail=self.DETAIL)
        self.assertEqual(self.run_submit(fake, self.video), "submitted")
        created = self.body(fake, "POST", "/v1/appStoreReviewDetails")["data"]
        self.assertEqual(created["attributes"]["contactEmail"], "review@example.test")
        self.assertEqual(created["relationships"]["appStoreVersion"]["data"]["id"], "newv")

    def test_no_review_information_stops_the_submission(self):
        fake = self.fake(review_detail=None)
        with self.assertRaisesRegex(asc_submit.SubmitError, "App Review Information"):
            self.run_submit(fake, self.video)
        self.assertNotIn(("POST", "/v1/reviewSubmissions"), self.paths(fake))

    def test_a_missing_video_stops_before_the_review_steps(self):
        fake = self.fake()
        missing = os.path.join(self.video_folder.name, "missing.mp4")
        with self.assertRaisesRegex(asc_submit.SubmitError, "no review video"):
            self.run_submit(fake, missing)


class TestServerCheckTests(unittest.TestCase):
    def serve(self, status_for):
        class Handler(BaseHTTPRequestHandler):
            def _answer(self):
                self.send_response(status_for(self.headers.get("User-Agent", "")))
                self.send_header("Content-Length", "0")
                self.end_headers()

            do_GET = _answer

            def log_message(self, *args):
                """Keep request logs out of the test output."""

        server = ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        self.addCleanup(thread.join)
        self.addCleanup(server.server_close)
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_port}/"

    def test_passes_a_block_on_the_default_python_user_agent(self):
        url = self.serve(lambda agent: 403 if agent.startswith("Python-urllib") else 200)
        self.assertTrue(asc_submit.check_test_server(url))

    def test_still_warns_when_the_server_fails(self):
        self.assertFalse(asc_submit.check_test_server(self.serve(lambda agent: 503)))


class NotesTests(unittest.TestCase):
    def test_limits(self):
        with tempfile.TemporaryDirectory() as folder:
            path = os.path.join(folder, "n.txt")
            for text, ok in (("Fixes", True), ("   ", False), ("x" * 4001, False)):
                with open(path, "w", encoding="utf-8") as handle:
                    handle.write(text)
                if ok:
                    self.assertEqual(asc_submit.read_notes(path), text)
                else:
                    with self.assertRaises(asc_submit.SubmitError):
                        asc_submit.read_notes(path)
            with self.assertRaises(asc_submit.SubmitError):
                asc_submit.read_notes(os.path.join(folder, "missing.txt"))


if __name__ == "__main__":
    unittest.main()
