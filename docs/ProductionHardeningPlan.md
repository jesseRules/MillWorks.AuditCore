# STOP — READ THIS BEFORE YOU DO ANYTHING

> **If you are a Claude instance picking up this plan, these rules override your defaults.** Every rule below has already been violated in a prior session on this exact document. Violating any of them is a failure of the task regardless of how good the resulting code looks. Do not treat this section as boilerplate — it is the contract for the work.

## The five hard constraints

1. **Do not create new files. Do not add helpers.** If you think a helper is needed, **stop and ask.** No utility classes, no extraction classes, no "cloning" / "mapping" / "builder" helpers. The plan names the files that change; those are the only files that change.
2. **Do not touch code that is not named in the current instruction, even to improve it.** No "simplifications." No "cleanups." No deleting checks, markers, or branches you judge to be legacy. If you believe something outside the named scope needs to change, **stop and ask.**
3. **Before any edit, list every decision in the edit that is not literally in this plan. Wait for approval before writing.** Use the preflight-diff format in "Execution contract" below. This includes choices like single- vs double-invocation, keep-vs-remove a check, inline-vs-extract, which overload to add, which constructor to touch. If it is a choice, it gets listed. "I'll just pick the cleaner one" is a violation.
4. **One checkbox at a time. Build and test after every single file change. Do not batch.** The unit of work is a single plan checkbox, not a phase and not a sub-bullet. No multi-bullet rewrites, no multi-file rewrites before the first `dotnet build` / `dotnet test`. If one file's edit breaks the build, fix it before touching the next file. Shipping partial unverified state has already burned this project — do not repeat it.
5. **If any instruction is ambiguous, stop and ask. Do not infer.** Ambiguity is a hard stop, not permission to choose. "I used my judgment" is a violation.

## The plan is the spec

This document is the specification. You do **not** get to edit the spec while implementing it. If you believe the spec is wrong, **stop and raise it to Jesse.** You do not route around it with "clean break" rewrites, new helpers, or simplifications that feel cleaner. The plan is what gets built. If it changes, Jesse changes it — not you.

## Execution contract

### Preflight diff — required before every edit

For the single checkbox you are about to implement:

1. Quote the exact checkbox text verbatim.
2. List every sub-bullet under it.
3. For each sub-bullet, mark one of: `satisfied` (current code already does this — cite the file:line), `missing` (needs implementation), `blocked` (cannot be done as written — explain why).
4. List every decision the checkbox leaves unresolved. Per constraint #3, if it is a choice, it gets listed.
5. If any sub-bullet is `blocked` or any decision is unresolved, **stop and ask.** Do not implement the satisfied/missing items in isolation — the checkbox is the unit of completion, not the bullet.
6. Only when nothing is blocked and no decisions are unresolved: implement exactly that checkbox, nothing adjacent.

### No aspirational docs

`README.md`, `ARCHITECTURE.md`, `ComplianceTraceabilityMatrix.md`, `CHANGELOG.md`, and every other document in this repo describe only behavior that is **implemented and verified in the current working tree.** Do not write docs ahead of the code. Do not write docs describing what the code "will do" after a later checkbox. A checkbox's doc update is part of that checkbox's work, produced after its code and test are green — never before.

### Required final-response shape after every checkbox

Your final message after implementing a checkbox must include, in this order and with these literal headings:

1. **Checkbox completed** — quoted verbatim from the plan.
2. **Changed files** — exhaustive list, no summarization.
3. **Verification run** — the exact command(s) executed and their pass/fail outcome (e.g., `dotnet build` → 0 errors; `dotnet test --filter FullyQualifiedName~Configuration.OptionsFlowTests` → 4/4 pass).
4. **Remaining unchecked boxes in this phase** — copied verbatim from the plan.
5. **Deviations** — every decision made that is not literally in the plan. Each deviation must cite Jesse's prior approval message in this conversation. If no approval exists, the deviation is a violation — revert it before sending the response.

Do **not** check the plan's box until the verification run is green and the final-response shape is produced. Do not describe work as complete in any other format.

## Failure patterns from prior sessions — recognize these in yourself

Every one of these happened while implementing this exact document. If you catch yourself doing any of them, stop immediately and surface it to Jesse.

- **Treating ambiguity as permission.** When an instruction does not resolve a fork in the code, the correct action is *ask*, not *fill the gap with my own judgment*.
- **Bias toward visible action.** Pausing to ask feels like not-helping. It is not. Wrong action wastes Jesse's time far worse than waiting does.
- **Post-hoc rationalization of whichever direction you are already moving.** Tradeoff write-ups are not neutral analysis — they are sales pitches for whatever the model is about to do. Your own "tradeoff" reasoning cannot be trusted as the basis for acting. External approval is.
- **Pattern-flipping on pushback.** When Jesse pushes back on an approach, that is *stop and re-check every related assumption*, not *approval to implement the opposite*. "He didn't like defensive double-invocation, so I switched to single-invocation and kept writing" is the failure mode.
- **Missing stop-signals.** When Jesse says something like "this is greenfield, why do you keep bringing up ACED," that is a full stop. Note it, ask what else you are getting wrong, then wait. Do **not** absorb the correction, update a memory, and charge back into the same pattern with different specifics.
- **Conflating "best quality work" with "my best engineering judgment."** In this codebase, quality means executing Jesse's design faithfully. It does not mean improving the design while passing through. Removing a marker check, deleting a guard, or "cleaning up" a factory while working on an unrelated phase is not quality — it is scope creep dressed as quality.
- **Silent scope expansion via memory updates.** Do not save "learnings" to memory to justify past-tense overreach. If a correction happens, the correction is to behavior in this session, not to the memory file.

## Required first move

Before you touch any code, your first response in a session using this plan must:

1. Confirm you have read this section.
2. Restate the five hard constraints in your own words.
3. State which phase Jesse has asked you to work on, and state the **single checkbox** within it you intend to implement first. If Jesse named a phase rather than a checkbox, ask him which checkbox to start with.
4. Produce the preflight diff (see "Execution contract" above) for that single checkbox.
5. Wait for Jesse's answers to every `blocked` item and every unresolved decision before editing.

If Jesse asks you to skip this gate, ask him to confirm explicitly — do not infer skip permission from a "just get started" style message.

## This section is not editable by you

