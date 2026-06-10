# OPC HDA Broker — User Guide

> How to install and run the OPC HDA Broker on any machine with KepServerEX 6.

This guide is for **IT operators and plant engineers** who received the broker as a ready-to-run package. No programming or build tools are needed.

---

## What Does This Do?

The OPC HDA Broker is a small Windows program that sits between **KepServerEX 6** and your visualization tools (Grafana, Power BI, web browsers). It reads historical tag data from the KepServerEX Local Historian and makes it available as a simple web API.

```
KepServerEX 6                OPC HDA Broker              Your Tools
┌──────────────┐   COM    ┌──────────────────┐   HTTP   ┌──────────────┐
│ Local         │ ──────► │ OpcHdaBroker.exe │ ──────► │ Grafana      │
│ Historian     │         │ (port 5000)      │         │ Power BI     │
│ (.TSD files)  │         │                  │         │ Web Browser  │
└──────────────┘         └──────────────────┘         └──────────────┘
```

---

## What You'll Need

| Requirement | Details |
|---|---|
| **Windows** | Windows 10, 11, or Windows Server 2016+ |
| **KepServerEX 6** | Any version (6.4 through 6.14+) — must be installed and running |
| **Local Historian** | The KepServerEX Local Historian plug-in must be enabled with at least one datastore |
| **Admin rights** | Needed to install the Windows service and open the network port |
| **The broker package** | A folder containing `OpcHdaBroker.exe` and its support files |

---

## Step 1 — Check That KepServerEX Is Running

Before starting the broker, make sure KepServerEX is running:

1. Look for the **KepServerEX icon** in the Windows system tray (bottom-right of the taskbar)
2. The icon should be **green** (running) — not red or gray
3. Right-click it → **Configuration** → verify you can see the **Local Historian** node in the tree

If the icon is not there, start KepServerEX from the Start Menu or run:

```
Start Menu → Kepware → KEPServerEX 6 → KEPServerEX 6 Configuration
```

---

## Step 2 — Copy the Broker to Its Permanent Location

Copy the broker folder to a permanent location on the machine. We recommend:

```
C:\Services\OpcHdaBroker\
```

The folder should contain these files:

```
C:\Services\OpcHdaBroker\
├── OpcHdaBroker.exe              ← Main program
├── OpcHdaBroker.exe.config       ← Settings file (editable)
├── OpcClientSdk472.dll           ← Required library (do not delete)
├── tags.txt                      ← Tag list (optional, editable)
├── install-service.bat           ← Service installer script
├── uninstall-service.bat         ← Service uninstaller script
└── (other .dll files)            ← Support libraries
```

> ⚠️ **Do not delete any `.dll` files** — they are all required for the broker to function.

---

## Step 3 — Configure the Broker

Open the file `OpcHdaBroker.exe.config` in **Notepad** (right-click → Open With → Notepad).

You'll see settings that look like this:

```xml
<appSettings>
  <add key="Hda.PrimaryUrl"  value="opchda://localhost/Kepware.KEPServerEX_HDA.V6" />
  <add key="Api.BaseUrl"     value="http://localhost:5000" />
  <add key="Log.Level"       value="Information" />
</appSettings>
```

### What You Might Need to Change

| Setting | Default | When to Change |
|---|---|---|
| `Hda.PrimaryUrl` | `opchda://localhost/...` | **Change only if** KepServerEX is on a different machine. Replace `localhost` with that machine's IP address. |
| `Api.BaseUrl` | `http://localhost:5000` | **Change to `http://+:5000`** if you want other computers on the network to access the broker (required for Windows Service mode). |
| `Log.Level` | `Information` | Change to `Debug` if troubleshooting, or `Warning` to reduce log output. |

### Example: KepServerEX on Another Machine

If KepServerEX is running on a machine with IP `192.168.1.100`:

```xml
<add key="Hda.PrimaryUrl" value="opchda://192.168.1.100/Kepware.KEPServerEX_HDA.V6" />
```

### Example: Allow Network Access

To let other computers reach the broker's web API:

```xml
<add key="Api.BaseUrl" value="http://+:5000" />
```

Save and close the file after making changes.

---

## Step 4 — Test by Running Directly

Before installing as a service, test that everything works by running the program directly.

### 4.1 — Double-click `OpcHdaBroker.exe`

A console window will appear showing:

```
  ╔═══════════════════════════════════════════════════╗
  ║  OPC HDA Broker — Console Mode                   ║
  ╚═══════════════════════════════════════════════════╝

  ✓  API ready at http://localhost:5000
  ✓  Swagger UI at http://localhost:5000/swagger

  Press Enter to stop...
```

If you see the `✓` marks — the broker is connected to KepServerEX and ready.

### 4.2 — Open Your Web Browser

Visit these URLs to verify:

| URL | What You Should See |
|---|---|
| `http://localhost:5000/api/health` | `{"status":"ok"}` |
| `http://localhost:5000/api/status` | Server name, version, tag count |
| `http://localhost:5000/api/tags` | List of all historian tag names |

