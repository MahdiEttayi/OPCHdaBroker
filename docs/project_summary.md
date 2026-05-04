# OPC HDA Broker — Project Summary

> Everything we've built together from **April 21 → May 3, 2026** across ~8 conversations.

---

## 🎯 The Goal

Build a **production-ready REST API bridge** between KepServerEX 6's OPC HDA COM historian and modern IT tools (Grafana, Power BI, dashboards). The OPC HDA protocol is legacy COM — no HTTP client can talk to it directly — so the broker translates HTTP/JSON ↔ COM calls.

---

## 📅 Timeline & Milestones

### Phase 1 — Understanding & Scaffolding *(Apr 21)*
- Reviewed the existing `OpcHdaWrapper.cs` foundation
- Understood the Technosoftware SDK (`OpcClientSdk472.dll`) and how it wraps OPC HDA COM
- Established the project structure: `ComInterop/`, `Api/`, `Cache/`, `Diagnostics/`

### Phase 2 — COM Interop & Connectivity *(Apr 23 → Apr 30)*
- **Biggest challenge of the project**: getting stable COM communication with KepServerEX
- Initial approach used raw `QueryInterface` (QI) — fragile and unreliable
- Refactored to use the SDK's native high-level API:
  - `TsCHdaServer.CreateBrowser()` for tag browsing
  - `TsCHdaServer.GetServerStatus()` for health checks
  - `TsCHdaTrend.ReadRaw()` for data retrieval
  - `TsCHdaServer.GetAggregates()` for aggregate queries
- Implemented **MTA threading model** via `StaThreadDispatcher` — all COM calls dispatched to a dedicated thread for thread affinity
- Result: **No raw COM QI needed** — the SDK handles all interop internally

### Phase 3 — Tag Discovery *(Apr 30)*
Built a **three-tier tag discovery strategy** (in priority order):

| Tier | Method | Reliability |
|------|--------|-------------|
| 1 | SDK `CreateBrowser()` — recursive namespace walk | Works 1-2 levels; deeper levels hit `E_INVALIDARG` (KepServerEX limitation) |
| 2 | **TSD Auto-Discovery** — reads `.name` metadata files from the historian datastore on disk | ✅ Most reliable — reads directly from `C:\ProgramData\Kepware\...\Historical Data\` |
| 3 | `tags.txt` config file + `POST /api/tags/add` runtime API | Manual fallback |

### Phase 4 — REST API Build-Out *(Apr 30 → May 3)*
Built the full OWIN/WebAPI surface on port 5000:

| Category | Endpoints |
|----------|-----------|
| **Tags** | `GET /api/tags`, `POST /api/tags/add`, `POST /api/tags/refresh` |
| **Data** | `GET /api/read/raw`, `GET /api/read/latest`, `GET /api/read/processed` |
| **Grafana-flat** | `GET /api/read/points`, `GET /api/read/latest/points`, `GET /api/read/latest/table`, `GET /api/status/list` |
| **System** | `GET /api/status`, `GET /api/health`, `GET /api/diagnostics`, `GET /api/read/aggregates` |

### Phase 5 — Grafana Integration *(May 3)*
- Installed **Grafana 13.0.1 OSS** as a Windows service
- Installed **Infinity plugin v3.8** for HTTP/JSON datasource
- Discovered and solved multiple Infinity quirks:
  - `root_selector` does **not** support array-index notation (`data[0].points` silently fails)
  - Stat panels need data wrapped in `[{...}]` arrays, not flat objects
  - `url_options.method` must always be explicitly set
- Created **Grafana-optimized flat endpoints** (`/api/read/points`, `/api/read/latest/table`, `/api/status/list`) to work around these quirks
- Built and provisioned a **10-panel dashboard** (`deploy/grafana-dashboard.json`):
  - 4 stat panels (Broker Status, KepServer Version, Tag Count, Uptime)
  - 5 timeseries panels (TAG_1 through TAG_5)
  - 1 table panel (Latest Values for all tags)

### Phase 6 — UTC Timestamp Normalization *(May 3)*
Fixed a **1-hour timezone drift** (host is UTC+1). Changes across three files:

| File | Fix |
|------|-----|
| `HdaReader.cs` | `DateTime.Now` → `DateTime.UtcNow` for lookback windows |
| `HdaReader.cs` | `DateTime.SpecifyKind(ts, DateTimeKind.Utc)` on SDK timestamps |
| `ReadController.cs` | `SpecifyKind` before `ToString("o")` at DTO serialization |

**Rule**: Every timestamp in the broker's JSON output now ends with `Z`.

### Phase 7 — Documentation & Power BI Guide *(May 3)*
- Wrote comprehensive [README.md](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/README.md) with architecture diagrams, API reference, and Grafana setup
- Wrote [PowerBI-Guide.md](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/docs/PowerBI-Guide.md) with:
  - Quick-start Web connector instructions
  - Power Query M code for dynamic date ranges
  - Parameterized function `fnGetTagData()` for multi-tag use
  - Latest values table query
  - Scheduled refresh options (Gateway / DirectQuery)

---

## 🏗️ Final Architecture

```
Power BI / Grafana / curl
        │
        │  HTTP GET (JSON)
        ▼