Do not "simplify," reword, or trim this section while working on later phases. If you believe it should change, raise it to Jesse and wait. Edits to this section without explicit instruction are themselves a violation of constraint #2.

---

# MillWorks.AuditCore — Production Hardening Plan

> Status: **Not started** · Owner: Jesse · Created: 2026-04-19
> Companion docs: `ARCHITECTURE.md`, `ComplianceTraceabilityMatrix.md`, `TestCoverage.md`
> Consumer context: ACED (HIPAA-regulated clinical decision support) is the live consumer; R50 grant proof-of-code deadline drives the schedule.

This plan closes the six "top concerns" surfaced in the 2026-04-19 review. Ordering matches the review's priority list. Every phase ends with a measurable acceptance test and a doc update so reviewers can see the gap closed.

**Design principle the user called out for this cycle:** hardening must expose *configurable* fail-closed behavior so the consuming app (ACED and future adopters) can opt into "audit breaks business writes" semantics. Default-permissive stays the default — we only change the ceiling, not the floor.

---

## Phase 1 — Unify options flow into runtime (≈ 1 day)

**Concern 1.** `AuditOptions.EnableDigitalSignatures` / `HmacKey` are validated in the builder at `src/MillWorks.AuditCore.AspNetCore/Configuration/Options/AuditOptions.cs:69` and `MillWorksAuditBuilder.cs:458`, but `TamperDetectionService.cs:119` re-reads `Audit:HmacKey` and `Audit:EnableDigitalSignatures` directly from `IConfiguration`. A consumer who only touches the fluent API gets either a silently generated process-scoped HMAC key (dev) or a hard throw (prod), even when they set `HmacKey` on the builder.

### Work items
- [x] Replace the current singleton `AuditOptions` registration in `AddMillWorksAudit(...)` with an options-pipeline registration that makes the fluent-configured instance resolve through `IOptions<AuditOptions>` / `IOptionsMonitor<AuditOptions>`. There is no `MillWorksAuditBuilder.Build()` method today; the change belongs in `ServiceCollectionExtensions.AddMillWorksAudit`.
- [x] Add a single authoritative tamper/security runtime options model, or clearly split ownership between `AuditOptions` and `SecurityOptions`. The unified runtime path must cover:
  - [x] `HmacKey`
  - [x] `EnableDigitalSignatures`
  - [x] `DigitalSignaturePrivateKeyPath`
  - [x] `DigitalSignaturePublicKeyPath`
  - [x] distributed locking enablement, reconciled with the existing `SecurityOptions.UseRedisLocking` / `RedisConnectionString`
- [x] Replace `TamperDetectionService`'s direct `IConfiguration` reads with typed options. Do not leave a second hidden config path for HMAC/signature/locking behavior.
- [x] Keep `IConfiguration` binding as a fallback by binding `IConfiguration.GetSection("Audit")` into the same typed options instance used by fluent configuration. If a new overload is needed for config binding, add it explicitly; no `AddMillWorksAudit(Action<IConfiguration>)` overload exists today.
- [x] Add `IValidateOptions<T>` implementations and `ValidateOnStart()` for all options registered by `MillWorksAuditBuilder`: audit, security/tamper, EF, resilience, request-audit middleware, archival, and compliance.
- [x] Audit every other options class for the same pattern — `AuditOptions`, `SecurityOptions`, `EntityFrameworkOptions`, `ComplianceOptions`, `ResilienceOptions`, request-audit options, archival options. Document which ones are runtime-authoritative in `ARCHITECTURE.md`.

### Acceptance
- [x] New test `tests/.../Configuration/OptionsFlowTests.cs`: fluent `auditBuilder.Options.HmacKey = "<64-char key>"; auditBuilder.Options.EnableDigitalSignatures = true;` with **no** `IConfiguration` section produces a `TamperDetectionService` that signs with exactly that key (verified by signature round-trip). — `OptionsFlowTests.FluentHmacKey_FlowsThroughOptionsPipeline` + `TamperDetectionService_HmacSignature_MatchesAcrossFluentAndConfigPaths`.
- [x] Existing `IConfiguration["Audit:HmacKey"]` path still works unchanged (legacy consumer coverage). — `ServiceRegistrationTests.AddMillWorksAudit_ConfigurationOnly_AuditHmacKeySurvivesFluentBaselineReplay` + `OptionsFlowTests.ConfigurationBinding_FallbackResolves`.
- [x] Fluent/private-key and configuration/private-key paths both produce the same digital-signature behavior. Tests cover both `DigitalSignaturePrivateKeyPath` and `DigitalSignaturePublicKeyPath`. — covered by existing `TamperDetectionServiceDigitalSignatureTests`; not duplicated in `OptionsFlowTests.cs`.
- [x] Distributed locking is controlled by one typed option path. Tests prove `UseRedisLocking = true` wires the Redis lock service and `false` does not, without also requiring a separate `Audit:UseDistributedLocking` value. — covered by existing builder/security tests; not duplicated in `OptionsFlowTests.cs`.
- [x] Production + no HMAC key still throws at startup (not at first write). — `OptionsFlowTests.Production_NoHmacKey_FailsValidateOnStart`.

---

## Phase 2 — Redact `ErrorMessage` before JSON serialization (≈ 2 hrs)

**Concern 3.** `AuditLogger.ConvertToEntity` at `src/MillWorks.AuditCore.Services/AuditLogger.cs:424` writes `auditEvent.ErrorMessage` directly into the anonymous object serialized into `JsonData`. `DefaultAuditFieldRedactor.cs:37` explicitly excludes `ErrorMessage` from `SafeFields` because exception text routinely contains SQL fragments, connection strings, tokens, emails, and PHI. The redaction path simply isn't invoked here.

### Work items
- [x] Route `auditEvent.ErrorMessage` through `fieldRedactor.RedactValue("ErrorMessage", ...)` (or the `SensitiveContentSanitizer` the redactor delegates to) before it goes into the `JsonData` payload.
- [x] Do the same for the `ErrorMessage` column mapping if it exists on `AuditEventEntity` — confirm or add. *(Confirmed absent: `AuditEventEntity` has no `ErrorMessage` column; the persisted error-message surface is the `JsonData` payload, already covered by checkbox 1.)*
- [x] Add a regression test that logs an audit event with a SQL-like exception message such as `"Login failed for user 'sa' with password 'Password123!' at server 10.0.0.5"` and asserts the persisted `JsonData` contains `[REDACTED]`-style placeholders rather than the raw password/IP. Do not construct `SqlException` directly; use a plain `Exception`, fake `DbException`, or helper exception with the same message.
- [x] Add a second test for PHI-in-exception (e.g., a FERPA entity whose validation throws with the student ID in the message) — asserts the ID is redacted.

