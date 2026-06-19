# Disconnected Entity Updates

**Status:** Deferred
**Origin:** EfInterceptorCoverageGaps.md finding #2
**Blocked By:** Test contract compatibility
**Priority:** High

## Problem

For entities attached via `DbSet.Update()` (the standard disconnected web pattern), EF Core seeds `OriginalValues` from `CurrentValues`. Every property comparison in the interceptor returns "unchanged," resulting in `changes.Count == 0`, and `BuildEnvelope` returns null.

The update persists to the database with **no audit trail and no warning**.

### Affected Patterns

```csharp
// Pattern 1: Web API update endpoint
[HttpPut("{id}")]
public async Task<IActionResult> Update(Guid id, CustomerDto dto)
{
    var entity = mapper.Map<Customer>(dto);
    entity.Id = id;
    context.Customers.Update(entity);  // OriginalValues = CurrentValues
    await context.SaveChangesAsync();  // No audit record created
    return Ok();
}

// Pattern 2: Soft delete on untracked entity
public async Task DeleteAsync(Guid id)
{
    var entity = new Customer { Id = id, IsDeleted = true };
    context.Customers.Update(entity);
    await context.SaveChangesAsync();  // No audit record
}
```

### Current Behavior

```
Entity attached via Update():
  OriginalValues.Name = "New Name"  (seeded from CurrentValues)
  CurrentValues.Name = "New Name"
  
Interceptor diff:
  for each property:
    if (original != current) → add to changes
  
Result: changes.Count == 0 → BuildEnvelope returns null → no audit
```

## Attempted Fix

The initial fix attempted to detect this case and fall back to fetching the original values from the database:

```csharp
if (entry.State == EntityState.Modified && changes.Count == 0)
{
    // Fetch original from DB
    var original = await context.Set<T>()
        .AsNoTracking()
        .FirstOrDefaultAsync(e => e.Id == entry.Entity.Id);
    
    if (original != null)
    {
        // Diff against DB values instead
        changes = DiffAgainst(entry.Entity, original);
    }
}
```

### Why It Failed

This broke the existing test contract. Tests that:
1. Create an entity
2. Modify it in-memory
3. Call `SaveChangesAsync`
4. Assert specific audit behavior

...started failing because the "original" fetched from DB was the pre-test state, not the in-memory state the test expected.

The fix also has production implications:
- Extra database round-trip per disconnected update
- Race condition: original could change between fetch and save
- Doesn't work for new entities being "updated" into existence

## Current Mitigation

The interceptor now:
1. Logs a warning when a `Modified` entry produces zero changes
2. Increments `IAuditDiagnostics` counter `DisconnectedUpdatesDropped`

This makes the gap observable but doesn't close it.

## Solution Options

### Option 1: Snapshot Envelope (Like Added/Deleted)

For disconnected updates, emit a snapshot-style envelope containing all current values, without a diff:

```csharp
if (entry.State == EntityState.Modified && changes.Count == 0)
{
    return new AuditEnvelope
    {
        Action = AuditAction.Modified,
        ChangeType = ChangeType.Snapshot,  // New enum value
        NewValues = SerializeCurrentValues(entry),
        OldValues = null,  // Unknown
        // ...
    };
}
```

**Pros:**
- No DB round-trip
- Something is captured
- Simple implementation

**Cons:**
- Can't show what changed, only final state
- Larger payload (all properties, not just changed)
- Different audit format than tracked updates

### Option 2: Require Tracked Pattern

Fail loud when disconnected updates are detected:

```csharp
if (entry.State == EntityState.Modified && changes.Count == 0)
{
    throw new AuditException(
        $"Disconnected update detected for {entry.Entity.GetType().Name}. " +
        "Use tracked updates or explicit audit submission.");
}
```

**Pros:**
- Forces correct usage
- No ambiguity

**Cons:**
- Breaking change
- Some legitimate patterns become errors

### Option 3: Explicit Audit Submission

Provide an API for callers who know they're doing disconnected updates:

```csharp
// Caller provides the diff explicitly
await auditService.RecordUpdateAsync(
    entity: customer,
    originalValues: new { Name = "Old Name", Email = "old@example.com" },
    newValues: new { Name = "New Name", Email = "new@example.com" });
```

**Pros:**
- Caller has the context
- Accurate diffs
- No magic

**Cons:**
- Opt-in, easy to forget
- Boilerplate at call sites

### Option 4: Shadow Property Tracking

Use EF shadow properties to store original values explicitly:

```csharp
// Before Update()
var original = await context.Customers.FindAsync(id);
context.Entry(original).State = EntityState.Detached;

// Store original values in shadow properties
var updated = mapper.Map<Customer>(dto);
context.Customers.Update(updated);
context.Entry(updated).Property("_OriginalJson").CurrentValue = 
    JsonSerializer.Serialize(original);
```

**Pros:**
- Works with existing interceptor pattern
- Explicit opt-in

**Cons:**
- Requires schema/model changes
- Still boilerplate

## Recommended Approach

Implement **Option 1 (Snapshot Envelope)** with clear documentation:

1. When `Modified` entry has zero detected changes, emit a `ChangeType.Snapshot` envelope
2. Document that this indicates a disconnected update pattern
3. Add `IAuditDiagnostics.DisconnectedUpdateSnapshots` counter
4. Log at Info level (not warning) since behavior is now defined
5. Update tests to expect snapshot envelopes for disconnected patterns

This closes the "silent drop" gap while maintaining backward compatibility. Callers who need accurate diffs can switch to tracked patterns.

## Implementation Outline

1. Add `ChangeType.Snapshot` to the enum
2. Modify `BuildEnvelope` to return snapshot envelope when `changes.Count == 0` for `Modified`
3. Update `AuditEventRepository` / providers to handle snapshot type
4. Add tests for disconnected update → snapshot envelope
5. Update documentation

## Test Contract Fix

The test failures from the original fix attempt need resolution:

```csharp
// Old test pattern (assumes tracked entity)
var entity = new Customer { Name = "Test" };
context.Add(entity);
await context.SaveChangesAsync();

entity.Name = "Updated";
await context.SaveChangesAsync();  // This IS tracked, should work

// Disconnected test pattern (new)
var dto = new CustomerDto { Id = existingId, Name = "Updated" };
var entity = mapper.Map<Customer>(dto);
context.Update(entity);
await context.SaveChangesAsync();  // This is disconnected, snapshot envelope
```

Ensure tests distinguish between these patterns.

## Related Documents

- `EfInterceptorCoverageGaps.md` — Origin finding
- `AuditWritePipelineDurability.md` — Related atomicity concerns
