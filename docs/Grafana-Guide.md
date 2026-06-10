# Grafana Integration Guide

Connect **Grafana** to the OPC HDA Broker to visualize real-time and historical KepServerEX tag data.

> **Prerequisites**: OPC HDA Broker running on `http://localhost:5000` (or reachable from Grafana).

---

## Quick Start (Import Pre-Built Dashboard)

If you already have Grafana with the Infinity plugin installed:

1. Open Grafana at `http://localhost:3000`
2. Go to **Dashboards** → **Import** → **Upload JSON file**
3. Select `deploy/grafana-dashboard.json` from the broker package
4. Click **Import** — you'll see status cards, 5 time-series charts, and a latest-values table

Skip to [Troubleshooting](#troubleshooting) if anything doesn't load.

---

## Step 1 — Install Grafana

```powershell
winget install GrafanaLabs.Grafana.OSS
```

Or download the Windows installer from [grafana.com/grafana/download](https://grafana.com/grafana/download?pg=oss-graf&plcmt=hero-btn-1).

After install, open `http://localhost:3000` and log in with `admin` / `admin`.

---

## Step 2 — Install the Infinity Datasource Plugin

The Infinity plugin lets Grafana fetch JSON from any REST API — it's how Grafana talks to the broker.

```powershell
# Create a plugins directory (any path works)
mkdir C:\Users\$env:USERNAME\grafana-plugins

# Install the Infinity plugin
& "C:\Program Files\GrafanaLabs\grafana\bin\grafana.exe" cli `
    --pluginsDir "C:\Users\$env:USERNAME\grafana-plugins" `
    plugins install yesoreyeram-infinity-datasource
```

### Configure Grafana to Use the Plugin

Create or edit `C:\Program Files\GrafanaLabs\grafana\conf\custom.ini`:

```ini
[paths]
plugins = C:\Users\Admin\grafano-plugins

[plugins]
allow_loading_unsigned_plugins = yesoreyeram-infinity-datasource
```

Restart Grafana:

```powershell
net stop grafana; net start grafana
```

---

## Step 3 — Add the Infinity Datasource

1. In Grafana, go to **Connections** → **Data Sources** → **Add data source**
2. Search for **Infinity** and select it
3. Name it `OPC HDA Broker`
4. Leave all defaults — the datasource URL is set per-panel, not globally
5. Click **Save & Test**

> The Infinity plugin passes the datasource UID to each panel. Every panel
> configures its own URL, parser, and columns — the datasource is just a
> container.

---

## Step 4 — Panel Types & Endpoints

The broker exposes Grafana-optimized endpoints that work cleanly with the
Infinity plugin. The key rule: **never use array-index notation in
`root_selector`**. Infinity silently returns empty for `data[0].points`.
The broker's flat endpoints avoid this.

### Stat Panel — Broker Status

Show a single value like connection status, tag count, or uptime.

**Query configuration:**

| Field | Value |
|---|---|
| `source` | `URL` |
| `URL` | `http://localhost:5000/api/status/list` |
| `Parser` | `Backend` |
| `Format` | `DataFrame` |
| `Root selector` | *(leave empty)* |

**Columns** (one per stat panel):

| Column | Selector | Type |
|---|---|---|
| Status | `serverStatus` | `string` |
| Version | `serverVersion` | `string` |
| Tags | `tagCount` | `number` |
| Uptime | `brokerUptime` | `string` |

### Time Series Panel — Historical Tag Data

Plot tag values over a selected time range.

**Query configuration:**

| Field | Value |
|---|---|
| `source` | `URL` |
| `URL` | `http://localhost:5000/api/read/points?tag=Channel.Device.TagName&from=${__from:date:iso}&to=${__to:date:iso}&maxValues=5000` |
| `Parser` | `Backend` |
| `Format` | `Time series` |
| `Root selector` | `data` |

**Columns:**

| Column | Selector | Type |
|---|---|---|
| Time | `t` | `timestamp` |
| Value | `v` | `number` |

> The `__from` / `__to` variables are Grafana's built-in time range placeholders.
> They automatically use the dashboard's selected time range.
> `url_options.method` must be set to `GET` explicitly in the Infinity query editor.

### Table Panel — Latest Values

Show the most recent value for multiple tags in a table.

**Query configuration:**

| Field | Value |
|---|---|
| `source` | `URL` |
| `URL` | `http://localhost:5000/api/read/latest/table?tags=Channel.Device.Tag1,Channel.Device.Tag2&lookbackMinutes=360` |
| `Parser` | `Backend` |
| `Format` | `DataFrame` |
| `Root selector` | `data` |

**Columns:**

| Column | Selector | Type |
|---|---|---|
| Tag | `tag` | `string` |
| Value | `value` | `number` |
| Timestamp | `timestamp` | `timestamp` |
| Quality | `quality` | `string` |

### Time Series Panel — Aggregated Data

Server-side aggregation (average, min, max, etc.) via the OPC HDA `ReadProcessed` API.

**Query configuration:**

| Field | Value |
|---|---|
| `source` | `URL` |
| `URL` | `http://localhost:5000/api/read/processed?tags=Channel.Device.TagName&from=${__from:date:iso}&to=${__to:date:iso}&aggregate=average&intervalSec=3600` |
| `Parser` | `Backend` |
| `Format` | `Time series` |
| `Root selector` | `data` |

**Columns:** same as time series (`t` as timestamp, `v` as number).

Available aggregate functions: `interpolative`, `average`, `total`, `min`, `max`, `count`, `stdev`, `range`, `start`, `end`, `delta`.

---

## Step 5 — Creating a Panel Step by Step

1. **Dashboard** → **New** → **Add panel**
2. Select the **Infinity** datasource
3. Set **Source** to `URL`
4. Paste the broker endpoint URL (e.g. `/api/read/points?tag=...`)
5. Set **Parser** to `Backend`
6. Set **Format** to `Time series` (charts) or `DataFrame` (stats/tables)
7. Set **Root selector** to `data`
8. Under **Columns**, add the fields you need (see tables above)
9. Click **Run query** — data should appear
10. Save the panel

### Important: Set `url_options.method` to `GET`

In the Infinity query editor, expand **URL options** and make sure **Method** is
set to **GET**. If left blank, Infinity defaults to POST and the endpoint won't
respond.

---

## Step 6 — Using Grafana Variables for Tag Selection

Instead of hard-coding tag paths, create a variable so users can pick tags dynamically.

1. Dashboard **Settings** → **Variables** → **Add variable**
2. Set:
   - **Name**: `tag`
   - **Type**: `Query`
   - **Data source**: Infinity
   - **Query**: `http://localhost:5000/api/tags`
   - **Parser**: `Backend`
   - **Root selector**: `data`
   - **Value selector**: `itemId`
3. Click **Run** — the dropdown will populate with all tags

Use the variable in panel URLs: `/api/read/points?tag=${tag}&from=...`

---

## Pre-Built Dashboard Structure

The included `deploy/grafana-dashboard.json` creates this layout:

```
┌──────────┬──────────┬──────────┬──────────┐
│  Status  │  Version │  Tags    │  Uptime  │
│  (stat)  │  (stat)  │  (stat)  │  (stat)  │
├──────────┴──────────┼──────────┴──────────┤
│   TAG_1 (timeseries)│   TAG_2 (timeseries)│
├──────────┬──────────┼──────────┬──────────┤
│ TAG_3    │ TAG_4    │ TAG_5    │
│ (ts)     │ (ts)     │ (ts)     │
├──────────┴──────────┴──────────┴──────────┤
│  Latest Values (table)                     │
└────────────────────────────────────────────┘
```

All panels use the `Simulations.Simulator 1.TAG_X` tags from KepServerEX's
Simulator driver. Replace the tag paths with your own.

---

## API Endpoint Reference

| Endpoint | Panel Type | Notes |
|---|---|---|
| `/api/status/list` | Stat | Returns array so Infinity column selectors work |
| `/api/read/points?tag=...&from=...&to=...` | Time series | Flat `[{t, v, q}]` — one tag at a time |
| `/api/read/latest/table?tags=...` | Table | Flat `[{tag, value, timestamp, quality}]` rows |
| `/api/read/raw?tags=...&from=...&to=...` | *(advanced)* | Nested per-tag JSON, harder to parse |
| `/api/read/processed?tags=...&from=...&to=...` | Time series | Server-side aggregation |
| `/api/read/latest/points?tag=...` | Stat/Gauge | Single tag latest value |
| `/api/tags` | Variable query | Tag list for dashboard variables |
| `/api/health` | Alerting | Simple liveness check |

---

## Troubleshooting

### Panel shows no data / empty response

| Cause | Fix |
|---|---|
| Missing `root_selector` | Set to `data` — the points are nested under `{ data: [...], meta: {...} }` |
| `url_options.method` not set | Expand **URL options** and set **Method** to `GET` |
| Wrong tag path | Check exact paths via `http://localhost:5000/api/tags` |
| Time range has no data | Widen the dashboard time range or check KepServerEX historian |
| Infinity plugin version | Use Infinity v2 or v3 (both work); v1 may need different config |

### Stat panel shows nothing

Use the `/api/status/list` endpoint (not `/api/status`). The `.../list` variant
wraps the response in an array, which Infinity v3 requires for column selectors.

### "Data source not found" when importing dashboard

1. Go to **Connections** → **Data Sources** → **Add data source** → **Infinity**
2. Name it anything (e.g. `Infinity`)
3. Copy the auto-generated UID (e.g. `bfkz8klh3klc0a`)
4. Edit the imported dashboard JSON: replace all `"uid"` values with your UID
5. Re-import

Or delete the pre-existing datasource UID from the JSON before import —
Grafana will prompt you to select a datasource during import.

### Timestamps show wrong time

All broker timestamps are ISO 8601 UTC with `Z` suffix. In Grafana, set
**Timezone** to `UTC` or `Browser` in dashboard settings. The Infinity timestamp
column must use type `timestamp` (not `string`).

### Filtering by tag in table panel

The `/api/read/latest/table` endpoint supports `tags=*` to return all known tags.
Use Grafana's **table filter** (or a dashboard variable) to narrow results.

### Infinity plugin returns "undefined" for columns

Ensure **Parser** is set to `Backend` (not `Auto` or `Simple JSON`). The Backend
parser correctly handles the nested JSON structure.
