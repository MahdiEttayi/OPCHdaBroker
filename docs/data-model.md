# Data Model Documentation

This document describes the data models for both the C# program and the TimescaleDB database.

---

## 1. Program Data Model (C#)

### 1.1 Core Domain Classes

#### TsdbDataPoint
Represents a single time-series data point ready for TimescaleDB insertion.

| Property | Type | Description |
|----------|------|-------------|
| `Timestamp` | `DateTime` | UTC timestamp of the data point |
| `Tag` | `string` | OPC tag path (e.g., `Channel.Device.Tag`) |
| `Value` | `double?` | Numeric value (null if text) |
| `ValueText` | `string` | Text value (null if numeric) |
| `Quality` | `string` | OPC quality (e.g., `Good`, `Bad`) |
| `BrokerId` | `string` | Source broker identifier |
| `ValueType` | `string` | Type hint: `double`, `float`, `int`, `long`, `short`, `decimal`, `string`, `parsed`, `unknown` |

```csharp
public class TsdbDataPoint
{
    public DateTime  Timestamp  { get; set; }
    public string    Tag        { get; set; }
    public double?   Value      { get; set; }
    public string    ValueText  { get; set; }
    public string    Quality    { get; set; }
    public string    BrokerId   { get; set; }
    public string    ValueType  { get; set; }
}
```

---

#### BackfillConfig
Configuration for historical data backfill, loaded from App.config.

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `Enabled` | `bool` | `false` | Whether backfill is enabled |
| `StartDate` | `DateTime` | 1 year ago | Start of backfill range |
| `EndDate` | `DateTime` | Now | End of backfill range |
| `ChunkDays` | `int` | 30 | Days per chunk |
| `MaxPointsPerCall` | `int` | 50000 | Max values per HDA ReadRaw call |
| `BatchSize` | `int` | 500 | DB insert batch size |
| `PauseBetweenChunksMs` | `int` | 500 | Delay between chunks |
| `AutoStart` | `bool` | `false` | Start backfill on app startup |
| `BrokerId` | `string` | `"kepserver01"` | Broker identifier |

---

#### BackfillStatus
Runtime status of the backfill process.

| Property | Type | Description |
|----------|------|-------------|
| `IsRunning` | `bool` | Whether backfill is active |
| `IsPaused` | `bool` | Whether backfill is paused |
| `StartTime` | `DateTime?` | When backfill started |
| `EndTime` | `DateTime?` | When backfill ended |
| `CurrentTime` | `DateTime?` | Current processing timestamp |
| `TotalPoints` | `long` | Total points written to DB |
| `TagsProcessed` | `int` | Number of tags processed |
| `TotalTags` | `int` | Total tags to process |
| `State` | `string` | Current state: `Starting`, `Running`, `Paused`, `Completed`, `Cancelled`, `Error: ...` |
| `Elapsed` | `TimeSpan` | Elapsed time |
| `ProgressPct` | `double` | Progress percentage (0-100) |
| `EstimatedRemaining` | `string` | Estimated time remaining |

---

### 1.2 API Response Models

#### ApiResponse<T>
Standard envelope for all REST API responses.

```csharp
public class ApiResponse<T>
{
    public T       Data { get; set; }  // Response payload
    public ApiMeta Meta { get; set; }   // Metadata (count, timing, etc.)
}

public class ApiMeta
{
    public int     Count       { get; set; }
    public long    ExecutionMs { get; set; }
    public string  From        { get; set; }
    public string  To          { get; set; }
    public string  Error       { get; set; }
}
```

---

#### PointDto
Compact time-series point for JSON responses.

| Property | Type | Description |
|----------|------|-------------|
| `T` | `string` | ISO 8601 UTC timestamp |
| `V` | `object` | Value (numeric, boolean, or string) |
| `Q` | `string` | Quality string |

---

#### TagDataDto
Read result for a single tag.

| Property | Type | Description |
|----------|------|-------------|
| `Tag` | `string` | Tag path |
| `Count` | `int` | Number of points |
| `Points` | `List<PointDto>` | Data points |
| `Error` | `string` | Error message if failed |

---

#### TsdbStatusDto
TimescaleDB connection and data status.

| Property | Type | Description |
|----------|------|-------------|
| `Connected` | `bool` | DB connection status |
| `Message` | `string` | Status message |
| `RowCount` | `long` | Total rows in `hda_data` |
| `TagCount` | `int` | Distinct tags in DB |
| `OldestTime` | `DateTime?` | Oldest timestamp |
| `NewestTime` | `DateTime?` | Newest timestamp |
| `Backfill` | `BackfillStatusDto` | Backfill progress |

---

### 1.3 Internal Data Classes

#### TimeSeriesPoint (from ComInterop)
Internal OPC HDA data point from COM reader.

```csharp
public class TimeSeriesPoint
{
    public DateTime Timestamp { get; set; }  // Kind = Unspecified, treated as UTC
    public object   Value     { get; set; }   // Numeric or string
    public string   Quality   { get; set; }   // "Good", "Bad", "Uncertain"
    public bool     IsGood    { get; set; }   // Helper: Quality == "Good"
}
```