### 4.3 — Stop the Broker

Go back to the console window and press **Enter** to stop.

### If It Doesn't Start

| Error Message | Solution |
|---|---|
| "Cannot connect to OPC HDA server" | KepServerEX is not running — start it first |
| "Address already in use" | Another program is using port 5000 — change the port in the config file |
| Window closes immediately | Run from Command Prompt to see the error: open `cmd`, navigate to the folder, type `OpcHdaBroker.exe` |

---

## Step 5 — Install as a Windows Service

Installing as a service means the broker starts automatically when Windows boots — no need to manually run the `.exe`.

### 5.1 — Open Command Prompt as Administrator

- Press **Windows key**, type `cmd`
- Right-click **Command Prompt** → **Run as administrator**

### 5.2 — Reserve the Network Port

Type this command and press Enter:

```cmd
netsh http add urlacl url=http://+:5000/ user=Everyone
```

You should see: `URL reservation successfully added`

### 5.3 — Make Sure Config Uses `http://+:5000`

Before installing the service, ensure the config file has:

```xml
<add key="Api.BaseUrl" value="http://+:5000" />
```

(Not `http://localhost:5000` — the service needs `+` to listen on all network interfaces.)

### 5.4 — Run the Installer Script

In the **Administrator Command Prompt**, navigate to the broker folder and run:

```cmd
cd C:\Services\OpcHdaBroker
install-service.bat
```

You should see:

```
═══════════════════════════════════════════════════
  OPC HDA Broker — Windows Service Installer
═══════════════════════════════════════════════════

  ✓  Service created. Start with:
     sc start OpcHdaBroker
```

### 5.5 — Start the Service

```cmd
sc start OpcHdaBroker
```

Or use the **Services** app:
1. Press **Windows key**, type `services.msc`, press Enter
2. Find **OPC HDA Broker** in the list
3. Right-click → **Start**
4. Verify the **Status** column shows **Running**

### 5.6 — Verify It's Working

Open your browser and visit:

```
http://localhost:5000/api/health
```

You should see `{"status":"ok"}`.

### 5.7 — The Service Starts Automatically

The service is set to **auto-start** with Windows. If KepServerEX is also set to auto-start, the entire system will be operational after a reboot with no manual steps.

---

## Step 6 — Add Your Tags

The broker automatically discovers tags from the KepServerEX historian datastore. If your tags are mapped to the Local Historian, they should appear at:

```
http://localhost:5000/api/tags
```

### If Tags Are Missing

You can manually add them by editing the `tags.txt` file:

1. Open `tags.txt` in Notepad
2. Add one tag per line using the format: `Channel.Device.TagName`
3. Save the file
4. Restart the broker (or call `http://localhost:5000/api/tags/refresh` via POST)

**Example `tags.txt`:**

```
# My plant tags (lines starting with # are ignored)
Modbus.PLC1.Temperature
Modbus.PLC1.Pressure
Modbus.PLC1.FlowRate
EtherNetIP.Drive1.Speed
```

### How to Find Tag Paths in KepServerEX

1. Open **KepServerEX Configuration**
2. Expand **Local Historian** → your **Datastore**
3. The tag path follows the tree: `Channel.Device.TagName`
4. Example: if you see `Modbus` → `PLC1` → `Temperature`, the path is `Modbus.PLC1.Temperature`

---

## Step 7 — Open the Firewall (For Network Access)

If Grafana or Power BI is running on **a different computer**, you need to open port 5000 in the Windows Firewall.

### Using Command Prompt (Administrator):

```cmd
netsh advfirewall firewall add rule name="OPC HDA Broker" dir=in action=allow protocol=TCP localport=5000
```

### Using Windows Firewall UI:

1. Open **Windows Defender Firewall** → **Advanced Settings**
2. Click **Inbound Rules** → **New Rule**
3. Select **Port** → **TCP** → enter `5000`
4. Select **Allow the connection**
5. Name it `OPC HDA Broker`
6. Click **Finish**

After this, other computers can access the broker at:

```
http://YOUR-MACHINE-IP:5000/api/status
```

---

## Step 8 — Connect Grafana (Optional)

### Install Grafana

