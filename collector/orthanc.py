#!/usr/bin/env python3
"""Orthanc V1 collector. Python 3.9+; standard library only."""
import argparse
import configparser
import datetime as dt
import json
import os
from pathlib import Path
import signal
import socket
import ssl
import subprocess
import sys
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

MAX_BYTES = 1024 * 1024
MAX_LINES = 20000
CHUNK_SIZE = 4096


def utc_now():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def config_locations():
    if os.name == "nt":
        return [Path(os.environ.get("PROGRAMDATA", "C:/ProgramData")) / "Orthanc/orthanc.conf",
                Path.home() / "orthanc.conf"]
    return [Path("/etc/orthanc.conf"), Path.home() / ".config/orthanc/orthanc.conf"]


def arguments(argv):
    command = []

    if "--" in argv:
        index = argv.index("--")
        command, argv = argv[index + 1:], argv[:index]

    early = argparse.ArgumentParser(add_help=False)
    early.add_argument("--config")
    known, _ = early.parse_known_args(argv)

    config = configparser.ConfigParser(interpolation=None)
    if known.config:
        with open(known.config, encoding="utf-8") as stream:
            config.read_file(stream)
    else:
        config.read([str(p) for p in config_locations()], encoding="utf-8")

    defaults = dict(config["orthanc"]) if config.has_section("orthanc") else {}

    parser = argparse.ArgumentParser(description=__doc__, epilog="Use -- before the child command. Configuration: CLI > ORTHANC_PSK (credential only) > INI > defaults.")
    parser.add_argument("--config")

    for name in ("jobname", "machinename", "psk", "server", "diagnostic-file", "ca-file"):
        value = os.environ.get("ORTHANC_PSK", defaults.get(name)) if name == "psk" else defaults.get(name)
        parser.add_argument("--" + name, default=value)

    for name in ("add-timestamps", "ignore-exitcode"):
        value = defaults.get(name, "false").lower()
        if value not in configparser.ConfigParser.BOOLEAN_STATES:
            parser.error("Invalid boolean configuration: " + name)
        parser.add_argument("--" + name, action=argparse.BooleanOptionalAction,
                            default=configparser.ConfigParser.BOOLEAN_STATES[value])

    parser.add_argument("--stdin", action="store_true")
    parser.add_argument("--exitcode", type=int, help="Known upstream exit code for stdin mode")
    parser.add_argument("--timeout", type=float, default=defaults.get("timeout"))
    parser.add_argument("--request-timeout", type=float, default=defaults.get("request-timeout", "10"))

    args = parser.parse_args(argv)

    if not args.jobname or not args.psk or not args.server:
        parser.error("--jobname, --server and --psk (or their configuration equivalents) are required")
    parsed = urllib.parse.urlsplit(args.server)
    if parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password or parsed.query or parsed.fragment:
        parser.error("server must be an HTTPS base URL without credentials, query or fragment")
    if args.stdin == bool(command):
        parser.error("Choose either --stdin or a command after --")
    if args.exitcode is not None and not args.stdin:
        parser.error("--exitcode is only valid with --stdin")
    if args.stdin and args.timeout is not None:
        parser.error("--timeout applies to child commands only")
    if not (0 < args.request_timeout <= 120) or (args.timeout is not None and not 0 < args.timeout < float("inf")):
        parser.error("Request timeout must be 0–120 seconds; child timeout must be positive and finite")

    args.machinename = args.machinename or socket.gethostname()
    import re
    for name in (args.machinename, args.jobname):
        if not re.fullmatch(r"[A-Za-z0-9._:/ -]{1,200}", name.strip()):
            parser.error("Machine and job names must be 1–200 ASCII letters, digits, spaces, or . _ - : /")

    return args, command


def diagnostic(args, message):
    # All messages are locally constructed: never print request headers, bodies, URLs or raw exceptions.
    text = "orthanc: " + message
    print(text, file=sys.stderr)

    if args.diagnostic_file:
        try:
            fd = os.open(args.diagnostic_file, os.O_WRONLY | os.O_CREAT | os.O_APPEND, 0o600)
            with os.fdopen(fd, "a", encoding="utf-8") as stream:
                stream.write(utc_now() + " " + text + "\n")
        except OSError:
            print("orthanc: could not write diagnostic file", file=sys.stderr)


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        # Prevent credentials from being forwarded to another origin (or an HTTP URL).
        return None


def post(args, path, payload):
    request = urllib.request.Request(args.server.rstrip("/") + path,
        data=json.dumps(payload, ensure_ascii=True, allow_nan=False).encode("utf-8"),
        headers={"Authorization": "Bearer " + args.psk, "Content-Type": "application/json"}, method="POST")
    try:
        context = ssl.create_default_context(cafile=args.ca_file)
        with urllib.request.build_opener(NoRedirect(), urllib.request.HTTPSHandler(context=context)).open(request, timeout=args.request_timeout) as response:
            return json.loads(response.read(65536))
    except urllib.error.HTTPError as error:
        diagnostic(args, "API submission rejected (HTTP %s)" % error.code)
    except (OSError, ValueError, urllib.error.URLError):
        diagnostic(args, "API submission failed; check connectivity, TLS and server configuration")
    return None


