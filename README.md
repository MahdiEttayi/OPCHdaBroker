# OPC HDA Broker

A **stateless** RESTful proxy that translates HTTP requests into OPC HDA COM calls against **KepServerEX 6 Local Historian** (.TSD/.Active files). The broker stores nothing — it is a pure translator, a window into your historian data. All historical data recorded weeks, months, or years before the broker was written is fully accessible.

## Status: What Works
| Feature | Status |
|---|---|
| Server Connection | ✅ `Operational` (KepServerEX 6.6.350) |
| Server Status API | ✅ SDK `GetServerStatus()` |
| Tag Discovery | ✅ Auto (TSD files) + Manual (tags.txt + API) |
| Raw Data Reads | ✅ `ReadRaw` via `TsCHdaTrend` |
| Latest Value | ✅ `ReadLatest` (1-hour lookback) |
| Processed Reads | ✅ `ReadProcessed` (aggregates) |
| Aggregate Query | ✅ SDK `GetAggregates()` |
| Diagnostics | ✅ `/api/diagnostics` endpoint |
| TimescaleDB Export | ✅ PostgreSQL/TimescaleDB ingestion + backfill |
| Backfill | ✅ Chunked historical read + upsert |

## Quick Start

```powershell
# Build (requires .NET Framework 4.7.2 SDK, x86)
cd src\OpcHdaBroker
dotnet build

# Run
dotnet run

# Test
Invoke-RestMethod http://localhost:5000/api/status
Invoke-RestMethod http://localhost:5000/api/tags
Invoke-RestMethod "http://localhost:5000/api/read/latest?tags=Simulations.Simulator 1.TAG_1"
```

## Architecture

```
┌─────────────────┐     HTTP/JSON      ┌─────────────────────────────┐
│  Power BI       │◄──────────────────►│  OPC HDA Broker             │
│  Grafana        │     REST API       │  ┌─────────────────────┐   │
│  Dashboard      │     port 5000      │  │ OWIN/WebAPI         │   │
│  curl / scripts │                    │  └──────┬──────────────┘   │
└─────────────────┘                    │         │                   │
                                       │  ┌──────▼──────────────┐   │
                                       │  │ BrokerEngine        │   │
                                       │  │ (orchestrator)      │   │
                                       │  └──────┬──────────────┘   │
                                       │         │ COM thread       │
                                       │  ┌──────▼──────────────┐   │
                                       │  │ HdaConnection       │   │
                                       │  │ HdaBrowser          │   │
                                       │  │ HdaReader           │   │
                                       │  └──────┬──────────────┘   │
                                       └─────────┼─────────────────┘
                                                 │ COM/DCOM
                                       ┌─────────▼─────────────────┐
                                       │  KepServerEX 6            │
                                       │  HDA Server               │
                                       │  (Kepware.KEPServerEX_    │
                                       │   HDA.V6)                 │
                                       │  ┌─────────────────────┐  │
                                       │  │ Local Historian     │  │
                                       │  │ .TSD / .Active      │  │
                                       │  └─────────────────────┘  │
                                       └───────────────────────────┘
```

### Data Lives in TSD Files

```
C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data\
├── Simulations/
│   └── Simulator 1/
│       ├── Simulations.Simulator 1.Active     ← currently being written to
│       ├── Simulations.Simulator 1.001.TSD    ← sealed historical archive
│       └── Simulations.Simulator 1.name       ← tag name index (metadata)
```

KepServerEX writes data every ~3 seconds into `.Active` files, which are sealed into `.TSD` archives as they grow. The broker **never writes** to these files — it reads through KepServerEX only.

### Two Separate Processes

**Tag Discovery** — The broker reads `.name` metadata files directly from the TSD datastore directory (no KepServerEX involved). This finds tag paths like `Simulations.Simulator 1.TAG_1`.

**Data Retrieval** — HTTP requests are translated into `TsCHdaTrend.ReadRaw()` COM calls. KepServerEX reads from `.TSD` / `.Active` files internally and returns points through COM. The broker normalizes timestamps to UTC and returns JSON.

```
Grafana → Broker → COM: ReadRaw("TAG_1", 08:00, 09:00) → KepServerEX → TSD files
                                                              ↑ reads binary data
                                                              (proprietary format)
```

### Historical Data

You can query any time range stored in the TSD files — including data recorded before the broker existed. The broker doesn't need to have been running when data was recorded.

```powershell
# Read data from April 2026 — before the broker was finalized
$uri = "http://localhost:5000/api/read/points?tag=Simulations.Simulator 1.TAG_1&from=2026-04-28T00:00:00Z&to=2026-04-28T23:59:59Z"
Invoke-RestMethod $uri
```

### Startup Sequence

