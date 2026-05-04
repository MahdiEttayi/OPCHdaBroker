# Data Aggregation Guide — From Raw Tags to KPIs

> How to turn thousands of raw OPC HDA data points into meaningful, actionable metrics.
> Includes a complete **dairy plant** example.

---

## The Problem

Your historian stores raw sensor readings every 3 seconds — ~28,800 points per tag per day.
A plant manager doesn't want to see 28,800 numbers. They want:

- "What was the **average** pasteurizer temperature last shift?"
- "How many minutes was the filling line **down** today?"
- "What's our **OEE** this week?"

**Aggregation** is the process of converting raw time-series data into these answers.

---

## Three Tiers of Aggregation

| Tier | Where It Runs | Speed | Best For |
|------|--------------|-------|----------|
| **1. Server-side** (OPC HDA Aggregates) | KepServerEX historian | ⚡ Fastest | Average, Min, Max, Count over time ranges |
| **2. Broker-side** (Custom API endpoints) | OPC HDA Broker | 🔧 Flexible | Cross-tag calculations, OEE, batch KPIs |
| **3. Client-side** (Grafana Transformations) | Grafana browser/server | 📊 Visual | Combining queries, formatting, thresholds |

> **Rule of thumb**: Push computation as close to the data source as possible.
> Tier 1 > Tier 2 > Tier 3 for performance.

---

## Tier 1: Server-Side Aggregates (Fastest)

KepServerEX's OPC HDA server can compute aggregates **directly on TSD files** — no raw data ever leaves the server. This is orders of magnitude faster than reading raw points and computing in code.

### Available Aggregates

Query your broker to see what's supported:

```powershell
Invoke-RestMethod http://localhost:5000/api/read/aggregates
```

Typical response from KepServerEX:

| ID | Aggregate | Description |
|----|-----------|-------------|
| 1  | Interpolative | Value at exact timestamp (interpolated) |
| 2  | Average | Mean value over interval |
| 3  | Total | Sum of values over interval |
| 4  | Minimum | Lowest value in interval |
| 5  | Maximum | Highest value in interval |
| 7  | Count | Number of raw samples in interval |
| 13 | StdDev | Standard deviation |
| 17 | Range | Max − Min |

### How to Use

```powershell
# Average temperature every hour for the last 24 hours
$tag = "Simulations.Simulator 1.TAG_1"
$from = "2026-05-03T00:00:00Z"
$to   = "2026-05-04T00:00:00Z"

Invoke-RestMethod "http://localhost:5000/api/read/processed?tags=$([uri]::EscapeDataString($tag))&from=$from&to=$to&aggregate=average&intervalSec=3600"
```

This returns **24 data points** (one per hour) instead of **28,800 raw points**. The historian does the math.

### Parameters

| Parameter | Description | Example |
|-----------|-------------|---------|
| `aggregate` | Function name or ID | `average`, `min`, `max`, `count`, `total`, `stdev` |
| `intervalSec` | Bucket size in seconds | `60` (per minute), `3600` (hourly), `86400` (daily) |
| `tags` | Comma-separated tag paths | `TAG_1,TAG_2` |
| `from` / `to` | ISO 8601 UTC time range | `2026-05-03T00:00:00Z` |

### Interval Cheat Sheet

| Goal | `intervalSec` | Points per day |
|------|--------------|----------------|
| Per-minute stats | `60` | 1,440 |
| Every 5 minutes | `300` | 288 |
| Hourly summary | `3,600` | 24 |
| Per-shift (8h) | `28,800` | 3 |
| Daily summary | `86,400` | 1 |

---

## Tier 2: Broker-Side Computed Endpoints

For calculations that span **multiple tags** or require **business logic**, add custom endpoints to the broker. These run in C# on the broker server.

### Example: Computed KPI Endpoint

You could add an endpoint like:

```
GET /api/kpi/efficiency?line=Pasteurizer_1&from=...&to=...
```

