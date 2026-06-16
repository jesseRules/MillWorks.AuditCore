# Plan — Export AuditCore Telemetry to AppLog (OTLP traces + logs)

**Status:** Draft / not started
**Date:** 2026-06-16
**Repos:** Emitter/source side lives here (`MillWorks.AuditCore`). The ingestion side lives in
`MillWorks` (`MillWorks.AppLog` + `MillWorks.Api`); see the paired ingestion plan
`MillWorks.AppLog/Plans/31-External-Worker-Telemetry-Ingestion.md` and its AuditCore-specific companion
`MillWorks.AppLog/Plans/33-AuditCore-Telemetry-Source.md`.

> **This library is a telemetry *producer*, not a worker process.** It only *defines* an `ActivitySource`
> and a `Meter`; it configures **no exporter**. All export wiring (and the `service.name` resource attribute)
> is owned by the **host that consumes the NuGet package**. So most of the work below is host-side
> configuration + AppLog-side ingestion, with **one** real code change in this repo: gating the
> user/entity identifiers currently set on query spans (§3.3).

## Prerequisites (do not start the host-export work until these land)

1. **AppLog plan 31 item 1** — `v1/traces` accepts `application/x-protobuf`. The standard .NET OTLP exporter
   speaks protobuf/gRPC, not OTLP/HTTP-JSON; until this ships no AuditCore host can export to AppLog. **Hard blocker.**
2. **AppLog plan 33 §2** *(recommended, not strictly blocking)* — `Spans.ScopeName` capture, so AuditCore
   spans are separable from the co-resident host's own spans. Without it, AuditCore spans are still queryable
   by the `audit.*`/`outbox.*`/`integrity.*` name prefix but share the host's `service.name`.

The **one library code change** (§3.3, PII gate) can be done independently and should land **before** any
host enables export, since AppLog does not redact spans.

---

## 1. Verified findings (measured 2026-06-16)

### 1a. What this library emits

**Traces** — one `System.Diagnostics.ActivitySource`: `MillWorks.AuditCore` v1.0.0
(`Abstractions/Diagnostics/AuditActivitySource.cs`). Operation names are namespaced and stable:
`audit.write`, `audit.write_batch`, `audit.query`, `audit.archive`, `audit.restore`, `outbox.write`,
`outbox.drain`, `integrity.write`, `integrity.flush`, `integrity.check`, `integrity.reconcile`. Pure
`System.Diagnostics`, zero-overhead when no listener is registered. Span sites confirmed in
`AuditQueryService`, `AuditArchivalService`, `Sinks/AuditOutboxWriter`, `Sinks/AuditOutboxDrainer`,
`TamperDetection/IntegrityWriteBatcher`, `Services/ResilientAuditLogger`.

**Metrics** — one `System.Diagnostics.Metrics.Meter`: `MillWorks.AuditCore` v1.0.0
(`Services/Telemetry/AuditMetrics.cs` + `AuditOutboxQueueObserver.cs`). Histograms (outbox batch size,
drain duration, row age), counters (envelopes published/failed/duplicate, retry attempts, DLQ routed, leases
recovered), and observable gauges (outbox pending count, in-flight count, oldest-pending age). **This is the
bulk of the library's observability value** — and AppLog ingests no metrics (companion plan 33 §1).

**Logs** — plain `Microsoft.Extensions.Logging.ILogger` throughout the Services layer. **No OTel logging
bridge in the library.**

**Diagnostics counters** — `IAuditDiagnostics` exposes aggregate health counters (snapshot fallbacks, DLQ
store/replay, integrity reconciliation, request-dispatcher backpressure). These are an in-process health
surface, not OTLP signals; out of scope for export.

### 1b. Export wiring is consumer-owned

- The library references **only `OpenTelemetry.Api` 1.16.0** (`Abstractions.csproj`). No SDK, no exporter,
  no `AddOpenTelemetry()` anywhere in the repo — by design.
- It ships `AddAuditCoreInstrumentation(this TracerProviderBuilder)` (registers the `ActivitySource` name)
  in `Abstractions/Diagnostics/AuditCoreTracingExtensions.cs`. **There is no metrics equivalent** —
  a consumer wanting AuditCore metrics must hand-write `.AddMeter("MillWorks.AuditCore")` (see §3.4).
- `service.name` is **not** set by the library — it comes from the host's `ResourceBuilder`. A library cannot
  self-identify as `"MillWorks.AuditCore"`; whatever the host names itself is what reaches
  `[log].[Spans].ServiceName`. The library *can* be distinguished by instrumentation scope (`MillWorks.AuditCore`)
  once AppLog captures it (plan 33 §2), or by span-name prefix.

### 1c. PII surface on spans (AppLog spans bypass redaction) — the one real gap here