```
Program.cs
  └── OWIN WebAPI starts on port 5000
  └── BrokerEngine.Initialize()

BrokerEngine.Initialize()
  └── Creates StaThreadDispatcher (dedicated MTA COM thread)
  └── HdaConnection.Connect()       [on MTA thread]
  └── HdaBrowser.DiscoverAllTags()  [on MTA thread]
       ├── Tier 1: SDK Browser         → may fail at depth 2 (KepServerEX limitation)
       ├── Tier 2: Raw COM BROWSE_DIRECT → fallback if SDK fails
       ├── Tier 3: TSD .name files     → reads metadata directly from disk
       └── Tier 4: tags.txt            → manual fallback
```

### Request Flow

```
HTTP GET /api/read/points
  └── ReadController.ReadPoints()
        └── BrokerEngine.ReadRawAsync()       → queues on MTA thread
              └── StaThreadDispatcher.InvokeAsync()
                    └── HdaReader.ReadRaw()   [on MTA thread]
                          └── TsCHdaTrend.ReadRaw()  → COM → KepServerEX
                                └── DateTime.SpecifyKind(ts, Utc)
  └── JSON response: { "data": [{t,v,q}], "meta": {...} }
```

## API Endpoints

### Tags

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/tags` | List all known tags |
| `GET` | `/api/tags?search=TAG` | Search tags by name |
| `POST` | `/api/tags/add` | Register new tags (JSON array body) |
| `POST` | `/api/tags/refresh` | Force refresh the tag cache |

### Data Reads

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/read/raw?tags=...&from=...&to=...` | Raw historical data |
| `GET` | `/api/read/latest?tags=...` | Most recent value (1h lookback) |
| `GET` | `/api/read/processed?tags=...&aggregate=average&intervalSec=3600` | Aggregated data |
| `GET` | `/api/read/aggregates` | List supported aggregates |

### Grafana-Optimized Endpoints

These endpoints were added specifically for the Grafana Infinity plugin (v3.8), which cannot use array-index notation in `root_selector` (e.g. `data[0].points` fails). They return flat arrays that Infinity can parse directly.

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/read/points?tag=...&from=...&to=...` | Single-tag flat points array `{data: [{t,v,q}]}` |
| `GET` | `/api/read/latest/points?tag=...` | Single-tag latest value |
| `GET` | `/api/read/latest/table?tags=...` | Multi-tag flat rows `{data: [{tag,value,timestamp,quality}]}` |
| `GET` | `/api/status/list` | Status wrapped in array `[{...}]` for Infinity column selectors |

### System

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/status` | Server status, version, tag count (flat object) |
| `GET` | `/api/status/list` | Same, wrapped in array for Grafana Infinity |
| `GET` | `/api/health` | Simple liveness probe |
| `GET` | `/api/diagnostics` | Full COM/SDK diagnostic report |

### TimescaleDB (PostgreSQL Export)

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/timescaledb/status` | Database connection status |
| `GET` | `/api/timescaledb/backfill/status` | Backfill progress |
| `POST` | `/api/timescaledb/backfill/start` | Start backfill |
| `POST` | `/api/timescaledb/backfill/pause` | Pause backfill |
| `POST` | `/api/timescaledb/backfill/resume` | Resume paused backfill |
| `POST` | `/api/timescaledb/backfill/stop` | Stop backfill |
| `POST` | `/api/timescaledb/backfill/range` | Backfill a custom date range |
| `DELETE` | `/api/timescaledb/data?confirm=true` | Truncate all data |

### Example: Read Raw Data

```powershell
$tag = "Simulations.Simulator 1.TAG_1"
$uri = "http://localhost:5000/api/read/raw?tags=$([uri]::EscapeDataString($tag))&from=2026-04-30T00:00:00Z&to=2026-04-30T23:59:59Z&maxValues=5"
Invoke-RestMethod $uri | ConvertTo-Json -Depth 5
```

Response:
```json
{
  "data": [
    {
      "tag": "Simulations.Simulator 1.TAG_1",
      "count": 5,
      "points": [
        { "t": "2026-04-30T10:21:14.5660000Z", "v": 0, "q": "(Good:Not Limited)" }
      ]
    }
  ],
  "meta": { "count": 5, "executionMs": 5 }
}
```

### Example: Grafana Single-Tag (flat)

```powershell
Invoke-RestMethod "http://localhost:5000/api/read/points?tag=Simulations.Simulator%201.TAG_1&from=2026-05-03T18:00:00Z&to=2026-05-03T20:00:00Z&maxValues=3"
```

Response — flat `data` array (no nesting):
```json
{
  "data": [
    { "t": "2026-05-03T18:59:59.3990000Z", "v": 50, "q": "(Good:Not Limited)" },
    { "t": "2026-05-03T19:00:02.4070000Z", "v": 60, "q": "(Good:Not Limited)" }
  ],
  "meta": { "count": 3, "executionMs": 4 }
}
```

## TimescaleDB Export

The broker can export historical data to **PostgreSQL/TimescaleDB** for long-term storage, SQL queries, and native Grafana datasource access.

- `TsdbRepository` creates the `hda_data` hypertable with 1-day chunks on first connect
- Data is bulk-upserted via `COPY` with `ON CONFLICT DO NOTHING`
- `BackfillService` reads from KepServerEX in configurable chunks and writes to TimescaleDB
- Backfill runs non-blocking — start it and check progress via `/api/timescaledb/backfill/status`

See `docs/report_k6.md` for load test results.

## Tag Discovery

Tags are discovered using a **four-tier strategy** (in order):

1. **SDK Browser** — `CreateBrowser()` → recursive namespace walk
2. **Raw COM Browser** — `IOPCHDA_Browser` with `BROWSE_DIRECT` (absolute path navigation) — fallback when the SDK's relative `DOWN` navigation fails at depth > 2
3. **TSD Auto-Discovery** — Reads tag paths from KepServerEX `.name` files in `C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data\`
4. **`tags.txt` Config File** — Manual tag list (one per line, `#` comments)
5. **`POST /api/tags/add`** — Runtime registration via API

