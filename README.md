# Orthanc

Collect scheduled-job output, identify exceptions, and email daily reports.
Orthanc observes jobs; cron or Task Scheduler still starts them.

V1 consists of an ASP.NET Core 10 server, MySQL, a standard-library-only Python
collector, and `orthanc-admin`. New jobs register automatically **without** daily
expectations. Administrators enable monitoring in the database.

## Build and verify

Clone with submodules (or run `git submodule update --init --recursive`). Install
.NET SDK 10 and Python 3.9+. The server uses Oracle's MySQL EF Core provider.

```text
dotnet restore Orthanc.slnx
dotnet build Orthanc.slnx --no-restore
dotnet test tests/Orthanc.Tests --no-restore
python -m unittest discover -s collector -v
```

The MySQL integration test is opt-in: set `ORTHANC_TEST_MYSQL` to a connection
string for an isolated MySQL 9.7 server. The test creates a uniquely named
`orthanc_test_*` database, applies the actual SQL file, exercises Oracle's provider,
and drops that database. The account needs CREATE/DROP DATABASE privileges.
Other server tests use SQLite and an in-process HTTP host; they send no real email.

## Database and configuration

Create a database manually and apply [database/schema.sql](database/schema.sql).
There are no migrations, automatic schema creation, or deployment orchestration.
The schema targets MySQL 9.7 LTS. All event timestamps are UTC; `ReportTime` and
`ExpectedDailyFrom` are customer-local scheduling configuration.

Configure the server through normal .NET configuration, preferably environment
variables or mounted secrets. Important settings:

| Setting | Purpose |
| --- | --- |
| `ConnectionStrings__Orthanc` | Server database connection string |
| `TrustedProxies__0` | Exact nginx proxy IP; add numbered entries as needed |
| `Reports__From` | Sender address |
| `Integrations__EmailConfiguration__Smtp__Host` | SMTP server |
| `Integrations__EmailConfiguration__Smtp__Port` | SMTP port, default 587 |
| `Integrations__EmailConfiguration__Smtp__UseTls` | STARTTLS, default true |
| `Integrations__EmailConfiguration__Smtp__Username` | Optional SMTP username |
| `Integrations__EmailConfiguration__Smtp__Password` | Optional SMTP password |
| `Reports__Enabled` | Background reports and retention, default true |
| `Reports__DebugEmail` | Explicit development-only simulated email, default false |
| `RetentionDays` | Completed-run retention, default 30; minimum 8 |

The API requires HTTPS, either directly or through nginx's trusted
`X-Forwarded-Proto`. Forwarded headers are ignored unless proxy IPs are configured.
Do not expose the backend directly when proxy TLS termination is used.
`GET /health` is liveness; `GET /health/ready` checks the database/schema. Neither
returns sensitive details. The Ubuntu runtime must contain timezone data.

API and admin logging use DotNetCommons `CommonLogging`, configured for UTC.
Request diagnostics omit credentials and raw output. DotNetCommons IP banning
handles repeated 401/403 responses (existing threshold: score above 50, decaying
one per minute; initial ban ten minutes). A separate per-IP token bucket permits
240 burst requests and replenishes 120 per minute. A run normally uses two requests.

## Administrative tool

`orthanc-admin` follows the DotNetCommons `CommandAction` pattern. It uses
`MySqlCnfReader` and MySQL login paths, not command-line passwords. Create login
paths with `mysql_config_editor`. Default files are
`%APPDATA%/MySQL/.mylogin.cnf` on Windows and `~/.mylogin.cnf` on Linux; override
with `--login-file`. The shared reader currently supports host, user and password;
custom port/socket fields are not supported.
For an absolute Unix path, use `--login-file=/path/to/.mylogin.cnf`: the shared
command parser also recognizes `/` as a Windows-style option prefix.

```text
dotnet run --project src/Orthanc.Admin -- --help
```

After publishing the tool, examples are:

```text
orthanc-admin bootstrap -c beta --name "Example" --timezone America/New_York --recipients ops@example.com --report-time 03:00
orthanc-admin customer list -c beta
orthanc-admin psk add -c beta --customer 1 --label backup-host
orthanc-admin psk list -c beta --customer 1
orthanc-admin psk revoke -c beta --customer 1 --key 2
orthanc-admin job list -c beta --customer 1
orthanc-admin report preview -c beta --customer 1 --date 2026-11-01
```

`--database` defaults to `orthanc`. Bootstrap creates a customer and first key in
an already-installed schema. Secrets are shown once, and only SHA-256 verifiers
of random 256-bit keys are stored. Rotate by adding a key, updating collectors,
then revoking the old key. One key belongs to exactly one customer.

`customer update` sets name, timezone, recipients and report time; supply all four
settings to avoid replacing them with defaults. `report test-send` explicitly sends
to the customer's configured recipients using the same SMTP integration. It reads
SMTP settings from environment variables or `appsettings.json` in the current
directory, and does not change automatic report-delivery records. A preview can
contain sensitive log excerpts; treat its output accordingly.

Enable or disable daily expectations manually:

```sql
UPDATE Job SET ExpectedDailyFrom = '2026-11-02' WHERE Id = 7;
UPDATE Job SET ExpectedDailyFrom = NULL WHERE Id = 7;
```

Set the first full local day that should be monitored. Multiple executions per day
are allowed; any observed start satisfies that day's presence expectation. Runs
without completion by the reporting deadline appear as Incomplete. Enabled jobs
with no start appear as Missing only when expectations are configured. Disabling
a job rejects future starts and excludes it from daily reports.

