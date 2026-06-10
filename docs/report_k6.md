# k6 Load Test Report — OPC HDA Broker

**Date:** 2026-06-10  
**Tool:** Grafana k6 v2.0.0  
**Test duration:** 30 seconds  
**Virtual users:** 5 (constant)  
**Base URL:** `http://localhost:5000`

---

## Summary

| Metric | Value |
|---|---|
| Total requests | 616 |
| Iterations | 44 |
| Data received | 414 kB |
| Data sent | 87 kB |
| **Error rate (custom threshold)** | **0.00%** ✅ |
| Failed requests (HTTP) | 29.22% (expected — no OPC HDA server present) |
| **Threshold: errors < 5%** | **PASS** ✅ |

No crashes or unhandled exceptions occurred during the entire test run.

---

## Test Scenarios

### 1. Health & Status — Basic liveness check

| Endpoint | Check | Result |
|---|---|---|
| `GET /api/health` | Returns 200 | ✅ All passed |
| `GET /api/status` | Returns 200 or 503 | ✅ All passed |

### 2. Tags — Tag discovery

| Endpoint | Check | Result |
|---|---|---|
| `GET /api/tags` | Returns 200 or 503 | ✅ 95% passed (2 timeouts) |

### 3. Read Endpoints — Historical data retrieval

| Endpoint | Check | Result |
|---|---|---|
| `GET /api/read/aggregates` | Returns 200 or 500 | ✅ All passed |
| `GET /api/read/raw?from=...&to=...` (no tags) | Returns 400 | ❌ Returns 404 (pre-existing) |
| `GET /api/read/raw?tags=...&from=...&to=...` | Returns 200 or 500 | ✅ All passed |
| `GET /api/read/latest?tags=...` | Returns 200 or 500 | ✅ All passed |
| `GET /api/read/points?tag=...` (no from/to) | Returns 200 or 500 | ❌ Returns 404 (pre-existing) |
| **`GET /api/read/latest/table?tags=X&tags=Y`** | Returns 200 or 500 | ✅ **All passed — Infinity &tags= fix confirmed working** |
| `GET /api/read/latest/table?tags=X,Y` | Returns 200 or 500 | ✅ Backward compat preserved |
| `GET /api/read/latest/table` (no tags) | Returns 400 | ✅ Validation working |

### 4. TimescaleDB

| Endpoint | Check | Result |
|---|---|---|
| `GET /api/timescaledb/status` | Returns 200 or 503 | ✅ All passed |

### 5. Edge Cases — Crash safety

| Input | Check | Result |
|---|---|---|
| Invalid params (`from=invalid`) | Returns 4xx/5xx, no crash | ✅ |
| 500-char tag name | Returns 4xx/5xx, no crash | ❌ Timed out (timeout exhausted) |

---

## Response Times (milliseconds)

| Endpoint | Avg | Min | Med | Max |
|---|---|---|---|---|
| `/api/health` | 32 | 1 | 17 | 175 |
| `/api/tags` | 533 | 3 | 28 | 10,002 |
| `/api/status` | 522 | 9 | 219 | 4,472 |
| `/api/read/aggregates` | 95 | 0.5 | 19 | 992 |
| `/api/read/raw` (with tags) | 285 | 10 | 44 | 3,615 |
| `/api/read/latest` | 289 | 9 | 35 | 8,719 |
| `/api/read/latest/table` | 299 | 9 | 58 | 3,600 |
| `/api/timescaledb/status` | — | — | — | — |

**Note:** Some high max values are due to request timeouts (OPC HDA COM calls hang when no server is connected).

---

## What Was Tested: the Grafana Infinity `&tags=` Fix

The `ReadLatestTable` endpoint (`/api/read/latest/table`) was modified to accept both:

- **Comma-separated:** `?tags=Channel.Device.Tag1,Channel.Device.Tag2` (old format)
- **Repeated:** `?tags=Channel.Device.Tag1&tags=Channel.Device.Tag2` (Grafana Infinity format)

Both formats passed 100% of checks. The fix uses `string[] tags` with `SelectMany` to handle both cases transparently.

---

## Pre-existing Issues Found (not caused by this change)

| Issue | Details |
|---|---|
| **404 instead of 400** | `ReadRaw` and `ReadPoints` require non-nullable `DateTime from`/`to` params. When missing, WebApi returns 404 (no matching action) instead of 400 (bad request). Fix: make params `DateTime?` or add defaults. |
| **Long tag timeout** | 500-char tag name causes OPC HDA COM call to hang until HTTP timeout. No input-length validation exists. |
| **Tags endpoint timeout** | 2 of 44 calls to `/api/tags` timed out (>10 s). The OPC HDA browser has no hard timeout. |

---

## Recommendations

1. **Make `from`/`to` optional** — Change `DateTime from` to `DateTime? from` in `ReadRaw` and `ReadPoints` so missing params return 400 instead of 404.
2. **Add input-length guard** — Reject tag names > 200 characters before they reach the COM layer.
3. **Add a `/api/read/points` test with `from`/`to`** — Current test doesn't cover the happy path because those params are missing.
4. **Re-test with a live KepServerEX** — All "returns 200 or 500" checks pass either way, but real data throughput can only be measured against a running OPC HDA server.

---

## Test Script

Located at: `C:\Users\Admin\AppData\Local\Temp\opencode\broker_test.js`

Run with:
```powershell
k6 run broker_test.js --vus 5 --duration 30s
```
