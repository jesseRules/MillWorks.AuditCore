# Host identity investigation — 2026-10-16

Status: read-only diagnosis; no identity fix or host acceptance claimed.

The user reported `audit_identity_unverified` on startup during Identity permission seeding,
after the host authorization routing correction. Authorization now reaches the entity writer.

Read-only observations in the configured local MillWorks database found 32,044 historical
`IdentityRoleClaim<Guid>` creation audit rows without comparison receipts and zero entity
receipts whose source was missing. Recent role concurrency-stamp updates have receipts;
no recent role-claim creation audit rows were observed. This does not identify the exact
failed envelope: the supplied exception log omits `AuditIdentityConflictException.LogicalIds`.

The retained `verify-claim-identity.py` reads at most 100 historical role-claim snapshots
matching current role/claim tuples. It does not print or retain their payloads. All 100
sampled snapshots contain temporary negative integer keys, and their stored EnvelopeIds
exactly match recomputation using the current interceptor's content-derived ID algorithm.
See `claim-identity-collision.json`. The script reads configuration credentials into the
child environment, never command arguments or output, and issues SELECT statements only.

The interceptor currently derives entity-change identity from entity name, primary key,
action and captured content. EF temporary store-generated keys are not durable operation
identities. Recreating the same content with a reused temporary key can address old audit
history, while a new creation should have its own operation identity. The ledger deliberately
classifies pre-receipt history as unverified; its refusal must not be weakened into a replay.
This mechanism is confirmed; attribution of the specific runtime failure remains inferred.

## Required fix outcomes

- Identify the exact failed envelope IDs and entity types using payload-safe diagnostics.
- Assign entity-change identities to actual mutation operations, retained across retries of
  that operation and distinct for later operations with the same values or reused temporary
  keys. Include added integer/composite-key entities and successful-save lifecycle handling.
- Prove real interceptor behavior for recreated role claims, same-operation retries, separate
  operations returning to the same values, rollback and ambiguous commit/reconstruction.
- Preserve historical IDs and receipts. Do not invent historical fingerprints from redacted
  snapshots, reset the application database, or loosen unverified/conflict classification.
- Keep permission/role changes audited. `[NoAudit]` is appropriate only after reviewing the
  particular entity/property's purpose; these diagnostics do not justify excluding claims.
- Publish any library fix as a fresh version, then batch the consumer rebuild and verify
  populated startup behavior before recording acceptance.

Reproduction command from the AuditCore repository:

```sh
python3 docs/evidence/audit-pipeline-2026-10/host-identity-investigation-2026-10-16/verify-claim-identity.py
```

No API build, service restart, migration or data modification was performed for this diagnosis.
