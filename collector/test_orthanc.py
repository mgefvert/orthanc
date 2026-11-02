import contextlib
import io
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

import orthanc


class CollectorTests(unittest.TestCase):
    def args(self, *command):
        return ["--server", "https://example.invalid", "--psk", "a" * 64, "--jobname", "test",
                "--", sys.executable, "-c", *command]

    def test_exit_code_and_stream_labels_survive_submission_failure(self):
        with patch.object(orthanc, "post", return_value=None), contextlib.redirect_stderr(io.StringIO()):
            result = orthanc.main(self.args("import sys; print('out'); print('err', file=sys.stderr); sys.exit(7)"))
        self.assertEqual(7, result)

    def test_completion_preserves_exit_and_timestamps(self):
        payloads = []
        def post(args, path, payload):
            payloads.append((path, payload))
            return {"id": "12345678-1234-1234-1234-123456789abc"}
        argv = self.args("import sys; print('out'); print('err', file=sys.stderr); sys.exit(4)")
        argv.insert(0, "--add-timestamps")
        argv.insert(0, "--ignore-exitcode")
        with patch.object(orthanc, "post", side_effect=post):
            result = orthanc.main(argv)
        self.assertEqual(4, result)
        completed = payloads[-1][1]
        self.assertEqual(4, completed["exitCode"])
        self.assertTrue(completed["ignoreExitCode"])
        self.assertEqual({"stdout", "stderr"}, {line["stream"] for line in completed["lines"]})
        self.assertTrue(all(line["timestampZ"] for line in completed["lines"]))
        self.assertNotIn("id", payloads[0][1])

    def test_stdin_unknown_and_explicit_exit(self):
        for known, expected_result in ((None, 0), (5, 5)):
            payloads = []
            def post(args, path, payload):
                payloads.append(payload)
                return {"id": "12345678-1234-1234-1234-123456789abc"}
            argv = ["--stdin", "--server", "https://example.invalid", "--psk", "a" * 64, "--jobname", "pipe"]
            if known is not None:
                argv += ["--exitcode", str(known)]
            source = io.TextIOWrapper(io.BytesIO(b"hello\n"))
            with patch.object(orthanc, "post", side_effect=post), patch.object(sys, "stdin", source):
                self.assertEqual(expected_result, orthanc.main(argv))
            self.assertEqual(known, payloads[-1]["exitCode"])

    def test_failed_start_is_registered_late_without_client_uuid(self):
        payloads = []
        def post(args, path, payload):
            payloads.append((path, dict(payload)))
            return None if len(payloads) == 1 else {"id": "12345678-1234-1234-1234-123456789abc"}
        with patch.object(orthanc, "post", side_effect=post):
            self.assertEqual(0, orthanc.main(self.args("pass")))
        self.assertFalse(payloads[0][1]["lateRegistration"])
        self.assertTrue(payloads[1][1]["lateRegistration"])
        self.assertNotIn("id", payloads[1][1])
        self.assertTrue(payloads[-1][0].endswith("/complete"))

    def test_capture_is_bounded_and_drain_continues(self):
        capture = orthanc.Capture()
        data = (b"x" * 4096 + b"\n") * 400
        terminal = io.TextIOWrapper(io.BytesIO())
        capture.read(io.BytesIO(data), "stdout", terminal)
        self.assertLessEqual(capture.bytes, orthanc.MAX_BYTES)
        self.assertGreater(capture.dropped, 0)
        self.assertTrue(capture.truncated)
        self.assertEqual(len(data), terminal.buffer.tell())

    def test_timeout_terminates_child_and_records_failure(self):
        payloads = []
        def post(args, path, payload):
            payloads.append(payload)
            return {"id": "12345678-1234-1234-1234-123456789abc"}
        argv = ["--timeout", "0.1"] + self.args("import time; time.sleep(60)")
        with patch.object(orthanc, "post", side_effect=post):
            self.assertNotEqual(0, orthanc.main(argv))
        self.assertTrue(payloads[-1]["timedOut"])
        self.assertIsNotNone(payloads[-1]["terminationReason"])

    def test_configuration_cli_precedence_and_literal_percent_secret(self):
        with tempfile.TemporaryDirectory() as temp:
            config = Path(temp) / "orthanc.conf"
            config.write_text("[orthanc]\nserver=https://example.invalid\npsk=literal%secret\njobname=config\n", encoding="utf-8")
            with patch.dict(os.environ, {}, clear=True):
                args, command = orthanc.arguments(["--config", str(config), "--jobname", "cli", "--", "echo", "hello world"])
            self.assertEqual("cli", args.jobname)
            self.assertEqual("literal%secret", args.psk)
            self.assertEqual(["echo", "hello world"], command)

    def test_insecure_urls_rejected(self):
        with contextlib.redirect_stderr(io.StringIO()), self.assertRaises(SystemExit):
            orthanc.arguments(["--server", "http://example.invalid", "--psk", "secret", "--jobname", "job", "--stdin"])

    def test_unreachable_https_real_process_preserves_exit_without_secret(self):
        result = subprocess.run([sys.executable, str(Path(orthanc.__file__)), "--server", "https://127.0.0.1:1",
            "--request-timeout", "0.1", "--psk", "private-secret", "--jobname", "test", "--", sys.executable,
            "-c", "import sys; print('hello'); sys.exit(6)"], capture_output=True, timeout=10)
        self.assertEqual(6, result.returncode)
        self.assertIn(b"hello", result.stdout)
        self.assertIn(b"API submission failed", result.stderr)
        self.assertNotIn(b"private-secret", result.stderr)


if __name__ == "__main__":
    unittest.main()
