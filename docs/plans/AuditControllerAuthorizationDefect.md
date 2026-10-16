# Defect: Audit.Read authorizes administrative writes and user export

**Date:** 2026-10-16  
**Status:** Open; confirmed in source, remediation not implemented  
**Priority:** High; independent prerequisite for the audit experience  
**Owning implementation:** MillWorks.Api with MillWorks.Identity policy ownership and AuditCore service integration  
**Related research:** [Audit experience and MCP spike](AuditExperienceAndMcpResearchSpike.md)

## Finding

`MillWorks/MillWorks.Api/Controllers/Audit/AuditController.cs` applies `[Authorize(AuditPermissions.Read)]` at class level. The actions below have no method-level authorization overrides or explicit subject/resource authorization checks in their action bodies. They delegate directly to audit services. The current read policy therefore serves as their operation authorization, although these are mutations or sensitive exports.

| Route under `/api/v1/audit` | Current exposure | Required remediation |
| --- | --- | --- |
| `POST compliance/anonymize/{userId}` | A reader can request user anonymization. | Require dedicated operation permission, authorized tenant/subject scope, and explicit destructive-action flow. |
| `POST archive` | A reader can request archival by date. | Require archival administration and constrain the affected scope. |
| `POST archive/restore/{archiveId}` | A reader can request restoration. | Require restore administration and authorize the archive and destination scope. |
| `GET compliance/export/{userId}` | A reader can request a full user audit-data export. | Require export authority, subject/tenant authorization, and disclosure filtering. Define any self-service export separately. |
| `POST test-event`, `POST test-event-scope`, `POST test-operation` | A reader can create synthetic audit records. | Remove test routes from the normal application surface or enforce explicit development/test controls and write authorization. |

This is a source-confirmed authorization defect. It is not a claim of a live exploit or a completed review of every host policy. Validate the complete authorization pipeline during remediation; do not treat unspecified global policy as the fix.

## Remediation scope

1. Inventory existing Identity permissions and choose operation-specific policies. Add missing policies with the owner; do not rely on a broad administrator label alone.
2. Enforce tenant, subject, archive, and record scope in the API composition layer and relevant service contracts. A stronger route permission alone does not establish data scope.
3. Apply disclosure/redaction to export and log access without duplicating sensitive exported content into telemetry.
4. Audit administrative actions with initiator, authorized scope, outcome, and correlation. Define explicit user confirmation for destructive actions in their product flows.
5. Review the remaining controller actions and current query endpoints for equivalent issues, including unscoped reads. Track any broader findings with concrete evidence.

## Acceptance evidence

- An authenticated fixture actor with only `Audit.Read` cannot anonymize, archive, restore, export another user's data, or invoke synthetic test writes.
- Each permitted operation succeeds only for the correct dedicated authority and authorized tenant/subject/archive scope.
- Unauthorized direct API calls are denied independently of hidden frontend controls or MCP tool visibility.
- Export results obey disclosure policy, including cross-tenant and unauthorized-subject denial.
- Denied mutations do not change audit state. Successful administrative operations preserve the required integrity and action evidence.
- Test routes are absent from the normal surface or demonstrably constrained by the selected development/test policy.

This repository document is the separate tracked issue; no remote issue has been created. Implementation, migration, and API-build authorization remain separate from this documentation update.