1. Download Grafana OSS from [grafana.com/grafana/download](https://grafana.com/grafana/download/?pg=oss-graf&plcmt=hero-btn-1)
2. Run the installer — it installs as a Windows service automatically
3. Open `http://localhost:3000` in your browser
4. Log in with username `admin`, password `admin`

### Install the Infinity Plugin

Open **Command Prompt as Administrator** and run:

```cmd
mkdir C:\Users\%USERNAME%\grafana-plugins
"C:\Program Files\GrafanaLabs\grafana\bin\grafana" cli --pluginsDir "C:\Users\%USERNAME%\grafana-plugins" plugins install yesoreyeram-infinity-datasource
```

### Configure Grafana

Create or edit the file `C:\Program Files\GrafanaLabs\grafana\conf\custom.ini`:

```ini
[paths]
plugins = C:\Users\YOUR_USERNAME\grafana-plugins

[plugins]
allow_loading_unsigned_plugins = yesoreyeram-infinity-datasource
```

Replace `YOUR_USERNAME` with your actual Windows username.

Then restart Grafana:

```cmd
net stop grafana
net start grafana
```

### Import the Pre-Built Dashboard

The broker package includes a ready-made Grafana dashboard file: `grafana-dashboard.json`

1. Open Grafana at `http://localhost:3000`
2. Go to **Dashboards** → **Import**
3. Click **Upload JSON file**
4. Select the `grafana-dashboard.json` file from the broker's `deploy` folder
5. Click **Import**

The dashboard will show broker status, tag counts, and historical data charts.

---

## Step 9 — Connect Power BI (Optional)

1. Open **Power BI Desktop**
2. Click **Get Data** → **Web**
3. Enter this URL:

```
http://localhost:5000/api/read/latest/table?tags=*
```

4. Click **OK** — Power BI will connect and show a table of all tag values
5. Click **Load** to import the data

For more advanced Power BI setups, see `docs\PowerBI-Guide.md`.

---

## Managing the Service

### Start / Stop / Restart

Using Command Prompt (Administrator):

```cmd
sc start OpcHdaBroker
sc stop OpcHdaBroker

:: Restart
sc stop OpcHdaBroker
timeout /t 3
sc start OpcHdaBroker
```

Or use the **Services** app (`services.msc`):
- Find **OPC HDA Broker** → right-click → **Start** / **Stop** / **Restart**

### Check Service Status

```cmd
sc query OpcHdaBroker
```

### View Logs

The broker writes log files to the `logs\` folder inside its installation directory:

```
C:\Services\OpcHdaBroker\logs\broker-2026-05-05.log
```

Open the latest log file in Notepad to see activity and any errors.

### Uninstall the Service

Run **as Administrator**:

```cmd
cd C:\Services\OpcHdaBroker
uninstall-service.bat
```

Or manually:

```cmd
sc stop OpcHdaBroker
sc delete OpcHdaBroker
```

---

## API Quick Reference

Once the broker is running, you can access these URLs from any web browser or tool:

| What You Want | URL |
|---|---|
| Check if broker is alive | `http://localhost:5000/api/health` |
| See server status | `http://localhost:5000/api/status` |
| List all tags | `http://localhost:5000/api/tags` |
| Search for a tag | `http://localhost:5000/api/tags?search=Temperature` |
| Get latest value | `http://localhost:5000/api/read/latest?tags=Channel.Device.Tag` |
| Get historical data | `http://localhost:5000/api/read/raw?tags=Channel.Device.Tag&from=2026-05-01T00:00:00Z&to=2026-05-02T00:00:00Z` |
| Full diagnostics | `http://localhost:5000/api/diagnostics` |

> **Tip**: Replace `localhost` with the broker machine's IP address if accessing from another computer.

---

## Troubleshooting

### The broker won't start

| Symptom | Fix |
|---|---|
| Console window closes immediately | Open `cmd`, navigate to the folder, run `OpcHdaBroker.exe` to see the error message |
| "Cannot connect to OPC HDA server" | KepServerEX is not running — start it first |
| "Address already in use" | Port 5000 is taken — change the port in `OpcHdaBroker.exe.config` |
| Service fails to start | Check logs in `logs\` folder. Also ensure `netsh http add urlacl` was run |

### No tags are showing

| Symptom | Fix |
|---|---|
| `/api/tags` returns empty | No tags are mapped to the Local Historian in KepServerEX. Map some tags, wait 10 seconds, then try again |
| Tags are there but data is empty | Check your `from` and `to` dates — the historian may not have data for that time range |

### Can't access from another computer

| Symptom | Fix |
|---|---|
| Connection refused | 1. Config must use `http://+:5000` (not `localhost`) |
| | 2. Run `netsh http add urlacl url=http://+:5000/ user=Everyone` |
| | 3. Open port 5000 in Windows Firewall (see Step 7) |

### Need to change the port

1. Open `OpcHdaBroker.exe.config` in Notepad
2. Change `5000` to your desired port (e.g., `8080`):
   ```xml
   <add key="Api.BaseUrl" value="http://+:8080" />
   ```
3. If running as a service, also update the URL reservation:
   ```cmd
   netsh http delete urlacl url=http://+:5000/
   netsh http add urlacl url=http://+:8080/ user=Everyone
   ```
4. Restart the broker

---

## Works With All KepServerEX 6 Versions

The broker is compatible with **every KepServerEX 6.x release**:

- ✅ KepServerEX 6.4
- ✅ KepServerEX 6.5
- ✅ KepServerEX 6.6 (tested with 6.6.350)
- ✅ KepServerEX 6.7 – 6.10
- ✅ KepServerEX 6.11 – 6.14+

No configuration changes are needed between versions. If you upgrade KepServerEX, just restart the broker service afterward.