That internally:
1. Reads `Temperature` tag (average over shift)
2. Reads `Flow_Rate` tag (total over shift)
3. Reads `Downtime_Flag` tag (count of Bad-quality periods)
4. Computes: `Efficiency = (Actual_Flow / Rated_Flow) × (Uptime / Shift_Duration)`
5. Returns a single JSON object with the KPI

This is more work to implement but gives you **full control** over the calculation logic.

---

## Tier 3: Grafana Transformations

For visual-layer calculations that don't need new API endpoints.

### Key Transformations

| Transformation | Use Case |
|---------------|----------|
| **Reduce** | Collapse a timeseries to a single value (avg, min, max, last) |
| **Add field from calculation** | Create computed fields (e.g., `Pressure_Out - Pressure_In`) |
| **Filter by value** | Show only values above/below a threshold |
| **Organize fields** | Rename, reorder, hide columns |
| **Group by** | Aggregate rows by a field (like SQL GROUP BY) |

### How to Apply in Grafana

1. Open a panel → **Edit**
2. Scroll down below the query → click **Transform**
3. Click **Add transformation** → select the type
4. Configure the parameters

---

## 🐄 Dairy Plant Example

### Plant Layout

```
┌─────────────────────────────────────────────────────────────┐
│                    DAIRY PROCESSING PLANT                     │
│                                                               │
│  ┌──────────┐    ┌──────────────┐    ┌──────────────────┐    │
│  │ Raw Milk  │───►│ Pasteurizer  │───►│ Homogenizer      │    │
│  │ Reception │    │ (72°C/15s)   │    │ (200 bar)        │    │
│  │ Tank      │    │              │    │                   │    │
│  │ [Level]   │    │ [Temp_In]    │    │ [Pressure]       │    │
│  │ [Temp]    │    │ [Temp_Out]   │    │ [Flow_Rate]      │    │
│  └──────────┘    │ [Flow]       │    └──────┬───────────┘    │
│                   └──────────────┘           │                │
│                                              │                │
│  ┌──────────────────────┐    ┌──────────────▼───────────┐    │
│  │ CIP (Clean-In-Place) │    │ Filling & Packaging      │    │
│  │                       │    │                           │    │
│  │ [CIP_Temp]           │    │ [Fill_Level]             │    │
│  │ [CIP_Flow]           │    │ [Line_Speed]             │    │
│  │ [CIP_Conductivity]   │    │ [Reject_Count]           │    │
│  │ [CIP_Phase]          │    │ [Good_Count]             │    │
│  └───────────────────────┘    └───────────────────────────┘    │
└─────────────────────────────────────────────────────────────┘
```

### Tag Mapping

| KepServerEX Tag Path | Sensor | Unit | Logging Rate |
|---|---|---|---|
| `Dairy.Pasteurizer.Temp_In` | Inlet temperature | °C | 1 sec |
| `Dairy.Pasteurizer.Temp_Out` | Outlet temperature | °C | 1 sec |
| `Dairy.Pasteurizer.Flow` | Milk flow rate | L/min | 3 sec |
| `Dairy.Reception.Tank_Level` | Raw milk tank level | % | 5 sec |
| `Dairy.Reception.Tank_Temp` | Raw milk storage temp | °C | 5 sec |
| `Dairy.Homogenizer.Pressure` | Homogenizer pressure | bar | 1 sec |
| `Dairy.Filling.Line_Speed` | Bottles/minute | units/min | 3 sec |
| `Dairy.Filling.Good_Count` | Good bottles produced | count | 3 sec |
| `Dairy.Filling.Reject_Count` | Rejected bottles | count | 3 sec |
| `Dairy.CIP.Temperature` | Cleaning temp | °C | 1 sec |
| `Dairy.CIP.Conductivity` | Chemical concentration | mS/cm | 1 sec |
| `Dairy.CIP.Phase` | Current CIP step | enum | on change |

### KPI Definitions

