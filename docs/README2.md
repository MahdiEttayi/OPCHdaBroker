# OPC HDA Broker — How It Works

## The Short Answer

> **The broker stores nothing.** It is a stateless translator.  
> **All your historical data in the `.TSD` files is accessible** — including data recorded weeks, months, or years before the broker was written.  
> You can point Grafana at any time range and see that old data.

---

## Where Does the Data Actually Live?

```
C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data\
├── Simulations/
│   └── Simulator 1/
│       ├── Simulations.Simulator 1.Active     ← currently being written to
│       ├── Simulations.Simulator 1.001.TSD    ← sealed historical archive
│       ├── Simulations.Simulator 1.002.TSD    ← older archive
│       └── Simulations.Simulator 1.name       ← tag name index (metadata)
└── Data Type Examples/
    └── ...
```

**KepServerEX** is the one that writes this data. Every ~3 seconds it samples the configured tags and appends `{timestamp, value, quality}` records into the `.Active` file. When that file reaches a size threshold, KepServerEX seals it as a `.TSD` file and starts a new `.Active` file.

**The broker never writes to these files.** It only reads — and it reads indirectly, through KepServerEX.

---

## Two Separate Processes: Discovery vs. Data Retrieval

This is the most important distinction to understand:

### 1. Tag Discovery — "What tags exist?"

```
Broker                                  Filesystem
  │                                        │
  │  reads .name files directly            │
  │  (no KepServerEX involved)             │
  ├───────────────────────────────────────►│
  │                                        │
  │  Finds: "Simulations.Simulator 1.TAG_1"│
  │         "Simulations.Simulator 1.TAG_2"│
  │         ... (9 tags total)              │
  │◄───────────────────────────────────────┤
  │                                        │
  │  Stores in memory (tag list cache)     │
  │  TTL: 60 seconds                       │
```

The broker reads the `.name` files in the TSD datastore directory to discover what tag paths exist. These `.name` files are small metadata files — they contain only the tag path strings, not the actual data values.

**This is the only place the broker touches the filesystem directly.**

It does NOT read the `.TSD` or `.Active` files. Those are in a proprietary binary format that only KepServerEX understands.

### 2. Data Retrieval — "What are the values for TAG_1 between 8am and 9am?"

```
Grafana                  Broker                    KepServerEX              TSD Files
  │                        │                          │                        │
  │  GET /api/read/points  │                          │                        │
  │  ?tag=TAG_1            │                          │                        │
  │  &from=08:00           │                          │                        │
  │  &to=09:00             │                          │                        │
  │───────────────────────►│                          │                        │
  │                        │                          │                        │
  │                        │  COM: ReadRaw(           │                        │
  │                        │    "TAG_1",              │                        │
  │                        │    08:00, 09:00)         │                        │
  │                        │─────────────────────────►│                        │
  │                        │                          │                        │
  │                        │                          │  reads binary data     │
  │                        │                          │  from .TSD + .Active   │
  │                        │                          │────────────────────────►│
  │                        │                          │◄────────────────────────│
  │                        │                          │                        │
  │                        │  COM result:             │                        │
  │                        │  [{ts, val, quality}]    │                        │
  │                        │◄─────────────────────────│                        │
  │                        │                          │                        │
  │                        │  Normalize to UTC        │                        │
  │                        │  Format as JSON          │                        │
  │                        │                          │                        │
  │  { "data": [           │                          │                        │
  │    {"t":"...Z",         │                          │                        │
  │     "v":50, "q":"Good"} │                         │                        │
  │  ]}                    │                          │                        │
  │◄───────────────────────│                          │                        │
```

**The actual data retrieval chain is:**

1. **Grafana** sends an HTTP request to the broker
2. **Broker** translates it into an OPC HDA COM call (`TsCHdaTrend.ReadRaw()`)
3. **KepServerEX** receives the COM call and reads from its own `.TSD` / `.Active` files
4. **KepServerEX** returns the data points back through COM
5. **Broker** normalizes timestamps to UTC, formats as JSON, returns to Grafana

