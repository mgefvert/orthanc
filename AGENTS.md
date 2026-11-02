# Orthanc contributor guidance

Follow the existing organization and the surrounding code when making changes. Optimize for straightforward, readable 
code. Avoid adding abstraction layers, projects, dependencies, or configuration options without a concrete need.

## Organization and naming

- Keep one main entity, command, controller, or middleware class per file. Do not consolidate unrelated types into large files 
  such as `Models.cs` or `Commands.cs`. Small supporting records may stay beside the service that uses them.
- Persistence entities belong in `Orthanc.Core/Entities` and use the `Db` prefix: `DbCustomer`, `DbJobRun`, etc.
- Business logic belongs in `Orthanc.Core/Services`. Use descriptive service names such as `IngestionService`, `ClassifierService`, 
  and `ReportService`. Small focused helpers such as `CustomerTime` need not be renamed just for a suffix.
- HTTP actions belong in `Orthanc.Server/Controllers`, and cross-cutting request behavior belongs in `Orthanc.Server/Middleware`.
- Admin commands belong in `Orthanc.Admin/Commands`, use the existing shared base commands, and follow DotNetCommons' 
  `CommandAction` pattern. Keep argument definitions in `AdminArgs.cs`.
- Use file-scoped namespaces that follow the project and folder structure.

## C# layout and readability

- Use four spaces and Allman braces. Separate logical steps with blank lines: validation, queries, object construction, persistence, 
  logging, and return values.
- Prefer early guard clauses. A simple conditional return/throw/continue can omit braces; use blocks for multi-statement branches. 
  Avoid compressed method bodies with several unrelated statements on one line.
- Use ordinary block-bodied methods for meaningful logic. Expression bodies are appropriate for short properties, simple helpers,
  and concise projections.
- Format substantial object initializers with one property per line. Put long record parameter lists and complicated conditions on 
  separate lines.
- Break longer LINQ and builder chains across lines, normally one operation per line. Give intermediate queries or expressions a 
  name when it makes the next step easier to read (for example, `orderedPatterns` or `recipients`).
- Match the local alignment style: related declarations, assignments, and object initializer properties may align their `=` signs. 
- Use camelCase parameters/locals, PascalCase types/members, and `_camelCase` private fields. Prefer descriptive helper names such 
  as `OpenContext`, `GetArgsCustomer`, and `ConfigureCustomerFromArgs` over vague names such as `Open` or `Configure`.
- Preserve established public API names; do not mechanically add `Async` suffixes or rename symbols as an incidental style cleanup.

## Controllers, middleware, and dependency injection

- Use `[ApiController]`, attribute routes, and `ActionResult`/`Task<ActionResult>` for HTTP endpoints. 
  Do not replace controllers with inline `MapGet`/`MapPost` handlers in `Program.cs`.
- Keep `Program.cs` focused on registrations, configuration, pipeline composition, and controller mapping.
- In controllers and middleware, prefer explicit constructors assigning dependencies to private readonly fields. Existing primary 
  constructors in small services/workers are also acceptable.
- Keep controller actions thin: translate HTTP input/output, delegate business work to services, and log useful events. 
  Keep authentication and exception handling in their dedicated middleware.

## Persistence and shared infrastructure

- Group EF mappings in one `b.Entity<DbEntity>(e => { ... })` block per entity. Keep properties, indexes, and relationships together.
- Use Oracle's MySQL provider. Maintain `database/schema.sql` manually alongside entity mappings; do not introduce 
  migrations or automatic production schema DDL. Preserve provider-specific converters and explicit column types intentionally.
- Store event timestamps in UTC with the established `...Z` naming convention; timezone conversion belongs at scheduling/reporting boundaries.
- Reuse DotNetCommons helpers. Read relevant `PROJECT_CONTEXT.md` files before changing or replacing shared behavior. 
  Use its email integration, CommonLogging format, IP-ban middleware, admin command infrastructure, and `MySqlCnfReader`.
- Keep cancellation tokens flowing through asynchronous I/O. Use `await using` for async-disposable resources in async methods where appropriate.
- Use structured logging; keep PSKs, connection strings, and raw job payloads out of diagnostics.  
  Preserve tenant checks, output bounds, and regex time limits.

## Scope and verification

- Preserve the Python collector's standard-library-only design and thin Bash/CMD launchers. Respect `.gitattributes` line endings.
- Keep changes focused. Separate behavioral changes from style cleanup, and do not silently change protocol, SQL precision,
  monitoring defaults, or reporting policy while reorganizing files.
- Run the existing checks relevant to a change:
  - `dotnet build Orthanc.slnx`
  - `dotnet test tests/Orthanc.Tests`
  - `python -m unittest discover -s collector -v` for collector changes.
- The MySQL test is opt-in via `ORTHANC_TEST_MYSQL` and creates/drops its own test database. Use an isolated test server. 
  Do not send real email during tests.
- Add focused behavioral tests for meaningful changes. Routine formatting and file moves do not require new tests; 
  controller/middleware changes should exercise the existing HTTP integration tests.
