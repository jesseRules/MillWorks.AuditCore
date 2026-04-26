# Phase 10 — README & docs rewrite

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase09-ConsumerLibraryMigrations.md`](Phase09-ConsumerLibraryMigrations.md)

## Goal

Reconcile every documentation artifact with the new architecture. After
this phase, the AuditCore README accurately describes the sink-based
contract, the MillWorks README's Bridge Taxonomy table accurately lists
`AuditBridge` (currently undocumented) and removes the stale
"audit logging" mention from the `SecurityBridge` row, and the
superseded `ConsumerDbContextAuditing.md` is clearly marked as
historical.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- **Documentation rewrites are not refactors.** Touch only the sections
  named below. If a tangential section reads stale, raise it instead
  of editing.
- **No code changes in this phase.** If implementation drift is
  discovered while writing docs, stop and open a follow-up phase.
- **Greenfield rule applies to docs too.** Delete obsolete sections;
  don't add "previously this worked differently" footnotes.

## Files

### AuditCore repo (`/Users/jesse/RiderProjects/MillWorks.AuditCore/`)

| Action | Path | Sections |
|---|---|---|
| Modified | `README.md` | Features → Automatic Entity Auditing; Features → Tamper Detection; Quick Start → Minimal Setup; Quick Start → Automatic Entity Change Tracking; Architecture diagram; Configuration → Database Initialization Defaults; Configuration → Custom SQL Server Schemas; Configuration → Fail-Closed Audit Failures; Database Schema (add `AuditOutbox`); Production Readiness |
| Modified | `docs/ConsumerDbContextAuditing.md` | Add "SUPERSEDED BY" header pointing to `RedesignPlan.md` |
| Modified | `docs/ARCHITECTURE.md` | Update if it references the old DbContext name or the old persistence path |
| Modified | `docs/ACEDProductionConfiguration.md` | Update if it references `AuditApplicationDbContext` |
| Modified | `CHANGELOG.md` | Note the breaking changes from Phases 03, 05, 06, 07 |

### MillWorks repo (`/Users/jesse/RiderProjects/MillWorks/`)

| Action | Path | Sections |
|---|---|---|
| Modified | `README.md` | Architecture → Bridge Taxonomy → SecurityBridge entry (line ~160) (drop "audit logging"); Architecture → Bridge Taxonomy → AuditBridge (new row); External Repositories → MillWorks.AuditCore (line ~260) |
| Modified | Per-library `README.md` (if present) | Each of the 9 migrated libraries — short paragraph on audit integration |

### Cross-repo

| Action | Path | Purpose |
|---|---|---|
| Updated | All phase docs `Phase01-...md` through `Phase09-...md` | Mark "Completed YYYY-MM-DD" with reference to the implementing PR |

## Edits in detail

### `MillWorks.AuditCore/README.md`

#### Features → Automatic Entity Auditing (line ~39-40)

Replace the current paragraph (which over-promises "any DbContext") with:

> **Automatic Entity Auditing.** Any DbContext that has
> `AuditSaveChangesInterceptor` registered will produce audit envelopes
> for tracked entity changes. The interceptor is a producer; the audit
> sink owns persistence. Consumer DbContexts do not need to map
> AuditCore entities — they may optionally implement `IAuditContextSource`
> to flow user/correlation context into envelopes, and
> `IAuditProviderDispatchSource` to participate in `IAuditProvider`
> dispatch.

#### Features → Tamper Detection (line ~42-50)

Update the modes table — remove the implication that integrity coverage
depends on which DbContext is saving. Add a third row for sink mode:

| Mode | Behavior |
|---|---|
| **Strict (Immediate sink)** | Audit envelope is published, persisted, and chain-extended on the audit-owned DbContext. Decoupled from consumer transaction. |
| **Strict (TransactionalOutbox sink)** | Audit envelope is staged in the saving consumer's transaction via outbox row; chain-extension happens after commit via background drainer. |
| **Batched (legacy IntegrityWriteBatcher)** | Existing high-throughput batched-integrity path. Independent of sink mode. |

#### Quick Start → Minimal Setup (line ~104-133)

The `audit.UseEntityFramework(...)` block stays. Add an `AuditSinkMode`
configuration line:

```csharp
audit.UseSecurity(security =>
{
    security.EnableTamperDetection = true;
    security.AuditSinkMode = AuditSinkMode.TransactionalOutbox;  // for FailClosedForRegulated
});
```

#### Quick Start → Automatic Entity Change Tracking (line ~173-182)

Replace the generic example with one that shows a consumer DbContext:

```csharp
public class ProjectDbContext(DbContextOptions<ProjectDbContext> options)
    : DbContext(options), IAuditContextSource
{
    public string? CurrentUserId { get; set; }
    public string? CurrentCorrelationId { get; set; }
    public string? CurrentIpAddress { get; set; }
    public string? CurrentUserAgent { get; set; }

    // No AuditCore entity mapping required — the sink owns persistence.
}

