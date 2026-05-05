# OPC HDA Broker — Deployment Guide

> Step-by-step guide to deploy the OPC HDA Broker and connect it to **any KepServerEX 6.x** installation.

---

## Table of Contents

1. [Prerequisites](#1-prerequisites)
2. [KepServerEX 6 Setup](#2-kepserverex-6-setup)
3. [Build the Broker](#3-build-the-broker)
4. [Configure the Broker](#4-configure-the-broker)
5. [Run in Console Mode (Testing)](#5-run-in-console-mode)
6. [Install as Windows Service (Production)](#6-install-as-windows-service)
7. [Verify the Deployment](#7-verify-the-deployment)
8. [Register Your Tags](#8-register-your-tags)
9. [Grafana Integration](#9-grafana-integration)
10. [Power BI Integration](#10-power-bi-integration)
11. [Firewall & Network Access](#11-firewall--network-access)
12. [Adapting to Different KepServerEX 6 Versions](#12-adapting-to-different-kepserverex-6-versions)
13. [Troubleshooting](#13-troubleshooting)

---

## 1. Prerequisites

### On the Broker Host Machine

| Requirement | Details |
|---|---|
| **OS** | Windows 10/11 or Windows Server 2016+ (64-bit) |
| **.NET Framework** | 4.7.2 or later (pre-installed on Windows 10 1803+) |
| **.NET SDK** | .NET SDK with `net472` targeting pack — [download](https://dotnet.microsoft.com/download) |
| **Platform** | The broker **must run as x86 (32-bit)** — OPC HDA COM is 32-bit only |
| **KepServerEX 6** | Any 6.x version (6.4 through 6.14+) with **Local Historian** plug-in enabled |
| **Admin rights** | Required for service installation and DCOM configuration |

### Verify .NET Framework

```powershell
# Check installed .NET Framework version
(Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full").Release
# Should be >= 461808 (meaning 4.7.2+)
```

### Verify KepServerEX is Running

```powershell
# Check if KepServerEX runtime is running
Get-Process -Name "server_runtime" -ErrorAction SilentlyContinue

# Or check the Windows service
Get-Service -Name "KEPServerEXV6" -ErrorAction SilentlyContinue | Select-Object Status
```

---

## 2. KepServerEX 6 Setup

### 2.1 — Enable the Local Historian Plug-in

1. Open **KepServerEX Configuration** (right-click tray icon → Configuration)
2. In the left tree, look for a **Local Historian** node
3. If it doesn't exist, you need to install the plug-in:
   - Run KepServerEX installer → **Modify** → check **Local Historian**
   - Restart the KepServerEX service after installation

### 2.2 — Create a Datastore

1. Right-click **Local Historian** → **New Datastore**
2. Name it (e.g., `Datastore`)
3. Configure storage path (default: `C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data\`)
4. Set **Archive Size** and **Retention** as needed for your environment

### 2.3 — Add Tags to the Historian

1. Expand your **Channel** → **Device** in the KepServerEX configuration tree
2. Right-click the tags you want to historize → **Properties**
3. Or: right-click the **Datastore** → **New Tag Mapping**
4. Map your OPC tags to the historian datastore
5. Verify data is being logged — check that `.TSD` and `.Active` files appear in the datastore directory

### 2.4 — Verify the OPC HDA ProgID

The broker connects using a **ProgID** string. For KepServerEX 6 it is always:

```
Kepware.KEPServerEX_HDA.V6
```

This ProgID is **identical across all KepServerEX 6.x versions** (6.4 through 6.14+). You do **not** need to change it when upgrading KepServerEX.

To verify it is registered on your machine:

```powershell
Get-ItemProperty "HKLM:\SOFTWARE\WOW6432Node\Classes\Kepware.KEPServerEX_HDA.V6" -ErrorAction SilentlyContinue
```

If the key exists, the HDA server is properly registered.

### 2.5 — Configure DCOM (Remote Deployments Only)

If the broker runs on a **different machine** than KepServerEX:

1. Run `dcomcnfg` (Component Services) on the KepServerEX machine
2. Navigate to: **Component Services** → **Computers** → **My Computer** → **DCOM Config**
3. Find `Kepware.KEPServerEX_HDA.V6`
4. Right-click → **Properties** → **Security** tab
5. Under **Launch and Activation Permissions**, add the broker machine's user account
6. Under **Access Permissions**, add the same account
7. Restart KepServerEX

For **local** deployments (broker and KepServerEX on the same machine), DCOM permissions are already configured by default — skip this step.

---

## 3. Build the Broker

### 3.1 — Get the Source Code

```powershell
# Clone from git
git clone <repo-url> C:\OpcHdaBroker
cd C:\OpcHdaBroker

# Or: copy the project folder to the target machine
```

### 3.2 — Verify the SDK DLL

The Technosoftware OPC HDA SDK is included in the `lib/` directory:

```
lib\OpcClientSdk472.dll    (393 KB)
```

Do **not** delete or replace this DLL — the broker depends on it for all COM interop.

### 3.3 — Build

```powershell
cd src\OpcHdaBroker
dotnet restore
dotnet build -c Release
```

The output will be in:

```
src\OpcHdaBroker\bin\x86\Release\net472\
```

Key files in the build output:

| File | Purpose |
|---|---|
| `OpcHdaBroker.exe` | Main executable |
| `OpcHdaBroker.exe.config` | Runtime configuration (copy of App.config) |
| `OpcClientSdk472.dll` | OPC HDA SDK |
| `tags.txt` | Tag configuration file |

---

## 4. Configure the Broker

Edit `src\OpcHdaBroker\App.config` **before** building, or edit `OpcHdaBroker.exe.config` in the output folder **after** building.

```xml
<appSettings>
  <!-- OPC HDA Connection -->
  <add key="Hda.PrimaryUrl"  value="opchda://localhost/Kepware.KEPServerEX_HDA.V6" />
  <add key="Hda.FallbackUrl" value="opchda://127.0.0.1/Kepware.KEPServerEX_HDA.V6" />

  <!-- REST API -->
  <!-- Console mode: "http://localhost:5000"    -->
  <!-- Service mode: "http://+:5000" (all IPs)  -->
  <add key="Api.BaseUrl"          value="http://localhost:5000" />
  <add key="Api.DefaultMaxValues" value="10000" />
  <add key="Api.AbsoluteMaxValues" value="100000" />

  <!-- Cache TTLs -->
  <add key="Cache.TagListTtlSec" value="60" />
  <add key="Cache.StatusTtlSec"  value="30" />

  <!-- Logging -->
  <add key="Log.Level"    value="Information" />
  <add key="Log.FilePath" value="logs\broker-.log" />
</appSettings>
```

### Settings You'll Typically Change

| Setting | When to Change | Example |
|---|---|---|
| `Hda.PrimaryUrl` | KepServerEX on a remote machine | `opchda://192.168.1.100/Kepware.KEPServerEX_HDA.V6` |
| `Api.BaseUrl` | Running as a service / remote access | `http://+:5000` |
| `Log.Level` | After initial debugging is done | `Information` or `Warning` |

---

## 5. Run in Console Mode

Use console mode first to verify everything works before installing as a service.

```powershell
cd src\OpcHdaBroker\bin\x86\Release\net472
.\OpcHdaBroker.exe
```

Expected output:

```
  ╔═══════════════════════════════════════════════════╗
  ║  OPC HDA Broker — Console Mode                   ║
  ╚═══════════════════════════════════════════════════╝

  ✓  API ready at http://localhost:5000
  ✓  Swagger UI at http://localhost:5000/swagger

  Press Enter to stop...
```

Quick validation (in a separate terminal):

```powershell
Invoke-RestMethod http://localhost:5000/api/health
Invoke-RestMethod http://localhost:5000/api/status
Invoke-RestMethod http://localhost:5000/api/tags
```

If all three return valid JSON — the broker is working. Press **Enter** in the console window to stop.

---

## 6. Install as Windows Service

### 6.1 — Create the Install Directory

```powershell
mkdir C:\Services\OpcHdaBroker
Copy-Item "src\OpcHdaBroker\bin\x86\Release\net472\*" "C:\Services\OpcHdaBroker\" -Recurse
```

### 6.2 — Update Config for Service Mode

Edit `C:\Services\OpcHdaBroker\OpcHdaBroker.exe.config`:

```xml
<add key="Api.BaseUrl" value="http://+:5000" />
<add key="Log.Level"   value="Information" />
```

### 6.3 — Reserve the HTTP URL

```powershell
# Run as Administrator
netsh http add urlacl url=http://+:5000/ user=Everyone
```

### 6.4 — Install the Service

**Option A** — Use the included batch file:

```powershell
Copy-Item "deploy\install-service.bat" "C:\Services\OpcHdaBroker\"
# Run as Administrator:
C:\Services\OpcHdaBroker\install-service.bat
```

**Option B** — Manual `sc` command:

```powershell
# Run as Administrator
sc create OpcHdaBroker binPath= "C:\Services\OpcHdaBroker\OpcHdaBroker.exe" DisplayName= "OPC HDA Broker" start= auto
sc description OpcHdaBroker "REST API proxy for KepServerEX Local Historian (OPC HDA)"
sc failure OpcHdaBroker reset= 86400 actions= restart/5000/restart/10000/restart/30000
```

### 6.5 — Start and Verify

```powershell
sc start OpcHdaBroker
Get-Service OpcHdaBroker | Select-Object Status, DisplayName
Invoke-RestMethod http://localhost:5000/api/health
```

### 6.6 — Uninstall (if needed)

```powershell
sc stop OpcHdaBroker
sc delete OpcHdaBroker
netsh http delete urlacl url=http://+:5000/
```

---

## 7. Verify the Deployment

Run these checks in order:

```powershell
# 1. Liveness
Invoke-RestMethod http://localhost:5000/api/health

# 2. Server status (shows KepServerEX version and connection state)
Invoke-RestMethod http://localhost:5000/api/status

# 3. Discovered tags
Invoke-RestMethod http://localhost:5000/api/tags

# 4. Read latest value for a specific tag (replace with your tag path)
$tag = "Simulations.Simulator 1.TAG_1"
Invoke-RestMethod "http://localhost:5000/api/read/latest?tags=$([uri]::EscapeDataString($tag))"

# 5. Full COM/SDK diagnostics
Invoke-RestMethod http://localhost:5000/api/diagnostics
```

---

## 8. Register Your Tags

The broker uses a **three-tier tag discovery strategy**:

### Tier 1 — TSD Auto-Discovery (Automatic, Most Reliable)

The broker reads `.name` metadata files from KepServerEX's historian datastore directory. This happens automatically — no configuration needed if your tags are being historized.

Default path: `C:\ProgramData\Kepware\KEPServerEX\V6\Historical Data\`

### Tier 2 — `tags.txt` File (Manual)

Edit the `tags.txt` file in the broker's directory:

```text
# One tag path per line — format: Channel.Device.Tag
PLF_A10.A10.10QT0002
PLF_A10.A10.10QT0003
Simulations.Simulator 1.TAG_1
```

### Tier 3 — REST API (Runtime)

```powershell
$body = '["PLF_A10.A10.10QT0002", "PLF_A10.A10.10QT0003"]'
Invoke-RestMethod http://localhost:5000/api/tags/add -Method POST -Body $body -ContentType "application/json"

# Force refresh
Invoke-RestMethod http://localhost:5000/api/tags/refresh -Method POST
```

### How to Find Your Tag Paths

1. Open **KepServerEX Configuration**
2. Navigate to **Local Historian** → **Datastore** → expand the tag tree
3. Tag path pattern: `Channel.Device.TagName`
4. Example: tag `Temperature` under device `PLC1` in channel `Modbus` → `Modbus.PLC1.Temperature`

---

## 9. Grafana Integration

### Quick Setup

```powershell
# 1. Install Grafana
winget install GrafanaLabs.Grafana.OSS

# 2. Install Infinity plugin
mkdir C:\Users\$env:USERNAME\grafana-plugins
grafana cli --pluginsDir "C:\Users\$env:USERNAME\grafana-plugins" plugins install yesoreyeram-infinity-datasource

# 3. Configure — create C:\Program Files\GrafanaLabs\grafana\conf\custom.ini
#    Or copy from: deploy\grafana-custom.ini
```

`custom.ini` contents:

```ini
[paths]
plugins = C:\Users\Admin\grafana-plugins

[plugins]
allow_loading_unsigned_plugins = yesoreyeram-infinity-datasource
```

```powershell
# 4. Restart Grafana
Restart-Service grafana

# 5. Import the pre-built dashboard
$cred = [Convert]::ToBase64String([Text.Encoding]::ASCII.GetBytes("admin:admin"))
$headers = @{ Authorization = "Basic $cred"; "Content-Type" = "application/json" }
$body = Get-Content "deploy\grafana-dashboard.json" -Raw
Invoke-RestMethod "http://localhost:3000/api/dashboards/db" -Method POST -Headers $headers -Body $body
```

Open `http://localhost:3000` → login (`admin` / `admin`) → find the **OPC HDA Historian** dashboard.

### Grafana-Optimized Endpoints

| Endpoint | Use For |
|---|---|
| `GET /api/read/points?tag=...&from=...&to=...` | Time series panels |
| `GET /api/read/latest/table?tags=...` | Table panels |
| `GET /api/status/list` | Stat panels |

---

## 10. Power BI Integration

See the full guide: `docs\PowerBI-Guide.md`

**Quick start:**

1. Open Power BI Desktop → **Get Data** → **Web**
2. Enter: `http://localhost:5000/api/read/latest/table?tags=*`
3. Power BI parses the JSON into a table automatically

---

## 11. Firewall & Network Access

To allow other machines on the network to reach the broker:

```powershell
# Run as Administrator
New-NetFirewallRule -DisplayName "OPC HDA Broker" -Direction Inbound -Port 5000 -Protocol TCP -Action Allow
```

Then access from other machines: `http://192.168.1.50:5000/api/status`

**Important**: `Api.BaseUrl` must be `http://+:5000` (not `http://localhost:5000`) for remote access.

---

## 12. Adapting to Different KepServerEX 6 Versions

### Version Compatibility

| KepServerEX Version | ProgID | Compatible |
|---|---|---|
| 6.4.x | `Kepware.KEPServerEX_HDA.V6` | ✅ |
| 6.5.x | `Kepware.KEPServerEX_HDA.V6` | ✅ |
| 6.6.x | `Kepware.KEPServerEX_HDA.V6` | ✅ (tested: 6.6.350) |
| 6.7.x – 6.10.x | `Kepware.KEPServerEX_HDA.V6` | ✅ |
| 6.11.x – 6.14.x | `Kepware.KEPServerEX_HDA.V6` | ✅ |

The ProgID is consistent across **all** KepServerEX 6.x releases — no config changes needed.

### What Stays the Same Across Versions

- HDA ProgID → no config change needed
- COM Interface (IOPCHDA) → binary compatible
- TSD file format → auto-discovery still works
- TSD storage path → default path is the same

### Steps to Deploy on a New Machine

1. Verify KepServerEX is running with Local Historian enabled
2. Copy the broker build output to the new machine
3. Edit the config — typically only the host IP changes:

```xml
<!-- Same machine (most common): -->
<add key="Hda.PrimaryUrl" value="opchda://localhost/Kepware.KEPServerEX_HDA.V6" />

<!-- Different machine: -->
<add key="Hda.PrimaryUrl" value="opchda://KEPSERVER-HOSTNAME/Kepware.KEPServerEX_HDA.V6" />
```

4. Run and test (Steps 5-7 above)
5. Tags are discovered automatically via TSD files

### Upgrading KepServerEX In-Place

1. Stop the broker service: `sc stop OpcHdaBroker`
2. Run the KepServerEX upgrade installer
3. Verify Local Historian is still enabled
4. Start the broker: `sc start OpcHdaBroker`
5. Test: `Invoke-RestMethod http://localhost:5000/api/status`

No broker config changes required.

---

## 13. Troubleshooting

### "Cannot connect to OPC HDA server"

| Cause | Fix |
|---|---|
| KepServerEX not running | Start the `KEPServerEXV6` service |
| HDA plug-in not installed | Re-run installer → Modify → enable HDA |
| Wrong ProgID | Verify with registry check (see §2.4) |
| DCOM permissions (remote) | Configure DCOM (see §2.5) |

### "No tags discovered"

| Cause | Fix |
|---|---|
| No tags mapped to historian | Map tags in KepServerEX (see §2.3) |
| Wrong TSD path | Check config matches your historian storage path |
| Tags not yet logging | Wait for one write cycle (~3-10 seconds) |

### "API returns empty data"

| Cause | Fix |
|---|---|
| Time range has no data | Adjust `from`/`to` parameters |
| Tag path incorrect | Use `GET /api/tags` to see exact paths |
| Timezone mismatch | Always use UTC timestamps with `Z` suffix |

### Service Won't Start

```powershell
# Check Windows Event Log
Get-EventLog -LogName Application -Source "OpcHdaBroker" -Newest 10

# Check broker logs
Get-Content "C:\Services\OpcHdaBroker\logs\broker-*.log" -Tail 50

# Ensure URL reservation exists
netsh http add urlacl url=http://+:5000/ user=Everyone
```

### Port 5000 Already in Use

```powershell
# Find what's using the port
netstat -ano | findstr :5000

# Fix: change Api.BaseUrl in OpcHdaBroker.exe.config to a different port
```

---

## Quick Reference

```
BUILD:     dotnet build -c Release
RUN:       .\OpcHdaBroker.exe
INSTALL:   sc create OpcHdaBroker binPath= "..." start= auto
START:     sc start OpcHdaBroker
STOP:      sc stop OpcHdaBroker
REMOVE:    sc delete OpcHdaBroker

HEALTH:    GET /api/health
STATUS:    GET /api/status
TAGS:      GET /api/tags
LATEST:    GET /api/read/latest?tags=...
RAW:       GET /api/read/raw?tags=...&from=...&to=...
DIAG:      GET /api/diagnostics

CONFIG:    OpcHdaBroker.exe.config
LOGS:      logs\broker-YYYY-MM-DD.log
TAGS FILE: tags.txt
PROGID:    Kepware.KEPServerEX_HDA.V6  (all 6.x versions)
PLATFORM:  x86 (32-bit) — required for COM interop
PORT:      5000 (default, configurable)
```
