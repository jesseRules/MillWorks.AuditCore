# Hosting, DI, and Configuration Issues

**Status:** Implemented
**Date:** 2026-06-09 (code review)
**Scope:** `MillWorksAuditBuilder`, `ServiceCollectionExtensions`, options binding, `AuditContextMiddleware`, sample project wiring

## Problem

Review of the AspNetCore wiring found one registration call that silently unregisters other components' hosted services, a configuration-binding scheme that makes the sample's appsettings dead config, and a client-controlled header that can suppress a request's audit row at the database. Several smaller issues make seemingly equivalent setups behave differently.

## Findings

### 1. `UseRequestAuditDispatcher` removes unrelated hosted services (High)

`Configuration/MillWorksAuditBuilder.cs:103-112`

The removal predicate deletes every `IHostedService` descriptor that has an `ImplementationFactory`, not just the dispatcher's:

```csharp
(s.ServiceType == typeof(IHostedService) && s.ImplementationFactory != null)
```

`UseSecurity` registers the integrity batcher exactly this way (`MillWorksAuditBuilder.cs:316`). So `UseSecurity(...)` followed by `UseRequestAuditDispatcher<T>()` silently unregisters the `IntegrityWriteBatcher` worker — batched integrity writes are enqueued but never flushed, with no error. Any consumer hosted service registered via factory before `AddMillWorksAudit` is also removed.

**Fix:** Capture the exact descriptor instance added in `ServiceCollectionExtensions.cs:71-72` and remove only that, or test that the factory target is `InProcessRequestAuditDispatcher`.

### 2. Sample appsettings sections never bind; flat `"Audit"` binding collides across options types (High)

`samples/.../appsettings.json:30-52`; `MillWorksAuditBuilder.cs:123, 294, 365, 443, 497`

All six options classes call `BindConfiguration("Audit")` — the same flat section — but the sample ships nested sections (`Audit:EntityFramework`, `Audit:Security`, `Audit:Resilience`, `Audit:Archival`). None of those nested keys match any property; they are silently dead config (e.g. `"Audit": { "Security": { "EnableTamperDetection": true } }` does nothing). Worse, the flat scheme makes `EntityFrameworkOptions.ConnectionString` and `ArchivalOptions.ConnectionString` both bind `Audit:ConnectionString`, so a config-driven deployment cannot give the audit DB and the blob archive different connection strings.

**Fix:** Bind each options type to its own subsection (`Audit:EntityFramework`, `Audit:Security`, ...) to match the sample's (correct-looking) shape, and update the sample/README together.

### 3. Client-controlled `X-Correlation-Id` (up to 128 chars) vs `nvarchar(36)` column (High)

`Services/AuditContextMiddleware.cs:29, 212-216`; `Services/AuditLogger.cs:405-406`; `EntityFramework/Migrations/20260420195321_Init.cs:79, 115`

The middleware accepts a client-supplied correlation id up to 128 chars, but `AuditEvents.CorrelationId` and `AuditLogs.CorrelationId` are `nvarchar(36)`, and the mapping applies no truncation (it also bypasses `SanitizeInput`/redactor, unlike IpAddress/UserAgent). Any caller sending a 37–128 char `X-Correlation-Id` (a W3C traceparent value is 55 chars) makes the SQL insert throw "string or binary data would be truncated" — a request header suppresses that request's audit row (DLQ churn at best). Affects both the deferred request-audit path and the EF interceptor path.

**Fix:** Align `_maxCorrelationIdLength` with the column (or widen the column) and truncate defensively at mapping. Note the same failure mode exists for `UserAgent`/`RequestPath` (500-char columns) if a custom redactor whitelists them.

### 4. Fluent builder values equal to a default are silently discarded (Medium)

`Extensions/ServiceCollectionExtensions.cs:95-123`

The config/fluent merge only overlays a fluent value when it differs from a baseline default: `if (auditOptions.Enabled != baselineAuditOptions.Enabled) opts.Enabled = ...`. A consumer who explicitly sets `Options.Enabled = true` cannot override config `"Audit:Enabled": false`. Concrete sample failure: `Program.cs:20` sets `Options.Environment = builder.Environment.EnvironmentName`; in Production that equals the baseline default `"Production"`, the overlay is skipped, and appsettings' `"Environment": "Development"` wins — production audit rows stamped "Development".