`audit.query` spans tag **actual user/entity identifiers** (`Services/AuditQueryService.cs`):
- `audit.entity.type` (`:47`), `audit.entity.id` (`:48`) — entity-trail query
- `audit.user.id` (`:85`) — user-activity query

Lower-risk identifiers elsewhere: `audit.event.id` (a GUID; `IntegrityWriteBatcher.cs:96`,
`ResilientAuditLogger.cs:61`) and `audit.event.type` (an event-category string; `ResilientAuditLogger.cs:62`).
Everything else tagged is non-sensitive: counts, batch sizes, outcomes, archive ids, retry attempts.

**AppLog does not redact spans** (plan 31 guardrail). So the three `audit.query` identifiers must be gated
off before any host exports AuditCore spans to a shared `[log]` store. This is the only library code change.

### 1d. Trace correlation across write→drain (set expectations)

- Request-triggered writes run inside the host's ambient request `Activity`, so `audit.write` /
  `audit.write_batch` spans **parent correctly to the originating request**.
- The **outbox drain, integrity flush, archival, and reconciliation** run on background threads with no
  ambient request Activity. There is **no traceparent persisted on `AuditOutboxEntity`** (confirmed: no
  trace column on the outbox entity), so these spans arrive as **new roots**, disconnected from the request
  that produced the envelope.
- Mitigation already present: audit *records* carry a W3C-capable `CorrelationId`/`RequestId`
  (`AuditEventEntity.cs:206`, `AuditLogEntity.cs:89`; set from `HttpContext.TraceIdentifier` in
  `BaseAuditProvider.cs:96`), so id-based correlation between a record/log and a trace still works. The
  span-to-span gap on the drain path is closed only by the optional propagation in §3.5.

---

## 2. Scope

1. **Host-side OTLP trace export** (no library change) — documented wiring so a consumer points the standard
   .NET OTLP exporter at AppLog `v1/traces`.
2. **Host-side / MEL log export** — route `ILogger` output to AppLog via the already-shipped MEL provider, or
   OTLP logs once AppLog ships `v1/logs`.
3. **Span-PII gate (library code change)** — make the `audit.query` user/entity identifiers opt-in / hashed,
   **default-off**. *This is the only code change in this repo.*
4. **Metrics registration helper** *(optional, minor)* — add `AddAuditCoreMetrics` to match the tracing
   helper, for non-AppLog backends.
5. **`service.name` + redaction guidance** — what the host must set, and the span-PII audit (§1c).
6. **Trace-context propagation on the outbox** *(optional, deferred)* — parent drain spans to the originating
   write.

## 3. Implementation plan

### 3.1 Host-side OTLP trace export (no library change)
Document (in `README` / `docs/`) the consumer snippet — the `ActivitySource` name is already public via
`AddAuditCoreInstrumentation()`:
```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("MyHost"))      // → Spans.ServiceName / source map
    .WithTracing(t => t
        .AddAuditCoreInstrumentation()                   // AddSource("MillWorks.AuditCore")
        .AddOtlpExporter(o =>
        {
            o.Endpoint = new Uri("https://<api>/v1/traces");
            o.Protocol = OtlpExportProtocol.HttpProtobuf; // requires AppLog plan 31 item 1
            o.Headers  = "X-AppLog-Ingest-Key=<from secret store>"; // if plan 31 item 3 enabled
        }));
```
- Requires `OpenTelemetry` (SDK) + `OpenTelemetry.Exporter.OpenTelemetryProtocol` on the **host** (the library
  ships neither). Add to a reference sample if one exists.
- Hard prerequisite: **AppLog plan 31 item 1** (accept `application/x-protobuf` on `v1/traces`).

### 3.2 Host-side log export
- **Easiest (no AppLog endpoint):** add the already-shipped `MillWorks.Extensions.Logging.AppLog` MEL provider
  to the host logging builder; AuditCore's `ILogger` output lands in `[log].[Logs]` source-stamped. Forbid
  `Database.Migrate()` on `AppLogDbContext` from the host (AppLog owns `[log]`).
- **OTLP alternative:** once AppLog ships `v1/logs` (plan 31 item 2), bridge MEL → OTLP logs and export over
  the same OTLP channel as traces.

### 3.3 Span-PII gate (library code change) — the real work here
- Add a tracing option (new `AuditTracingOptions`, or a field on the existing telemetry/security options)
  `IncludeSensitiveSpanTags` **defaulting to `false`**, resolvable where `AuditQueryService` builds its spans.
- When `false` (default): omit `audit.user.id`, `audit.entity.id`, `audit.entity.type` entirely, **or** set a
  stable salted hash (decision: prefer omission — the span name + counts already give operational value; a
  hash only helps if cross-span correlation on those ids is genuinely needed). When `true`: current behavior,
  for hosts whose trace backend is private and not the shared `[log]` store.
