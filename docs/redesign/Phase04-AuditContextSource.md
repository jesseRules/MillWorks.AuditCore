# Phase 04 — IAuditContextSource

Master plan: [`../RedesignPlan.md`](../RedesignPlan.md)
Depends on: [`Phase03-InterceptorRefactor.md`](Phase03-InterceptorRefactor.md)

## Goal

Add `IAuditContextSource` to `MillWorks.AuditCore.Abstractions` —
a small interface a consumer DbContext can implement to expose request
context (`CurrentUserId`, `CurrentCorrelationId`, `CurrentIpAddress`,
`CurrentUserAgent`) to the interceptor and sink without those components
casting to a specific DbContext type.

After this phase, the interceptor stops doing
`context as AuditApplicationDbContext` to read context fields. It tries
`context as IAuditContextSource` instead. `AuditApplicationDbContext`
implements the interface (no behavior change). Consumer DbContexts
(`ComplianceDbContext`, etc.) can implement it in Phase 09.

## Constraints

The five hard rules from `feedback_plan_is_spec.md` apply. Additionally:

- The interface must live in `MillWorks.AuditCore.Abstractions` so
  consumer libraries can implement it without referencing the EF package.
- Existing `AuditApplicationDbContext` properties (`CurrentUserId`, etc.)
  must keep their setter surface — middleware uses them.

## Files

| Action | Path | Purpose |
|---|---|---|
| New | `src/MillWorks.AuditCore.Abstractions/Interfaces/IAuditContextSource.cs` | The interface |
| Modified | `src/MillWorks.AuditCore.EntityFramework/Data/AuditApplicationDbContext.cs` | Implement `IAuditContextSource` |
| Modified | `src/MillWorks.AuditCore.EntityFramework/Interceptors/AuditSaveChangesInterceptor.cs` | Read context via `IAuditContextSource` |
| Modified | `src/MillWorks.AuditCore.Services/Sinks/ImmediateSink.cs` (and the writer from Phase 02) | Read context via `IAuditContextSource` if present |
| New | `tests/MillWorks.AuditCore.Tests/Abstractions/AuditContextSourceTests.cs` | Verifies a non-AuditApplicationDbContext that implements the interface flows context into envelopes |

## Type introduced

```csharp
namespace MillWorks.AuditCore.Abstractions.Interfaces;

/// <summary>
/// Implemented by a DbContext to expose request context for the audit
/// pipeline. The interceptor and sink read these properties when building
/// audit envelopes; values flow through to AuditEvent / AuditLog rows.
///
/// All properties are nullable — implementations should return null when
/// the value is not available (e.g., background work outside an HTTP
/// request).
/// </summary>
public interface IAuditContextSource
{
    string? CurrentUserId { get; }
    string? CurrentCorrelationId { get; }
    string? CurrentIpAddress { get; }
    string? CurrentUserAgent { get; }
}
```

`AuditApplicationDbContext` already exposes these as properties. Adding
the interface is a one-line declaration change; the existing properties
satisfy the contract.

## Refactor outline

In `AuditSaveChangesInterceptor`, every site that today does:

```csharp
var auditCtx = context as AuditApplicationDbContext;
var correlationId = auditCtx?.CurrentCorrelationId;
var ipAddress = auditCtx?.CurrentIpAddress;
var userAgent = auditCtx?.CurrentUserAgent;
```

becomes:

```csharp
var contextSource = context as IAuditContextSource;
var correlationId = contextSource?.CurrentCorrelationId;
var ipAddress = contextSource?.CurrentIpAddress;
var userAgent = contextSource?.CurrentUserAgent;
```

There are several such sites — quote the current cast locations during
implementation; do not introduce a helper. (The plan-is-spec rule "no
helpers" applies: if it looks like a `GetContextSource(context)` helper
would clean it up, raise it instead of writing it.)

`AddComplianceSecurityEvent`, `EnforceConsentRequirements`, and any other
method that casts `context as AuditApplicationDbContext` for context
fields gets the same treatment.

## Decisions left to Jesse

1. **Setters on the interface.** The current `AuditApplicationDbContext`
   has both getters and setters (middleware sets them). Should
   `IAuditContextSource` expose setters? **Recommendation:** no — readers
   (interceptor, sink) only need getters. Setting is an
   implementation-specific concern; consumer DbContexts can expose their
   own setter API. Keeping the interface read-only minimizes the contract
   surface.
2. **Default behavior when context does NOT implement
   `IAuditContextSource`.** Today, casting to
   `AuditApplicationDbContext` returns null and all four context fields
   become null. Same behavior with the new interface. No change.
3. **Are there other cast sites worth lifting now?** The interceptor also
   casts to `AuditApplicationDbContext` for `ScopedServiceProvider`,
   `IsDispatchingProviders`, `PendingProviderDispatches`. Those are
   provider-dispatch concerns, not context-source concerns. **Do NOT
   include them in this phase.** They get lifted in Phase 07 / 08 when
   provider dispatch becomes cross-context. Phase 04 is strictly the
   four read-only context fields.

## Verification

```bash
dotnet build MillWorks.AuditCore.sln
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj
dotnet test tests/MillWorks.AuditCore.Tests/MillWorks.AuditCore.Tests.csproj \
    --filter "FullyQualifiedName~AuditContextSource"
```

Acceptance:
- `AuditApplicationDbContext` implements `IAuditContextSource` (no test
  changes; existing tests should keep passing).
- `AuditContextSourceTests` covers: a custom `DbContext : DbContext,
  IAuditContextSource` returns user/correlation values that flow into
  the envelope when the interceptor publishes.
- All existing tests pass.

## README impact

Defer to Phase 10. The "Automatic Entity Change Tracking" section of
`MillWorks.AuditCore/README.md` will mention `IAuditContextSource` as
the contract for consumer DbContexts.

## Out of scope

- Consumer DbContexts implementing `IAuditContextSource` → Phase 09.
- Lifting `ScopedServiceProvider` / `IsDispatchingProviders` /
  `PendingProviderDispatches` → Phase 07 / 08.

## Done when

- `IAuditContextSource.cs` exists in Abstractions.
- `AuditApplicationDbContext` declares the interface.
- Interceptor + sink read context via `IAuditContextSource`, with no
  remaining `context as AuditApplicationDbContext` casts for context
  fields. (Casts for provider dispatch fields stay; those are Phase 07.)
- `AuditContextSourceTests` is green.
- Full test suite is green.
- Phase doc updated with "Completed YYYY-MM-DD".
