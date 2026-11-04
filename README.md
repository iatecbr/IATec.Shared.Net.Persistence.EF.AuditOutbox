# IATec.Shared.Persistence.EF.AuditOutbox

**Optional** audit capability based on the **Outbox** pattern for the `IATec.Shared.Persistence.EF`
library. When the client project enables it via `AddAuditOutboxLog()`, every successfully persisted
entity (insert, update or delete) produces an audit record in a local intermediate table
(`AuditLogEntry`), written within the **same transaction** as the business operation to guarantee
atomicity. A background worker delivers those records resiliently (retry + circuit breaker) to a
remote audit service, deleting them once delivery is confirmed and marking them as failed when
appropriate.

The functionality lives in a separate package so the core `IATec.Shared.Persistence.EF` does NOT
pull in HTTP or worker dependencies. Only clients that reference this package and call
`AddAuditOutboxLog()` get the auditing behavior.

---

## Table of Contents

- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Installation](#installation)
- [Configuration](#configuration)
- [DI registration](#di-registration)
- [User resolution (ICurrentUser)](#user-resolution-icurrentuser)
- [The auditable entity (IEntity)](#the-auditable-entity-ientity)
- [Payload format sent](#payload-format-sent)
- [Contract with the remote service](#contract-with-the-remote-service)
- [The AuditLogEntry table](#the-auditlogentry-table)
- [Record lifecycle](#record-lifecycle)
- [Transactions and atomicity](#transactions-and-atomicity)
- [Idempotency (DedupKey)](#idempotency-dedupkey)
- [Complete example](#complete-example)
- [Considerations and limits](#considerations-and-limits)
- [Troubleshooting](#troubleshooting)

---

## How it works

1. **Registration (DI):** `AddAuditOutboxLog<TContext>()` registers the configuration, the
   `SaveChangesInterceptor`, a fallback `ICurrentUser` (if the client does not provide one), the
   `IServiceClient` from `IATec.Shared.HttpClient`, the worker (`BackgroundService`) and an
   initializer that creates the `AuditLogEntry` table if missing.
2. **Modeling:** `UseAuditOutbox(sp)` includes `AuditLogEntry` in the client's `DbContext` model
   and attaches the interceptor, without the client having to edit its `OnModelCreating`.
3. **Atomic capture:** on `SaveChanges`, the interceptor detects the affected `IEntity` entities
   (Added/Modified/Deleted) and inserts one `AuditLogEntry` per entity within the **same unit of
   work**. The `owner` is resolved after database-generated keys have been assigned.
4. **Draining:** the worker reads batches of pending `AuditLogEntry` rows, marks them `Processing`,
   does `POST {LogServiceUrl}/v1/log` with the `PayloadJson` through `IServiceClient`. Success →
   deletes the record; failure → `Failed` + `AdditionalInfo` + `RetryCount++`.

```
┌───────────────┐   SaveChanges    ┌────────────────────────────┐
│  Repository   │ ───────────────▶ │ AuditOutboxSaveChangesInter │
│  (business    │                  │ ceptor: inserts AuditLogEntry│
│  operation)   │                  │ in the SAME transaction      │
└───────────────┘                  └──────────────┬──────────────┘
                                                   │ commit
                                                   ▼
                                       ┌────────────────────────┐
                                       │ AuditLogEntry table     │
                                       └────────────┬───────────┘
                                                    │ poll (BatchSize)
                                    ┌───────────────▼───────────────┐
                                    │ AuditOutboxWorker (BackgroundService)
                                    │  POST /v1/log via IServiceClient│
                                    └───────────────┬───────────────┘
                                                    │
                          success: DELETE  ◀────────┴────────▶  failure: Status=Failed
```

---

## Requirements

- **.NET 10** (`net10.0`).
- A **relational provider** for EF Core (SQL Server, PostgreSQL or MySQL). Automatic table creation
  and the outbox pattern require a relational provider.
- The business entity must implement the `IEntity` contract from `IATec.Shared.Domain`
  (via `Entity<T>` / `EntityInt32`, etc.) and expose a non-null *source type* to be audited.
- A remote audit service that accepts `POST {LogServiceUrl}/v1/log`.

Dependencies brought in by the package (the core does NOT inherit them):

| Package | Version |
|---------|---------|
| `IATec.Shared.HttpClient` | `3.0.0` |
| `Microsoft.EntityFrameworkCore.Relational` | `10.0.8` |
| `Microsoft.Extensions.Hosting.Abstractions` | `10.0.8` |
| `IATec.Shared.Persistence.EF` (ProjectReference / PackageReference) | core |

---

## Installation

```xml
<PackageReference Include="IATec.Shared.Persistence.EF.AuditOutbox" Version="1.0.0" />
```

> The final version number is to be confirmed with the platform team.

---

## Configuration

Options are bound from the **`AuditOutboxLog`** section of `appsettings.json`.

```json
{
  "AuditOutboxLog": {
    "ContainerKey": "my-container",
    "LogServiceUrl": "https://audit.my-domain.com",
    "PollInterval": "00:00:10",
    "BatchSize": 100,
    "RetryLimit": 3,
    "ProcessingTimeout": "00:05:00",
    "RequestTimeout": "00:00:10",
    "CircuitBreakerFailuresAllowedBeforeBreaking": 3,
    "CircuitBreakerDuration": "00:00:30"
  }
}
```

| Property | Type | Default | Required | Description |
|----------|------|---------|----------|-------------|
| `ContainerKey` | `string` | — | **Yes** | Container key included in every payload. |
| `LogServiceUrl` | `string` | — | **Yes** | Base (absolute) URL of the remote audit service. |
| `PollInterval` | `TimeSpan` | `00:00:10` | No | Interval between worker cycles. |
| `BatchSize` | `int` | `100` | No | Maximum number of records processed per cycle. |
| `RetryLimit` | `int` | `3` | No | Maximum number of send retries for a failed record. |
| `ProcessingTimeout` | `TimeSpan` | `00:05:00` | No | Maximum time a record may stay `Processing` before it is considered abandoned (e.g. after a restart) and rescued by a later cycle. |
| `RequestTimeout` | `TimeSpan` | `00:00:10` | No | Timeout per HTTP request to the remote service. |
| `CircuitBreakerFailuresAllowedBeforeBreaking` | `int` | `3` | No | Consecutive failures before the circuit opens. |
| `CircuitBreakerDuration` | `TimeSpan` | `00:00:30` | No | How long the circuit stays open. |

**Validation:** `ContainerKey` and `LogServiceUrl` are required and `LogServiceUrl` must be an
absolute URL. Validation runs at **startup** (`ValidateOnStart`): if a value is missing, the
application fails to start with a clear message naming the property.

---

## DI registration

Registration has **two steps**: register the outbox services and **attach** it to the client's
`DbContext`.

```csharp
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;

// 1) Register the outbox services (configuration + interceptor + worker + HTTP client + ...)
builder.Services.AddAuditOutboxLog<ApplicationDbContext>(builder.Configuration);

// 2) Attach the outbox to the DbContext: use the IServiceProvider overload
builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
{
    options.UseSqlServer(connectionString);   // or UseNpgsql / UseMySql
    options.UseAuditOutbox(sp);               // attaches the interceptor + includes AuditLogEntry in the model
});
```

### What does `AddAuditOutboxLog<TContext>()` do?

- Binds and validates `AuditOutboxOptions` (section `AuditOutboxLog`) with `ValidateOnStart`.
- Registers a fallback `ICurrentUser` **only if the client has not registered its own**
  (`TryAddScoped`).
- Registers the `AuditOutboxSaveChangesInterceptor` (scoped).
- Registers the named `"AuditOutbox"` `IServiceClient` with retry/circuit breaker/timeout policies
  mapped from the options and `BaseAddress = LogServiceUrl`.
- Registers the `AuditLogSender` (scoped).
- Registers the `AuditOutboxWorker<TContext>` as a `HostedService`.
- Registers the table initializer (`AuditLogTableInitializer`) and its startup hosted service.

### What does `UseAuditOutbox(sp)` do?

- Attaches the `AuditOutboxSaveChangesInterceptor` resolved from the scope.
- Includes `AuditLogEntry` in the `DbContext` model (equivalent to `AddAuditOutboxModel()`), without
  you having to touch your `OnModelCreating`.

> **Important:** use the `AddDbContext` overload that receives `(IServiceProvider sp, options)`,
> because the interceptor is *scoped* and is resolved from the service provider.

---

## User resolution (ICurrentUser)

The `userId` field of every payload comes from `ICurrentUser.UserId`.

```csharp
namespace IATec.Shared.Persistence.EF.AuditOutbox.Abstractions;

public interface ICurrentUser
{
    string UserId { get; }
}
```

- If you do **not** register an implementation, the outbox uses `SystemCurrentUser`, which returns
  `"system"` by default. It does not pull in an ASP.NET dependency.
- To audit the authenticated user, register your own implementation **before or after**
  `AddAuditOutboxLog` (it takes precedence over the fallback thanks to `TryAddScoped`):

```csharp
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserId =>
        accessor.HttpContext?.User?.FindFirst("sub")?.Value ?? "anonymous";
}

// Registration (takes precedence over the fallback)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddAuditOutboxLog<ApplicationDbContext>(builder.Configuration);
```

---

## The auditable entity (IEntity)

The interceptor only audits entities that implement `IEntity` (from `IATec.Shared.Domain`) and whose
*source type* (`GetSourceType()`) is **not null**. For each captured entity it uses:

- `GetSourceType()` → payload `source` (if `null`, the entity is skipped).
- `GetOwner()` → payload `owner` (resolved **after** persistence, to reflect database-generated
  keys).
- `GetLogContent()` → payload `content`, serialized to JSON and embedded as a **string**. For `DELETED`, the content is read BEFORE persistence, so it captures the state of the deleted element. Only `""` is emitted when `GetLogContent()` returns no data.

Actions are derived from the change tracker state:

| EF State | Action |
|----------|--------|
| `Added` | `ADDED` |
| `Modified` | `MODIFIED` |
| `Deleted` | `DELETED` |
| `Unchanged` with a modified *owned type* | `MODIFIED` |

---

## Payload format sent

The `PayloadJson` is a **stable** JSON (fixed property order, no indentation) so the deduplication
hash is reproducible. **All six fields are strings** (required by the audit service): `content` is
the serialized object embedded as a **string** (the inner JSON is escaped within quotes), not a
nested JSON object; null `source`, `owner` and `content` are emitted as an empty string `""`:

```json
{"containerKey":"my-container","source":"Customer","owner":"42","action":"MODIFIED","userId":"user-7","content":"{\"Id\":42,\"Name\":\"Alice\"}"}
```

| Field | Source |
|-------|--------|
| `containerKey` | `AuditOutboxOptions.ContainerKey` |
| `source` | `entity.GetSourceType()`; `""` if null |
| `owner` | `entity.GetOwner()` resolved after persistence; `""` if null |
| `action` | `ADDED` / `MODIFIED` / `DELETED` |
| `userId` | `ICurrentUser.UserId` |
| `content` | `entity.GetLogContent()` serialized to JSON and embedded as a **string** (escaped JSON). On `DELETED` it captures the deleted element's state (read before persistence); `""` only when there is no content |

All payload fields are **strings** (a requirement of the audit service). The `DedupKey` is
`SHA256(UTF8(PayloadJson))` in uppercase hexadecimal.

---

## Contract with the remote service

- **Method/Route:** `POST {LogServiceUrl}/v1/log`
- **Content-Type:** `application/json`
- **Body:** the `PayloadJson` as-is (see previous section). The six fields (`containerKey`, `source`, `owner`, `action`, `userId`, `content`) are required strings; `content` is the serialized object as a string. The worker sends the `PayloadJson` as a **raw body** (`StringContent`); it is NOT re-serialized (see note below).
- **Success:** response with `Success == true` → the record is deleted from the table.
- **Failure:** unsuccessful response, timeout or open circuit → the record stays `Failed`,
  `RetryCount` is incremented and the reason is stored in `AdditionalInfo`.

Retry and circuit breaker are applied by `IATec.Shared.HttpClient` (Polly). When the circuit is
open, the `POST` fails fast and the record stays retriable on the next cycle once the circuit
closes.

> **Important (avoid `400 Bad Request`).** The `PayloadJson` is already serialized JSON, so it is
> sent as a raw body with `new StringContent(payloadJson, Encoding.UTF8, "application/json")`.
> Do NOT use helpers that serialize again (e.g. `GenerateStringContent` or `PostAsJsonAsync` on the
> `PayloadJson`): they would serialize the string again and send an escaped JSON (a string) instead
> of the object with its six fields, and the service responds with `400`.

---

## The AuditLogEntry table

It is created automatically at startup (silent and idempotent, via `IRelationalDatabaseCreator`,
without raw SQL). Logical schema:

| Column | Type | Notes |
|--------|------|-------|
| `Id` | `long` | Auto-increment PK |
| `PayloadJson` | `string` | JSON body to send |
| `CreatedAt` | `DateTime` | UTC creation time |
| `Status` | `int` | `Pending=0`, `Processing=1`, `Failed=2` |
| `AdditionalInfo` | `string?` | Failure reason (max 2000) |
| `DedupKey` | `string` | Payload hash, **unique index** (max 128) |
| `RetryCount` | `int` | Number of failed attempts |
| `ProcessingSince` | `DateTime?` | UTC of the claim; allows rescuing abandoned `Processing` records |

---

## Record lifecycle

```
Pending ──(worker claims)──▶ Processing ──┬─ success ──▶ (DELETE, gone)
                                          │
                                          └─ failure ──▶ Failed (RetryCount++)
                                                          │
                              RetryCount < RetryLimit ◀───┤
                                 (retriable)              │
                              RetryCount >= RetryLimit ───┘
                                 (not selected again)
```

On each cycle the worker selects: `Status == Pending`; **or** (`Status == Failed` **and**
`RetryCount < RetryLimit`); **or** (`Status == Processing` **and** its `ProcessingSince` is older
than `ProcessingTimeout`, i.e. a claim abandoned after a restart). It orders by `Id`, takes
`BatchSize`, marks the batch `Processing` stamping `ProcessingSince` (anti double-pick claim across
replicas), sends each one and saves.

---

## Transactions and atomicity

The `AuditLogEntry` rows are inserted within the **same unit of work** as the business operation:

- **With an ambient transaction** (e.g. opened by `GenericTransaction.BeginTransaction()`): the
  insert enlists in it. A `commit` persists business + audit; a `rollback` discards both.
- **Without an ambient transaction:** the interceptor opens a **local** transaction so the business
  save and the log insert are all-or-nothing.

```csharp
public class ProductTransaction(ApplicationDbContext context)
    : GenericTransaction(context), IProductTransaction;

public async Task CreateAsync(Product product)
{
    try
    {
        transaction.BeginTransaction();
        await repository.AddAsync(product);
        await repository.SaveChangesAsync();   // inserts the AuditLogEntry in the same transaction
        transaction.CommitTransaction();       // persists business + audit
    }
    catch
    {
        transaction.RollbackTransaction();     // discards business + audit
        throw;
    }
}
```

---

## Idempotency (DedupKey)

Two operations with an identical payload (same `containerKey/source/owner/action/userId/content`)
produce the same `DedupKey` and are considered the **same event**. Deduplication works at two
levels:

1. **Pre-check** before inserting: `DedupKey`s that already exist (and duplicates within the same
   batch) are discarded.
2. **Unique index** as the final safeguard: if a uniqueness violation still occurs, it is treated as
   a **silent discard** without aborting the business operation.

---

## Complete example

```csharp
// Program.cs
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;

var builder = WebApplication.CreateBuilder(args);

// (optional) authenticated user for the userId field
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

// 1) Outbox services
builder.Services.AddAuditOutboxLog<ApplicationDbContext>(builder.Configuration);

// 2) DbContext with the outbox attached
builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default"));
    options.UseAuditOutbox(sp);
});

// Core repositories/UoW (without ILogDispatcher)
builder.Services.AddScoped<IProductRepository, ProductRepository>();

var app = builder.Build();
app.Run();
```

```csharp
// Repository (core): no longer receives ILogDispatcher
public class ProductRepository(ApplicationDbContext context)
    : GenericWriteRepository<Product>(context), IProductRepository;
```

With this, every `SaveChangesAsync` that affects auditable entities will automatically produce its
`AuditLogEntry` rows, and the worker will deliver them to the remote service in the background.

---

## Considerations and limits

- **Relational providers only.** With a non-relational provider, table creation and the outbox do
  not apply (a warning is logged and it is skipped).
- **Multiple replicas and restarts.** The `Processing` state + persisting the claim prevent double
  delivery across active replicas. If a replica dies (or the host restarts) while a record is
  `Processing`, that record is NOT stuck: `ProcessingSince` is stamped when claimed and, once it
  exceeds `ProcessingTimeout`, a later cycle rescues and retries it. Tune `ProcessingTimeout` so it
  is comfortably larger than how long a delivery takes, so a record another replica is legitimately
  sending is not "stolen".
- **Deleted content.** For `DELETED`, the `content` captures the state of the deleted element: it is
  read with `GetLogContent()` in `SavingChanges` (before the DELETE), while the entity is still
  complete in the change tracker. This way the service receives a non-empty `content`.
- **The core is audit-agnostic.** Auditing lives entirely in this package; the core
  (`GenericWriteRepository` / `GenericTransaction`) no longer captures or dispatches logs.

---

## Troubleshooting

| Symptom | Likely cause | Solution |
|---------|--------------|----------|
| The app fails to start with a message about `ContainerKey`/`LogServiceUrl` | A required value is missing or the URL is not absolute | Complete the `AuditOutboxLog` section of `appsettings.json`. |
| No `AuditLogEntry` records are generated | `UseAuditOutbox(sp)` was not called in `AddDbContext`, or the entity does not implement `IEntity` / `GetSourceType()` is `null` | Attach the outbox to the `DbContext` and verify the `IEntity` contract. |
| Records stay in `Failed` | The remote service responds with an error, there is a timeout or the circuit is open | Check `AdditionalInfo`; validate `LogServiceUrl` and the availability of the service. |
| The remote service responds with `400 Bad Request` | The body was re-serialized (escaped JSON as a string) instead of sent raw | Send the `PayloadJson` with `StringContent`, not with `GenerateStringContent`/`PostAsJsonAsync`. Verify that `content` arrives as a string and that no field is `null`. |
| `userId` is always `"system"` | You did not register an `ICurrentUser` implementation | Register your `ICurrentUser` before/after `AddAuditOutboxLog`. |
| `Failed` records that are not retried | `RetryCount >= RetryLimit` | By design; tune `RetryLimit` if you need more retries. |
| Records stuck in `Processing` after a restart | The worker died between the claim and the delivery | They rescue themselves after `ProcessingTimeout`; lower that value if you need a faster rescue. |
