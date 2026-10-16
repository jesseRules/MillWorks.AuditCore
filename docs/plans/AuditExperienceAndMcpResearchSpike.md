# Research spike: Audit experience and conversational investigation

**Date:** 2026-10-16  
**Status:** Research and proposed feature scope; implementation decisions remain open  
**Scope:** AuditCore, MillWorks consuming API, MillWorks frontend, MillWorks.Ai, and SQL Builder  
**Delivery target:** Minimum marketable product (MMP) at minimum; no deadline-driven scope reduction  
**Accountable owner:** MillWorks product/architecture owner; implementation responsibilities are assigned below  
**Spike exit criterion:** Record the coverage matrix, query-path and query-representation decisions, authorization ownership, result contract, and complete MMP acceptance/work breakdown. Implementation and live acceptance are separate gates.

## Objective

Enable users and administrators to understand recorded activity through record history, an audit explorer, and natural-language questions in MillWorks.Ai and external MCP clients. All surfaces should return consistent evidence under the same authorization and disclosure rules.

The primary questions are:

- Who changed this record, when, and what were the previous and new values?
- What did a particular person or automated actor do during a period?
- What related activity happened during the same operation?
- What security-relevant activity was recorded, and which records were affected?
- Is this evidence verifiable, and what exactly was verified?
- Can I export the relevant evidence?
- Is the audit pipeline keeping up, or is history delayed or incomplete?

**User direction:** Reuse SQL Builder and its existing MCP investment for querying. Provide friendly audit-specific tools and frontend flows over that capability. Users should work with people, records, actions, and dates; SQL and physical schema details belong in optional technical views.

This file records research, candidate features, implementation seams, and acceptance scenarios. It does not authorize implementation or claim that proposed capabilities already exist.

**Scope clarification after review:** Preserve the complete user, administrator, and MCP feature set in this document. Delivery slices describe implementation order; they do not define a smaller release or defer the difficult capabilities out of the MMP. The user requested an MMP at minimum and stated there is no rush. Every feature table row is in MMP scope; workstream ordering appears in the delivery slices. No feature is removed by the architectural decisions or prerequisite fixes identified in this review.

## External research

Primary sources reviewed on 2026-10-16. These are concrete examples from audit platforms; they do not establish a universal MCP convention among embedded .NET audit libraries.

| System | Published approach | Implication for MillWorks |
| --- | --- | --- |
| Pangea Secure Audit Log | MCP tools include `search_log` and `log_entry`. The repository is archived. | A compact domain-specific search surface is a useful reference; archived code is not a dependency recommendation. |
| Pangea audit API | Search supports actor/action/target/status, old/new fields, time ranges, pagination, and optional proof context. | Keep filters structured and return evidence plus explicit verification context. |
| WorkOS audit harness | `workos_audit_query` creates a filtered export, polls, parses CSV, and returns action/actor/target summaries and sample records. | Compute summaries in the backend and retain supporting records for the assistant's answer. |
| AWS CloudTrail MCP | Supports event-history lookup and CloudTrail Lake queries for conversational activity and security investigation. | Support targeted record lookup and aggregate investigation through the existing query infrastructure. |

Sources:

