# Broker / Middleware System Design Notions

> Design principles, patterns, and architectural concepts applied in the OPC HDA Broker — a protocol-translating middleware between legacy COM and modern HTTP/JSON.

---

## 1. Protocol Translation (Broker Pattern)

**What it is**: A broker sits between two systems that speak different protocols and translates requests/responses between them.

**How we use it**: The OPC HDA Broker translates HTTP/JSON requests into OPC HDA COM calls and returns the results as JSON. Neither side knows about the other's protocol.

```
HTTP Client  ──►  [ Broker ]  ──►  COM Server
  (JSON)          (translates)       (binary)
```

**Where in code**:
- `ReadController.cs` — receives HTTP GET, calls `BrokerEngine`, returns JSON
- `BrokerEngine.cs` — dispatches COM calls via `StaThreadDispatcher`
- `HdaReader.cs` — executes actual COM operations (`ReadRaw`, `ReadProcessed`)

**Why it matters**: Legacy OPC HDA COM has no HTTP interface. Without the broker, modern tools (Grafana, Power BI, browsers) cannot access historian data.

---

## 2. Stateless API Design

**What it is**: Each request contains all the information needed to process it. The server does not store client session state between requests.

**How we use it**: Every API call includes the tag name, time range, and parameters in the URL. The broker doesn't track "who asked what last time."

```
GET /api/read/raw?tags=TAG_1&from=2026-05-01T00:00:00Z&to=2026-05-02T00:00:00Z
```

**Benefits**:
- Any request can go to any broker instance (horizontal scaling)
- No session cleanup or timeout management
- Clients can retry failed requests without side effects
- Simpler debugging — each request is self-contained

---

## 3. Thread Affinity / Dedicated Thread Pattern

**What it is**: Certain resources (like COM objects) must be accessed from the same thread that created them. A dedicated thread owns these resources, and all other threads queue work to it.

**How we use it**: `StaThreadDispatcher` creates a single MTA thread for all COM operations. API controllers queue work to this thread and await results.

```
API Thread 1  ─┐
API Thread 2  ─┤──►  BlockingCollection  ──►  COM Thread (single)  ──►  KepServerEX
API Thread 3  ─┘       (work queue)            (processes serially)
```

**Where in code**: `ComInterop/StaThreadDispatcher.cs`
- Uses `BlockingCollection<WorkItem>` as a producer-consumer queue
- `InvokeAsync<T>()` queues work + returns a `Task<T>` for the caller to await
- The COM thread loops through `GetConsumingEnumerable()` processing work items sequentially

**Why it matters**: COM objects created by KepServerEX are apartment-threaded. Calling them from a random thread pool thread causes `RPC_E_WRONG_THREAD` or data corruption. The dispatcher serializes all COM access to one thread, guaranteeing thread affinity.

---

## 4. Singleton Orchestrator

**What it is**: A single static instance that owns and coordinates all subsystems. Only one exists for the lifetime of the application.

**How we use it**: `BrokerEngine` is a static class that owns the COM connection, thread dispatcher, browser, reader, and cache. All API controllers call into this single instance.

```
BrokerEngine (static singleton)
  ├── StaThreadDispatcher  (1 COM thread)
  ├── HdaConnection        (1 connection to KepServerEX)
  ├── HdaBrowser           (tag discovery)
  ├── HdaReader            (data retrieval)
  └── MemoryCache          (TTL-based cache)
```

**Where in code**: `BrokerEngine.cs` — `public static class BrokerEngine`

**Trade-offs**:
- ✅ Simple, no dependency injection framework needed
- ✅ Guaranteed single COM connection (avoids resource contention)
- ⚠️ Harder to unit test (static coupling)
- ⚠️ Not suitable if multiple OPC servers were needed simultaneously

---

## 5. Graceful Degradation / Fallback Strategy

**What it is**: When the primary approach fails, the system automatically falls back to alternative methods rather than failing entirely.

### 5a. Connection Fallback

```
Primary URL: opchda://localhost/Kepware.KEPServerEX_HDA.V6
    ↓ (if fails)
Fallback URL: opchda://127.0.0.1/Kepware.KEPServerEX_HDA.V6
```