The TSD auto-discovery is the most reliable method for this deployment — it reads tag paths directly from the historian datastore metadata files, including from files locked by KepServerEX.

## Configuration

Copy `App.config.example` → `App.config` and edit to match your environment:

```xml
<appSettings>
  <!-- OPC HDA Connection -->
  <add key="Hda.PrimaryUrl"  value="opchda://localhost/Kepware.KEPServerEX_HDA.V6" />
  <add key="Hda.FallbackUrl" value="opchda://127.0.0.1/Kepware.KEPServerEX_HDA.V6" />

  <!-- TSD Datastore Path -->
  <add key="Hda.TsdDataPath" value="C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data" />

  <!-- REST API -->
  <add key="Api.BaseUrl"          value="http://localhost:5000" />

  <!-- Cache -->
  <add key="Cache.TagListTtlSec" value="60" />

  <!-- Logging -->
  <add key="Log.Level"    value="Debug" />
  <add key="Log.FilePath" value="logs\broker-.log" />

  <!-- TimescaleDB (optional) -->
  <add key="Tsdb.ConnectionString" value="Host=localhost;Port=5432;Database=hda;Username=postgres;Password=your_password_here" />

  <!-- Backfill (optional) -->
  <add key="Backfill.Enabled"     value="false" />
  <add key="Backfill.StartDate"   value="2024-01-01T00:00:00Z" />
  <add key="Backfill.EndDate"     value="2026-05-01T00:00:00Z" />
  <add key="Backfill.ChunkDays"   value="30" />
</appSettings>
```

---

## Grafana Integration

### End-to-End Data Flow

```
KepServerEX 6        OPC HDA Broker       Grafana
Local Historian       (localhost:5000)     (localhost:3000)
     │                      │                   │
     │  logs every ~3s      │   HTTP GET         │
     │  ─────────────────► │ ◄─────────────── │ Infinity plugin
     │                     │   {data:[{t,v,q}]}│
     │                     │ ───────────────► │
```

1. **KepServerEX** logs simulated tag values into `.TSD` files every ~3 seconds
2. **OPC HDA Broker** discovers tags by reading `.name` metadata files
3. Grafana Infinity sends HTTP GET to the broker
4. Broker dispatches `ReadRaw()` COM call to KepServerEX
5. Timestamps normalized to **UTC with trailing `Z`**
6. Grafana renders the timeseries chart, auto-refreshing every 30 seconds

### Prerequisites

- **Grafana OSS** ≥ 13.0 installed as a Windows service
- **Infinity plugin** (yesoreyeram-infinity-datasource) v3.8+

```powershell
# Install Grafana OSS
winget install GrafanaLabs.Grafana.OSS

# Create plugin directory
mkdir C:\Users\$env:USERNAME\grafana-plugins

# Install Infinity plugin
grafana cli --pluginsDir "C:\Users\$env:USERNAME\grafana-plugins" plugins install yesoreyeram-infinity-datasource
```

Configure `custom.ini` (`C:\Program Files\GrafanaLabs\grafana\conf\custom.ini`):
```ini
[paths]
plugins = C:\Users\USERNAME\grafana-plugins

[plugins]
allow_loading_unsigned_plugins = yesoreyeram-infinity-datasource
```

```powershell
# Restart Grafana
Restart-Service grafana

# Import dashboard
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("admin:admin"))
$headers = @{ Authorization = "Basic $cred"; "Content-Type" = "application/json" }
$body = Get-Content "deploy\grafana-dashboard.json" -Raw
Invoke-RestMethod "http://localhost:3000/api/dashboards/db" -Method POST -Headers $headers -Body $body
```

---

## Timezone Handling (UTC Normalization)

