# OPC HDA Broker

A stateless RESTful proxy that translates HTTP requests into OPC HDA COM calls against **KepServerEX 6 Local Historian** (.TSD/.Active files). Designed for IT consumption by Power BI, Grafana, custom dashboards, and any HTTP client.

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

## Quick Start

```powershell
# Build (requires .NET Framework 4.72 SDK, x86)
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

**Why these exist**: The original endpoints (`/api/read/raw`, `/api/read/latest`) wrap data in a nested structure — `data[0].points[0].v` — which the Infinity plugin cannot traverse. The `/points` and `/table` endpoints flatten the response so Infinity uses `root_selector: "data"` with simple `{selector: "v"}` column mappings.

### System

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/status` | Server status, version, tag count (flat object) |
| `GET` | `/api/status/list` | Same, wrapped in array for Grafana Infinity |
| `GET` | `/api/health` | Simple liveness probe |
| `GET` | `/api/diagnostics` | Full COM/SDK diagnostic report |

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
        { "t": "2026-04-30T10:21:14.5660000Z", "v": 0, "q": "(Good:Not Limited)" },
        { "t": "2026-04-30T10:21:17.5560000Z", "v": 10, "q": "(Good:Not Limited)" },
        { "t": "2026-04-30T10:21:20.5580000Z", "v": 20, "q": "(Good:Not Limited)" }
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

Response — note the flat `data` array (no nesting):
```json
{
  "data": [
    { "t": "2026-05-03T18:59:59.3990000Z", "v": 50, "q": "(Good:Not Limited)" },
    { "t": "2026-05-03T19:00:02.4070000Z", "v": 60, "q": "(Good:Not Limited)" },
    { "t": "2026-05-03T19:00:05.4090000Z", "v": 70, "q": "(Good:Not Limited)" }
  ],
  "meta": { "count": 3, "executionMs": 4 }
}
```

## Tag Discovery

Tags are discovered using a **three-tier strategy** (in order):

1. **SDK Browser** — `CreateBrowser()` → recursive namespace walk
2. **TSD Auto-Discovery** — Reads tag paths from KepServerEX `.name` files in `C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data\`
3. **`tags.txt` Config File** — Manual tag list (one per line, `#` comments)
4. **`POST /api/tags/add`** — Runtime registration via API

The TSD auto-discovery is the most reliable method for this deployment — it reads tag paths directly from the historian datastore metadata files, including from files locked by KepServerEX.

## Configuration

All settings in `App.config`:

```xml
<appSettings>
  <add key="Hda.PrimaryUrl" value="opchda://localhost/Kepware.KEPServerEX_HDA.V6" />
  <add key="Hda.FallbackUrl" value="opchda://127.0.0.1/Kepware.KEPServerEX_HDA.V6" />
  <add key="Hda.TsdDataPath" value="C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data" />
  <add key="Api.Port" value="5000" />
  <add key="Cache.TagListTtlSec" value="60" />
  <add key="Log.Level" value="Debug" />
</appSettings>
```

---

## Grafana Integration

### End-to-End Data Flow

```
┌──────────────────────┐
│  KepServerEX 6       │  OPC HDA COM server. Logs simulation tag values
│  Local Historian      │  every 3 seconds into .TSD/.Active datastore files.
│  (port: native COM)  │  Tags: Simulations.Simulator 1.TAG_1 … TAG_8
└──────────┬───────────┘
           │ COM/DCOM (MTA thread)
           │ TsCHdaTrend.ReadRaw() / GetServerStatus()
           │
┌──────────▼───────────┐
│  OPC HDA Broker      │  Translates COM calls into HTTP/JSON.
│  (localhost:5000)     │  All timestamps normalized to UTC with trailing Z.
│                       │  Grafana-specific flat endpoints:
│  /api/read/points     │    → flat [{t,v,q}] for timeseries panels
│  /api/read/latest/    │    → flat [{tag,value,timestamp,quality}]
│    table              │       for table panels
│  /api/status/list     │    → [{status fields}] for stat panels
└──────────┬───────────┘
           │ HTTP GET (JSON)
           │ Grafana Infinity plugin proxies requests
           │
┌──────────▼───────────┐
│  Grafana 13.0.1 OSS  │  Dashboarding & visualization.
│  (localhost:3000)     │  Infinity datasource → Broker API.
│                       │  10 panels: 4 stat, 5 timeseries, 1 table.
│  Dashboard UID:       │
│  opc-hda-historian    │
└──────────────────────┘
```

**How it connects step by step:**

1. **KepServerEX** logs simulated tag values into `.TSD` files on disk every ~3 seconds
2. **OPC HDA Broker** discovers those tags by reading the `.name` metadata files in the TSD datastore directory
3. When Grafana requests data, the **Infinity plugin** sends an HTTP GET to the broker (e.g. `GET /api/read/points?tag=...&from=...&to=...`)
4. The broker dispatches a `ReadRaw()` COM call to KepServerEX on the MTA thread, retrieves the historian data points
5. The broker normalizes all timestamps to **UTC with trailing `Z`** and returns flat JSON
6. Grafana Infinity parses the flat JSON using `root_selector: "data"` and maps columns (`t` → timestamp, `v` → number)
7. Grafana renders the timeseries chart, auto-refreshing every 30 seconds