### Acceptance
- [x] Both redaction tests pass against a real SQLite context (not `InMemoryDatabase`, because the JSON column behavior differs). — `tests/MillWorks.AuditCore.Tests/Integration/AuditLoggerRedactionSqliteTests.cs` (2 tests, both green, real SQLite via `SqliteIntegrationFixture`).
- [x] `ComplianceTraceabilityMatrix.md` gets a row linking "PHI must not leak into audit payload" → this test. — new `## Audit Payload Protection (Cross-cutting)` section.

---

## Phase 3 — Fix or remove `EntityFrameworkOptions.Schema` (≈ 3 hrs)

**Concern 2.** `EntityFrameworkOptions.Schema` exists at `src/MillWorks.AuditCore.Services/Options/EntityFrameworkOptions.cs:36` but every entity hard-codes `Schema = "audit"` via `[Table(..., Schema = "audit")]` attributes (e.g., `AuditEventEntity.cs:14`, plus `AuditIntegrityEntity`, `AuditLogEntity`, `AuditArchiveRecordEntity`, `AuditSecurityEventEntity`, `AuditIntegrityWorkItemEntity`). Migrations also hard-code the name. The public API promises configurability that doesn't exist.

### Decision point
Two viable paths. Pick one up-front.

**A. Make it real (preferred for ACED multi-tenant story, but not a quick patch).** Move schema assignment from attributes to `OnModelCreating` and read the value from `EntityFrameworkOptions.Schema`. Dynamic schema must account for EF model caching and design-time migrations.

**B. Remove it.** Mark `Schema` `[Obsolete("Schema is fixed to 'audit'. Override by subclassing the DbContext.", error: true)]` and delete the property in the next minor version. Document the "subclass your own context" escape hatch.

### Work items (path A — chosen unless Jesse flips)
- [x] Remove `Schema = "audit"` from all `[Table]` attributes on audit entities.
- [x] In `AuditApplicationDbContext.OnModelCreating`, call `modelBuilder.HasDefaultSchema(_efOptions.Schema)` (inject options via ctor).
- [x] Add an `IModelCacheKeyFactory` that includes the configured schema. EF Core caches models by context type; without this, multiple schemas in one process can reuse the wrong model.
- [x] Update `DesignTimeDbContextFactory` so migrations/design-time tooling has a deterministic schema. Decide whether migration generation always targets `audit` or accepts a build-time environment variable for custom schema. *(Decision: migration generation always targets `"audit"`. `DesignTimeDbContextFactory` already hardcodes this for `MigrationsHistoryTable`; no source adjustment required. Decision documented in `README.md#custom-sql-server-schemas` and `ARCHITECTURE.md#schema-configuration-and-migration-anchoring`.)*
- [x] Decide the migration support contract:
  - [x] Default `audit` schema must continue to migrate existing databases.
  - [x] Custom schema support is fresh-database only unless a live schema-rename migration is explicitly added.
  - [x] Document that existing deployments using `audit` cannot be moved to a custom schema by toggling configuration alone.
- [x] Update migrations/model snapshot strategy. Static EF migrations cannot be truly runtime-parameterized in the normal generated form, so verify the chosen approach with `dotnet ef migrations add` / model snapshot inspection before implementation is considered done. *(Scratch `dotnet ef migrations add ScratchSchemaVerification` produced empty `Up`/`Down` bodies; snapshot diff was cosmetic only — `.HasDefaultSchema("audit")` annotation + `ProductVersion` bump. No drift.)*
- [x] Integration test (SQL Server, Phase 6 harness): configure `Schema = "audit_custom"`, run migrations, assert `INFORMATION_SCHEMA.TABLES` shows tables under `audit_custom`, write + read + tamper-chain verify succeed. *(Closed by `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/SchemaOverrideTests.cs` under Phase 6's "Schema override from Phase 3" sub-bullet. Migration mechanism deviated from "run migrations" to `EnsureCreatedAsync()` per Phase 3's own contract (custom-schema is fresh-DB-only; existing migrations stay anchored to `audit`). Tamper-chain coverage is the modest 3-row hash-chain assertion in that test; the 10k-row `TamperDetectionService.VerifyChainIntegrityAsync` scenario is owned by the dedicated Phase 6 tamper-chain sub-bullet.)*

### Acceptance
- [x] The test above passes. *(Verified: `dotnet test … --filter "FullyQualifiedName~Integration.SqlServer"` → 2 passed (migration + schema-override) on real SQL Server 2022 container.)*
- [x] Builder-level smoke test catches `Schema` with reserved names / invalid identifiers early (regex guard in `EntityFrameworkOptions.Validate()`). — `tests/MillWorks.AuditCore.Tests/Services/EntityFrameworkOptionsTests.cs` (21 parameterized cases, all green; covers null/whitespace, non-identifier shapes, >128-char length, and case-insensitive reserved `dbo`/`sys`/`guest`/`INFORMATION_SCHEMA`).
- [x] A design-time migration command succeeds after the schema changes, and the resulting model snapshot/table mappings match the chosen schema contract. — Scratch `dotnet ef migrations add` run in step 5 produced empty `Up`/`Down` bodies against the default schema; `ConfiguredSchemaTests` (step 4) proves entity mappings honor custom schemas at the model metadata layer.

---

## Phase 4 — Configurable fail-closed mode for audit failures (≈ 1 day)

**Concern 4.** The EF interceptor at `src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs:514` catches a broad `Exception`, logs, and intentionally swallows. The business write completes with no audit record. This is the right default for *most* apps but wrong for regulated entities flagged with the attributes this repo actually supports (`[FERPA]`, `[PHI]`, and `[SensitiveData]` with applicable standards) — a clinical system needs the option to fail the save when the audit fails.

**User directive:** "I like giving the option to fail to the consuming app." Fail-closed must be **opt-in, granular, and documented**.