- Wire the flag into the three set-sites in `AuditQueryService.cs:47-48,85`. Leave the low-risk
  `audit.event.id` / `audit.event.type` tags as-is (GUID + category), but document them.
- Zero behavior change when no listener is attached (tagging is already `activity?.SetTag`).

### 3.4 Metrics registration helper (optional, minor)
- Add `AddAuditCoreMetrics(this MeterProviderBuilder b) => b.AddMeter(AuditMetrics.MeterName)` alongside the
  tracing helper (feasible against `OpenTelemetry.Api` alone, same as the tracing extension).
- **Caveat:** AppLog ingests no metrics (plan 33 §1) — this helper is for hosts exporting AuditCore metrics to
  a *metrics* backend (Prometheus / Azure Monitor / OTLP-metrics collector), **not** for the AppLog path.

### 3.5 Trace-context propagation on the outbox (optional, deferred)
- To parent the `outbox.drain` (and downstream integrity/archival) spans to the originating write, persist
  `Activity.Current?.Id` (W3C traceparent) on the outbox row at `outbox.write`, and restore it as the span
  parent (or an `ActivityLink`) at `outbox.drain`. Mirrors the BackgroundJobs emitter plan §3.3.
- **Deferred** because audit records already carry a W3C-capable `CorrelationId` (§1d), so id-based
  correlation works today; this only adds span-to-span linkage. Costs a nullable column + migration on
  `AuditOutboxEntity`. Decide parent-vs-link if pursued; keep null-safe (no traceparent → unchanged new root).

### 3.6 `service.name` + redaction guidance
- Document that the host **must** set `service.name` deliberately (§3.1) and that AppLog seeds its source map
  from it (plan 33). Note that AuditCore spans share the host's `service.name`; rely on `Spans.ScopeName`
  (plan 33 §2) or the span-name prefix to separate them.
- Run the §1c span-PII audit and confirm the §3.3 gate is off before enabling; AppLog does not redact spans.

### 3.7 Metrics — explicitly unchanged
- AuditCore metrics stay on the host's existing metrics backend. AppLog ingests no metrics (plan 31 item 4
  non-goal). If metrics-in-AppLog is genuinely wanted — especially the outbox queue-depth gauges — that is a
  new AppLog `v1/metrics` + `[log].[Metrics]` project, tracked in plan 33, **not** in scope here.

## 4. Guardrails
- **PII gate default-off.** No host may export AuditCore spans to `[log]` with `IncludeSensitiveSpanTags = true`.
- **No regression when unlistened.** All span tagging stays `activity?.SetTag`; the gate only changes which
  tags are set when an Activity exists.
- **Host never migrates `[log]`.** The MEL fallback must not call `Database.Migrate()` on `AppLogDbContext`.
- **Backpressure is droppable.** The host's OTLP exporter must treat AppLog `429`/`413` as droppable, never
  fatal — telemetry export must never stall the audit write path or the drain loop.
- **Span volume is bursty** on write-heavy hosts (a span per write/batch). Confirm AppLog's `"otlp"`
  rate-limit + `AppLogSpanOptions.MaxBatchSize` (plan 33 §4).

## 5. Required tests
- **PII gate:** with `IncludeSensitiveSpanTags = false` (default), `audit.query` spans carry no
  `audit.user.id` / `audit.entity.id` / `audit.entity.type` (assert via a recorded `ActivityListener`);
  with `true`, they do. Low-risk `audit.event.*` tags unaffected.
- **Request correlation:** an `audit.write` triggered inside an active Activity is a child of the request
  trace; a background `outbox.drain` is a new root (unchanged) and throws nothing.
- **Export round-trip** (integration, gated on AppLog plan 31 item 1): a host configured with the OTLP/
  HttpProtobuf exporter + `AddAuditCoreInstrumentation()` lands `audit.*` spans in `[log].[Spans]` with the
  configured `service.name` and, once plan 33 §2 ships, `ScopeName = "MillWorks.AuditCore"`.

## 6. Done when
A host consuming AuditCore, configured with the standard OTLP exporter (`HttpProtobuf`, endpoint
`<api>/v1/traces`, optional ingestion key) and the AppLog MEL log provider, lands its `audit.*` / `outbox.*` /
`integrity.*` spans in `[log].[Spans]`/`[log].[Traces]` (distinguishable by `ScopeName` or name prefix) and its
`ILogger` output in `[log].[Logs]` — with request-triggered `audit.write` spans **correlated to the Api request
by trace id**, **no user/entity identifiers on spans** (gate off), and **no Azure Monitor connection string**.
Metrics remain on the host's metrics backend (documented non-goal).