**Where**: `HdaConnection.Connect()` — tries each URL in sequence

### 5b. Tag Discovery — Three-Tier Fallback

| Priority | Method | Reliability |
|---|---|---|
| 1 | SDK `CreateBrowser()` — namespace walk | Limited by KepServerEX browse depth |
| 2 | TSD file auto-discovery (reads `.name` files) | Most reliable — reads disk directly |
| 3 | `tags.txt` config file + runtime API | Manual fallback |

**Where**: `HdaBrowser.DiscoverAllTags()` — tries all three, merges results

### 5c. Aggregate Fallback

If `GetAggregates()` fails, hardcoded defaults are used (`Average`, `Min`, `Max`, etc.)

**Where**: `HdaReader.GetSupportedAggregates()` — catches exceptions, calls `AddDefaultAggregates()`

**Why it matters**: Industrial systems must be robust. A broker that fails because one discovery method is broken is unacceptable when alternatives exist.

---

## 6. TTL-Based Caching

**What it is**: Frequently-requested data is cached in memory with a time-to-live (TTL). After the TTL expires, the next request triggers a fresh fetch.

**How we use it**: Tag lists and server status are cached to avoid hitting the COM layer on every HTTP request.

| Cache Key | TTL | Why |
|---|---|---|
| `tags` | 60 seconds | Tag list rarely changes; COM browse is expensive |
| `aggregates` | 10 minutes | Aggregate list is static for a given server |
| Status | 30 seconds | Server status doesn't change rapidly |

**Where in code**: `Cache/MemoryCache.cs`
- `ConcurrentDictionary` for thread safety
- `GetOrAdd<T>(key, factory, ttl)` — lazy evaluation with expiry
- `Invalidate(key)` — manual cache bust (used when tags are added via API)

**Pattern**: Cache-Aside (Lazy Loading) — the cache doesn't know about the data source; the caller provides a factory function.

---

## 7. Adapter Pattern (Data Shape Transformation)

**What it is**: Transform data from one shape to another to satisfy different consumers without changing the underlying logic.

**How we use it**: The same COM data is served in multiple JSON shapes:

| Consumer | Endpoint | Shape |
|---|---|---|
| General | `/api/read/raw` | `{ data: [{ tag, points: [{t,v,q}] }] }` (nested) |
| Grafana Infinity | `/api/read/points` | `{ data: [{t,v,q}] }` (flat array) |
| Grafana Table | `/api/read/latest/table` | `{ data: [{tag,value,timestamp,quality}] }` (flat rows) |
| Grafana Stat | `/api/status/list` | `[{...}]` (array-wrapped object) |

**Why**: The Infinity plugin cannot parse nested JSON with array-index selectors (`data[0].points` silently fails). Rather than forcing consumers to adapt, the broker provides consumer-optimized endpoints.

**Principle**: "Make the consumer's job easy" — the broker absorbs complexity so consumers don't have to.

---

## 8. Self-Hosted Web Server (No External Dependencies)

**What it is**: The application hosts its own HTTP server internally rather than requiring IIS, Nginx, or another external web server.

**How we use it**: OWIN + HttpListener provides a self-contained HTTP stack inside the `.exe`.

```csharp
using (WebApp.Start<Api.Startup>(baseUrl))  // starts HTTP listener
```

**Benefits**:
- Zero infrastructure dependencies — just run the `.exe`
- Runs as both console app (dev) and Windows Service (prod)
- No IIS configuration, app pools, or deployment pipelines
- Simplifies deployment on industrial PCs with minimal software

---

## 9. Dual-Mode Execution (Console + Service)

**What it is**: The same executable can run interactively (console) for development/debugging or as a background Windows Service for production.

**How we use it**: `Program.cs` detects the execution mode:

```csharp
bool isService = !Environment.UserInteractive;
if (isService)
    ServiceBase.Run(new BrokerWindowsService());
else
    RunAsConsole();
```

**Benefits**:
- One binary for both dev and prod
- Console mode shows real-time logs for debugging
- Service mode auto-starts on boot with failure recovery