- [Pangea MCP server](https://github.com/pangeacyber/pangea-mcp-server)
- [Pangea Secure Audit Log API](https://pangea.cloud/docs/api/audit)
- [WorkOS audit harness](https://github.com/workos/workos-audit-harness)
- [AWS CloudTrail MCP announcement](https://aws.amazon.com/about-aws/whats-new/2025/09/aws-cloudtrail-mcp-server-enhanced-security-analysis/)
- [AWS Labs CloudTrail MCP server source](https://github.com/awslabs/mcp/tree/main/src/cloudtrail-mcp-server)
- [AWS Labs CloudTrail MCP tool documentation](https://awslabs.github.io/mcp/servers/cloudtrail-mcp-server)

The proposed MillWorks design below is an architectural recommendation derived from these examples and local code inspection.

### Competitive evidence for the user experience

The MCP examples establish useful integration patterns. The following primary product sources add evidence about the investigation experience. Product documentation establishes published behavior; it does not establish preference frequency among MillWorks users. Cerbos' export announcement reports customer requests, which is vendor-reported demand. Direct user validation remains a spike deliverable and does not reduce the agreed MMP scope.

| User question or task | Competitive evidence | MillWorks MMP interaction |
| --- | --- | --- |
| “What changed before this stopped working?” | Datadog documents resource-specific investigation, actor summaries, and an Inspect Changes diff. [Guide](https://docs.datadoghq.com/account_management/audit_trail/guides/track_monitor_access_and_configuration_changes/) | Start from the affected resource, inspect grouped before/after changes, and pivot to related requests, workflows, and diagnostics. Temporal proximity is a lead, not a causal conclusion. |
| “Why was access allowed or denied?” | Cerbos displays principal roles/attributes, resource context, ALLOW/DENY results, and the policy responsible, with policy links. [Documentation](https://docs.cerbos.dev/cerbos-hub/audit-log-collection) | Show the retained decision and policy/version references, where captured, with separately authorized policy detail. A current policy is not evidence of the policy used at the time. |
| “Export exactly what I investigated.” | Cerbos reports repeated customer requests for external use and provides filter-preserving background exports with status, expiry, and download. [Product update](https://www.cerbos.dev/blog/cerbos-hub-audit-log-exports-and-a-playground-that-tells-you-what-s-wrong) | Freeze explicit investigation parameters and available snapshot/evidence basis, display export lifecycle, and produce a manifest-bearing evidence artifact. |
| “Let my team reopen this investigation.” | Datadog documents saved views, share links, favorites, and reusable troubleshooting contexts. [Documentation](https://docs.datadoghq.com/account_management/audit_trail/) | Save and share authorized investigation definitions, distinguish live queries from retained evidence, and reauthorize recipients. |
| “Tell me when this happens again.” | Datadog supports audit-event threshold monitors and scheduled dashboard reports. [Monitors](https://docs.datadoghq.com/monitors/types/audit_trail/), [reporting](https://docs.datadoghq.com/account_management/audit_trail/) | Convert an investigation into an event/threshold subscription or recurring report using Workflow, BackgroundJobs, Reporting, and Notification. |
| “Did a person, integration, or agent do this?” | GitHub's event reference includes bot/agent attribution, programmatic-access type, request IDs, and token-related identifiers on applicable events. [Reference](https://docs.github.com/en/organizations/keeping-your-organization-secure/managing-security-settings-for-your-organization/audit-log-events-for-your-organization) | Distinguish initiating user, effective/executing actor, service/application, agent/tool run, delegation, and approval references where actually recorded. Never expose credential material. |
| “Let our organization investigate its own activity.” | WorkOS describes actor/event/target/date filtering and self-service SIEM configuration in its customer administration experience. [Product page](https://workos.com/audit-logs) | Provide organization-scoped administration independently of platform operations, with authorized export/integration configuration and delivery status using existing platform capabilities. |

Related internal research: [ActivityGraph competitive analysis](../../../MillWorks/MillWorks.ActivityGraph/ProjectReview.md), [Security review](../../../MillWorks/MillWorks.Security/ProjectReview.md), [Compliance review](../../../MillWorks/MillWorks.Compliance/ProjectReview.md), and [DataGovernance review](../../../MillWorks/MillWorks.DataGovernance/ProjectReview.md). These are inputs for capability mapping; their older competitor/pricing/superiority claims require verification before reuse.

## Current local foundation and gaps

Paths in this section are relative to the named repository. Inspection establishes source behavior, not live deployment acceptance.

| Area | Source evidence | Consequence |
| --- | --- | --- |
| Consuming API | MillWorks: `MillWorks.Api/Controllers/Audit/AuditController.cs` | Existing routes cover event search/detail, entity trails, user activity, summaries/charts, security-event search, integrity, compliance, and archival. Route existence does not establish complete coverage or suitable permissions for every proposed user flow. |
| Search filters | AuditCore: `src/MillWorks.AuditCore.Abstractions/Requests/AuditSearchRequest.cs` | Current contract has dates, user, event type, entity type, text, offset, and limit. Field-specific, correlation, outcome, and subject investigation need further design. |
| Entity-trail implementation | AuditCore: `src/MillWorks.AuditCore.Services/AuditQueryService.cs` | `GetEntityAuditTrailAsync`, user activity, and recent activity query `AuditEvents` and map results to `AuditLogDto`. The DTO name does not mean these methods read `AuditLogs`. |
| Automatic field changes: confirmed read gap | AuditCore: `src/MillWorks.AuditCore.Services/Sinks/Writers/AuditEntityBatchWriter.cs` and `AuditQueryService.cs` | Automatic before/after rows persist into `AuditLogs`; the current trail, user-activity, and recent-activity methods do not read them. The proposed field-change History and deadline investigation cannot work through the current read path. Reading both stores is a prerequisite of delivery slice 1. |
| Tenant isolation: confirmed query/schema gap | AuditCore: `src/MillWorks.AuditCore.Services/AuditQueryService.cs`, `src/MillWorks.AuditCore.EntityFramework/Data/AuditDbContext.cs`, and `Entities/AuditLogEntity.cs` | The query service does not filter by tenant, and AuditDbContext defines no `HasQueryFilter`. `AuditEvents` has `TenantId`; `AuditLogEntity` has no tenant column. Normalized tenant attribution for change rows is available through `AuditLogSubjectLinkEntity`, where links exist. Define and implement tenant isolation before exposing the expanded reads. |
| Change detail: confirmed contract gap | AuditCore: `src/MillWorks.AuditCore.Abstractions/Dto/AuditLogDto.cs`, `src/MillWorks.AuditCore.EntityFramework/Entities/AuditLogEntity.cs` | The DTO lacks tenant, EnvelopeId, separate occurrence/ingestion times, and outcome. Its EntityId is required while the entity's is nullable. Replace or reshape this contract for the new experience. EnvelopeId already exists on the entity and can support grouping where populated; carry it through the read projection. |
| Controller authorization: confirmed defect | MillWorks: `MillWorks.Api/Controllers/Audit/AuditController.cs` | One class-level `Audit.Read` annotation covers anonymization, archive/restore, user export, and three test-write routes, with no method-level authorization overrides. This is an existing operation-authorization defect, independent of the proposed UX. Track immediate remediation in [AuditControllerAuthorizationDefect.md](AuditControllerAuthorizationDefect.md). |
| Subject discovery | AuditCore: `src/MillWorks.AuditCore.Services/IAuditSubjectLookupService.cs` | Tenant-scoped discovery spans explicit events, change logs, and security records. It returns record locations; host authorization and disclosure still apply. A subject is distinct from the actor who performed an action. |
| Shared AI tools | MillWorks: `MillWorks.Ai/Services/Tools/DataViewToolHandlers.cs`, `Services/Mcp/McpToolCatalog.cs` | Existing handler/bridge and catalog patterns support shared tools with permissions and MCP exposure configuration. |
| Existing SQL tools | MillWorks: `MillWorks.Ai/Services/Tools/DataConnectionToolHandlers.cs`, `MillWorks.Api/Bridge/Ai/DataConnectionToolBridge.cs` | Existing SQL/saved-query execution uses canonical SQL Builder governance, warning review, execution status, and DataView continuation. The inspected bridge targets registered external connections. Internal audit-source support is a spike deliverable. |
| Security ownership | [SecurityEventQuerySurface.md](SecurityEventQuerySurface.md) | Its superseding decision assigns security querying/export to MillWorks.Security. Compose with that owner rather than reviving the superseded parallel query surface. |
| Integrity boundary | [EntityChangeChainIntegrity.md](EntityChangeChainIntegrity.md), current writers, and verification services | An existing proposal documents different protection for explicit events and entity-change rows. Reconcile that proposal with current code before assigning verification badges. A healthy event chain must not imply every displayed record is cryptographically verified. |

Additional relevant sources: [ConsumerDbContextAuditing.md](../ConsumerDbContextAuditing.md), [AuditDispositionRecovery.md](AuditDispositionRecovery.md), and [TelemetryAppLogExport.md](TelemetryAppLogExport.md).

## Platform composition and ownership

The full MMP is an assembly of existing MillWorks capabilities plus audit-specific read, authorization, evidence, and presentation work. Planning must account for that existing investment. Rebuilding reporting, scheduling, approvals, notifications, chart rendering, or a generic activity feed inside AuditCore would duplicate platform responsibilities. The scope is not reduced because these integrations cross libraries.

The neighboring [MillWorks solution README](../../../MillWorks/README.md) describes an application factory and reference integration host, with a dated inventory of 295 projects and 63 production EF contexts. Its architecture explicitly assigns composition and cross-domain bridges to the application host. The [BackgroundJobs README](../../../MillWorks.BackgroundJobs/README.md) describes the SQL App Cluster, optional Raft federation, capability-aware Fleet placement, fencing, and documented live soak/failover/chaos acceptance for its stated workload. Those counts and qualifications are context, not a new benchmark or evidence that every audit integration is already wired.

The surfaces below were checked in the neighboring source trees on 2026-10-16. “Existing” means a concrete library contract or implementation exists. The audit binding, its permissions, and end-to-end acceptance remain implementation work unless explicitly stated otherwise.

| Audit feature | Existing owning capability and inspected surface | Audit composition work |
| --- | --- | --- |
| Evidence packages and exports | DocumentGeneration's [IDocumentGenerationService](../../../MillWorks/MillWorks.DocumentGeneration/Interfaces/IDocumentGenerationService.cs) generates single/multiple formats and template output; Reporting's [IReportExecutionService](../../../MillWorks/MillWorks.Reporting/Services/Interfaces/IReportExecutionService.cs) provides execution/history/result streams; [Storage](../../../MillWorks/MillWorks.Storage/README.md) implements the unified blob plane. | Supply the authorized evidence dataset and pinned manifest, define package formats, attach supported proofs, store artifacts, and authorize creation/download. DocumentGeneration generates bytes; it does not own tenant resolution or storage authorization. |
| Governed report data | Reporting's [IReportingDataSourceService](../../../MillWorks/MillWorks.Reporting/Services/Interfaces/IReportingDataSourceService.cs) exposes SQL, saved-query, and version-specific query execution. | Bind audit query representations through the existing host bridges to SQL Builder governance. Pin parameters, versions, disclosure, and source coverage for reproducible reports. |
| Summary charts | ChartEngine's [IChartRenderService](../../../MillWorks/MillWorks.ChartEngine/Services/Interfaces/IChartRenderService.cs) renders chart data and produces frontend configurations. [Visualization](../../../MillWorks/MillWorks.Visualization/README.md) owns reusable versioned charts and governed source bindings. | Map authorized aggregates into chart series and drill-down references. Visualization is marked InDevelopment in its README; qualify the specific integration. Chart rendering does not compute audit truth or authorize source records. |
| Drill-down and larger result exploration | [DataView](../../../MillWorks/MillWorks.DataView/README.md) owns source sessions, saved views, and export metadata; [IDataViewSourceProvider](../../../MillWorks/MillWorks.DataView/Services/Interfaces/Local/IDataViewSourceProvider.cs) includes window reads and optional aggregate/drill-through/export/history capabilities. | Add an authorized audit provider and advertise only implemented capabilities. Default interface methods can reject unsupported operations; contract presence alone does not enable them. Keep count units and as-of/source coverage consistent with tools. |
| Audit dashboard and saved investigations | [Dashboard](../../../MillWorks/MillWorks.Dashboard/README.md) supplies layouts, widgets, snapshots, pins, and saved activity filters; [ISavedFilterService](../../../MillWorks/MillWorks.Dashboard/Services/Interfaces/ISavedFilterService.cs) provides user filter CRUD. | Supply audit widget data and invalidation. Decide whether saved investigations fit Dashboard filters or DataView saved views; current activity-filter storage is not automatically a complete case/investigation model. Retain source references and reauthorize reopened investigations. |
| Alerts and subscriptions | Notification's [INotificationService](../../../MillWorks/MillWorks.Notification/Services/Interfaces/INotificationService.cs) and [README](../../../MillWorks/MillWorks.Notification/README.md) cover delivery, templates, channels, preferences, and queuing. Workflow supplies durable orchestration. BackgroundJobs' [IRecurringJobService](../../../MillWorks.BackgroundJobs/src/MillWorks.BackgroundJobs/Application/Abstractions/IRecurringJobService.cs) supplies recurring registration and operator-scoped controls. | Define audit trigger predicates, checkpoints, deduplication, recipient authorization, and subscription lifecycle. Domain/application logic decides when to alert; Notification owns delivery. Schedule retained queries and reevaluate current authority at execution and delivery. |
| Compliance controls and governance | [Compliance](../../../MillWorks/MillWorks.Compliance/README.md) owns frameworks, controls, review/gap tracking, incidents, and related workflows. DataGovernance's [IDataRetentionPolicyService](../../../MillWorks/MillWorks.DataGovernance/Services/Interfaces/IDataRetentionPolicyService.cs) exposes retention policy and legal-hold queries; its [README](../../../MillWorks/MillWorks.DataGovernance/README.md) describes classification, masking, consent, lineage, and DSAR machinery. | Map AuditCore check results to control evidence, connect retention/legal-hold decisions, and retain policy provenance. Keep audit verification, automated compliance checks, and an organization's control assessment distinct. |
| Security investigation | Security's [ISecurityAlertService](../../../MillWorks/MillWorks.Security/Services/Interfaces/ISecurityAlertService.cs) owns alert lifecycle. The API's [AuditIntegrityAlertProjection](../../../MillWorks/MillWorks.Api/Bridge/Audit/AuditIntegrityAlertProjection.cs) already projects integrity findings into durable Security alerts. | Extend the existing integration with authorized links and investigation views. Preserve event-tenant versus explicit platform-operations-tenant ownership described in the solution README. Avoid a duplicate alert store in AuditCore. |
| Audit Operations and pipeline health | [AppLog](../../../MillWorks/MillWorks.AppLog/README.md) supplies logs, issues, traces, and correlation; BackgroundJobs has [operator recurring contracts](../../../MillWorks.BackgroundJobs/src/MillWorks.BackgroundJobs/Application/Abstractions/IRecurringJobService.cs) and [cluster dashboard contracts](../../../MillWorks.BackgroundJobs/src/MillWorks.BackgroundJobs.EntityFramework/Persistence/Dashboard/IClusterDashboardService.cs). | Compose job/execution state and correlated diagnostics with AuditCore-owned outbox/dead-letter/freshness metrics. Job health alone cannot establish audit delivery or capture completeness. |
| Approval-gated administrative actions | HumanApproval's [IHumanApprovalService](../../../MillWorks/MillWorks.HumanApproval/Services/Interfaces/IHumanApprovalService.cs) supports proposal, decision, and consumption. The API already supplies [SQL warning approval integration](../../../MillWorks/MillWorks.Api/Bridge/HumanApproval/SqlQueryWarningApprovalKindHandler.cs); [Workflow](../../../MillWorks/MillWorks.Workflow/README.md) owns durable execution and approval/checkpoint orchestration. | Define audit-action approval kinds and reviewed scope/fingerprint, bind approval to the exact action, and enforce authorization again at consumption/execution. Route and service permission defects still require direct remediation. Approval infrastructure does not itself authorize an anonymization or restore. |
| Long-text field comparison | TextDiff's [WordDiffBuilder](../../../MillWorks/MillWorks.TextDiff/Rendering/WordDiffBuilder.cs), [SideBySideBuilder](../../../MillWorks/MillWorks.TextDiff/Rendering/SideBySideBuilder.cs), and [scope documentation](../../../MillWorks/MillWorks.TextDiff/README.md) supply presentation-neutral text/sequence diffs. | Compare disclosed text values after redaction and map output to accessible UI. Audit property rows already identify changed fields; scalar, structured-object, and permission comparisons need domain-aware formatting. |
| Actor and personal timelines | ActivityGraph's [IActivityQueryService](../../../MillWorks/MillWorks.ActivityGraph/Services/Interfaces/IActivityQueryService.cs) supports activity queries/entity summaries; [IPersonalActivityFeedService](../../../MillWorks/MillWorks.ActivityGraph/Services/Interfaces/IPersonalActivityFeedService.cs) supplies cursor-based personal feeds. | Use the existing operational activity backbone for its intended views, with links into detailed audit evidence. Resolve authority, coverage, and count semantics before combining it with forensic history; see the boundary decision below. |
| Friendly language and provenance | SQL Builder and Ai supply the inspected query/tool pipeline. [Glossary](../../../MillWorks/MillWorks.Glossary/README.md) defines preferred labels, aliases, and exact version pins; [IGlossaryRegistryService](../../../MillWorks/MillWorks.Glossary.EntityFramework/Services/Interfaces/IGlossaryRegistryService.cs) supports authorized registry/search/exact retrieval. [Semantics](../../../MillWorks/MillWorks.Semantics/README.md) provides source/time provenance, version envelopes, and projection manifests. | Bind audit fields/actions to domain vocabulary and retain definition/source versions where needed. Glossary is not an automatic SQL schema mapper; Semantics does not establish motive or causality. Keep AI answers grounded in the returned audit evidence. |

BackgroundJobs integration must use the contracts connected to the selected runtime. Its README explicitly distinguishes the current processing model from the next-release governed-execution redesign; the new Abstractions operator contracts are described as not yet connected to the runtime. Do not assume target contracts are available merely because their types exist. The existing recurring/operator/dashboard surfaces above provide concrete integration starting points. Federation and Fleet are available platform capabilities, not mandatory dependencies for ordinary audit queries or reports.

### ActivityGraph versus AuditCore: boundary decision

ActivityGraph owns meaningful operational activity, feeds, follow subscriptions, aggregation, and user-facing activity summaries. AuditCore owns captured change/explicit-event evidence and its actual integrity semantics. The read facade should compose these responsibilities without creating a third competing activity store.

The spike must decide:

1. Which questions route to operational activity versus detailed audit investigation. “Show my feed” and “show the old deadline value” have different evidence requirements even when they concern the same action.
2. How ActivityGraph IDs, audit source references, envelope IDs, and correlation IDs link to one action. Define missing links and deduplication explicitly; grouped or dropped activity events cannot stand in for a complete forensic trail.
3. How actor timelines disclose source coverage, retention differences, tenant/resource authority, and aggregation. Neither store's authorization automatically grants access to the other.
4. Whether existing activity publishing needs additional audit-reference metadata, and which host bridge owns it. Preserve each owner's capture and retention semantics; avoid copying raw protected payloads into the activity feed.
5. How counts distinguish meaningful activities, audit events, operations, and property changes. A merged timeline must not silently sum these as equivalent units.

### Integration ownership and MMP evidence

AuditCore owns the evidence schema/read contract repairs and verification behavior. Each platform library retains its capability ownership. MillWorks.Api owns composition bridges, domain ACL resolution, registrations, and operation policy. MillWorks.Ai owns friendly tool contracts and handler integration. The frontend owns the coherent user/admin experience over those authorized contracts.

The new work is the audit-specific data projection, tenant attribution and record policy, shared evidence contract, platform adapters, UX, and cross-surface acceptance. Reuse reduces duplicated infrastructure; it does not remove those obligations. Record one integration receipt per feature: the actual provider/bridge, policy, artifact or execution reference, and acceptance scenario. Library availability alone is not proof that the assembled feature is complete.

## Frontend feature set: users

Feature visibility follows tenant, record, and audit permissions. These features do not imply general access to coworkers' activity or organization-wide logs.

| Feature | Intended experience | Release scope |
| --- | --- | --- |
| Record History tab | On projects, tickets, invoices, documents, and other audited records: chronological actor, action, timestamp, and field changes. | MMP |
| Before/after detail | Friendly field labels, readable values, changed fields only, and explicit hidden/unavailable values. | MMP |
| Operation grouping | Expand one save/operation into its constituent field changes. Preserve individual evidence references. | MMP |
| Actor and record links | Resolve permitted display names and links; label system/service actors. Keep useful historical identifiers when an actor or record no longer exists. | MMP |
| Audit explorer | Search by time range, actor, action/event type, record type, record, and text. Filters remain visible and shareable under the recipient's own permissions. | MMP |
| Event detail drawer | Show readable context, supporting records, outcome where recorded, and optional raw technical detail. | MMP |
| Ask about this record | Seed the assistant with authorized record context and suggested questions such as “Who last changed the deadline?” | MMP |
| Related activity | Follow a correlation ID across authorized records and domains. Explain incomplete linkage. | MMP |
| Evidence export | Export an authorized filtered investigation with scope, timestamps, record references, and verification context. | MMP |
| Personal activity | Show one's own recorded actions and links back to accessible records. | MMP |
| Troubleshoot from a record | Open History with the resource and incident window preselected; pivot to requests, workflow runs, and AppLog diagnostics with source-aware evidence links. | MMP |
| Execution identity | Explain who initiated an action and which user/service/integration/agent executed it, including delegation and approval links where captured. | MMP |

Illustrative history entry:

> Alex Morgan changed Invoice INV-1042 · Oct 16, 10:42 AM  
> Status: Draft → Approved  
> Due date: Oct 30 → Nov 15  
> View related activity · View evidence

Presentation requirements:

- Use the viewer's timezone and expose the exact timestamp/offset. Distinguish occurrence and ingestion time where available.
- Group by a durable operation/envelope identity when available; correlation alone may span several operations.
- Distinguish empty, null, redacted, encrypted, unavailable, and unchanged values.
- Keep raw JSON in optional detail, subject to the same disclosure rules.
- Support keyboard navigation, accessible diff labels, and text equivalents for charts and status colors.
- Distinguish “no matching records” from access denial, delayed capture, unavailable archives, query failure, and incomplete source coverage.
- History records evidence; editing a past audit entry is not a user action. Any future revert feature is a separately authorized domain operation.

## Frontend feature set: administrators and investigators

| Feature | Intended experience | Release scope |
| --- | --- | --- |
| Organization audit explorer | Authorized cross-record investigation with actor, subject, resource, action, outcome, source, date, and correlation filters. | MMP |
| User activity investigation | Timeline for an authorized actor, distinguishing manual, automated, and agent actions where provenance exists. | MMP |
| Verification detail | Check a supported event/range and show verification scope, method, status, time, and proof availability. | MMP |
| Security investigation | Integrate MillWorks.Security events and alerts; link to audit evidence without implying all security data lives in AuditCore. | MMP |
| Evidence package | Export filtered records, applied filters, source coverage, generation time, and available verification proofs. Track export creation and access. | MMP |
| Audit Operations | Pending writes, outbox backlog, oldest pending age, failed deliveries, retry/dead-letter counts, last successful processing, and integrity failures. | MMP |
| Retention and archives | Show configured retention, searchable coverage, archive inventory, validation results, and restoration status. | MMP |
| Compliance checks | Show the checks performed, findings, severity, supporting evidence, and remediation. Label these as automated control checks with explicit scope. | MMP |
| Saved investigations and alerts | Reuse filters and subscribe to meaningful events once query semantics and ownership are settled. | MMP |
| Access-decision explanation | Show recorded allow/deny outcomes, effective roles/attributes that may be disclosed, and exact policy/version references where retained. Label unavailable historical context explicitly. | MMP |
| Investigation collaboration | Name, favorite, reopen, and share investigation definitions and authorized evidence references. Distinguish a live view from retained evidence; recheck recipient access. | MMP |
| Export lifecycle | Show submitted/running/completed/failed/cancelled/expired status, applied filters, source basis, available counts, creation/expiry time, retry/cancel options, and authorized downloads. | MMP |
| Organization audit administration | Let delegated organization administrators manage permitted views, subscriptions, export/report delivery, and existing security/SIEM connections and status without granting platform-wide operations access. | MMP |

Keep pipeline health separate from cryptographic verification and capture coverage. Each answers a different question. A green chain check cannot establish that every business action was captured.

Administrative archive/restore, anonymization, retry/recovery, and retention changes require separately defined permissions and explicit action flows. The current controller exposes anonymization, archive/restore, user export, and test writes under `Audit.Read` without action-level overrides. Correct this existing defect through the [separate remediation issue](AuditControllerAuthorizationDefect.md); it must not wait for completion of the research spike. User export also needs authorized subject/tenant scope and disclosure filtering, rather than a full user dump gated only by read permission.

Charts should summarize meaningful actions and outcomes with drill-down. “Top users” is an investigation aid; recorded event volume is not a productivity measure.

## Full journey: investigate, explain, share, export, and monitor

This is the end-to-end MMP experience. Each step identifies the visible interaction, service handoff, retained state, and completion/failure behavior. Route and tool names not already present remain proposed. A neutral fixture illustrates the journey: invoice INV-1042 unexpectedly has a later due date after an approval workflow. Alex Morgan is the investigating user; another user initiated the workflow and a service actor applied its result.

### 1. Enter from the affected record or an incident

Alex opens INV-1042 and selects History or “Ask about this record.” The frontend supplies the record identity, current authorized scope, and an optional incident time window. Entry from a Security alert, AppLog issue, ActivityGraph item, or notification carries source references and any recorded correlation; an explorer entry begins with explicit filters.

The API resolves the authenticated tenant and record authority through the owning domain. Caller-supplied IDs never establish ownership. A shared/deep link reconstructs the investigation request under the recipient's permissions. Unavailable or inaccessible evidence produces a safe, explicit result. For a deleted record, use the selected historical-authorization policy; absence of a current ACL does not grant access.

**Handoff and state:** frontend → API facade/domain authorization → chosen typed read or SQL Builder path. Preserve the entry resource, filter state, and timezone. A separate platform-operations tenant is not inferred from the current user's organization.

### 2. Load and understand the history

History opens with grouped operations, actor labels, occurrence times, source coverage, and paging. Expand the due-date operation to see old/new values and the individual evidence references. Read explicit events and automatic field changes through the corrected projection; carry EnvelopeId where available. Render disclosed long text through TextDiff and scalars through domain formatting.

The initial query returns a bounded window with stable continuation and count semantics. History differentiates loading, genuinely empty results, delayed capture, unavailable sources, and authorization failure. A user can widen the period or select a particular field. Large histories continue through authorized pagination/DataView without discarding the filter or silently stopping at a limit.

**Handoff and state:** facade → authorized projections → normalized result → History/drawer. Retain query/execution reference, applied filters, source categories, time basis, cursor, and as-of marker where supported. An unchecked record is not displayed as verified.

### 3. Establish actor, authority, and recorded reason

Alex opens the operation detail. It distinguishes the initiating user from the workflow/service actor that executed the change, and links the exact workflow run, agent/tool run, delegation, or approval when recorded. The drawer shows outcome and any retained reason/comment, without converting an absent reason into an inferred one.

For an access question, show the recorded allow/deny decision and historical policy/version reference. Resolve policy details through the owning authorization system under its own permission checks. Clearly label missing decision inputs, unavailable versions, and current-policy views. A later policy edit must not rewrite the explanation of the earlier decision.

**Handoff and state:** API composes evidence with authorized Identity/Security, Workflow, HumanApproval, and domain metadata. New capture fields needed for missing execution provenance are explicit implementation work; the frontend cannot reconstruct reliable delegation from display names.

### 4. Follow related activity and test the explanation

Alex selects “Related activity.” The facade follows recorded envelope/correlation/source links into other authorized changes and relevant workflow, security, activity, and diagnostic records. The frontend shows one operation with its constituent evidence, retaining the source distinction. Filters support include/exclude pivots by actor, action, outcome, resource, field, and time window.

A chart or summary groups the authorized full set by the selected unit, such as operations or changed resources. Selecting a segment opens its matching evidence with equivalent filters. ActivityGraph's grouped activity counts are not silently summed with property-row counts. AppLog errors or nearby changes are shown as related observations; the assistant and UI do not assert that timing proves cause.

**Handoff and state:** SQL Builder computes authorized search/aggregates; DataView supplies governed continuation/drill-through; existing source-owner services resolve linked detail. Preserve source coverage and unavailable-link state rather than representing an incomplete joined result as a complete operation.

### 5. Save and share the investigation

Alex names the investigation “INV-1042 due-date approval,” saves its filters and display configuration, and optionally favorites it. The saved definition records its timezone, source selection, and version/concurrency identity. Reopening a live query evaluates current evidence and current authority; a retained evidence package remains a separately identified artifact with its original generation basis.

Alex shares a link with an authorized reviewer. The reviewer authenticates and sees only currently permitted results; a saved definition, favorite, or link grants no data access. Sharing must not leak protected filter values, titles, or embedded evidence labels to unauthorized recipients. Changes to a shared definition are authorized and versioned so they cannot silently alter the meaning of an already retained package.

**Handoff and state:** selected Dashboard/DataView saved-view owner persists the definition; API owns sharing/record policy. Record whether the link opens a live investigation, a pinned dataset, or an evidence package. Exact storage and collaboration ownership are spike decisions, not a reason to build a second generic saved-view system.

### 6. Verify the evidence in scope

Alex or the reviewer requests verification for supported evidence or a bounded range. The owning AuditCore verification service performs the check. The UI reports the method, checked scope/time, supported records, failures, and available proofs. Unsupported field-change rows remain explicitly unsupported even when related explicit events verify successfully.

**Handoff and state:** facade → verification service → scoped verification receipt/proof reference. Verification failure can link to the existing Security integrity-alert lifecycle. Capture freshness, cryptographic verification, and authorization remain independently visible.

### 7. Generate and receive the evidence package

Alex selects “Export this investigation,” reviews the applied scope/format and available verification context, and submits. The server authorizes export separately, binds the request to an explicit source/evidence basis, and returns an export/job reference. Define whether the export pins record identities or an executable snapshot; if neither is supported, disclose that it is an execution-time query rather than promise an exact historical snapshot.

BackgroundJobs runs the export through Reporting with the selected query representation and current authorization. DocumentGeneration renders requested formats and Storage retains the output through the unified plane. Include a manifest containing applied filters, timezone and date bounds, source/version basis, count units, completeness limits, generation time, and proof references. Freeze disclosed evidence as generated output; never regenerate a retained package silently from live data.

The Exports view shows progress and submitted/running/completed/failed/cancelled/expired state, plus safe failure details and available counts. Completion sends an authorized Notification link. Download rechecks artifact authority and expiry. A permission change before execution or download cannot be bypassed by the saved request. Define how retained artifacts are restricted or replaced after policy changes; audit generation and downloads without copying sensitive output into telemetry.

Cancellation and retry follow the owning execution contract. A retry has a traceable attempt and states whether the source basis is retained or newly evaluated. Expired artifacts offer an explicit new export, not a stale download. Organization SIEM/warehouse delivery uses existing authorized integration capabilities with visible delivery health and retry semantics.

**Handoff and state:** facade → Reporting/SQL Builder → BackgroundJobs → DocumentGeneration/Storage → Notification/download. Preserve requester/tenant, approved scope, execution and attempt IDs, source basis, output identity/digest, lifecycle, and expiry. Map these product states onto actual owner contracts; identify any missing state as implementation work.

### 8. Monitor recurrence and schedule reporting

Alex selects “Watch this investigation” or “Schedule report.” The frontend distinguishes an event match, a threshold over a time window, and a recurring evidence report. Show the retained filter, count unit, evaluation timezone/schedule, recipients, delivery channels, and authorized scope before saving. This is an explicitly requested subscription, not an automatic side effect of viewing history.

Workflow and BackgroundJobs execute the evaluation; SQL Builder uses the same authorized query semantics. Notification applies recipient policy and preferences. Persist a checkpoint/watermark and deduplication rule so repeated polling does not send duplicates. Report missed/late runs, failures, suppressed notifications, and unavailable sources instead of treating them as zero matching events. Changes in authority invalidate or restrict execution and delivery according to the selected policy.

The user can inspect last evaluation/delivery, pause, edit, or remove the subscription. A delivered alert links back to the matching evidence and filters. Recurring reports create traceable artifacts through the export/report path. Closing the original investigation does not silently delete evidence or cancel a subscription; those are explicit actions.

**Handoff and state:** saved investigation → Workflow/BackgroundJobs → governed query → Notification/Reporting. Store subscription version, checkpoint, evaluation/execution receipts, recipient decision, and delivery outcome in the appropriate owning modules.

### 9. Reach a reviewable resolution

The reviewer can explain that the service changed the due date during a particular approval run, cite the old/new values and linked reason if present, and identify what remains unknown. A remediation request links to the owning ticket/workflow/domain action; it never edits audit history. Any authorized domain revert creates a new business operation and new evidence.

The investigation remains reopenable, with its retained packages and related alerts distinguishable from its live view. If conclusions or notes are retained, select the existing owner and authorization model during the spike; do not treat a user-authored conclusion as an immutable audit fact.

### Equivalent journey through chat, agents, and MCP

| User turn | Tool/action and handoff | Required visible answer |
| --- | --- | --- |
| “Who changed the due date on INV-1042?” | Resolve an unambiguous authorized resource; call `get_entity_audit_trail` with the field filter. | Actor/time, permitted before/after values, coverage, and evidence references. Ask for disambiguation only when needed. |
| “Was that the user or an automation?” | `get_audit_event` and authorized linked workflow/agent execution detail. | Separate initiator, effective executor, application, and recorded approval; label absent provenance. |
| “Why was it allowed?” | Resolve retained access-decision evidence through the owning security/authorization surface. | Recorded decision and policy/version references, distinct from business rationale and current policy. |
| “What else happened?” | `get_correlated_audit_activity`, then authorized source-owner tools as necessary. | Grouped related evidence with limits and source links; no invented causal explanation. |
| “How many invoices had this change this month?” | `summarize_audit_activity` through SQL Builder, with explicit operation/resource count unit and timezone. | Backend-computed authorized total and a drill-down reference, distinct from a preview count. |
| “Save and share this investigation.” | Reuse authorized saved-view/investigation mutations through existing tools or a dedicated adapter. | Definition/reference and live-versus-retained basis; recipients receive no authority from the link itself. |
| “Verify and export that evidence.” | Scoped verification service, then authorized asynchronous evidence-package submission/status/download adapter. | Verification scope plus real job state and authorized artifact link when ready. No fabricated completion or unsupported blanket proof. |
| “Notify our reviewers when this happens again.” | Explicit subscription proposal through owning Workflow/Notification/scheduling tools, with required review under those policies. | Confirm scope, predicate/count unit, schedule, recipients, and subscription reference after creation. |

Reuse existing platform tools for saving, sharing, reporting, and subscriptions when their contracts express the exact authorized operation. Add audit-friendly adapters where needed; the original seven investigation tools are not a cap on MMP capability. Discoverability and result references must let the model continue the journey without exposing physical SQL/schema details. Pending jobs, approval-required states, expired links, failed runs, and revoked access remain real states across transports.

### Organization administrator and platform operator journey

An organization administrator opens the organization audit workspace, selects its date/source/resource filters, reviews access decisions or security events, and creates authorized exports, subscriptions, and delivery connections through the paths above. The workspace displays searchable coverage, retention/hold constraints, and integration delivery status. It never grants platform operations authority merely because the user administers an organization.

A platform operator opens Audit Operations, sees delayed capture or delivery failures, and drills into the affected audit pipeline component, BackgroundJobs execution, AppLog trace, or Security integrity alert. The operator distinguishes queued/failed processing from missing producer capture. A failed verification is investigated through its scoped receipt and Security alert; healthy queue processing does not dismiss it.

For archive/restore, retention, anonymization, or recovery, show the exact target scope and expected effects, require the operation-specific authority and applicable HumanApproval/Workflow review, and recheck authority and reviewed scope at execution. Return a durable job/operation reference, show progress/failure, and record the outcome. An approval for one scope cannot authorize a different scope or retry with changed parameters. Current legal holds and domain constraints remain authoritative.

After completion, refresh coverage and operational state, retain the action receipt, and let the operator follow the new audit evidence. Failed or partial operations expose their actual state and permitted recovery action. This closes the operational loop without suggesting that a restore proves integrity, that anonymization is a simple history edit, or that scheduling a job establishes completion.

## SQL Builder-backed query design

The proposed foundation is one governed audit query facade shared by frontend and AI adapters, with SQL Builder providing governed search and aggregate execution over authorized audit projections. AuditCore continues to own capture, storage, and integrity semantics; MillWorks.Security owns its security-query responsibilities. The fixed-read execution path is an explicit architecture decision below. Both options retain SQL Builder, the complete feature scope, and the same evidence/disclosure contract across all surfaces.

```text
Frontend audit views ──> API audit query facade ───────────┐
                                                        │
MillWorks chat / agents / external MCP                   │
  └─> MillWorks.Ai audit tool handlers                   │
       └─> API implementation of an AI-local bridge ─────┤
                                                        v
                                  Shared authorization and result contract
                                    ├─> SQL Builder search and aggregates
                                    └─> fixed reads: SQL Builder or typed service (decision)
                                        └─> approved audit projections
                                             ├─> explicit events
                                             ├─> automatic field changes
                                             └─> authorized owner-provided sources

Verification / archive / pipeline operations ──> owning services
```

Research whether the facade should use parameterized query templates, canonical saved queries, or a typed SQL Builder query model. Prefer server-owned query shapes for the friendly tools. Their public parameters should express audit intent, without requiring SQL, connection IDs, table names, or arbitrary joins.

### Decision required: fixed reads and SQL Builder execution

| Option | Fixed History/detail reads | Search and aggregates | Decision evidence |
| --- | --- | --- | --- |
| A: SQL Builder for every read | Server-owned constrained queries through the canonical executor. | SQL Builder. | Demonstrate suitable internal-source support and predictable History latency; document which fixed queries can produce warning review or pending states. |
| B: typed fixed reads behind the same facade | Typed AuditCore read service for known record trails and event detail, with host authorization. | SQL Builder. | Demonstrate equivalent tenant/resource filtering, masking, coverage, grouping, and evidence references across both paths. |

Keep this decision open until evaluated. The user's SQL Builder direction remains authoritative; option B is a review alternative for fixed reads, not an adopted replacement for that direction. Ordinary History should have a predictable loading experience. Do not manufacture a query-review interaction for a fixed authorized lookup; if SQL Builder genuinely requires review, preserve it and address the query design. A typed path must not be used as a fallback to bypass a denied or review-required SQL query.

### Ownership of tenant and record authorization

MillWorks.Api owns authorization composition in the facade and bridge. Domain owners remain authoritative for record ACL decisions. AuditCore must require tenant-scoped read inputs and enforce storage/query tenant boundaries; it does not infer host ACLs. Introduce an `IAuditRecordAuthorizer`-style host seam if required by the chosen typed-read design; the exact interface location remains an implementation decision.

The API owner must supply an authorized record set or equivalent query predicate before aggregation, pagination, and totals. A per-row callback after an unrestricted aggregate is insufficient. SQL Builder projections and typed reads must consume equivalent host policy. Counts describe only the authorized set and must not expose hidden-row totals or infer their existence through diagnostics. If a domain cannot express or evaluate the necessary authorization, refuse that query or explicitly report unavailable coverage; do not return an unrestricted aggregate or present a partial callback scan as exact.

Deleted-record access is an explicit unresolved policy: current-record ACL lookup cannot authorize a deleted resource. Evaluate retained authorization metadata, an authorized parent scope, or a separately permissioned historical-investigation policy with the domain owner. Retention does not itself grant disclosure. Until a policy is selected and enforceable, deny disclosure of the deleted record's evidence. Include archive restoration and ownership-transfer behavior in that decision.

Tenant schema decision: prefer adding authoritative tenant attribution to `AuditLogEntity` under the greenfield architecture, subject to confirming the producer contract and any deliberately global event semantics. Compare this with subject-link joins; subject links are not guaranteed to exist on every row and are not automatically an authoritative record-ownership boundary. Record the schema decision and handling of unattributed evidence before implementation. Do not assign tenants by guesswork.

Required investigation:

1. Identify the canonical internal-source registration and execution path. The external-connection MCP bridge cannot be assumed to accept internal audit data unchanged.
2. Define authorized projections across `AuditEvents`, `AuditLogs`, subject links, and owner-provided security sources. Map actor, subject, resource, event/envelope ID, occurrence time, ingestion time, and correlation without inventing missing values.
3. Preserve underlying records while distinguishing operations, envelopes, events, and property rows. Define count units and avoid duplicate activity when one action produces multiple kinds of evidence.
4. Fix the confirmed absence of tenant predicates in the read service and decide direct tenant attribution for `AuditLogs`, which currently has no tenant column. Apply the API-owned record authorization and masking policy to search, aggregates, detail, joins, and continuation. Treat unattributed evidence explicitly.
5. Reuse compilation, policy evaluation, parameter binding, row limits, timeouts, execution receipts, and warning/approval behavior. A friendly wrapper must preserve actual governance decisions.
6. Determine permission composition: ordinary audit users should not automatically receive arbitrary SQL execution. Decide how audit permissions authorize constrained templates within SQL Builder's policy model.
7. Reuse DataView handles for larger authorized results where appropriate. Revalidate access on continuation and detail retrieval.
8. Evaluate indexed filters, join cardinality, bounded result sizes, stable pagination, and snapshot/as-of semantics while ingestion continues.

Integrity verification, archive validation/restore, and operational recovery remain service operations. Reading hashes or queue rows through SQL does not perform these actions.

## Friendly MCP and assistant feature set

Implement audit tools in the existing MillWorks.Ai handler/catalog pipeline so in-app chat, agents, and external MCP share behavior. Define an AI-local bridge contract and implement it in MillWorks.Api to preserve library boundaries. Tool names below are proposals.

| Tool | Friendly inputs | Questions and output |
| --- | --- | --- |
| `search_audit_events` | Time range, actor, action, resource type/ID, outcome, optional field, text, cursor, limit | “Were any invoices deleted this week?” Returns bounded evidence and scope. |
| `get_entity_audit_trail` | Resource type/ID, optional field and dates, cursor | “Who changed this deadline?” Returns grouped changes with source record references. |
| `get_audit_event` | Source-aware evidence reference | “Show me the evidence.” Returns permitted detail and related references. |
| `get_correlated_audit_activity` | Correlation ID, optional time bounds, cursor | “What else happened during that operation?” Returns authorized related activity with grouping and coverage. |
| `summarize_audit_activity` | Date range, filters, allowed grouping and count unit | “How many permission changes happened this month?” Returns backend-computed totals and drill-down references. |
| `verify_audit_integrity` | Supported evidence reference or bounded range | “Has this evidence passed verification?” Calls the owning verification service and states the precise scope. |
| `get_audit_pipeline_status` | Authorized scope | “Is history delayed?” Returns pipeline freshness and backlog indicators for administrators. |

Reuse existing entity discovery to resolve names such as “the Acme project”; return ambiguity when multiple records match. Distinguish an actor filter from a subject filter. Security-specific questions should compose with the owning security tools/services.

Tool selection rule: use `get_entity_audit_trail` when one known record is the subject and the user wants chronological grouped changes or a particular field's history. Use `search_audit_events` for discovery, multiple resources, or broader event/outcome filters; its resource filter may narrow an investigation but does not replace the grouped History presentation. Both use the same underlying evidence semantics. Validate this distinction with representative model calls; merging them into one tool with an explicit mode remains an option if selection is unreliable.

Implement search, entity history, event detail, and aggregate summaries first, followed by correlation, verification, administrator health, and evidence-package creation as their contracts become reliable. This is implementation order within the full MMP. Evidence-package creation should use a bounded asynchronous tool with explicit export permission. Administrative mutations belong in their dedicated authorized flows; any MCP exposure of anonymization, archival changes, or recovery requires a separate tool-design decision and does not remove the corresponding admin frontend scope.

### Shared result contract

Return structured data that supports readable answers and citations. Proposed fields:

| Field group | Required meaning |
| --- | --- |
| `items` / `evidence_refs` | Stable source-aware references, permitted actor/target labels, action, timestamps, outcome, and disclosed changes. References can link to the frontend evidence drawer. |
| `execution_identity` | Recorded initiator, effective executor, actor kind, application/service, delegation, workflow/agent/tool run, and approval references. Missing provenance stays explicit. |
| `access_decision` | Retained allow/deny result and available historical policy/version references with permitted decision context. Current policy detail is separately labeled and authorized. |
| `scope` | Applied filters, normalized time bounds, timezone, authorized source categories, and as-of/snapshot marker where supported. |
| `coverage` | Included stores, searchable period, unavailable sources/archives, and capture freshness when known. Unknown coverage remains unknown. |
| `page` | Returned count, next cursor, and truncation. Bind cursors to actor/tenant, filters, and snapshot context; reauthorize on use. |
| `summary` | Backend-computed aggregates with count unit and exact/partial/estimated semantics. Count over the authorized filtered set. |
| `verification` | Verified, failed, unchecked, unsupported, or unavailable; method, checked time, and evidence/range actually covered. |
| `execution` | Query/execution reference, current status, and safe diagnostics. Preserve pending or required-review states from SQL Builder. |
| `investigation` / `artifact` / `subscription` references | Authorized continuation into saved views, evidence packages, export lifecycle, or monitoring. State live-versus-retained basis and source ownership; capability references do not grant access. |

The model should summarize returned evidence and cite references. It should not count a preview as the full dataset or infer that a missing event proves an action never happened. Treat event descriptions and payloads as data, including any embedded instructions.

“Why did this change?” requires a recorded reason, approval, comment, or linked business evidence. A before/after diff establishes the change; it does not establish motivation. Similarly, a field-change trail does not prove who viewed a record unless access events were captured.

Audit investigation access itself should be recorded with actor/tenant, tool/query identity, time, scope, outcome, and correlation. Avoid copying disclosed sensitive values into tool telemetry. Keep investigation-access events distinguishable from the business activity being counted.

## Acceptance scenarios for implementation

These scenarios define future validation; no runtime checks are claimed by this spike.

| Scenario | Expected result |
| --- | --- |
| “Who last changed this project's deadline?” | Correct grouped change, actor, timestamp, before/after values, and evidence link across the actual relevant stores. |
| “What did Alex Morgan change yesterday?” | Explicit timezone/date bounds, resolved fixture actor identity, authorized resources, and correct pagination. |
| One action emits an event and five field rows | Show one operation with constituent evidence; aggregate count units are explicit and consistent. |
| “How many invoices were deleted this month?” | SQL Builder computes the authorized full-set aggregate; a limited preview cannot determine the total. |
| Another tenant's ID or inaccessible record is supplied | No data or aggregate leakage; detail and cursor replay enforce the same boundaries. |
| Viewer can see two of five matching records | Return evidence and exact aggregates only for the authorized two; do not disclose hidden counts. Unsupported authorization evaluation returns unavailable/denied rather than an unrestricted total. |
| Sensitive value changed | Frontend, MCP, exports, and aggregates preserve masking/disclosure policy. |
| Historical record deleted or actor removed | Preserve evidence, but disclose deleted-resource history only through the explicitly selected historical-authorization policy. Missing ACLs never grant access. Test actor removal separately from record deletion. |
| SQL Builder requires warning review or remains pending | Tool returns the genuine execution state and existing review flow; no alternate execution path bypasses it. |
| Source unavailable, backlog present, or old history archived | Answer states the known coverage/freshness limit and does not claim absence. |
| Event verifies but associated change rows lack proof | Verification applies only to supported evidence; no blanket verified badge. |
| “Why was this approved?” without a recorded reason | Report what is known and that no reason was found in the queried evidence. |
| Same investigation through frontend, chat, and MCP | Equivalent authorized evidence, filters, count semantics, and verification scope. |
| Automated change initiated by a person | Identify both recorded identities and the exact workflow/tool/approval references without inventing delegation. |
| Historical allow decision followed by policy change | Explain the retained historical decision; current policy is separately labeled and cannot replace the original evidence. |
| Shared investigation opened by a less privileged reviewer | Reauthorize filters, titles, labels, evidence, and aggregates; the link grants no access and reveals no hidden counts. |
| Export submitted, then permission revoked | Execution/download enforce current policy; no stale request or notification link bypasses authority. Preserve the actual failed/restricted state. |
| Data changes during an export or retry | Manifest and UI identify pinned evidence versus execution-time data; retry lineage and any changed source basis are explicit. |
| Alert query has no matches versus evaluation fails | Zero matches and failed/unavailable evaluation are distinct; checkpoints avoid duplicate deliveries and links reopen the matching scope. |
| Full invoice investigation across all surfaces | Follow entry → grouped diff → execution identity/access context → related activity → save/share → scoped verification → export lifecycle → recurrence monitoring → reviewable resolution, preserving owner contracts and evidence at every handoff. |

## Spike deliverables and decisions

- [ ] Pre-spike remediation: resolve [controller authorization defect](AuditControllerAuthorizationDefect.md) independently; include existing endpoint tenant isolation and field-change read coverage as explicit prerequisites rather than new-surface-only work.
- [ ] Inventory frontend audit screens and current domain-specific histories before proposing navigation changes.
- [ ] Validate the competitive user journeys with representative record users, organization administrators, investigators, and platform operators; record direct feedback separately from vendor feature evidence.
- [ ] Record the platform composition matrix, actual bridge/provider registrations, current-versus-target runtime availability, and integration acceptance for every MMP feature; reuse existing capability owners.
- [ ] Decide ActivityGraph versus AuditCore routing, source linkage, retention/authorization behavior, and deduplicated count units for actor timelines.
- [ ] Produce an endpoint-to-store and producer-coverage matrix, including field changes, explicit events, security sources, and relevant platform change logs.
- [ ] Record the fixed-read architecture decision, SQL Builder's internal-source integration seam, and constrained query representation. Preserve the user's SQL Builder direction and full MMP scope.
- [ ] Prototype the read contract on disposable fixtures covering both explicit events and field changes; record an execution receipt and backend aggregate.
- [ ] Assign API/domain authorization responsibilities; decide direct tenant attribution on AuditLogs, deleted-record access, audit-versus-SQL permissions, disclosure rules, and administrator operation permissions.
- [ ] Replace or reshape AuditLogDto for source-aware references, nullable resource identity, EnvelopeId, tenant attribution, and explicit timestamp/outcome availability.
- [ ] Specify evidence references, operation grouping, count units, pagination, coverage, and verification semantics.
- [ ] Map every journey step to concrete API/tool, authorization owner, producer fields, saved definition/job/artifact/subscription state, and failure/continuation behavior. Resolve execution provenance and historical policy-decision capture gaps.
- [ ] Select owners for saved investigations/sharing, export lifecycle, recurrence checkpoints/deduplication, and organization delivery configuration; reuse the platform composition matrix.
- [ ] Sketch History tab, audit explorer, event drawer, administrator operations, and assistant evidence links.
- [ ] Validate representative natural-language questions against the proposed tool descriptions and structured results.
- [ ] Split implementation into repository-owned work items with dependencies and acceptance evidence.

Suggested delivery slices (all are part of the full MMP; these are sequencing boundaries, not release scope cuts):

1. Repair existing endpoint authorization and tenant isolation; establish field-change reads, authoritative tenant attribution, host ACL enforcement, and the new result contract. Record the SQL Builder/fixed-read decision and integration design.
2. Deliver record History, explorer, and detail drawer alongside friendly search/history/detail/summary tools.
3. Add correlation investigations, scoped verification, and evidence packages.
4. Complete security composition, operations health, archive/retention and compliance/admin flows, personal activity, saved investigations, and alerts using the platform owners and bridges above. Validate all frontend and MCP feature tables together before calling the deliverable marketable.

MMP completion requires the full feature tables and assistant/MCP contract to be implemented and accepted with evidence, including export, security composition, operations, and administration. A prototype or search/history-only increment is progress, not MMP completion. Any future scope change requires an explicit product decision from the user.

Open decisions include fixed-read execution, permission granularity for record History, deleted-record authorization, direct tenant attribution, retention of historical display labels, query-source registration, export format/lifecycle, live versus cached verification cost, and freshness guarantees. Resolve these with source evidence and representative fixtures before committing to implementation estimates.
