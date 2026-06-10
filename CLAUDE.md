# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

OPC HDA Broker is a self-hosted REST API that bridges HTTP clients (Grafana, Power BI) to KepServerEX's Local Historian via OPC HDA COM. It runs on .NET Framework 4.7.2 (x86) and uses OWIN self-host.

## Build and Run Commands

```powershell
# Build (requires .NET Framework 4.7.2 SDK, x86)
cd src/OpcHdaBroker
dotnet build

# Run in console mode
dotnet run

# The broker listens on http://localhost:5000 by default
# (use http://+:5000 in App.config when running as a Windows Service)
```

## Key Dependencies

- **OpcClientSdk472.dll** (in `lib/`) — Technosoftware SDK wrapping OPC HDA COM. All HDA calls go through this SDK, not raw COM QI.
- **Microsoft.AspNet.WebApi.OwinSelfHost** — Self-hosted WebAPI on OWIN.
- **Serilog** — Structured logging to console and file.
- **Npgsql** — PostgreSQL/TimescaleDB connectivity for data export.

## Architecture

### Request Flow

```
HTTP Request → OWIN Pipeline → API Controller → BrokerEngine → StaThreadDispatcher → HdaReader/HdaBrowser → OPC HDA COM → KepServerEX
```

### Threading Model (Critical)

All COM calls MUST run on a single MTA thread. `StaThreadDispatcher` owns this thread and exposes `InvokeAsync<T>()` for fire-and-forget async dispatch. API controllers call `BrokerEngine` methods which all route through the dispatcher. Never call HdaConnection/HdaReader/HdaBrowser directly from a controller.

### Core Classes

| Class | Responsibility |
|---|---|
| `BrokerEngine` | Singleton orchestrator — owns connection, browser, reader, cache. All public methods dispatch to the STA thread. |
| `HdaConnection` | Manages OPC HDA server connection lifecycle (Connect/Reconnect/Disconnect). |
| `HdaBrowser` | Tag discovery via 3-tier strategy: SDK browser → TSD file reader → tags.txt fallback. |
| `HdaReader` | ReadRaw, ReadLatest (ReadRaw with maxValues=1), ReadProcessed (server-side aggregation). |

### COM SDK Wrapper Pattern

The SDK (`OpcClientSdk472.dll`) wraps all raw OPC HDA COM interop. Usage pattern:
```csharp
var trend = new TsCHdaTrend(_connection.Server) {
    StartTime = new TsCHdaTime(startTime.ToUniversalTime()),
    EndTime   = new TsCHdaTime(endTime.ToUniversalTime()),
    MaxValues = maxValues
};
trend.AddItem(new OpcItem(tagPath));
var results = trend.ReadRaw(items);
```

### Tag Discovery (HdaBrowser.DiscoverAllTags)

1. Try `CreateBrowser()` → recursive namespace walk
2. Fall back to reading `.name` files in the TSD datastore path from App.config
3. Merge with entries in `tags.txt`

## Timestamp Handling

All SDK timestamps have `Kind=Unspecified`. The broker treats them as UTC by calling `DateTime.SpecifyKind(ts, DateTimeKind.Utc)` at read time. This is critical — without it, the JSON serializer produces non-Z timestamps causing Grafana to mis-interpret data. Never call `.ToUniversalTime()` on raw SDK timestamps; it would double-convert UTC+1 host times.

## Grafana Infinity Integration

The Infinity plugin cannot use array-index notation in `root_selector` (e.g. `data[0].points` silently returns empty). Two flat endpoints exist to work around this:

- `/api/read/points?tag=...` → returns `[{t, v, q}]` directly
- `/api/read/latest/table?tags=...` → returns `[{tag, value, timestamp, quality}]` directly

Use these instead of the nested `/api/read/raw` and `/api/read/latest` endpoints when connecting via Infinity.

## Configuration (App.config)

| Key | Purpose |
|---|---|
| `Hda.PrimaryUrl` | OPC HDA server URL |
| `Hda.FallbackUrl` | Fallback URL for reconnect |
| `Hda.TsdDataPath` | Path to KepServerEX TSD datastore for tag discovery |
| `Api.BaseUrl` | HTTP listen address (localhost for console, `+` for service) |
| `Cache.TagListTtlSec` | How long to cache the tag list |
| `Tsdb.ConnectionString` | PostgreSQL/TimescaleDB connection string |
| `Ingestion.BrokerId` | Unique identifier for this broker instance |
| `Backfill.*` | Backfill configuration (StartDate, EndDate, ChunkDays, AutoStart) |

## TimescaleDB Export

The broker can export historical HDA data to TimescaleDB for long-term storage, SQL queries, and Grafana native datasource access.

### New Components

| Class | Responsibility |
|---|---|
| `TimescaleDb/TsdbRepository` | Manages schema creation (hypertable), bulk upsert via COPY, connection pooling. |
| `TimescaleDb/BackfillService` | Reads historical ranges from KepServerEX in configurable chunks and writes to TimescaleDB. Supports pause/resume/stop. |
| `Api/Controllers/TsdbController` | REST endpoints for TimescaleDB management. |

### TimescaleDB Schema

The `hda_data` hypertable is auto-created on first connect:

```sql
CREATE TABLE hda_data (
    time        TIMESTAMPTZ NOT NULL,
    tag         TEXT        NOT NULL,
    value       DOUBLE PRECISION,
    value_text  TEXT,
    quality     TEXT,
    broker_id   TEXT,
    value_type  TEXT,
    PRIMARY KEY (time, tag)
);
-- Converted to hypertable with 1-day chunks by TsdbRepository.EnsureSchema()
-- Index: (tag, time DESC)
```

Data is deduplicated by `(time, tag)` using `INSERT ... ON CONFLICT DO NOTHING`.

### Backfill API Endpoints

| Method | Endpoint | Description |
|---|---|---|
| `GET` | `/api/timescaledb/status` | Overall TimescaleDB status |
| `GET` | `/api/timescaledb/backfill/status` | Backfill progress |
| `POST` | `/api/timescaledb/backfill/start` | Start backfill |
| `POST` | `/api/timescaledb/backfill/pause` | Pause backfill |
| `POST` | `/api/timescaledb/backfill/resume` | Resume paused backfill |
| `POST` | `/api/timescaledb/backfill/stop` | Stop backfill |
| `POST` | `/api/timescaledb/backfill/range` | Run backfill on custom date range (blocking) |
| `DELETE` | `/api/timescaledb/data?confirm=true` | Truncate all data |

### Backfill Threading

Backfill runs on the calling thread (HTTP thread for the range endpoint, a `ThreadPool` thread for `StartAsync`). It dispatches HDA `ReadRaw` calls through `StaThreadDispatcher` for COM thread affinity. DB writes happen on the calling thread — no COM involvement.

## Known Limitations

- SDK browser may fail with `E_INVALIDARG` at deep browse levels. TSD file reading compensates.
- Tags use dotted `Channel.Device.Tag` notation.
- Grafana Infinity requires `url_options.method` to be set explicitly.