┌─────────────────────────────┐
│  OPC HDA Broker             │
│  (localhost:5000)            │
│                              │
│  OWIN/WebAPI Controllers     │
│       │                      │
│  BrokerEngine (orchestrator) │
│       │                      │
│  ComInterop/                 │
│   HdaConnection              │
│   HdaBrowser (3-tier tags)   │
│   HdaReader (ReadRaw/etc)    │
│   StaThreadDispatcher (MTA)  │
└──────────┬───────────────────┘
           │ COM/DCOM
           ▼
┌───────────────────────────┐
│  KepServerEX 6.6.350     │
│  Local Historian          │
│  (.TSD / .Active files)   │
└───────────────────────────┘
```

---

## 📁 Deliverables

| File | Purpose |
|------|---------|
| [OpcHdaBroker.sln](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/OpcHdaBroker.sln) | Solution file |
| [src/OpcHdaBroker/](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/src/OpcHdaBroker) | All source code (.NET Framework 4.72, x86) |
| [README.md](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/README.md) | Full project documentation (358 lines) |
| [docs/PowerBI-Guide.md](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/docs/PowerBI-Guide.md) | Power BI integration guide (296 lines) |
| [deploy/grafana-dashboard.json](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/deploy/grafana-dashboard.json) | 10-panel Grafana dashboard |
| [deploy/grafana-custom.ini](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/deploy/grafana-custom.ini) | Grafana plugin configuration |
| [deploy/install-service.bat](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/deploy/install-service.bat) | Windows service installer |
| [tags.txt](file:///c:/Users/Admin/Desktop/HistorianDashboard/OPCHdaBroker/src/OpcHdaBroker/tags.txt) | Manual tag configuration |

---

## 🔑 Key Technical Decisions

1. **SDK over raw COM** — Eliminated fragile `QueryInterface` calls; the Technosoftware SDK handles all COM marshalling
2. **MTA thread dispatcher** — Ensures COM thread affinity without STA apartment issues
3. **TSD file discovery** — Most reliable tag discovery method; reads directly from KepServerEX's datastore metadata
4. **Flat Grafana endpoints** — Created separate `/points`, `/table`, `/list` endpoints to work around Infinity plugin's inability to parse nested JSON
5. **UTC everywhere** — All timestamps forced to `DateTimeKind.Utc` and serialized with trailing `Z` to prevent timezone drift
6. **OWIN self-hosted** — No IIS dependency; runs as console app or Windows service

---

## 🚧 Parallel Work (Layer 2 — Docker Stack)

In a separate effort (Apr 29), we also built a **Docker-based OT/IT Data Bridge** with 5 components:
- OPC UA Collector → EMQX Message Bus → Tag Mapping Service → TimescaleDB → Consumer REST API

This is a different architecture path (OPC UA + MQTT + TimescaleDB) vs the current project (OPC HDA + COM + REST). Both serve the same goal: making OT historian data accessible to IT tools.

---

## ✅ Current Status

The OPC HDA Broker is **fully operational**:
- ✅ Server connection to KepServerEX 6.6.350
- ✅ Tag discovery (21 tags via TSD auto-discovery)
- ✅ Raw, Latest, and Processed data reads
- ✅ Grafana dashboard live with 10 panels, 30s auto-refresh
- ✅ Power BI integration documented and tested
- ✅ UTC timestamps normalized across all endpoints
- ✅ Comprehensive documentation and deployment scripts