### Prerequisites

- **Grafana OSS** ≥ 13.0 installed as a Windows service
- **Infinity plugin** (yesoreyeram-infinity-datasource) v3.8+

### Install Steps

```powershell
# 1. Install Grafana OSS (if not already installed)
winget install GrafanaLabs.Grafana.OSS

# 2. Create a plugin directory (avoid Program Files permission issues)
mkdir C:\Users\$env:USERNAME\grafana-plugins

# 3. Install the Infinity plugin
grafana cli --pluginsDir "C:\Users\$env:USERNAME\grafana-plugins" plugins install yesoreyeram-infinity-datasource

# 4. Configure Grafana — create/edit custom.ini
#    Location: C:\Program Files\GrafanaLabs\grafana\conf\custom.ini
#    (or copy from deploy\grafana-custom.ini)
```

**`custom.ini`** contents:
```ini
[paths]
plugins = C:\Users\Admin\grafana-plugins

[plugins]
allow_loading_unsigned_plugins = yesoreyeram-infinity-datasource
```

```powershell
# 5. Restart Grafana to load the plugin
Restart-Service grafana

# 6. Import the dashboard via API
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("admin:admin"))
$headers = @{ Authorization = "Basic $cred"; "Content-Type" = "application/json" }
$body = Get-Content "deploy\grafana-dashboard.json" -Raw
Invoke-RestMethod "http://localhost:3000/api/dashboards/db" -Method POST -Headers $headers -Body $body
```

### Dashboard Panels

| Panel | Type | Broker Endpoint | What it shows |
|---|---|---|---|
| Broker Status | Stat (green) | `/api/status/list` → `serverStatus` | ONLINE / Error |
| KepServerEX | Stat | `/api/status/list` → `serverVersion` | e.g. `6.6.350` |
| Tags | Stat (purple) | `/api/status/list` → `tagCount` | Number of discovered tags |
| Uptime | Stat (orange) | `/api/status/list` → `brokerUptime` | Running duration |
| TAG_1 – TAG_5 | Time series | `/api/read/points?tag=...` | Historical value chart |
| Latest Values | Table | `/api/read/latest/table?tags=...` | All tags with last value |

### Grafana Files

| File | Purpose |
|---|---|
| `deploy/grafana-custom.ini` | Grafana `custom.ini` — plugin path + unsigned allow |
| `deploy/grafana-dashboard.json` | Exportable dashboard JSON (import via API or UI) |

---

## Timezone Handling (UTC Normalization)

The broker runs on a **UTC+1 (WEST)** host. Without normalization, timestamps would be ambiguous — Grafana and Power BI would misinterpret them, causing a 1-hour drift.

**The fix applied across three files:**

| File | Change | Why |
|---|---|---|
| `HdaReader.cs` | `DateTime.Now` → `DateTime.UtcNow` | The `ReadLatest` lookback window used local time, so a "last 1h" query actually asked the historian for data offset by +1h |
| `HdaReader.cs` | `DateTime.SpecifyKind(ts, DateTimeKind.Utc)` | Timestamps returned by the SDK have `Kind=Unspecified`; marking them as UTC ensures `.ToString("o")` appends `Z` |
| `ReadController.cs` | `SpecifyKind` before `ToString("o")` | The DTO serialization point — forces every JSON timestamp to end with `Z` (e.g. `2026-05-03T19:00:02.4070000Z`) |

**Rule**: Every timestamp in the broker's JSON output ends with `Z`. Grafana interprets `Z` as UTC and applies the browser's local timezone in the UI automatically.

---

## Project Structure

```
src/OpcHdaBroker/
├── Program.cs                          # Entry point (console + service)
├── BrokerEngine.cs                     # Central orchestrator
├── App.config                          # Configuration
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
│   │   ├── StatusController.cs         # /api/status + /api/status/list + /api/health
│   │   └── DiagnosticsController.cs    # /api/diagnostics
│   └── Models/
│       └── ApiModels.cs                # DTOs
├── Cache/
│   └── MemoryCache.cs                  # In-memory TTL cache
├── Diagnostics/
│   └── DiagnosticRunner.cs             # Comprehensive SDK/COM diagnostic tool
└── tags.txt                            # Tag configuration file

deploy/
├── grafana-custom.ini                  # Grafana config (plugin path + unsigned plugins)
└── grafana-dashboard.json              # Provisioned Grafana dashboard (10 panels)
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
- **SDK Browse Depth**: The SDK's `ITsCHdaBrowser.Browse()` can navigate 1-2 levels but fails at deeper levels with `E_INVALIDARG` on `ChangeBrowsePosition`. This is a KepServerEX HDA browsing limitation — TSD auto-discovery compensates for this.
- **Tag Path Format**: Tags use the `Channel.Device.Tag` dotted notation (e.g., `Simulations.Simulator 1.TAG_1`).