class Capture:
    def __init__(self, timestamps=False):
        self.lines = []
        self.bytes = 0
        self.dropped = 0
        self.truncated = False
        self.timestamps = timestamps
        self.lock = threading.Lock()
        self.closed = False

    def add(self, stream, text):
        size = len(text.encode("utf-8", errors="replace")) + 1
        with self.lock:
            if self.closed:
                return
            if self.bytes + size > MAX_BYTES or len(self.lines) >= MAX_LINES:
                self.truncated = True
                self.dropped += size
                return
            self.bytes += size
            self.lines.append({"stream": stream, "text": text,
                               "timestampZ": utc_now() if self.timestamps else None})

    def read(self, source, label, terminal):
        import codecs
        decoder = codecs.getincrementaldecoder("utf-8")(errors="replace")
        pending = ""
        read = getattr(source, "read1", source.read)
        while True:
            chunk = read(CHUNK_SIZE)
            if not chunk:
                break
            try:
                terminal.buffer.write(chunk)
                terminal.buffer.flush()
            except (OSError, AttributeError):
                pass  # A closed terminal must not stop draining the child pipes.
            pending += decoder.decode(chunk)
            while "\n" in pending:
                line, pending = pending.split("\n", 1)
                self.add(label, line.rstrip("\r"))
            while len(pending) >= CHUNK_SIZE:
                self.add(label, pending[:CHUNK_SIZE])
                pending = pending[CHUNK_SIZE:]
        pending += decoder.decode(b"", final=True)
        if pending:
            self.add(label, pending.rstrip("\r"))


def terminate(process):
    try:
        if os.name == "nt":
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, timeout=10)
        else:
            os.killpg(process.pid, signal.SIGKILL)
    except (OSError, subprocess.SubprocessError):
        pass
    if process.poll() is None:
        process.kill()
    process.wait()


def main(argv=None):
    try:
        args, command = arguments(list(sys.argv[1:] if argv is None else argv))
    except (OSError, configparser.Error):
        print("orthanc: unable to read configuration", file=sys.stderr)
        return 2

    start = {"machineName": args.machinename, "jobName": args.jobname, "startedZ": utc_now(),
             "protocolVersion": "1", "mode": "stdin" if args.stdin else "command", "lateRegistration": False}
    registered = post(args, "/api/v1/jobruns/start", start)

    capture = Capture(args.add_timestamps)
    beginning = time.monotonic()
    timed_out = False
    reason = None

    if args.stdin:
        try:
            capture.read(sys.stdin.buffer, "stdin", sys.stdout)
            exitcode = args.exitcode
            result = args.exitcode if args.exitcode is not None else 0
        except KeyboardInterrupt:
            exitcode, result, reason = None, 130, "collector interrupted"
    else:
        try:
            options = {"creationflags": subprocess.CREATE_NEW_PROCESS_GROUP} if os.name == "nt" else {"start_new_session": True}
            process = subprocess.Popen(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE, **options)
        except OSError:
            diagnostic(args, "could not start child command")
            process = None
        threads = []
        if process is None:
            exitcode, result, reason = None, 127, "child could not be started"
        else:
            for source, label, terminal in ((process.stdout, "stdout", sys.stdout), (process.stderr, "stderr", sys.stderr)):
                thread = threading.Thread(target=capture.read, args=(source, label, terminal), daemon=True)
                thread.start()
                threads.append(thread)
            try:
                exitcode = process.wait(timeout=args.timeout)
            except subprocess.TimeoutExpired:
                timed_out = True
                reason = "child runtime exceeded timeout"
                terminate(process)
                exitcode = process.returncode
            except KeyboardInterrupt:
                reason = "collector interrupted"
                terminate(process)
                exitcode = process.returncode
            result = exitcode if exitcode >= 0 else 128 - exitcode
            for thread in threads:
                thread.join(timeout=2)
            for thread, source in zip(threads, (process.stdout, process.stderr)):
                if not thread.is_alive():
                    source.close()
            if any(thread.is_alive() for thread in threads):
                reason = reason or "output pipes remained open after child exit"
                with capture.lock:
                    capture.truncated = True

    with capture.lock:
        capture.closed = True
        completion = {"finishedZ": utc_now(), "durationSeconds": time.monotonic() - beginning,
                      "exitCode": exitcode, "lines": list(capture.lines), "ignoreExitCode": args.ignore_exitcode,
                      "timedOut": timed_out, "terminationReason": reason,
                      "truncated": capture.truncated, "droppedBytes": capture.dropped}

    if not registered or not isinstance(registered, dict) or not registered.get("id"):
        start["lateRegistration"] = True
        registered = post(args, "/api/v1/jobruns/start", start)

    if isinstance(registered, dict) and isinstance(registered.get("id"), str):
        # Validate only; the server is the sole UUID issuer.
        import uuid
        try:
            run_id = str(uuid.UUID(registered["id"]))
        except ValueError:
            diagnostic(args, "API returned an invalid run identifier")
        else:
            post(args, "/api/v1/jobruns/" + run_id + "/complete", completion)
    else:
        diagnostic(args, "output was not submitted; no run identifier available")

    return result


if __name__ == "__main__":
    sys.exit(main())