---

## 10. UTC Normalization

**What it is**: All timestamps in the system are forced to UTC before serialization, eliminating timezone ambiguity.

**The problem**: The host runs in UTC+1. The SDK returns timestamps with `DateTimeKind.Unspecified`. Calling `.ToUniversalTime()` on an unspecified-kind DateTime assumes it's local time and subtracts 1 hour — creating a silent 1-hour drift.

**The fix**: Use `DateTime.SpecifyKind(ts, DateTimeKind.Utc)` to stamp the value as UTC without converting it, then serialize with `ToString("o")` which appends `Z`.

**Rule**: Every timestamp in the broker's JSON output ends with `Z`.

**Where applied**:
- `HdaReader.cs` — when creating `TimeSeriesPoint` objects
- `ReadController.cs` — in the `MapToDto()` helper and Grafana endpoints
- `BrokerEngine.cs` — `DateTime.UtcNow` for lookback windows

**Why it matters**: A 1-hour timestamp drift in an industrial historian makes data useless for correlation, alarms, and compliance.

---

## 11. Separation of Concerns (Layered Architecture)

**What it is**: Each layer has a single responsibility and only depends on the layer below it.

```
┌──────────────────────────────────┐
│  API Layer (Controllers)         │  HTTP routing, input validation,
│  ReadController, TagsController  │  JSON serialization, response shaping
├──────────────────────────────────┤
│  Orchestration (BrokerEngine)    │  Async dispatch, caching,
│  Static singleton                │  reconnect logic
├──────────────────────────────────┤
│  COM Interop Layer               │  OPC HDA protocol, SDK calls,
│  HdaConnection, HdaBrowser,     │  thread affinity, COM lifecycle
│  HdaReader, StaThreadDispatcher  │
├──────────────────────────────────┤
│  Cross-Cutting                   │  Caching, diagnostics, logging,
│  MemoryCache, DiagnosticRunner   │  configuration
└──────────────────────────────────┘
```

**Why it matters**: Changing the API (e.g., adding a new endpoint) doesn't require touching COM code. Changing the COM layer (e.g., upgrading the SDK) doesn't require changing controllers.

---

## 12. Defensive Programming

**What it is**: Assume external systems will fail and handle every failure path explicitly.

**Examples in this codebase**:

| Technique | Where | What it does |
|---|---|---|
| Null-coalescing defaults | `App.config` reads | `?? "default"` on every `ConfigurationManager.AppSettings` call |
| Try-catch per tag | `HdaReader.ReadRaw()` | One bad tag doesn't crash the whole batch |
| Graceful disconnect | `HdaConnection.Disconnect()` | Swallows exceptions during cleanup |
| `FileShare.ReadWrite` | `HdaBrowser.DiscoverFromTsdNameFiles()` | Reads TSD files even while KepServerEX has them locked |
| Max value clamping | `ReadController.ReadRaw()` | `if (maxValues > 100000) maxValues = 100000` — prevents OOM |
| Reconnect on failure | `BrokerEngine.EnsureConnected()` | Auto-reconnects before each read if connection was lost |

---

## 13. Observability (Structured Logging + Diagnostics API)

**What it is**: The system provides multiple ways to understand its internal state without attaching a debugger.

### Structured Logging (Serilog)
- Context-per-class: `Log.ForContext<HdaReader>()`
- Structured properties: `Log.Information("Discovered {Count} tags", tags.Count)`
- Dual sinks: Console (dev) + rolling file (prod, 30-day retention)

### Diagnostics Endpoint
- `GET /api/diagnostics` — runs a comprehensive COM/SDK diagnostic suite
- Tests: SDK API surface, GetStatus, CreateBrowser, tag path formats, raw COM QI, threading apartment
- Returns a structured `DiagnosticReport` with pass/fail for each test

### Health & Status Endpoints
- `GET /api/health` — simple liveness probe
- `GET /api/status` — server version, tag count, uptime, connection state

---

## 14. Producer-Consumer Pattern

**What it is**: One or more producers create work items; one or more consumers process them asynchronously.