The broker runs on a **UTC+1 (WEST)** host. Without normalization, timestamps would be ambiguous — Grafana and Power BI would misinterpret them, causing a 1-hour drift.

**The fix applied across three files:**

| File | Change | Why |
|---|---|---|
| `HdaReader.cs` | `DateTime.Now` → `DateTime.UtcNow` | The `ReadLatest` lookback window used local time, so a "last 1h" query actually asked the historian for data offset by +1h |
| `HdaReader.cs` | `DateTime.SpecifyKind(ts, DateTimeKind.Utc)` | Timestamps returned by the SDK have `Kind=Unspecified`; marking them as UTC ensures `.ToString("o")` appends `Z` |
| `ReadController.cs` | `SpecifyKind` before `ToString("o")` | The DTO serialization point — forces every JSON timestamp to end with `Z` |

**Rule**: Every timestamp in the broker's JSON output ends with `Z`. Grafana interprets `Z` as UTC and applies the browser's local timezone in the UI automatically.

---

## Project Structure

```
src/OpcHdaBroker/
├── Program.cs                          # Entry point (console + service)
├── BrokerEngine.cs                     # Central orchestrator
├── App.config.example                  # Configuration template (copy to App.config)
├── ComInterop/
│   ├── HdaConnection.cs                # OPC HDA server connection
│   ├── HdaBrowser.cs                   # Tag discovery (3-tier)
│   ├── HdaReader.cs                    # ReadRaw/ReadLatest/ReadProcessed
│   ├── StaThreadDispatcher.cs          # COM thread affinity (MTA)
│   └── ReflectionHelper.cs             # SDK field access utilities
├── Api/
│   ├── Startup.cs                      # OWIN/WebAPI configuration
│   ├── Controllers/
│   │   ├── TagsController.cs           # /api/tags endpoints
│   │   ├── ReadController.cs           # /api/read + Grafana-friendly endpoints
│   │   ├── TsdbController.cs           # /api/timescaledb endpoints
│   │   ├── StatusController.cs         # /api/status + /api/status/list + /api/health
│   │   └── DiagnosticsController.cs    # /api/diagnostics
│   └── Models/
│       └── ApiModels.cs                # DTOs
├── Cache/
│   └── MemoryCache.cs                  # In-memory TTL cache
├── Diagnostics/
│   └── DiagnosticRunner.cs             # Comprehensive SDK/COM diagnostic tool
├── TimescaleDb/
│   ├── TsdbRepository.cs               # Schema creation, bulk upsert via COPY
│   └── BackfillService.cs              # Chunked historical read + DB write
└── tags.txt                            # Tag configuration file

deploy/
├── grafana-custom.ini                  # Grafana config (plugin path + unsigned plugins)
├── grafana-dashboard.json             # Provisioned Grafana dashboard (10 panels)
├── install-service.bat                # Register broker as Windows Service
├── uninstall-service.bat              # Unregister Windows Service
└── setup-services.bat                 # One-click setup script
```

## Technical Notes

### COM Threading
All OPC HDA COM calls are dispatched to a dedicated MTA thread via `StaThreadDispatcher`. This ensures thread affinity for the COM objects created by KepServerEX.

### SDK Usage
The broker uses the Technosoftware `OpcClientSdk472.dll` (placed in `lib/`). All HDA operations are performed through the SDK's high-level API:
- `TsCHdaServer.CreateBrowser()` for tag browsing
- `TsCHdaServer.GetServerStatus()` for server status
- `TsCHdaServer.GetAggregates()` for supported aggregates
- `TsCHdaTrend.ReadRaw()` for historical data retrieval

**No raw COM QueryInterface (QI) is needed** — the SDK handles all COM interop internally.

### Infinity Plugin v3.8 Quirks
- **No array-index in `root_selector`** — `data[0].points` silently returns empty. Use flat endpoints instead.
- **Explicit columns require array responses** — A flat object `{key: val}` returns empty frames when `columns` are defined; wrap in `[{...}]`.
- **`url_options.method` is mandatory** — Omitting it causes `Cannot read properties of undefined (reading 'method')`.

### Known Limitations
- **SDK Browse Depth**: The SDK's `ITsCHdaBrowser.Browse()` can navigate 1-2 levels but fails at deeper levels with `E_INVALIDARG` on `ChangeBrowsePosition`. The broker now tries raw COM `IOPCHDA_Browser` with `BROWSE_DIRECT` (absolute path navigation) as a fallback layer. If that also fails, TSD auto-discovery compensates by reading `.name` files directly from disk.
- **Tag Path Format**: Tags use the `Channel.Device.Tag` dotted notation (e.g., `Simulations.Simulator 1.TAG_1`).
- **Data Retention**: The broker has no influence on data retention. TSD file lifecycle is controlled entirely by KepServerEX's historian configuration.