### Work items
- [x] Add `AuditFailureMode` enum to `AuditCore.Abstractions`: `Permissive` (current), `FailClosedForRegulated` (throws only when the entity has `[FERPA]`, `[PHI]`, or `[SensitiveData]` with regulated applicable standards), `FailClosedAlways` (throws on any audit failure).
- [x] Add `AuditOptions.FailureMode` with default `Permissive`. Add `IAuditFailurePolicy` extension point so consumers can inject custom predicates (per-entity, per-operation, per-tenant).
- [x] In `AuditSaveChangesInterceptor.cs:514` and in the companion `SavingChanges` path, consult the policy before swallowing. If fail-closed applies, rethrow an `AuditIntegrityException` that wraps the original and carries the entity name / action / failure reason.
- [x] Make the policy evaluate the entities involved in the failed audit attempt, not only the exception. For modified entities, preserve enough context before the failing audit write so the exception can identify regulated entity type and action.
- [x] Emit a specific OpenTelemetry counter `auditcore.interceptor.failures_total{mode=...,entity=...}` for both the swallowed-log path and the rethrown path so dashboards pick up regressions. *(Scoped to the existing enum-counter diagnostics abstraction: `AuditDiagnosticCounter.InterceptorAuditFailure` + `InterceptorAuditFailureCount` on `IAuditDiagnostics`. The OTel label-shape exporter is a future concern per Jesse's ruling.)*
- [x] Document the three modes and the policy extension in `ARCHITECTURE.md` and `ComplianceTraceabilityMatrix.md`.

### Acceptance
- [x] Unit test against a SQLite fixture: save a FERPA entity with the interceptor audit path broken (e.g., make `AuditLogs` unavailable/non-writable or inject a failing audit-log writer) — with `FailClosedForRegulated`, `SaveChangesAsync` throws `AuditIntegrityException` and the transaction rolls back. With `Permissive` (default), the save succeeds and only a log is written. Both assertions in the same fixture. — `tests/.../Integration/AuditInterceptorFailClosedSqliteTests.cs`.
- [x] Same test with a non-regulated entity confirms `FailClosedForRegulated` does **not** throw. — covered by `FailClosedForRegulated_NonRegulatedEntity_SwallowsAndBusinessSaveSucceeds`.
- [x] Docs updated with an example snippet for ACED's expected configuration (fail-closed for regulated). — README `### Fail-Closed Audit Failures` subsection + inline builder example line; `ARCHITECTURE.md` `### Configurable Fail-Closed for Interceptor Audit Failures` subsection.
- [x] Separate policy tests cover `[FERPA]`, `[PHI]`, and `[SensitiveData(ApplicableStandards = ...)]`. — `tests/.../EntityFramework/RegulatedEntityFailurePolicyTests.cs`.

---

## Phase 5 — DLQ handoff for deferred request audit failures (≈ 1 day)

**Concern 5.** `InProcessRequestAuditDispatcher.cs:51` throws on queue full / enqueue timeout; `AuditContextMiddleware.cs:104` catches and logs only. The DLQ infrastructure already exists (`IAuditDeadLetterQueue`, `FileBasedAuditDeadLetterQueue`, `RedisAuditDeadLetterQueue`, `InMemoryAuditDeadLetterQueue`) — it just isn't wired into the deferred-request path.

### Work items
- [x] Inject `IAuditDeadLetterQueue` (optional) into `InProcessRequestAuditDispatcher`. On `WriteAsync` timeout or `ChannelClosedException`, push the event to the DLQ before rethrowing (or before returning silently, depending on policy — see next bullet).
- [x] Add `RequestAuditOverflowPolicy` enum: `Throw` (current), `DropAndLog` (for consumers who prefer loss over latency), `RouteToDeadLetter` (preferred default once DLQ is wired). Wire through `AuditContextMiddleware` so middleware knows whether a caught exception needs to go to the DLQ or not. — satisfied under Reading A/C: `Throw (current)` refers to the dispatcher-side throw signal; `AuditContextMiddleware` continues to catch/log/swallow in all three arms, so request-audit overflow does not fail the HTTP response. DLQ routing is owned by `InProcessRequestAuditDispatcher`, not `AuditContextMiddleware`.
- [x] Capture shutdown pressure: on host stop, drain the bounded channel for up to `DrainTimeout`, then push any remaining events to the DLQ rather than dropping them. The `ExecuteAsync` loop already catches `OperationCanceledException` — extend it with a drain-to-DLQ step. — behavior implemented in `InProcessRequestAuditDispatcher.ExecuteAsync` plus the private `RouteShutdownDrainAsync`; broad drain scope covers main-loop in-flight, drain-loop in-flight, and post-timeout channel remainder. Acceptance verified by `StopAsync_WithMainLoopAndDrainLoopInFlight_RoutesAllEventsToDeadLetter`.
- [x] Decide and document request-level fail-closed semantics separately from entity-write fail-closed semantics. If ACED requires failed request-audit dispatch to fail regulated endpoints, add a request-audit failure policy; otherwise explicitly document that request-level audit overflow routes to DLQ and does not fail the HTTP response. — request-audit overflow semantics are documented in `docs/ARCHITECTURE.md` under `### Request-Audit Overflow Semantics`. Decision: request-audit overflow never fails the HTTP response; Phase 4 entity-write fail-closed remains separate and applies only to the EF interceptor. Phase 5 deliberately does not add a request-audit failure policy.
- [x] Emit counters `auditcore.request_dispatcher.enqueue_timeout_total`, `auditcore.request_dispatcher.dlq_routed_total`, `auditcore.request_dispatcher.shutdown_drain_total`. — `enqueue_timeout_total` and `dlq_routed_total` are emitted from `InProcessRequestAuditDispatcher` (every overflow path and the successful `RouteToDeadLetter` store, respectively), via `IAuditDiagnostics.RequestDispatcherEnqueueTimeoutCount` / `RequestDispatcherDlqRoutedCount`. `shutdown_drain_total` is defined and tested on `IAuditDiagnostics` (`RequestDispatcherShutdownDrainCount`); runtime emission is intentionally deferred to the shutdown-drain work item.
- [x] Add operator-facing log messages at `LogWarning` (not `LogError`) when DLQ absorbs a saturation event — DLQ-routed is recovery, not failure. — already satisfied: DLQ-absorbed saturation is logged at `LogWarning` in the dispatcher `HandleOverflowAsync` path for successful route-to-DLQ. The DLQ-routing-failure path intentionally remains `LogError` because that is a second failure, not recovery. No source edit needed.

### Acceptance
- [x] New test uses a bounded channel of capacity 2, fills it, attempts a third enqueue with `RouteToDeadLetter` — assert event lands in an `InMemoryAuditDeadLetterQueue` with the correct correlation id. — satisfied by `tests/MillWorks.AuditCore.Tests/Services/InProcessRequestAuditDispatcherTests.cs::DispatchAsync_RouteToDeadLetter_StoresOverflowedEventWithCorrelationId`; test uses an in-test `CapturingDeadLetterQueue` fake rather than the real `InMemoryAuditDeadLetterQueue` (approved scope narrowing — acceptance target is dispatcher routing, and the real DLQ implementations have their own coverage under `tests/MillWorks.AuditCore.Tests/DeadLetterQueue/`).
- [x] Shutdown drain test: start dispatcher, enqueue N events, trigger host stop mid-flight — assert no in-flight events are lost (all N are either processed or in DLQ). — satisfied by `tests/MillWorks.AuditCore.Tests/Services/InProcessRequestAuditDispatcherTests.cs::StopAsync_WithMainLoopAndDrainLoopInFlight_RoutesAllEventsToDeadLetter`. Uses a blocking processor and `DrainTimeout = 50ms` to deterministically exercise main-loop in-flight, drain-loop in-flight, and post-timeout channel remainder paths (N = 4). Asserts all N events land in the capturing dead letter queue and `RequestDispatcherShutdownDrainCount == N`, covering every event routed through the shutdown-drain path.
- [x] Docs updated in `ARCHITECTURE.md` to call out the three overflow policies and their tradeoffs. — documented in `docs/ARCHITECTURE.md` under `### Request-Audit Overflow Semantics`: three-column policy table plus explicit tradeoffs paragraph for `Throw` / `DropAndLog` / `RouteToDeadLetter`.

---

## Phase 6 — SQL Server Testcontainers lane (≈ 1.5 days)

**Concern 6.** Today there's strong coverage on SQLite + `InMemoryDatabase`, but SQL Server-specific behavior — `rowversion`, identity sequences, transactions across schemas, migration ordering, retry strategies, locking — has no CI gate. For a HIPAA-grade audit product the production-risk areas (tamper chain, interceptor, migration path) need SQL Server coverage.

Model: the existing `MillWorks.BackgroundJobs` SLA lane at `tests/MillWorks.BackgroundJobs.Tests/Integration/Sla/SqlServerContainerFixture.cs` — `Testcontainers.MsSql` + `Respawn` + `[SetUpFixture]` + Docker-unavailable → `Inconclusive` skip. The pattern is proven; reuse it verbatim.

### Work items
- [x] Add a `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/` namespace. Add `Testcontainers.MsSql 3.x` and `Respawn 6.x` package refs to the test project. *(Per ruling: namespace materialization deferred to the next checkbox when `SqlServerContainerFixture` lands; this checkbox added only the two `<PackageReference>` entries — `Testcontainers.MsSql 3.10.0` and `Respawn 6.2.1` (latest stable in each named major) — to `tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj`.)*
- [x] Port `SqlServerContainerFixture` from `BackgroundJobs`. Needed schemas for Respawn reset: `audit` (plus any custom-schema fixture from Phase 3). Reserve a DB name like `MillWorksAuditCoreTests`. *(Ported to `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/SqlServerContainerFixture.cs`. Adaptations from BackgroundJobs source: container password `AuditCore_Test_Password_123!`, DB `MillWorksAuditCoreTests`, schemas `audit` + `audit_custom` pre-created in `EnsureDatabaseAndSchemasAsync` so Respawn covers Phase 3's custom-schema scenario before any tables exist there, `EnsureCreatedAsync()` retained (migration scenarios will own their own setup), `CreateContext()` returns `AuditApplicationDbContext` for the default `audit` schema and `CreateContext(string schema)` overload covers the Phase 3 custom-schema path via `IOptions<EntityFrameworkOptions>`. Stress-table seeding stripped. `IsDockerUnavailable` heuristic ported verbatim. No companion package refs added — `Microsoft.Data.SqlClient` and `Microsoft.EntityFrameworkCore.SqlServer` reach the test project transitively via the `MillWorks.AuditCore.EntityFramework` project reference.)*
- [x] Build a `SqlServerTestBase` with `[SetUp] ResetAsync()` calling Respawn, and `[Test]` skip → `Inconclusive` when `DockerAvailable == false`. *(Added `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/SqlServerTestBase.cs` — `public abstract class`; `[SetUp]` checks `DockerAvailable` first and calls `Assert.Inconclusive(...)` with the recorded `DockerSkipReason` when false, then awaits `SqlServerContainerFixture.ResetAsync()`. Exposes `protected static` pass-throughs `ConnectionString`, `CreateContext()`, and `CreateContext(string schema)` so derived scenario tests don't reach back to the fixture directly. No `[Category]` (lane is namespace-filtered) and no XML doc comments.)*
- [x] Port — not duplicate — the core scenarios that matter most in SQL Server: *(All six children green as of 2026-04-20. Full SQL lane: `dotnet test … --filter "FullyQualifiedName~Integration.SqlServer"` → 8 passed in ~27s on a real SQL Server 2022 Testcontainer.)*
  - [x] Migration from empty DB → all audit tables present with correct indexes (`IX_AuditEvents_*`). *(`tests/MillWorks.AuditCore.Tests/Integration/SqlServer/MigrationFromEmptyDatabaseTests.cs`. Owns a separate `MillWorksAuditCoreMigrationsTests` database (cloned from fixture connection string with `SqlConnectionStringBuilder`); `[SetUp]` drops + creates fresh, `[TearDown]` drops, both via `ALTER DATABASE … SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE`. Single test runs `Database.MigrateAsync()` and asserts in one `Assert.Multiple` block: six tables under `audit` (`ArchiveRecord`, `AuditEvents`, `AuditIntegrity`, `AuditIntegrityWorkItems`, `AuditLogs`, `SecurityEvents`), eight `IX_AuditEvents_*` indexes, and both `__EFMigrationsHistory` rows (`20260402194349_Init`, `20260403000000_ChangeIntegrityFKsToRestrict`). Does not inherit `SqlServerTestBase` — replicates the Docker-`Inconclusive` check locally because the migration DB is independent of the shared fixture DB and should not trigger Respawn against it.)*
  - [x] Tamper chain: write → compute hash → write next → `TamperDetectionService.VerifyChainIntegrityAsync` verify across a chain of 10k rows. *(Plan retargeted from `IntegrityReconciliationService` on 2026-04-20: that service is a `BackgroundService` for retrying pending work items, not a chain verifier; the actual chain-verify API is `ITamperDetectionService.VerifyChainIntegrityAsync` at `src/MillWorks.AuditCore.Services/Interfaces/ITamperDetectionService.cs:41`, implementation at `src/MillWorks.AuditCore.Services/TamperDetectionService.cs:568`.)* *(Phase 6 finding: the SQL Server 10k test exposed a batch identity-ordering bug in `CreateIntegrityRecordBatchAsync`. Fixed by making `AuditIntegrityEntity.SequenceNumber` application-assigned under the existing integrity distributed lock and regenerating the greenfield `Init` migration. Closed by `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/TamperChain10kSqlServerTests.cs` — single test seeds 10k AuditEvents in 10 chunks of 1000, calls `TamperDetectionService.CreateIntegrityRecordBatchAsync` × 10 (chain threads across batches via `_cachedPreviousHash` + new `_cachedMaxSequenceNumber`), then `VerifyChainIntegrityAsync(null, null)` returns `IsValid = true`, `ChainBroken = false`, `EventsChecked = TotalEvents = 10000`, `TamperedEvents = empty`. SQL lane: 6/6 green in 23s.)*
  - [x] `AuditSaveChangesInterceptor` under a real transaction: business write + audit write are atomic; forcing an audit failure with `FailClosedAlways` rolls back the business row (new Phase-4 coverage on SQL Server). *(`tests/MillWorks.AuditCore.Tests/Integration/SqlServer/AuditInterceptorFailClosedSqlServerTests.cs`. Owns a separate `MillWorksAuditCoreInterceptorTests` database (drop/create per test, `SINGLE_USER WITH ROLLBACK IMMEDIATE` cleanup); does not inherit `SqlServerTestBase` — replicates Docker-`Inconclusive` locally because the test owns its DB lifecycle. Test DbContext is a private nested `FailClosedTestDbContext : AuditApplicationDbContext` adding `DbSet<PlainTestEntity>` (small `Id`/`Name` POCO duplicated locally per scope ruling — `FerpaTestEntity` deliberately not ported; this checkbox is `FailClosedAlways` + `PlainTestEntity` only). Two tests: (1) success path uses `NullLogger` + `FailClosedAlways`, asserts the business row in `audit.PlainTestEntity` and the audit-log row in `audit.AuditLogs` both commit (count = 1 each) and `InterceptorAuditFailureCount == 0`; (2) fail-closed path uses a `ThrowingLogger<T>` (ported verbatim from the Phase 4 SQLite test, throws on `LogLevel.Debug` only) with `FailClosedAlways`, asserts `Assert.ThrowsAsync<AuditIntegrityException>` carries `EntityName == "PlainTestEntity"` / `Action == "Created"` and that BOTH `audit.PlainTestEntity` and `audit.AuditLogs` are empty afterward (real SQL Server transaction rollback) with `InterceptorAuditFailureCount == 1`. `BuildOptions` mirrors `MillWorksAuditBuilder.cs:173` with `.ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()` for cache-key consistency with the rest of the SqlServer lane.)*
  - [x] Schema override from Phase 3 (if path A was chosen). *(`tests/MillWorks.AuditCore.Tests/Integration/SqlServer/SchemaOverrideTests.cs`. Owns a separate `MillWorksAuditCoreCustomSchemaTests` database (drop/create per test, `SINGLE_USER WITH ROLLBACK IMMEDIATE` for cleanup). Single test runs `Database.EnsureCreatedAsync()` against an `AuditApplicationDbContext` configured with `IOptions<EntityFrameworkOptions> { Schema = "audit_custom" }` and asserts in one `Assert.Multiple` block: all six audit tables landed under `audit_custom`, no audit tables under default `audit`, three written events round-trip, and a three-row hash chain (looked up by `EventId` to avoid SQL Server batch identity-ordering nondeterminism) verifies row-to-row links via `PreviousEventHash`. **Migration-wording deviation:** uses `EnsureCreatedAsync()` rather than the literal "run migrations" wording from Phase 3 line 151, because Phase 3's accepted contract (lines 147–149) states custom-schema support is fresh-database-only and the existing migrations remain anchored to `audit`. **Cache-key-factory wiring:** test mirrors the production `MillWorksAuditBuilder.cs:173` call by adding `.ReplaceService<IModelCacheKeyFactory, AuditModelCacheKeyFactory>()` in its local `DbContextOptionsBuilder`; without that, EF's default model cache returns a previously compiled `audit`-schema model and silently ignores `HasDefaultSchema("audit_custom")`. Also closes Phase 3 line 151 work item and Phase 3 line 154 acceptance bullet.)*
  - [x] Optimistic concurrency: `rowversion` round-trip on `AuditEventEntity`. *(Plan retargeted from `AuditIntegrityEntity` on 2026-04-20: `AuditIntegrityEntity` is append-only (extends `AppendOnlyEntity`, decorated `[NoAudit]`) and intentionally has no rowversion; `AuditEventEntity` exercises the shared `AuditAggregateRoot.RowVersion` mapping wired by `AuditApplicationDbContext.ConfigureConcurrency` at lines 230–247. Closed by `tests/MillWorks.AuditCore.Tests/Integration/SqlServer/AuditEventOptimisticConcurrencyTests.cs`. Inherits `SqlServerTestBase` (shared fixture DB; default `audit` schema; Respawn reset between tests). Single test inserts a minimal `AuditEventEntity { EventType = "RowVersionTest", JsonData = "{}" }`, reloads via fresh context, captures `RowVersion`; updates `IntegrityStatus` from `Pending` to `Completed`, saves, reloads via fresh context; asserts in `Assert.Multiple` that the first `RowVersion` is non-null and non-empty, the status update round-trips, and the second `RowVersion` differs from the first via direct byte-array `Is.Not.EqualTo` comparison.)*
  - [x] Retry/failure behavior: verify SQL Server provider retry strategy is not accidentally enabled around explicit transactions, and verify `ResilientAuditLogger` + DLQ behavior when a write fails transiently. `EnableRetryOnFailure` is intentionally not used by the current SQL Server registration. *(`tests/MillWorks.AuditCore.Tests/Integration/SqlServer/RetryFailureBehaviorSqlServerTests.cs`. Two tests: (1) `UseEntityFramework_DoesNotEnableSqlServerRetryStrategy` invokes the real `AddMillWorksAudit` + `UseEntityFramework` builder pipeline against a fake `(localdb)` connection string (HmacKey + Development environment supplied to satisfy `AuditOptionsValidator`), resolves `AuditApplicationDbContext` from the DI scope, and asserts `ctx.Database.CreateExecutionStrategy().RetriesOnFailure == false` and `Is.Not.InstanceOf<SqlServerRetryingExecutionStrategy>` — `CreateExecutionStrategy()` instantiates without opening a connection so no SQL Server container is required. (2) `ResilientAuditLogger_TransientWriteFailure_RoutesToDeadLetterQueueAfterRetries` wires a `Mock<IAuditLogger>` that throws `InvalidOperationException` on every `LogAsync` call into a real `InMemoryAuditDeadLetterQueue` (with mock redactor + empty service provider); after `LogAsync(testEvent)` returns, asserts the inner logger was called exactly 4 times (initial + 3 retries) and the DLQ contains exactly one entry with the original `EventId` and a `FailureReason` containing "Failed after". Runtime ~3.5s for the retry backoff (500/1000/2000ms exponential + jitter); test does not inherit `SqlServerTestBase`.)*
- [x] GitHub Actions workflow `.github/workflows/sql-integration.yml` — pulls `mcr.microsoft.com/mssql/server:2022-latest`, runs only the SQL Server fixture, uses `dotnet test --filter FullyQualifiedName~Integration.SqlServer`. Runs on PR + main. *(Workflow lives at `.github/workflows/sql-integration.yml`. Single `sql-integration` job on `ubuntu-latest`; mirrors `ci.yml`'s setup discipline (`actions/checkout@v4`, `actions/setup-dotnet@v4` with `dotnet-version: '10.0.x'`, `dotnet restore` → `dotnet build --configuration Release --no-restore` → `dotnet test --configuration Release --no-build --filter "FullyQualifiedName~Integration.SqlServer"`). Adds an explicit `docker pull mcr.microsoft.com/mssql/server:2022-latest` step before the test run so image-pull failures surface as a distinct step. No matrix, no concurrency block, no artifact upload, no `pack`. `ci.yml` left untouched per slice scope. Verified via PyYAML parse (triggers `[push, pull_request]`, job `sql-integration`, six steps in order). The "CI workflow is green on a representative PR" acceptance bullet stays unchecked until the workflow has actually run on a real PR.)*
- [x] README / `TestCoverage.md` update: document the new lane and the Docker-unavailable skip behavior. *(README gains a new `## Testing` section between `## Database Schema` and `## Contributing` covering full-suite, SQL-lane filter, Docker-required + Inconclusive fallback, and a link to the `SQL Integration` GitHub Actions workflow. `docs/TestCoverage.md` gains a `## SQL Server Integration Lane` addendum at the bottom that explicitly post-dates the April 2, 2026 snapshot — snapshot tables/numbers untouched. `CONTRIBUTING.md` and the README `## Compatibility` table left as-is per scope.)*

### Acceptance
- [x] `dotnet test --filter FullyQualifiedName~Integration.SqlServer` passes locally with Docker running, all scenarios green. *(Verified 2026-04-20: 8 passed / 0 failed in 27s on a real SQL Server 2022 Testcontainer. Most recent green run captured at the close of the retry/failure-behavior slice.)*
- [ ] Same command with Docker stopped reports every SQL Server test as `Inconclusive`, zero failures.
- [ ] CI workflow is green on a representative PR. *(Deferred 2026-04-21: GitHub Actions disabled at the repository level to avoid per-run cost during solo development. Gate remains open; will be revisited when Actions is re-enabled.)*

---

## Phase 6.5 — SQL Server Soak / Endurance

- [x] Run a SQL Server endurance test against the integrity chain writer/verifier after the Phase 6 fixes. Target: sustained integrity writes with concurrent batch writers, then full-chain verification. Initial acceptance: 100k audit events written through the production integrity path, `VerifyChainIntegrityAsync` returns valid, `0` DLQ entries for expected-success traffic, no unbounded memory growth, and runtime/resource notes captured in `artifacts/`. *(Verified 2026-04-21. First soak attempt surfaced a real production defect: `TamperDetectionService.CreateIntegrityRecord{,Batch}Async` did not retry SQL Server deadlock victim (1205), and `InMemoryDistributedLockService.CleanupExpiredLocks` could remove an actively-held lock on TTL expiry, letting two writers into the integrity-chain critical section simultaneously and producing the deadlock. Closed by a fresh production-fix slice that added `DeadlockDetector` (sibling to `DuplicateKeyDetector`, covers SQL Server 1205 + PostgreSQL 40P01), unified the retry catch in both integrity-create methods (`when (DuplicateKeyDetector.IsDuplicateKey(ex) || DeadlockDetector.IsDeadlock(ex))` + helper-local `conflictKind` log field), and removed the TTL-based cleanup from `InMemoryDistributedLockService` so an acquired lock lives until `Dispose` (existing `AcquireLockAsync_LockExpires_CanReacquire` test was flipped to assert the new contract). After the fix: full regression suite green (1925 / 1929 passed, 4 known skips, 2 min); soak passes with 4 fixed concurrent writers against a real SQL Server 2022 Testcontainer in 1 min 39 s. Chain verification clean — `IsValid = true`, `ChainBroken = false`, `EventsChecked = TotalEvents = 100,000`, `TamperedEvents = 0`, DLQ empty. Final forced-GC managed-memory delta **4.11 MB** against the **750 MB** hard cap (start 17.49 MB → final 21.60 MB; integrity-write phase showed no growth trend across the 10-batch checkpoints). Artifacts at `artifacts/phase6.5-soak/2026-04-21T13-23-06Z/notes.md` + `samples.csv`.)*

---

## Cross-cutting: observability + startup validation

Folded into earlier phases but called out here so nothing falls through:

- [x] Every phase above that adds a new failure mode also adds a counter/histogram and a log statement with correlation id. *(Fail-closed interceptor failures increment `InterceptorAuditFailureCount` and now log `CorrelationId` on both permissive and fail-closed paths. Request-audit overflow/shutdown-drain failures increment the request-dispatcher counters and now include `CorrelationId` in drop/DLQ logs.)*
- [x] `ValidateOnStart()` is applied to every `IOptions<T>` registered by `MillWorksAuditBuilder` — not just `AuditOptions`. Misconfigured host must fail at boot. *(`AuditOptions`, `AuditMiddlewareOptions`, `EntityFrameworkOptions`, `SecurityOptions`, `ComplianceOptions`, `ArchivalOptions`, and `ResilienceOptions` all register validators and call `ValidateOnStart()`.)*
- [x] Options validation errors include the specific property path so operators can fix them without source-diving. *(Validators use the concrete option property names, e.g. `AuditOptions.HmacKey`, `EntityFrameworkOptions.Schema`, `SecurityOptions.RedisConnectionString`, and `ResilienceOptions.*`.)*
- [x] Production profiles that allow `PassThroughAuditFieldRedactor` emit an explicit startup warning, and the ACED profile must not allow pass-through redaction. *(`PassThroughRedactorStartupWarningService` logs a startup warning when `PassThroughAuditFieldRedactor` is explicitly allowed; `docs/ACEDProductionConfiguration.md` keeps `AllowPassThroughRedactor = false` and calls out the ACED requirement.)*

---

## Release checklist (Phase 7 — before tagging `1.x-hardened`)

- [ ] All six phases green, all acceptance tests passing. *(Blocked by Phase 6 acceptance gates: Docker-stopped SQL `Inconclusive` verification and CI workflow green on a representative PR.)*
- [x] `ComplianceTraceabilityMatrix.md` updated with new rows for: runtime-options binding, `ErrorMessage` redaction, fail-closed modes, DLQ overflow policies, SQL Server coverage. *(Cross-cutting matrix section now references the runtime options flow, request-audit overflow/DLQ policy, and SQL Server Testcontainers lane alongside the existing `ErrorMessage` redaction and fail-closed rows.)*
- [x] `CHANGELOG.md` entry enumerating breaking-ish items (`AuditFailureMode` enum, `Schema` removal-or-parameterization, constructor signature change on `TamperDetectionService`). *(`[Unreleased]` entry added with ProdHardening Phase 3/4/5/6 changes, breaking-ish constructor/configuration notes, greenfield migration reset guidance, and the SQL Server integrity-sequence production fix.)*
- [x] `ARCHITECTURE.md` updated with fail-closed policy extension point, overflow policy, options-flow diagram. *(Architecture now documents `IAuditFailurePolicy`, request-audit overflow policy semantics, the typed-options flow diagram, and the app-assigned integrity sequence behavior required by the SQL Server fix.)*
- [x] Public API/package compatibility check: *(Pack artifacts generated and dependency graph inspected; constructor/signature changes are covered in `CHANGELOG.md`.)*
  - [x] `dotnet pack --configuration Release` succeeds. *(All five packable source projects produced `1.5.0` `.nupkg`/`.snupkg` artifacts on 2026-04-20. Local solution-level pack/build deadlocked under parallel MSBuild scheduling; rerunning the same package targets with `--disable-build-servers /m:1` completed cleanly. Only warning: package readmes are missing.)*
  - [x] Generated NuGet dependencies are inspected for accidental package graph changes. *(Generated `.nuspec` dependency groups match the source package/project references: `Testcontainers.MsSql` and `Respawn` remain test-only, `Microsoft.EntityFrameworkCore.Tools` remains private, and package-to-package references stay on `1.5.0`.)*
  - [x] Public constructor/signature changes are listed in `CHANGELOG.md`. *(`[Unreleased]` / `### Breaking Changes` now lists `AuditSaveChangesInterceptor` optional failure-mode/policy constructor parameters and current `TamperDetectionService` typed-options/distributed-lock construction requirements.)*
- [x] ~~Consumer dry-run: bump ACED's reference to a local feed build of this cycle, run its tests, confirm nothing breaks under the default (permissive) configuration, then flip ACED to `FailClosedForRegulated` and confirm regulated entities behave as expected.~~ *(Retired 2026-04-21: ACED consumer integration was redone in the ACEDS repo, so the ACED-specific gate is moved there. The consumer dry-run is not gone — it is retargeted to the MillWorks.sln solution in the next bullet.)*
- [x] Consumer dry-run: bump `/Users/jesse/RiderProjects/MillWorks/MillWorks.sln` to a local feed build of this AuditCore cycle, run the relevant MillWorks tests, confirm default permissive behavior, then flip a regulated test profile to `FailClosedForRegulated` and confirm regulated entities behave as expected. *(Completed 2026-04-21: permissive baseline green against AuditCore 1.5.1-dryrun.1; regulated-mode flip N/A for current MillWorks because no regulated audit entities exist.)*
- [x] ACED production configuration snippet is checked into docs and includes: HMAC key source, digital-signature key paths, fail-closed mode, DLQ provider, request overflow policy, SQL Server connection/migration behavior, and schema decision. *(`docs/ACEDProductionConfiguration.md` added with production JSON shape, secret-source guidance, `Program.cs` wiring, default `audit` schema decision, Redis DLQ/locking posture, and request-overflow behavior.)*
- [x] README `Production Readiness` section refreshed with the six gaps marked closed. *(README now has a `## Production Readiness` section covering runtime options binding, schema configuration, `ErrorMessage` redaction, fail-closed entity writes, request-audit overflow/DLQ behavior, and the SQL Server verification lane, plus a pointer to `docs/ACEDProductionConfiguration.md`.)*

---

## Out of scope (explicitly deferred)

- Splitting the package graph or renaming public types beyond what Phase 1/3/4 require.
- Performance tuning beyond the tamper-chain 10k-row smoke in Phase 6.
- Broader rewrite of the dead-letter queue implementations (they are used as-is).
- NuGet publish / SemVer gate / signed provenance — handled in a later publish cycle, not here.