The broker is a **translator** — it converts HTTP/JSON into COM/DCOM and back. It never caches or stores the historian data itself.

---

## Can I See Data From Before the Broker Existed?

**Yes, absolutely.** Here's why:

```
Timeline:
─────────────────────────────────────────────────────────────►
2024          2025          2026-Apr      2026-May
 │             │              │             │
 │  KepServerEX logging      │             │
 │  TAG_1, TAG_2 every 3s    │  Broker     │  You are here
 │  into .TSD files          │  created    │
 │             │              │             │
 └─────────── ALL THIS DATA ──────────────►│
               IS IN THE TSD FILES         │
               AND FULLY ACCESSIBLE        │
```

When you ask Grafana to show `TAG_1` from January 2025, this happens:

1. Grafana sends: `GET /api/read/points?tag=TAG_1&from=2025-01-01T00:00:00Z&to=2025-01-31T23:59:59Z`
2. Broker sends COM call: `ReadRaw("TAG_1", Jan 1 2025, Jan 31 2025)`
3. KepServerEX opens the `.TSD` files from that period, reads the stored values
4. Data flows back through COM → broker → JSON → Grafana chart

**The broker doesn't need to have been running when the data was recorded.** The data lives permanently in the TSD files. The broker just provides a window into it.

### Try it yourself

```powershell
# Read data from April 2026 — before the broker was finalized
$tag = "Simulations.Simulator 1.TAG_1"
$from = "2026-04-28T00:00:00Z"
$to   = "2026-04-28T23:59:59Z"
$uri  = "http://localhost:5000/api/read/points?tag=$([uri]::EscapeDataString($tag))&from=$from&to=$to&maxValues=10"
Invoke-RestMethod $uri | ConvertTo-Json -Depth 5
```

If KepServerEX was logging TAG_1 on April 28, you'll see those values — even though the broker wasn't running that day.

---

## What the Broker Does NOT Do

| Action | Does the broker do this? |
|---|---|
| Write data to TSD files | ❌ No — only KepServerEX writes |
| Cache historical data on disk | ❌ No — every request queries KepServerEX live |
| Store data in a database | ❌ No — it is fully stateless |
| Read TSD binary files directly | ❌ No — only KepServerEX can parse the proprietary format |
| Read `.name` metadata files | ✅ Yes — for tag discovery only |
| Need to be running to record data | ❌ No — KepServerEX records independently |

## What the Broker DOES Do

| Action | How |
|---|---|
| Discover tag names | Reads `.name` files from the TSD datastore directory |
| Translate HTTP → COM | Converts REST requests into `TsCHdaTrend.ReadRaw()` calls |
| Normalize timestamps | Converts `Kind=Unspecified` to `Kind=Utc`, appends `Z` |
| Flatten data for Grafana | `/api/read/points` returns `[{t,v,q}]` instead of nested structures |
| Provide server status | Calls `GetServerStatus()` via COM, returns as JSON |

---

## Internal Process — Step by Step

### Startup Sequence

```
1. Program.cs
   └── Starts OWIN self-hosted WebAPI on port 5000
   └── Calls BrokerEngine.InitializeAsync()

2. BrokerEngine.InitializeAsync()
   └── Creates StaThreadDispatcher (dedicated MTA COM thread)
   └── Calls HdaConnection.ConnectAsync()
   └── Calls HdaBrowser.DiscoverTagsAsync()

3. HdaConnection.ConnectAsync()     [runs on MTA thread]
   └── new TsCHdaServer(url)
   └── server.Connect()             → COM connection to KepServerEX
   └── Stores server reference for all future calls

4. HdaBrowser.DiscoverTagsAsync()   [runs on MTA thread]
   ├── Tier 1: SDK Browse
   │   └── server.CreateBrowser()
   │   └── browser.Browse() at depth 0, 1, 2...
   │   └── Fails at depth 2 (E_INVALIDARG) — KepServerEX limitation
   │   └── Result: 0 tags from SDK
   │
   ├── Tier 2: TSD Auto-Discovery (primary method)
   │   └── Scans C:\ProgramData\Kepware\...\Historical Data\
   │   └── Finds *.name files
   │   └── Reads tag paths from each .name file
   │   └── Result: 9 tags discovered
   │
   └── Tier 3: tags.txt (if exists)
       └── Reads manual tag paths from config file
       └── Adds any not already discovered

5. REST API is now ready
   └── "API ready at http://localhost:5000"
```

