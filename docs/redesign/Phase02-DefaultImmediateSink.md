# Phase 02 — Default ImmediateSink

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase01-AuditSinkAbstraction.md`](Phase01-AuditSinkAbstraction.md)

## Goal

Implement `ImmediateSink : IAuditSink` in `MillWorks.AuditCore.Services`.
The sink routes envelopes to the existing persistence paths (interceptor's
`AuditLogEntity` write logic for `EntityChange`; `IAuditLogger.LogAsync`
for `ExplicitEvent`). Register it as the default `IAuditSink` binding.

After this phase, the sink path exists end-to-end, but nothing yet calls
it — the interceptor still writes directly. Phase 03 wires the interceptor
to publish via the sink.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply (see Phase 01
for the restatement). Additionally:

- `ImmediateSink` must NOT change any existing persistence behavior. It
  is a parallel path; existing tests must pass unchanged.
- `IAuditLogger.LogAsync` is reused as-is; do not modify it.

## Files

| Action | Path | Purpose |
|---|---|---|
| New | `src/MillWorks.AuditCore.Services/Sinks/ImmediateSink.cs` | Default sink implementation |
| Modified | `src/MillWorks.AuditCore.AspNetCore/Configuration/MillWorksAuditBuilder.cs` | Register `ImmediateSink` as `IAuditSink` |
| New | `tests/MillWorks.AuditCore.Tests/Sinks/ImmediateSinkTests.cs` | Unit + SQLite integration |

A new `Sinks/` folder is created under `src/MillWorks.AuditCore.Services/`.
That folder name is part of the spec — do not rename to `Audit/Sinks/` or
similar.

## Type introduced

### `ImmediateSink`

Constructor signature (subject to D2 below):

```csharp
namespace MillWorks.AuditCore.Services.Sinks;

using MillWorks.AuditCore.Abstractions.Interfaces;
using MillWorks.AuditCore.Abstractions.Models;
using MillWorks.AuditCore.Abstractions.Enums;

public sealed class ImmediateSink(
    IAuditLogger auditLogger,
    IAuditEntityWriter auditEntityWriter,
    ILogger<ImmediateSink> logger) : IAuditSink
{
    public async Task PublishAsync(
        AuditEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        switch (envelope.Kind)
        {
            case AuditEnvelopeKind.EntityChange:
                await auditEntityWriter.WriteEntityChangeAsync(envelope, cancellationToken);
                break;

            case AuditEnvelopeKind.ExplicitEvent:
                await auditLogger.LogAsync(BuildAuditEvent(envelope), cancellationToken);
                break;

            default:
                logger.LogError("Unknown AuditEnvelopeKind {Kind}", envelope.Kind);
                throw new InvalidOperationException(
                    $"Unhandled AuditEnvelopeKind: {envelope.Kind}");
        }
    }

    private static AuditEvent BuildAuditEvent(AuditEnvelope envelope) { /* ... */ }
}
```

`IAuditEntityWriter` is a new internal abstraction introduced this phase.
Its purpose: encapsulate the `context.Set<AuditLogEntity>().Add(...)` logic
that currently lives inside `AuditSaveChangesInterceptor.ProcessAuditableEntries`,
so both the interceptor (Phase 03) and the sink can call into the same code.

### `IAuditEntityWriter` (internal)

```csharp
namespace MillWorks.AuditCore.Services.Sinks;

internal interface IAuditEntityWriter
{
    Task WriteEntityChangeAsync(AuditEnvelope envelope, CancellationToken cancellationToken);
}
```

Default implementation in this phase: `AuditDbContextEntityWriter` — opens
or reuses a scoped `AuditApplicationDbContext` (renamed to `AuditDbContext`
in Phase 05; keep the current name in this phase) and writes the
`AuditLogEntity` row(s).

## Decisions left to Jesse

1. **`IAuditEntityWriter` visibility.** `internal` (sink-private extraction
   point) or `public` (extension point for custom writers)?
   **Recommendation:** `internal` — sink composition is the public extension
   point; writer is implementation detail.
2. **Reuse the saving DbContext or open a new one?** In this phase the writer
   resolves a fresh scoped `AuditApplicationDbContext` from DI. That means
   the audit row is written in a separate transaction from the consumer's
   save. **Recommendation:** that's the intended end-state (Phase 05
   formalizes it); start that way now to surface any test breakage early.
   Confirm before Phase 02 starts — this changes the implicit contract.
3. **`BuildAuditEvent` mapping.** `AuditEnvelope.ExplicitEvent` → `AuditEvent`
   is straightforward field copy (EventType, EntityName, UserId, etc.).
   No exotic mapping needed.

If Jesse picks "reuse saving DbContext" for D2, the writer needs a way to
discover it — either a scoped `IAuditWriteContextProvider` populated by the
interceptor before publishing, or by inspecting an ambient context. That
adds complexity I'd rather avoid; raising as a decision.

## Verification

```bash
# After ImmediateSink.cs lands
dotnet build MillWorks.AuditCore.sln

# After MillWorksAuditBuilder.cs is updated
dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj

# After ImmediateSinkTests.cs lands
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~Sinks.ImmediateSinkTests"
```

Acceptance:
- All existing tests (~1850+) pass unchanged.
- `ImmediateSinkTests` covers: `EntityChange` envelope writes one
  `AuditLogEntity` row; `ExplicitEvent` envelope causes one
  `AuditLogger.LogAsync` invocation; unknown kind throws.
- DI resolution: `serviceProvider.GetRequiredService<IAuditSink>()`
  returns `ImmediateSink`.

## README impact

Defer to Phase 10. Do not edit README in this phase.

## Out of scope

- Wiring the interceptor to use the sink → Phase 03.
- Cross-DbContext context propagation → Phase 04.
- Switching the writer to use a dedicated `AuditDbContext` → Phase 05.
- Outbox sink → Phase 06.
- `IAuditEntityWriter` becoming public → not planned; revisit if a real
  external use case appears.

## Done when

- `ImmediateSink.cs` and `IAuditEntityWriter.cs` (+ default impl) exist.
- `MillWorksAuditBuilder.cs` registers `IAuditSink` → `ImmediateSink`.
- `ImmediateSinkTests` is green.
- Full test suite is green (no regressions).
- Phase doc is updated with a one-line "Completed YYYY-MM-DD" note.