// In the host (e.g., MillWorks.Api/Program.cs):
services.AddDbContext<ProjectDbContext>((sp, options) =>
{
    options.UseSqlServer(connectionString);
    options.AddInterceptors(sp.GetRequiredService<AuditSaveChangesInterceptor>());
});
```

#### Architecture (line ~247-263)

Replace the diagram with the one from `RedesignPlan.md` (the four-layer
producer/sink/audit-context/audit-DbContext diagram).

#### Configuration → Fail-Closed Audit Failures (line ~393-419)

Add a paragraph clarifying that `FailClosedForRegulated` works under
both sink modes:
- Under `AuditSinkMode.Immediate` (default), audit-build OR
  audit-publish failures are visible inside the interceptor's `try`
  block, so they rethrow `AuditIntegrityException` and the consumer's
  `SaveChangesAsync` rolls back the business write. The audit-side
  write happens on a separate connection — it never lands.
- Under `AuditSinkMode.TransactionalOutbox`, the same rethrow happens
  on outbox-write failure (which propagates through the interceptor's
  `try`). The added value of outbox is durability across audit-subsystem
  crashes — once the outbox row commits with the consumer transaction,
  a later drainer crash doesn't lose the envelope. Use this mode when
  zero-loss durability is required (HIPAA / FERPA / PCI-DSS or any
  posture where audit-subsystem failures must not lose envelopes).

#### Database Schema (line ~478-491)

Add the `AuditOutbox` row to the table:

| Table | Purpose | Key Columns |
|---|---|---|
| `AuditOutbox` | Durable handoff for `TransactionalOutbox` sink | `Id`, `EnvelopeJson`, `Status`, `CreatedAt`, `CompletedAt`, `AttemptCount`, `LastError` |

#### Production Readiness (line ~527-540)

Add a row for the sink redesign:

| Area | Status |
|---|---|
| Sink-based audit pipeline | `IAuditSink` + `ImmediateSink` / `TransactionalOutboxSink` decouple persistence from interceptor; consumer DbContexts no longer require AuditCore entity mapping. Documented in `docs/RedesignPlan.md`. |

### `docs/ConsumerDbContextAuditing.md`

Add a header at the top (do NOT delete the file or its content):

```markdown
> **SUPERSEDED.** This plan is replaced by
> [`RedesignPlan.md`](RedesignPlan.md). The original three items map to
> phases in the redesign:
> - Item 01 → Phase 07 (consumer DbContexts no longer need to map
>   `AuditLogEntity`)
> - Item 02 → Phases 02, 06, 07 (chain coverage falls out of sink-owned
>   persistence)
> - Item 03 → tracked separately as a follow-up after Phase 11
>
> Retained for historical context.
```

### `MillWorks/README.md` SecurityBridge entry (line ~160)

Today:

> | `SecurityBridge` | All security/encryption interfaces | 14 libraries — audit logging, credential encryption, data masking |

`SecurityBridge` is not the audit landing point — `AuditBridge` is.
Drop "audit logging" from the SecurityBridge row:

> | `SecurityBridge` | All security/encryption interfaces | 13 libraries — credential encryption, data masking |

(The library count drops from 14 to 13 because the audit-publishing
interfaces move out of `SecurityBridge`'s scope. Verify the count
against the actual interface list during the edit.)

### `MillWorks/README.md` AuditBridge entry (new row, in same Bridge Taxonomy table)

Insert a new row near `SecurityBridge`:

> | `AuditBridge` | `IAuditPublisher` + per-library audit interfaces (`IFinanceAuditService`, etc.) | 9 libraries — audit publishing routed through `IAuditSink` |

### `MillWorks/README.md` External Repositories — MillWorks.AuditCore (line ~260)

Update the description to reference the sink architecture:

> **MillWorks.AuditCore** | Tamper-evident audit logging and compliance
> platform. Sink-based pipeline (`IAuditSink` / `ImmediateSink` /
> `TransactionalOutboxSink`); consumer DbContexts integrate via the
> interceptor + `IAuditContextSource`. Most libraries depend only on
> `MillWorks.AuditCore.Abstractions`; libraries that use field-level
> encryption (`[EncryptedField]` + `UseFieldEncryption`) keep the
> `MillWorks.AuditCore.EntityFramework` reference for the EF
> value-converter coupling. SHA-256 hash chains with HMAC signatures,
> AES-256-GCM field-level encryption, 7 compliance validators, dead
> letter queue, Azure Blob archival.

## Decisions left to Jesse

1. **CHANGELOG style.** AuditCore's CHANGELOG already has a format
   (likely Keep-a-Changelog). Match it. **Pre-implementation check:**
   read the most recent CHANGELOG entries.
2. **Per-library README updates.** Each of the 9 migrated libraries
   may or may not have a README. **Pre-implementation check:** for
   each library, check if `README.md` exists; if yes, draft a 2-3
   sentence audit-integration paragraph; if no, skip.
3. **ARCHITECTURE.md scope.** The current file may go into more
   detail than the README — diagrams, sequence flows, etc.
   **Pre-implementation check:** read the full ARCHITECTURE.md before
   editing; preserve its structure.

## Verification

```bash
# Markdown lint (if configured)
markdownlint docs/ README.md CHANGELOG.md

# Visual review checklist:
# - Every code snippet in the AuditCore README compiles against the
#   current shipped code (no stale APIs).
# - Every documented file path / line number reference matches the
#   current code.
# - Diagram in Architecture section matches the real layout.
# - SUPERSEDED header on ConsumerDbContextAuditing.md is the first
#   non-blank line.
```

Acceptance:
- All sections listed above are updated.
- A reader new to the project can follow the AuditCore README's Quick
  Start without consulting older docs.
- The MillWorks README accurately describes how audit fits into the
  bridge taxonomy.
- `ConsumerDbContextAuditing.md` is clearly marked superseded.

## README impact

This phase IS the README impact. No further README changes after this.

## Out of scope

- Code changes (any drift discovered → follow-up phase).
- New diagrams from scratch (reuse the one in `RedesignPlan.md`).
- Translating any docs to other languages.
- Adding tutorials, migration guides, or recipe books.

## Done when

- All listed files updated.
- Visual review checklist passes.
- Phases 01-09 docs all carry a "Completed YYYY-MM-DD" line.
- Phase doc updated with "Completed YYYY-MM-DD".