---

## 2. Database Schema (TimescaleDB)

### 2.1 Main Data Table: hda_data

The hypertable storing all historical OPC HDA data.

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
```

#### Column Reference

| Column | Type | Constraints | Description |
|--------|------|-------------|-------------|
| `time` | `TIMESTAMPTZ` | NOT NULL, PK part 1 | UTC timestamp |
| `tag` | `TEXT` | NOT NULL, PK part 2 | OPC tag path |
| `value` | `DOUBLE PRECISION` | NULL | Numeric value |
| `value_text` | `TEXT` | NULL | String value (if non-numeric) |
| `quality` | `TEXT` | NULL | OPC quality string |
| `broker_id` | `TEXT` | NULL | Source broker identifier |
| `value_type` | `TEXT` | NULL | Value type hint |

#### Indexes

```sql
-- Primary index on (tag, time DESC) for fast lookups
CREATE INDEX idx_hda_data_tag_time ON hda_data (tag, time DESC);
```

#### Hypertable Configuration

- **Chunk Interval**: 1 day per chunk
- **TimescaleDB Extension**: Required (`shared_preload_libraries = 'timescaledb'`)

---

### 2.2 Data Flow Diagram

```
┌─────────────────┐     ┌─────────────────┐     ┌─────────────────┐
│   KepServerEX   │────▶│  OpcHdaBroker   │────▶│  TimescaleDB    │
│   (OPC HDA COM) │     │   (.NET 4.7.2)  │     │  (PostgreSQL)   │
└─────────────────┘     └─────────────────┘     └─────────────────┘
                                │
                                ▼
                        ┌─────────────────┐
                        │  HdaReader      │
                        │  ReadRaw()      │
                        └─────────────────┘
                                │
                                ▼
                        ┌─────────────────┐
                        │  BackfillService│
                        │  ConvertToTsdb  │
                        └─────────────────┘
                                │
                                ▼
                        ┌─────────────────┐
                        │  TsdbRepository │
                        │  BulkUpsert()    │
                        └─────────────────┘
```

---

### 2.3 Data Transformation

**HDA COM → Program → Database**

| Source (COM) | Program (TsdbDataPoint) | Database (hda_data) |
|--------------|------------------------|---------------------|
| `ITimestamp` | `Timestamp` (SpecifyKind UTC) | `time` TIMESTAMPTZ |
| Tag path string | `Tag` | `tag` TEXT |
| Numeric value | `Value` (double?) | `value` DOUBLE PRECISION |
| String value | `ValueText` | `value_text` TEXT |
| Quality string | `Quality` | `quality` TEXT |
| Config value | `BrokerId` | `broker_id` TEXT |
| Type detection | `ValueType` | `value_type` TEXT |

---

### 2.4 Deduplication Strategy

Uses PostgreSQL `ON CONFLICT DO NOTHING` to handle duplicates:

```sql
INSERT INTO hda_data (time, tag, value, value_text, quality, broker_id, value_type)
VALUES (@t, @tag, @val, @vtxt, @q, @bid, @vtype)
ON CONFLICT (time, tag) DO NOTHING;
```

This prevents inserting duplicate points with the same `(time, tag)` primary key.

---

## 3. Configuration Reference

### App.config Keys

| Key | Example Value | Description |
|-----|---------------|-------------|
| `Tsdb.ConnectionString` | `Host=localhost;Port=5432;Database=hda;Username=postgres;Password=admin123` | PostgreSQL connection |
| `Ingestion.BrokerId` | `kepserver01` | Unique broker identifier |
| `Backfill.Enabled` | `false` | Enable backfill |
| `Backfill.StartDate` | `2024-01-01T00:00:00Z` | Backfill start |
| `Backfill.EndDate` | `2026-05-01T00:00:00Z` | Backfill end |
| `Backfill.ChunkDays` | `30` | Days per chunk |
| `Backfill.AutoStart` | `false` | Auto-start on boot |

---

## 4. Query Examples

### Count rows
```sql
SELECT COUNT(*) FROM hda_data;
```

### Distinct tag count
```sql
SELECT COUNT(DISTINCT tag) FROM hda_data;
```

### Time range query
```sql
SELECT * FROM hda_data
WHERE tag = 'Channel.Device.Tag'
  AND time BETWEEN '2025-01-01' AND '2025-01-31'
ORDER BY time DESC;
```

### Average by hour (TimescaleDB hyperfunction)
```sql
SELECT time_bucket('1 hour', time) AS bucket, AVG(value)
FROM hda_data
WHERE tag = 'Channel.Device.Tag'
  AND time >= NOW() - INTERVAL '7 days'
GROUP BY bucket
ORDER BY bucket;
```

### Delete old data
```sql
DELETE FROM hda_data WHERE time < NOW() - INTERVAL '2 years';
```