| KPI | Formula | Tier | How to Calculate |
|-----|---------|------|-----------------|
| **Avg Pasteurizer Temp** | `Average(Temp_Out)` per hour | 1 | `aggregate=average, intervalSec=3600` |
| **Max Pressure** | `Maximum(Pressure)` per shift | 1 | `aggregate=max, intervalSec=28800` |
| **Daily Throughput** | `Total(Flow)` per day | 1 | `aggregate=total, intervalSec=86400` |
| **Temp Compliance** | % time `Temp_Out ≥ 72°C` | 2 | Custom endpoint: count Good ≥ 72 / total |
| **Filling OEE** | Availability × Performance × Quality | 2 | Cross-tag calculation |
| **Pressure Drop** | `Pressure_In − Pressure_Out` | 3 | Grafana binary operation |

---

### Practical API Calls

#### 1. Hourly Average Pasteurizer Temperature (Tier 1)

```powershell
# Returns 24 hourly averages — KepServerEX computes on TSD files
$tag = "Dairy.Pasteurizer.Temp_Out"
Invoke-RestMethod "http://localhost:5000/api/read/processed?tags=$([uri]::EscapeDataString($tag))&from=2026-05-03T00:00:00Z&to=2026-05-04T00:00:00Z&aggregate=average&intervalSec=3600"
```

#### 2. Shift-by-Shift Max Pressure (Tier 1)

```powershell
# 3 values per day (8-hour shifts)
$tag = "Dairy.Homogenizer.Pressure"
Invoke-RestMethod "http://localhost:5000/api/read/processed?tags=$([uri]::EscapeDataString($tag))&from=2026-05-03T00:00:00Z&to=2026-05-04T00:00:00Z&aggregate=max&intervalSec=28800"
```

#### 3. Daily Production Total (Tier 1)

```powershell
# 1 value per day — total liters processed
$tag = "Dairy.Pasteurizer.Flow"
Invoke-RestMethod "http://localhost:5000/api/read/processed?tags=$([uri]::EscapeDataString($tag))&from=2026-05-01T00:00:00Z&to=2026-05-04T00:00:00Z&aggregate=total&intervalSec=86400"
```

---

### Grafana Dashboard Design

```
┌────────────────────────────────────────────────────────────────┐
│  DAIRY PLANT OVERVIEW                              [Last 24h] │
├──────────┬──────────┬──────────┬──────────┬───────────────────┤
│ 🟢 Plant │ 🔵 Past. │ 🟡 Daily │ 🟣 OEE   │                   │
│ ONLINE   │ 72.4°C   │ 45,200L  │ 87.3%    │                   │
│          │ avg temp │ produced │          │                   │
├──────────┴──────────┴──────────┴──────────┘                   │
│                                                                │
│  ┌─────────────────────────────┐ ┌────────────────────────────┐│
│  │ Pasteurizer Temperature     │ │ Homogenizer Pressure       ││
│  │ ────────────────────72°C──  │ │ ──────────────200bar──     ││
│  │ ▁▂▃▅▆▇███████████▇▆▅▃▂▁   │ │ ▅▆▇██▇▆▅▃▂▁▂▃▅▆▇██▇▆▅   ││
│  │ [Threshold: 72°C min]      │ │ [Threshold: 250 bar max]   ││
│  └─────────────────────────────┘ └────────────────────────────┘│
│                                                                │
│  ┌─────────────────────────────┐ ┌────────────────────────────┐│
│  │ Filling Line Speed          │ │ Production (Hourly Totals) ││
│  │ ▅▆▇▇▇▇▇▇▆▅    ▅▆▇▇▇▇▇▆▅  │ │ ▁▃▅▇█▇▅▃  ▁▃▅▇█▇▅▃▁     ││
│  │ [Good vs Reject overlay]   │ │ [aggregate=total, 3600s]   ││
│  └─────────────────────────────┘ └────────────────────────────┘│
│                                                                │
│  ┌────────────────────────────────────────────────────────────┐│
│  │ Shift Summary Table                                        ││
│  │ Shift    │ Avg Temp │ Total Flow │ Rejects │ OEE          ││
│  │ Morning  │ 72.3°C   │ 15,200L    │ 12      │ 91.2%        ││
│  │ Afternoon│ 72.1°C   │ 14,800L    │ 18      │ 88.4%        ││
│  │ Night    │ 72.5°C   │ 15,100L    │ 8       │ 92.1%        ││
│  └────────────────────────────────────────────────────────────┘│
└────────────────────────────────────────────────────────────────┘
```

