# IATec.Shared.Persistence.EF.AuditOutbox

Capacidad **opcional** de auditoría basada en el patrón **Outbox** para la librería
`IATec.Shared.Persistence.EF`. Cuando el proyecto cliente la habilita vía `AddAuditOutboxLog()`,
cada entidad persistida con éxito (insert, update o delete) genera un registro de auditoría en una
tabla intermedia local (`AuditLogEntry`), escrito dentro de la **misma transacción** que la
operación de negocio para garantizar atomicidad. Un worker en segundo plano envía esos registros
de forma resiliente (retry + circuit breaker) a un servicio de auditoría remoto, eliminándolos al
confirmarse el envío y marcándolos como fallidos cuando corresponde.

La funcionalidad vive en un paquete separado para que el core `IATec.Shared.Persistence.EF` NO
arrastre dependencias de HTTP ni del worker. Solo los clientes que referencien este paquete y
llamen a `AddAuditOutboxLog()` obtienen el comportamiento de auditoría.

---

## Índice

- [Cómo funciona](#cómo-funciona)
- [Requisitos](#requisitos)
- [Instalación](#instalación)
- [Configuración](#configuración)
- [Registro en DI](#registro-en-di)
- [Resolución del usuario (ICurrentUser)](#resolución-del-usuario-icurrentuser)
- [La entidad auditable (IEntity)](#la-entidad-auditable-ientity)
- [Formato del payload enviado](#formato-del-payload-enviado)
- [Contrato con el servicio remoto](#contrato-con-el-servicio-remoto)
- [La tabla AuditLogEntry](#la-tabla-auditlogentry)
- [Ciclo de vida de un registro](#ciclo-de-vida-de-un-registro)
- [Transacciones y atomicidad](#transacciones-y-atomicidad)
- [Idempotencia (DedupKey)](#idempotencia-dedupkey)
- [Ejemplo completo](#ejemplo-completo)
- [Consideraciones y límites](#consideraciones-y-límites)
- [Solución de problemas](#solución-de-problemas)

---

## Cómo funciona

1. **Registro (DI):** `AddAuditOutboxLog<TContext>()` registra la configuración, el
   `SaveChangesInterceptor`, un `ICurrentUser` de respaldo (si el cliente no provee uno), el
   `IServiceClient` de `IATec.Shared.HttpClient`, el worker (`BackgroundService`) y un inicializador
   que crea la tabla `AuditLogEntry` si falta.
2. **Modelado:** `UseAuditOutbox(sp)` incluye `AuditLogEntry` en el modelo del `DbContext` del
   cliente y adjunta el interceptor, sin que el cliente tenga que editar su `OnModelCreating`.
3. **Captura atómica:** al hacer `SaveChanges`, el interceptor detecta las entidades `IEntity`
   afectadas (Added/Modified/Deleted) e inserta un `AuditLogEntry` por cada una en la **misma
   unidad de trabajo**. El `owner` se resuelve después de asignarse las claves generadas.
4. **Drenaje:** el worker lee lotes de `AuditLogEntry` pendientes, los marca `Processing`, hace
   `POST {LogServiceUrl}/v1/log` con el `PayloadJson` a través de `IServiceClient`. Éxito → elimina
   el registro; fallo → `Failed` + `AdditionalInfo` + `RetryCount++`.

```
┌───────────────┐   SaveChanges    ┌────────────────────────────┐
│  Repositorio  │ ───────────────▶ │ AuditOutboxSaveChangesInter │
│ (operación de │                  │ ceptor: inserta AuditLogEntry│
│   negocio)    │                  │ en la MISMA transacción      │
└───────────────┘                  └──────────────┬──────────────┘
                                                   │ commit
                                                   ▼
                                       ┌────────────────────────┐
                                       │ Tabla AuditLogEntry     │
                                       └────────────┬───────────┘
                                                    │ poll (BatchSize)
                                    ┌───────────────▼───────────────┐
                                    │ AuditOutboxWorker (BackgroundService)
                                    │  POST /v1/log vía IServiceClient│
                                    └───────────────┬───────────────┘
                                                    │
                            éxito: DELETE  ◀────────┴────────▶  fallo: Status=Failed
```

---

## Requisitos

- **.NET 10** (`net10.0`).
- Un **provider relacional** de EF Core (SQL Server, PostgreSQL o MySQL). La creación automática de
  la tabla y el patrón outbox requieren un provider relacional.
- La entidad de negocio debe implementar el contrato `IEntity` de `IATec.Shared.Domain`
  (vía `Entity<T>` / `EntityInt32`, etc.) y exponer un *source type* no nulo para ser auditada.
- Un servicio de auditoría remoto que acepte `POST {LogServiceUrl}/v1/log`.

Dependencias que trae el paquete (el core NO las hereda):

| Paquete | Versión |
|---------|---------|
| `IATec.Shared.HttpClient` | `3.0.0` |
| `Microsoft.EntityFrameworkCore.Relational` | `10.0.8` |
| `Microsoft.Extensions.Hosting.Abstractions` | `10.0.8` |
| `IATec.Shared.Persistence.EF` (ProjectReference / PackageReference) | core |

---

## Instalación

```xml
<PackageReference Include="IATec.Shared.Persistence.EF.AuditOutbox" Version="1.0.0" />
```

> El número de versión final se confirma con el equipo de plataforma.

---

## Configuración

Las opciones se enlazan desde la sección **`AuditOutboxLog`** del `appsettings.json`.

```json
{
  "AuditOutboxLog": {
    "ContainerKey": "mi-contenedor",
    "LogServiceUrl": "https://audit.mi-dominio.com",
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

| Propiedad | Tipo | Default | Requerido | Descripción |
|-----------|------|---------|-----------|-------------|
| `ContainerKey` | `string` | — | **Sí** | Clave de contenedor incluida en cada payload. |
| `LogServiceUrl` | `string` | — | **Sí** | URL base (absoluta) del servicio de auditoría remoto. |
| `PollInterval` | `TimeSpan` | `00:00:10` | No | Intervalo entre ciclos del worker. |
| `BatchSize` | `int` | `100` | No | Máximo de registros procesados por ciclo. |
| `RetryLimit` | `int` | `3` | No | Máximo de reintentos de envío para un registro fallido. |
| `ProcessingTimeout` | `TimeSpan` | `00:05:00` | No | Tiempo máximo que un registro puede quedar en `Processing` antes de considerarse abandonado (p.ej. por reinicio) y ser rescatado por un ciclo posterior. |
| `RequestTimeout` | `TimeSpan` | `00:00:10` | No | Timeout por request HTTP al servicio remoto. |
| `CircuitBreakerFailuresAllowedBeforeBreaking` | `int` | `3` | No | Fallos consecutivos antes de abrir el circuito. |
| `CircuitBreakerDuration` | `TimeSpan` | `00:00:30` | No | Tiempo que el circuito permanece abierto. |

**Validación:** `ContainerKey` y `LogServiceUrl` son obligatorios y `LogServiceUrl` debe ser una URL
absoluta. La validación se ejecuta al **arranque** (`ValidateOnStart`): si falta un valor, la
aplicación falla al iniciar con un mensaje claro que nombra la propiedad.

---

## Registro en DI

El registro tiene **dos pasos**: registrar los servicios del outbox y **adjuntarlo** al `DbContext`
del cliente.

```csharp
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;

// 1) Registrar los servicios del outbox (configuración + interceptor + worker + cliente HTTP + ...)
builder.Services.AddAuditOutboxLog<ApplicationDbContext>(builder.Configuration);

// 2) Adjuntar el outbox al DbContext: usa la sobrecarga con IServiceProvider
builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
{
    options.UseSqlServer(connectionString);   // o UseNpgsql / UseMySql
    options.UseAuditOutbox(sp);               // adjunta el interceptor + incluye AuditLogEntry en el modelo
});
```

### ¿Qué hace `AddAuditOutboxLog<TContext>()`?

- Enlaza y valida `AuditOutboxOptions` (sección `AuditOutboxLog`) con `ValidateOnStart`.
- Registra un `ICurrentUser` de respaldo **solo si el cliente no registró el suyo**
  (`TryAddScoped`).
- Registra el `AuditOutboxSaveChangesInterceptor` (scoped).
- Registra el `IServiceClient` nombrado `"AuditOutbox"` con las políticas de retry/circuit
  breaker/timeout mapeadas desde las opciones y `BaseAddress = LogServiceUrl`.
- Registra el `AuditLogSender` (scoped).
- Registra el `AuditOutboxWorker<TContext>` como `HostedService`.
- Registra el inicializador de tabla (`AuditLogTableInitializer`) y su hosted service de arranque.

### ¿Qué hace `UseAuditOutbox(sp)`?

- Adjunta el `AuditOutboxSaveChangesInterceptor` resuelto del scope.
- Incluye `AuditLogEntry` en el modelo del `DbContext` (equivale a `AddAuditOutboxModel()`), sin que
  tengas que tocar tu `OnModelCreating`.

> **Importante:** usa la sobrecarga de `AddDbContext` que recibe `(IServiceProvider sp, options)`,
> porque el interceptor es *scoped* y se resuelve del proveedor de servicios.

---

## Resolución del usuario (ICurrentUser)

El campo `userId` de cada payload proviene de `ICurrentUser.UserId`.

```csharp
namespace IATec.Shared.Persistence.EF.AuditOutbox.Abstractions;

public interface ICurrentUser
{
    string UserId { get; }
}
```

- Si **no** registras una implementación, el outbox usa `SystemCurrentUser`, que devuelve
  `"system"` por defecto. No arrastra dependencia de ASP.NET.
- Para auditar el usuario autenticado, registra tu propia implementación **antes o después** de
  `AddAuditOutboxLog` (tiene precedencia sobre el respaldo, gracias a `TryAddScoped`):

```csharp
public sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public string UserId =>
        accessor.HttpContext?.User?.FindFirst("sub")?.Value ?? "anonymous";
}

// Registro (precede al fallback)
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
builder.Services.AddAuditOutboxLog<ApplicationDbContext>(builder.Configuration);
```

---

## La entidad auditable (IEntity)

El interceptor solo audita entidades que implementan `IEntity` (de `IATec.Shared.Domain`) y cuyo
*source type* (`GetSourceType()`) **no es nulo**. Por cada entidad capturada se usa:

- `GetSourceType()` → `source` del payload (si es `null`, la entidad se ignora).
- `GetOwner()` → `owner` del payload (se resuelve **después** de persistir, para reflejar claves
  generadas por la base de datos).
- `GetLogContent()` → `content` del payload, serializado a JSON y embebido como **string**. Para `DELETED`, el contenido se lee ANTES de persistir, así que captura el estado del elemento eliminado. Solo se emite `""` cuando `GetLogContent()` no devuelve datos.

Las acciones se derivan del estado del change tracker:

| Estado EF | Acción |
|-----------|--------|
| `Added` | `ADDED` |
| `Modified` | `MODIFIED` |
| `Deleted` | `DELETED` |
| `Unchanged` con *owned type* modificado | `MODIFIED` |

---

## Formato del payload enviado

El `PayloadJson` es un JSON **estable** (orden de propiedades fijo, sin indentación) para que el
hash de deduplicación sea reproducible. **Los seis campos son strings** (requerido por el servicio
de auditoría): `content` es el objeto serializado embebido como un **string** (el JSON interno va
escapado dentro de comillas), no un objeto JSON anidado; `source`, `owner` y `content` nulos se
emiten como string vacío `""`:

```json
{"containerKey":"mi-contenedor","source":"Customer","owner":"42","action":"MODIFIED","userId":"user-7","content":"{\"Id\":42,\"Name\":\"Alice\"}"}
```

| Campo | Origen |
|-------|--------|
| `containerKey` | `AuditOutboxOptions.ContainerKey` |
| `source` | `entity.GetSourceType()`; `""` si es nulo |
| `owner` | `entity.GetOwner()` resuelto tras persistir; `""` si es nulo |
| `action` | `ADDED` / `MODIFIED` / `DELETED` |
| `userId` | `ICurrentUser.UserId` |
| `content` | `entity.GetLogContent()` serializado a JSON y embebido como **string** (JSON escapado). En `DELETED` captura el estado del elemento eliminado (leído antes de persistir); `""` solo si no hay content |

Todos los campos del payload son **strings** (requisito del servicio de auditoría). El `DedupKey` es `SHA256(UTF8(PayloadJson))` en hexadecimal en mayúsculas.

---

## Contrato con el servicio remoto

- **Método/Ruta:** `POST {LogServiceUrl}/v1/log`
- **Content-Type:** `application/json`
- **Cuerpo:** el `PayloadJson` tal cual (ver sección anterior). Los seis campos (`containerKey`, `source`, `owner`, `action`, `userId`, `content`) son strings requeridos; `content` es el objeto serializado como string. El worker envía el `PayloadJson` como **cuerpo crudo** (`StringContent`); NO se re-serializa (ver nota abajo).
- **Éxito:** respuesta con `Success == true` → el registro se elimina de la tabla.
- **Fallo:** respuesta no exitosa, timeout o circuito abierto → el registro queda `Failed`, se
  incrementa `RetryCount` y se guarda el motivo en `AdditionalInfo`.

El retry y el circuit breaker los aplica `IATec.Shared.HttpClient` (Polly). Cuando el circuito está
abierto, el `POST` falla rápido y el registro queda reintentable en el siguiente ciclo cuando el
circuito se cierra.

> **Importante (evita `400 Bad Request`).** El `PayloadJson` ya es JSON serializado, así que se
> envía como cuerpo crudo con `new StringContent(payloadJson, Encoding.UTF8, "application/json")`.
> No uses helpers que serializan de nuevo (p.ej. `GenerateStringContent` o `PostAsJsonAsync` sobre
> el `PayloadJson`): volverían a serializar el string y enviarían un JSON escapado (un string) en
> lugar del objeto con sus seis campos, y el servicio responde `400`.

---

## La tabla AuditLogEntry

Se crea automáticamente al arranque (silenciosa e idempotente, vía `IRelationalDatabaseCreator`,
sin SQL crudo). Esquema lógico:

| Columna | Tipo | Notas |
|---------|------|-------|
| `Id` | `long` | PK autoincremental |
| `PayloadJson` | `string` | Cuerpo JSON a enviar |
| `CreatedAt` | `DateTime` | UTC de creación |
| `Status` | `int` | `Pending=0`, `Processing=1`, `Failed=2` |
| `AdditionalInfo` | `string?` | Motivo del fallo (máx. 2000) |
| `DedupKey` | `string` | Hash del payload, **índice único** (máx. 128) |
| `RetryCount` | `int` | Nº de intentos fallidos |
| `ProcessingSince` | `DateTime?` | UTC del reclamo; permite rescatar registros `Processing` abandonados |

---

## Ciclo de vida de un registro

```
Pending ──(worker reclama)──▶ Processing ──┬─ éxito ──▶ (DELETE, desaparece)
                                           │
                                           └─ fallo ──▶ Failed (RetryCount++)
                                                          │
                              RetryCount < RetryLimit ◀───┤
                                 (reintentable)           │
                              RetryCount >= RetryLimit ────┘
                                 (no se vuelve a seleccionar)
```

El worker en cada ciclo selecciona: `Status == Pending`; **o** (`Status == Failed` **y**
`RetryCount < RetryLimit`); **o** (`Status == Processing` **y** su `ProcessingSince` es más viejo
que `ProcessingTimeout`, es decir, un reclamo abandonado tras un reinicio). Ordena por `Id`, toma
`BatchSize`, marca el lote `Processing` sellando `ProcessingSince` (reclamo anti doble-toma entre
réplicas), envía cada uno y guarda.

---

## Transacciones y atomicidad

Los `AuditLogEntry` se insertan en la **misma unidad de trabajo** que la operación de negocio:

- **Con transacción ambiental** (p.ej. abierta por `GenericTransaction.BeginTransaction()`): el
  insert se enlista en ella. Un `commit` persiste negocio + auditoría; un `rollback` descarta ambos.
- **Sin transacción ambiental:** el interceptor abre una transacción **local** para que el guardado
  de negocio y la inserción de los logs sean todo-o-nada.

```csharp
public class ProductTransaction(ApplicationDbContext context)
    : GenericTransaction(context), IProductTransaction;

public async Task CrearAsync(Product product)
{
    try
    {
        transaction.BeginTransaction();
        await repository.AddAsync(product);
        await repository.SaveChangesAsync();   // inserta el AuditLogEntry en la misma transacción
        transaction.CommitTransaction();       // persiste negocio + auditoría
    }
    catch
    {
        transaction.RollbackTransaction();     // descarta negocio + auditoría
        throw;
    }
}
```

---

## Idempotencia (DedupKey)

Dos operaciones con payload idéntico (mismos `containerKey/source/owner/action/userId/content`)
producen el mismo `DedupKey` y se consideran el **mismo evento**. La deduplicación opera en dos
niveles:

1. **Pre-chequeo** antes de insertar: se descartan los `DedupKey` que ya existen (y los repetidos
   dentro del mismo lote).
2. **Índice único** como última barrera: si aún así ocurre una violación de unicidad, se trata como
   **descarte silencioso** sin abortar la operación de negocio.

---

## Ejemplo completo

```csharp
// Program.cs
using IATec.Shared.Persistence.EF.AuditOutbox.Configuration;

var builder = WebApplication.CreateBuilder(args);

// (opcional) usuario autenticado para el campo userId
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();

// 1) Servicios del outbox
builder.Services.AddAuditOutboxLog<ApplicationDbContext>(builder.Configuration);

// 2) DbContext con el outbox adjunto
builder.Services.AddDbContext<ApplicationDbContext>((sp, options) =>
{
    options.UseSqlServer(builder.Configuration.GetConnectionString("Default"));
    options.UseAuditOutbox(sp);
});

// Repositorios/UoW del core (sin ILogDispatcher)
builder.Services.AddScoped<IProductRepository, ProductRepository>();

var app = builder.Build();
app.Run();
```

```csharp
// Repositorio (core): ya NO recibe ILogDispatcher
public class ProductRepository(ApplicationDbContext context)
    : GenericWriteRepository<Product>(context), IProductRepository;
```

Con esto, cada `SaveChangesAsync` que afecte entidades auditables generará automáticamente sus
`AuditLogEntry`, y el worker los enviará al servicio remoto en segundo plano.

---

## Consideraciones y límites

- **Solo providers relacionales.** Con un provider no relacional, la creación de tabla y el outbox
  no aplican (se registra una advertencia y se omite).
- **Múltiples réplicas y reinicios.** El estado `Processing` + el guardado del reclamo evitan el
  doble envío entre réplicas activas. Si una réplica muere (o el host se reinicia) mientras un
  registro está en `Processing`, ese registro NO queda atascado: se sella `ProcessingSince` al
  reclamarlo y, una vez que supera `ProcessingTimeout`, un ciclo posterior lo rescata y reintenta.
  Ajusta `ProcessingTimeout` para que sea cómodamente mayor que lo que tarda una entrega, de modo
  que no se “robe” un registro que otra réplica está enviando legítimamente.
- **Content de Deleted.** Para `DELETED` el `content` captura el estado del elemento eliminado: se lee con `GetLogContent()` en `SavingChanges` (antes del DELETE), cuando la entidad aún está completa en el change tracker. Así el servicio recibe un `content` no vacío.
- **El core es audit-agnóstico.** La auditoría vive por completo en este paquete; el core
  (`GenericWriteRepository` / `GenericTransaction`) ya no captura ni despacha logs.

---

## Solución de problemas

| Síntoma | Causa probable | Solución |
|---------|----------------|----------|
| La app falla al iniciar con un mensaje sobre `ContainerKey`/`LogServiceUrl` | Falta un valor requerido o la URL no es absoluta | Completa la sección `AuditOutboxLog` del `appsettings.json`. |
| No se generan `AuditLogEntry` | No se llamó `UseAuditOutbox(sp)` en `AddDbContext`, o la entidad no implementa `IEntity` / `GetSourceType()` es `null` | Adjunta el outbox al `DbContext` y verifica el contrato `IEntity`. |
| Los registros quedan en `Failed` | El servicio remoto responde con error, hay timeout o el circuito está abierto | Revisa `AdditionalInfo`; valida `LogServiceUrl` y la disponibilidad del servicio. |
| El servicio remoto responde `400 Bad Request` | El cuerpo se re-serializó (JSON escapado como string) en vez de enviarse crudo | Envía el `PayloadJson` con `StringContent`, no con `GenerateStringContent`/`PostAsJsonAsync`. Verifica que `content` llegue como string y que ningún campo sea `null`. |
| `userId` siempre es `"system"` | No registraste una implementación de `ICurrentUser` | Registra tu `ICurrentUser` antes/después de `AddAuditOutboxLog`. |
| Registros `Failed` que no se reintentan | `RetryCount >= RetryLimit` | Es por diseño; ajusta `RetryLimit` si necesitas más reintentos. |
| Registros atascados en `Processing` tras un reinicio | El worker murió entre el reclamo y el envío | Se rescatan solos tras `ProcessingTimeout`; reduce ese valor si necesitas un rescate más rápido. |
