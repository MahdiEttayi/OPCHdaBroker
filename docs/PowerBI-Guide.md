# Power BI Integration Guide

Connect **Power BI Desktop** to the OPC HDA Broker to visualize historian data from KepServerEX.

## How It Works

```
Power BI Desktop                OPC HDA Broker              KepServerEX
     │                              │                           │
     │  Power Query (M)             │                           │
     │  Web.Contents(url)           │                           │
     │─────────────────────────────►│                           │
     │                              │  COM ReadRaw()            │
     │                              │──────────────────────────►│
     │                              │◄──────────────────────────│
     │                              │                           │
     │  JSON response               │                           │
     │  { data: [{t,v,q}] }        │                           │
     │◄─────────────────────────────│                           │
     │                              │                           │
     │  Expand to table             │                           │
     │  Plot as chart               │                           │
```

Power BI uses **Power Query (M language)** to call the broker's REST API, parse the JSON response, and load it into a dataset. No plugins needed — just the built-in `Web.Contents` function.

---

## Prerequisites

- **Power BI Desktop** installed (free download from Microsoft)
- **OPC HDA Broker** running on `http://localhost:5000`
- Broker and Power BI on the **same machine** (or replace `localhost` with the broker's IP)

---

## Step 1: Connect to a Single Tag (Quick Start)

1. Open **Power BI Desktop**
2. Click **Get Data** → **Web**
3. Select **Advanced** and enter:

```
http://localhost:5000/api/read/points?tag=Simulations.Simulator%201.TAG_1&from=2026-04-30T00:00:00Z&to=2026-05-03T23:59:59Z&maxValues=10000
```

4. Click **OK** → Power BI will show the JSON structure
5. Click **Into Table** → **Expand columns** → you'll see `t`, `v`, `q` columns
6. Set column types:
   - `t` → **Date/Time/Timezone** (it's ISO 8601 with Z)
   - `v` → **Decimal Number**
   - `q` → **Text**
7. Click **Close & Apply**
8. Create a **Line Chart**: X-axis = `t`, Y-axis = `v`

---

## Step 2: Power Query with Dynamic Date Range

For a more practical setup, use Power Query M code directly. This lets you control the time range with parameters.

### Create the Query

1. **Get Data** → **Blank Query**
2. Click **Advanced Editor** and paste:

```m
let
    // ── Configuration ──────────────────────────────────────
    BrokerUrl = "http://localhost:5000",
    TagPath   = "Simulations.Simulator 1.TAG_1",
    FromDate  = DateTime.ToText(DateTime.LocalNow() - #duration(7,0,0,0), "yyyy-MM-ddTHH:mm:ssZ"),
    ToDate    = DateTime.ToText(DateTime.LocalNow(), "yyyy-MM-ddTHH:mm:ssZ"),
    MaxValues = "10000",

    // ── Build URL ──────────────────────────────────────────
    Url = BrokerUrl & "/api/read/points"
        & "?tag=" & Uri.EscapeDataString(TagPath)
        & "&from=" & FromDate
        & "&to=" & ToDate
        & "&maxValues=" & MaxValues,

    // ── Fetch JSON ─────────────────────────────────────────
    Source = Json.Document(Web.Contents(Url)),
    Data   = Source[data],
    Table  = Table.FromList(Data, Splitter.SplitByNothing(), null, null, ExtraValues.Error),

    // ── Expand columns ─────────────────────────────────────
    Expanded = Table.ExpandRecordColumn(Table, "Column1", {"t", "v", "q"}, {"Timestamp", "Value", "Quality"}),

    // ── Set types ──────────────────────────────────────────
    Typed = Table.TransformColumnTypes(Expanded, {
        {"Timestamp", type datetimezone},
        {"Value", type number},
        {"Quality", type text}
    })
in
    Typed
```

3. Click **Done** → Power BI loads the last 7 days of TAG_1 data
4. Rename the query to `TAG_1`

### What Each Part Does

| Line | Purpose |
|---|---|
| `BrokerUrl` | Base URL of the broker — change if not on localhost |
| `TagPath` | The KepServerEX tag path (Channel.Device.Tag format) |
| `FromDate` | 7 days ago in ISO 8601 — adjust `#duration(7,0,0,0)` for other ranges |
| `Web.Contents(Url)` | HTTP GET to the broker API |
| `Source[data]` | Extracts the `data` array from `{data: [...], meta: {...}}` |
| `Table.ExpandRecordColumn` | Flattens `{t, v, q}` objects into table columns |
| `type datetimezone` | Tells Power BI the timestamp is timezone-aware (UTC with Z) |

---

## Step 3: Multiple Tags

Duplicate the query for each tag, or use a function:

### Option A: Duplicate Query

1. Right-click `TAG_1` query → **Duplicate**
2. Open **Advanced Editor**
3. Change `TagPath` to `"Simulations.Simulator 1.TAG_2"`
4. Rename query to `TAG_2`
5. Repeat for TAG_3, TAG_4, etc.

### Option B: Parameterized Function (Recommended)

Create a reusable function that any tag can call.

1. **New Blank Query** → **Advanced Editor**:

```m
// Name this query: fnGetTagData
(TagPath as text, DaysBack as number) as table =>
let
    BrokerUrl = "http://localhost:5000",
    FromDate  = DateTime.ToText(DateTime.LocalNow() - #duration(DaysBack,0,0,0), "yyyy-MM-ddTHH:mm:ssZ"),
    ToDate    = DateTime.ToText(DateTime.LocalNow(), "yyyy-MM-ddTHH:mm:ssZ"),

    Url = BrokerUrl & "/api/read/points"
        & "?tag=" & Uri.EscapeDataString(TagPath)
        & "&from=" & FromDate & "&to=" & ToDate
        & "&maxValues=50000",

    Source    = Json.Document(Web.Contents(Url)),
    Data      = Source[data],
    Table     = Table.FromList(Data, Splitter.SplitByNothing(), null, null, ExtraValues.Error),
    Expanded  = Table.ExpandRecordColumn(Table, "Column1", {"t", "v", "q"}, {"Timestamp", "Value", "Quality"}),
    Typed     = Table.TransformColumnTypes(Expanded, {
        {"Timestamp", type datetimezone},
        {"Value", type number},
        {"Quality", type text}
    })
in
    Typed
```

2. Name it `fnGetTagData`
3. Now create tag queries that call it:

```m
// Query: TAG_1
let Source = fnGetTagData("Simulations.Simulator 1.TAG_1", 7) in Source
```

```m
// Query: TAG_2
let Source = fnGetTagData("Simulations.Simulator 1.TAG_2", 7) in Source
```

```m
// Query: TAG_3
let Source = fnGetTagData("Simulations.Simulator 1.TAG_3", 7) in Source
```

---

## Step 4: Latest Values Table

For a summary table showing the most recent value of every tag:

```m
let
    BrokerUrl = "http://localhost:5000",
    Tags = "Simulations.Simulator 1.TAG_1,Simulations.Simulator 1.TAG_2,Simulations.Simulator 1.TAG_3,Simulations.Simulator 1.TAG_4,Simulations.Simulator 1.TAG_5",

    Url = BrokerUrl & "/api/read/latest/table"
        & "?tags=" & Uri.EscapeDataString(Tags)
        & "&lookbackMinutes=360",

    Source   = Json.Document(Web.Contents(Url)),
    Data     = Source[data],
    Table    = Table.FromList(Data, Splitter.SplitByNothing(), null, null, ExtraValues.Error),
    Expanded = Table.ExpandRecordColumn(Table, "Column1",
        {"tag", "value", "timestamp", "quality"},
        {"Tag", "Value", "Timestamp", "Quality"}),
    Typed    = Table.TransformColumnTypes(Expanded, {
        {"Tag", type text},
        {"Value", type number},
        {"Timestamp", type datetimezone},
        {"Quality", type text}
    })
in
    Typed
```

---

## Step 5: Server Status Card

Show broker health on your dashboard:

```m
let
    Source   = Json.Document(Web.Contents("http://localhost:5000/api/status")),
    AsTable = Record.ToTable(Source),
    Pivoted = Table.Pivot(AsTable, List.Distinct(AsTable[Name]), "Name", "Value")
in
    Pivoted
```

This gives you a single-row table with columns: `serverStatus`, `serverVersion`, `tagCount`, `brokerUptime`, etc. Use **Card** visuals to display them.

---

## Step 6: Scheduled Refresh

Power BI Desktop queries are **manual refresh** (click Refresh button). For automatic refresh:

### Option A: Power BI Service (Cloud)
1. Publish the report to **Power BI Service** (app.powerbi.com)
2. Install **On-premises Data Gateway** on the broker machine
3. Configure a scheduled refresh (every 15 min, hourly, etc.)
4. The gateway calls `localhost:5000` on your behalf

### Option B: Power BI Desktop Auto-Refresh
1. In Power BI Desktop: **File** → **Options** → **Data Load**
2. Set **Page refresh interval** (minimum 30 seconds for DirectQuery)
3. Note: only works with DirectQuery, not Import mode

---

## Troubleshooting

| Problem | Fix |
|---|---|
| "Access to the resource is forbidden" | In Power BI: **File → Options → Security** → set Privacy to "Ignore" |
| "Unable to connect" | Verify broker is running: `curl http://localhost:5000/api/health` |
| Timestamps shifted by 1 hour | Ensure you're using the latest broker build (timestamp fix applied) |
| Empty results | Check the time range — data may not exist in that window |
| "Formula.Firewall" error | **File → Options → Privacy** → "Ignore Privacy Levels" |
| Slow queries (>10s) | Reduce `maxValues` or narrow the time range |

---

## API Endpoints Used by Power BI

| Endpoint | Power BI Use Case |
|---|---|
| `/api/read/points?tag=...&from=...&to=...` | Time-series line charts (single tag) |
| `/api/read/latest/table?tags=...` | Summary table with latest values |
| `/api/status` | Dashboard status cards |
| `/api/tags` | Dynamic tag picker (advanced) |
| `/api/read/raw?tags=...&from=...&to=...` | Multi-tag raw data (nested JSON, more complex M code) |

---

## Example Dashboard Layout

```
┌──────────────────────────────────────────────────────────┐
│  ┌──────────┐  ┌──────────┐  ┌──────────┐  ┌──────────┐ │
│  │ Status   │  │ Version  │  │ Tags: 21 │  │ Uptime   │ │
│  │ Online   │  │ 6.6.350  │  │          │  │ 2.03:15  │ │
│  └──────────┘  └──────────┘  └──────────┘  └──────────┘ │
│                                                          │
│  ┌────────────────────────┐  ┌────────────────────────┐  │
│  │  TAG_1 — Line Chart    │  │  TAG_2 — Line Chart    │  │
│  │  ▁▂▃▅▆▇█▇▆▅▃▂▁        │  │  ▁▃▅▇█▇▅▃▁            │  │
│  └────────────────────────┘  └────────────────────────┘  │
│                                                          │
│  ┌──────────────────────────────────────────────────────┐ │
│  │  Latest Values — Table                              │ │
│  │  Tag                      │ Value │ Timestamp       │ │
│  │  Simulator 1.TAG_1        │  50   │ 2026-05-03 ... │ │
│  │  Simulator 1.TAG_2        │  80   │ 2026-05-03 ... │ │
│  └──────────────────────────────────────────────────────┘ │
└──────────────────────────────────────────────────────────┘
```

Use **Card** visuals for the status row, **Line Chart** for trends, and **Table** for the latest values summary.