---

## OEE Calculation Deep Dive

OEE (Overall Equipment Effectiveness) is the gold standard metric for manufacturing:

```
OEE = Availability × Performance × Quality
```

### Breaking It Down for a Dairy Filling Line

| Component | Formula | Data Source |
|-----------|---------|-------------|
| **Availability** | `Run_Time / Planned_Time` | `Line_Running` tag: count Good seconds ÷ shift duration |
| **Performance** | `Actual_Speed / Rated_Speed` | `aggregate=average` on `Line_Speed` ÷ 120 rated |
| **Quality** | `Good_Count / Total_Count` | `aggregate=total` on `Good_Count` ÷ (`Good + Reject`) |

### Example Calculation

```
Shift: 8 hours (480 minutes)
Downtime: 35 minutes (CIP + minor stops)
Rated speed: 120 bottles/min
Actual average: 108 bottles/min
Good bottles: 47,520
Total bottles: 48,060

Availability = (480 - 35) / 480 = 92.7%
Performance  = 108 / 120 = 90.0%
Quality      = 47,520 / 48,060 = 98.9%

OEE = 0.927 × 0.900 × 0.989 = 82.5%
```

### OEE Benchmarks

| Level | OEE | Meaning |
|-------|-----|---------|
| World Class | ≥ 85% | Top-tier manufacturing |
| Good | 70–85% | Room for improvement |
| Average | 60–70% | Significant losses |
| Poor | < 60% | Major improvement needed |

---

## Quick Start: Add Aggregated Panels to Your Existing Dashboard

### Step 1: Verify Aggregates

```powershell
Invoke-RestMethod http://localhost:5000/api/read/aggregates
```

### Step 2: Test a Query

```powershell
$tag = "Simulations.Simulator 1.TAG_1"
$from = [DateTime]::UtcNow.AddDays(-1).ToString("yyyy-MM-ddTHH:mm:ssZ")
$to = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
Invoke-RestMethod "http://localhost:5000/api/read/processed?tags=$([uri]::EscapeDataString($tag))&from=$from&to=$to&aggregate=average&intervalSec=3600"
```

### Step 3: Add a Grafana Panel

1. Edit dashboard → **Add Panel** → Infinity datasource
2. Parser: **Backend**, Format: **Time Series**
3. URL:
   ```
   http://localhost:5000/api/read/processed?tags=Simulations.Simulator%201.TAG_1&from=${__from:date:iso}&to=${__to:date:iso}&aggregate=average&intervalSec=3600
   ```
4. root_selector: `data[0].points`
5. Columns: `t` → timestamp, `v` → number

### Step 4: Add Thresholds

Panel settings → **Thresholds** → base=green, add 30=yellow, 80=red

---

## Summary: Which Approach to Use When

| Scenario | Approach |
|----------|----------|
| "Average temperature per hour" | **Tier 1** — `aggregate=average, intervalSec=3600` |
| "Max pressure this shift" | **Tier 1** — `aggregate=max, intervalSec=28800` |
| "Total production today" | **Tier 1** — `aggregate=total, intervalSec=86400` |
| "Pressure drop across equipment" | **Tier 3** — Grafana transform (Tag_A − Tag_B) |
| "OEE for the filling line" | **Tier 2** — Custom broker endpoint |
| "Compare this week vs last week" | **Tier 3** — Grafana time shift |

> **Start with Tier 1** — it's zero-code. Just change the URL in your Grafana panel from
> `/api/read/points` (raw) to `/api/read/processed?aggregate=average&intervalSec=3600`.
> You already have the endpoint — no broker code changes needed.