## Collector

Copy `orthanc.py` and the matching launcher together. On Linux, install both into
the same directory on PATH and make `orthanc` executable (`chmod +x orthanc`). On
Windows, put that directory on PATH; `orthanc.cmd` invokes `python`.

```bash
orthanc --jobname nightly-backup --add-timestamps -- /opt/backup.sh
/opt/backup.sh 2>&1 | orthanc --jobname nightly-backup --stdin
```

Use [collector/orthanc.conf.example](collector/orthanc.conf.example) as a starting
point. Default search order is `/etc/orthanc.conf` then
`~/.config/orthanc/orthanc.conf` on Linux; `%PROGRAMDATA%/Orthanc/orthanc.conf`
then `~/orthanc.conf` on Windows. Later files override earlier files. `--config`
loads only the named file. Precedence is CLI, then `ORTHANC_PSK` for the credential,
then INI, then built-in defaults. Restrict credential files to their owner.
For an internal certificate authority, set `ca-file` in INI or use `--ca-file` to
load its PEM trust bundle. Certificate and hostname verification remain enabled.

Arguments after `--` are passed as an argument array without an intervening shell.
To execute shell syntax, explicitly invoke that shell. Stdin mode has unknown
upstream status unless `--exitcode N` is supplied. It returns zero when ingestion
finishes and no upstream status is known; this does not mean the job succeeded.

The collector passes output through unchanged, retains stream labels, and optionally
timestamps stored lines. Cross-stream ordering is best effort. UTF-8 decoding
replaces invalid bytes. Very long unterminated lines are split into bounded chunks.
Stored capture is limited to one MiB and 20,000 lines; output continues draining
after truncation. The API permits one MiB of message text, 20,000 lines, 16,384
characters per line, and an eight-MiB JSON body. Truncated or incompletely classified
output cannot produce a clean Success outcome.

`--timeout` kills the child process tree on expiry. Actual return codes are stored;
POSIX signal exits are returned to the shell as `128 + signal`. `--ignore-exitcode`
changes classification only. API failure prints a local diagnostic and preserves
the child result. Optional `--diagnostic-file` appends local diagnostics; it is not
a retry queue. There is no durable retry storage.

The server assigns the run UUID at start. If registration fails, the command still
runs, and the collector makes one final registration attempt after execution,
marking it late, before completing the run. An ambiguous lost start response can
leave an orphan start record: V1 deliberately has no client-generated identity or
start deduplication token. Completion itself is idempotent; conflicting replacement
data receives HTTP 409. If both registrations fail, captured output is lost when
the collector exits. API outages are not proof the underlying job failed.

## Classification and reports

Rules match raw message text before timestamp/stream formatting. Precedence is
job-specific, customer-specific, global database rules, then built-in rules;
within each scope sort by Priority then Id. First match wins for each line.
Built-ins recognize whole words panic/fatal, error/errors and warn/warning(s).
Severity is `Ignore`, `Warning`, `Error` or `Fatal`. Use an Ignore rule to suppress
false positives, for example:

```sql
INSERT INTO LogPattern (CustomerId, JobId, Regex, Severity, Priority, Enabled)
VALUES (1, 7, '^0 errors$', 'Ignore', 0, TRUE);
```

Patterns are case-insensitive and culture-invariant. Processing is bounded by
100 patterns, a 25 ms timeout per regex evaluation, and a two-second per-run budget.
Invalid patterns are logged by ID and skipped. Original captured output remains
authoritative. Fatal takes precedence over Failed, then Warning. Nonzero exit,
timeout or execution interruption causes failure unless the nonzero exit alone
is explicitly ignored. Unknown exit status with no stronger evidence is Unknown.

Daily reports cover the previous customer-local calendar day, using start times.
A run started yesterday and completed at 02:00 is complete for a 03:00 report.
A completion after the configured report time remains Incomplete in that day's
report. Missing, incomplete, fatal/error and warning details precede a compact
success count. HTML safely encodes logs and includes a plain-text alternative.
Email caps details at 100 exceptions and excerpts at 1,600 characters; counts
include all entries. Full logs remain queryable in MySQL; no read/search API or
web UI is included in V1.

Hourly exception digests consider completion **receipt** times in the preceding
UTC hour and send only when regex error/fatal counts are nonzero. This includes
late completions without rescanning old execution dates. Nonzero exits without
matching error text and warnings appear in the daily report only. Logs arrive
at completion, so hourly alerts do not provide live streaming detection.

The background worker checks every minute, catches up seven daily periods and six
hourly periods after a restart, and retries failed email attempts after five minutes.
Delivery records prevent routine duplicate reports. A crash between SMTP accepting
a message and recording success can still produce a duplicate. Report periods use
timezone-aware local boundaries (including 23/25-hour days); a nonexistent report
time advances to the first valid minute, and an ambiguous time uses its first
occurrence. Historical schedule/timezone edits are not versioned in V1.

Completed runs older than `RetentionDays` are deleted in batches. Unfinished runs
are retained for manual investigation. SMTP is provided by
`DotNetCommons.Services.Email.IEmailIntegration`; additional SMTP providers or SMS
can be added through that library later.

## Runtime image

Publish outside Docker, then package the existing output:

```text
dotnet publish src/Orthanc.Server -c Release -o publish
docker build -t orthanc .
```

The Dockerfile contains no build stage and runs as the unprivileged `app` user on
port 8080 in the Ubuntu .NET 10 runtime. Nginx, TLS certificates, MySQL hosting,
Compose, and production orchestration are intentionally outside this repository.