**Fix:** Track which properties the consumer actually set (setter flags on the builder options object) rather than comparing against defaults.

### 5. Migration timeout applied as the command timeout for all runtime queries (Medium)

`Configuration/MillWorksAuditBuilder.cs:176`

`sqlOptions.CommandTimeout(efOptions.MigrationTimeoutSeconds)` makes the documented "timeout for migration operations" (default 300s) the per-command timeout for every audit query/insert. A hung audit write holds the request for 5 minutes; lowering it breaks long migrations.

**Fix:** Add a separate runtime `CommandTimeoutSeconds`; set the migration timeout only inside the migration path (`Database.SetCommandTimeout` in `DatabaseInitializationService`).

### 6. Pass-through-redactor guard bypassed by factory/instance registrations (Medium)

`Configuration/MillWorksAuditBuilder.cs:549-552`; `Services/PassThroughRedactorStartupWarningService.cs:27`

`ValidateConfiguration` only matches `ImplementationType == typeof(PassThroughAuditFieldRedactor)`; registering it via factory or instance bypasses the throw, and the startup warning service returns early when `AllowPassThroughRedactor` is false — silent in exactly the bypass case. `ValidateConfiguration` also reads only the fluent options snapshot, so config-set `AllowPassThroughRedactor`/`Environment` are invisible to it.

**Fix:** Detect pass-through at startup from the *resolved* `IAuditFieldRedactor` instance (the hosted warning service already resolves it) using the final bound `IOptions<AuditOptions>`, and fail there in production.

### 7. Middleware captures identity before authentication can run; sample encodes the risky ordering (Medium)

`Services/AuditContextMiddleware.cs:51, 75-87, 158-181`; `samples/.../Program.cs:104-105`

User attribution is read from `context.User` before `await next(context)`. The sample places `app.UseMillWorksAudit()` immediately before `app.UseAuthorization()` — the spot where the template inserts `UseAuthentication()` — and nothing documents or detects misordering. Apps copying the sample with auth enabled produce request audits with empty user fields.

**Fix:** Document/enforce "after UseAuthentication", or defer user capture to the `finally` block (post-`next`), where the response status is already being read.

### 8. Explicit `IEnumerable<IComplianceValidator>` registration shadows DI composition (Medium)

`Configuration/MillWorksAuditBuilder.cs:400-434`

Registering `IEnumerable<IComplianceValidator>` directly overrides the container's enumerable resolution, so a consumer's `AddScoped<IComplianceValidator, MyValidator>()` is silently ignored.

**Fix:** Register the standard-selected validators individually via `TryAddEnumerable` factories gated on `options.Standards`, letting the container compose the enumerable.

### 9. Low-severity items

- Hardcoded excluded path prefixes (`Services/AuditContextMiddleware.cs:31-40`): `/test`, `/cdn` etc. are static, non-configurable, apply to write methods, and match by bare `StartsWith` — `POST /testimonials` is never request-audited in any deployment. Make the list configurable via `AuditMiddlewareOptions` and match on segment boundaries.
- `AddMillWorksAudit` is not idempotent for the dispatcher hosted service (`Extensions/ServiceCollectionExtensions.cs:71-72`): everything else uses `TryAdd*`, but the `IHostedService` registration appends unconditionally; a second call starts two readers on a `SingleReader = true` channel. Use the `TryAddEnumerable` descriptor shape.

## Implementation Outline

1. Fix #1 (descriptor-targeted removal) with a test: `UseSecurity` + `UseRequestAuditDispatcher` must leave `IntegrityWriteBatcher` registered.
2. Restructure options binding (#2) into subsections; this is a breaking config-shape change, which greenfield policy allows — update sample and README in the same change.
3. Fix the correlation-id width mismatch (#3) at both middleware and mapping.
4. Replace the default-comparison merge (#4) with explicit set-tracking.
5. Apply #5–#9.

## Non-Goals

- `X-Forwarded-For` handling — reviewed clean: the library correctly relies on `ForwardedHeadersMiddleware` rather than parsing proxy headers itself.
- Changing scoped/singleton lifetimes — reviewed clean (interceptor uses `IServiceScopeFactory`; no captured scoped state; no cross-request context bleed).