### Request Processing

```
HTTP GET /api/read/points?tag=Simulations.Simulator 1.TAG_1&from=...&to=...
  │
  ▼
ReadController.ReadPoints()
  │  Validates parameters
  │  
  ▼
BrokerEngine.ReadRawAsync(tags, from, to, maxValues)
  │  Queues work onto the MTA COM thread
  │
  ▼
StaThreadDispatcher.InvokeAsync(action)
  │  Ensures COM thread affinity
  │  (KepServerEX COM objects must be used from
  │   the same thread that created them)
  │
  ▼
HdaReader.ReadRaw()           [on MTA thread]
  │  Creates TsCHdaTrend
  │  Sets StartTime, EndTime, MaxValues
  │  Calls trend.ReadRaw()     → COM call to KepServerEX
  │  KepServerEX reads from TSD files
  │  Returns TsCHdaItemValueCollection
  │
  ▼
ReadController.ReadPoints()    [back on HTTP thread]
  │  For each data point:
  │    DateTime.SpecifyKind(ts, DateTimeKind.Utc)
  │    .ToString("o")  → "2026-05-03T19:00:02.4070000Z"
  │  Wraps in { data: [...], meta: {...} }
  │
  ▼
HTTP 200 OK  →  JSON response to Grafana
```

### The COM Threading Model

```
┌─────────────────────────────────────────────┐
│  HTTP Thread Pool (ASP.NET / OWIN)          │
│                                             │
│  Request 1 ─┐                               │
│  Request 2 ─┤   queue                       │
│  Request 3 ─┘     │                         │
│                    ▼                         │
│  ┌─────────────────────────────────────┐    │
│  │  StaThreadDispatcher                │    │
│  │  (single dedicated MTA thread)      │    │
│  │                                     │    │
│  │  Processes COM calls sequentially   │    │
│  │  to maintain thread affinity for    │    │
│  │  KepServerEX COM objects            │    │
│  └─────────────────────────────────────┘    │
│                    │                         │
│                    ▼                         │
│  COM/DCOM call to KepServerEX               │
└─────────────────────────────────────────────┘
```

All OPC HDA COM objects (server connection, trends, browsers) are created on this single MTA thread. Every data request is queued and executed on this thread. This is required because COM objects have thread affinity — they can only be called from the apartment (thread) that created them.

---

## Data Retention

The broker has **no influence on data retention**. That is entirely controlled by KepServerEX's Local Historian configuration:

- **Datastore size limits** — configured in KepServerEX (e.g. max 100 TSD files per datastore)
- **Time-based rollover** — KepServerEX seals `.Active` into `.TSD` based on time or size thresholds
- **Purge policy** — KepServerEX can be configured to delete oldest TSD files when limits are reached

If KepServerEX has been logging for a year and hasn't purged old TSD files, you can query all 12 months of data through the broker.

---

## Summary

```
┌──────────────┐     writes      ┌──────────────┐
│  Simulated   │────────────────►│  .TSD files  │  ← Permanent storage
│  PLC Tags    │   every ~3s     │  .Active     │     (managed by KepServerEX)
└──────────────┘                 └──────┬───────┘
                                       │
                              reads    │  reads binary data
                              .name    │  via COM/HDA
                              files    │
                                │      │
                         ┌──────▼──────▼───────┐
                         │  OPC HDA Broker     │  ← Stateless translator
                         │  (localhost:5000)    │     (stores nothing)
                         └──────────┬──────────┘
                                    │ HTTP/JSON
                         ┌──────────▼──────────┐
                         │  Grafana            │  ← Visualization
                         │  (localhost:3000)    │     (renders charts)
                         └─────────────────────┘

The broker is a WINDOW into your historian data.
It doesn't record, store, or own any data.
All data lives in the TSD files, accessible at any time range.
```