**How we use it**: `StaThreadDispatcher` is a classic single-consumer producer-consumer:

```
Producers (API threads)    →    BlockingCollection    →    Consumer (COM thread)
      InvokeAsync()                  (queue)                ProcessQueue()
           ↓                                                     ↓
    TaskCompletionSource  ←─────── TrySetResult ──────────── result
```

**Why this pattern**: It decouples the multi-threaded HTTP world from the single-threaded COM world safely.

---

## 15. Configuration Externalization

**What it is**: All environment-specific values are externalized into configuration files, not hardcoded.

**How we use it**: `App.config` / `OpcHdaBroker.exe.config` contains:
- Connection URLs (primary + fallback)
- API port and listen address
- TSD datastore path
- Cache TTLs
- Log level and file path

**Every config value has a sensible default** via null-coalescing:

```csharp
string primaryUrl = ConfigurationManager.AppSettings["Hda.PrimaryUrl"]
    ?? "opchda://localhost/Kepware.KEPServerEX_HDA.V6";
```

**Why**: The same binary runs on any KepServerEX 6 machine — only the `.config` file changes.

---

## 16. API Design Principles

### RESTful Resource Naming
```
/api/tags              → tag collection
/api/read/raw          → raw data read operation
/api/read/latest       → latest value operation
/api/status            → server status resource
```

### Consistent Response Envelope
Every endpoint returns the same wrapper:
```json
{
  "data": [ ... ],
  "meta": { "count": 5, "executionMs": 12 }
}
```

### Input Validation at the Edge
Controllers validate parameters before calling the engine:
```csharp
if (string.IsNullOrWhiteSpace(tags))
    return BadRequest("'tags' parameter is required");
if (maxValues > 100000) maxValues = 100000;
```

### CORS Enabled
All origins allowed (`*`) — the broker is an internal-network tool, not a public API.

### Self-Documenting (Swagger)
Swagger UI auto-generated at `/swagger` for API exploration without external docs.

---

## 17. Idempotent Reads

**What it is**: Repeating the same request produces the same result without side effects.

**How we use it**: All data endpoints are `GET` requests that only read from the historian. No state is modified. You can call them 1 time or 1000 times — same result, no side effects.

**Exception**: `POST /api/tags/add` and `POST /api/tags/refresh` are intentionally non-idempotent (they modify state).

---

## 18. Service Recovery Pattern

**What it is**: The system is configured to automatically recover from crashes without human intervention.

**How we use it**: The Windows Service is installed with automatic failure recovery:

```cmd
sc failure OpcHdaBroker reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

- 1st failure: restart after 5 seconds
- 2nd failure: restart after 10 seconds
- 3rd failure: restart after 30 seconds
- Reset failure count after 24 hours

Combined with `start= auto`, the broker survives reboots and crashes without manual intervention.

---

## Summary Table

| # | Principle | Key Benefit |
|---|---|---|
| 1 | Protocol Translation (Broker) | Bridges legacy COM and modern HTTP |
| 2 | Stateless API | Scalable, retryable, debuggable |
| 3 | Thread Affinity | Safe COM interop without crashes |
| 4 | Singleton Orchestrator | Single connection, coordinated lifecycle |
| 5 | Graceful Degradation | Three-tier tag discovery, connection fallback |
| 6 | TTL Caching | Reduces expensive COM calls |
| 7 | Adapter Pattern | Consumer-optimized data shapes |
| 8 | Self-Hosted Server | Zero infrastructure dependencies |
| 9 | Dual-Mode Execution | Dev console + prod service from one binary |
| 10 | UTC Normalization | Eliminates timezone drift |
| 11 | Layered Architecture | Separation of concerns |
| 12 | Defensive Programming | Resilient to partial failures |
| 13 | Observability | Structured logs + diagnostics API |
| 14 | Producer-Consumer | Thread-safe async COM dispatch |
| 15 | Config Externalization | One binary, many environments |
| 16 | RESTful API Design | Consistent, self-documenting |
| 17 | Idempotent Reads | Safe to retry, cache, repeat |
| 18 | Service Recovery | Auto-restart on failure |
