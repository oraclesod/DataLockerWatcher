# DataLockerWatcher

DataLockerWatcher is a Windows solution designed to detect the insertion of approved DataLocker USB devices, guide the user through unlock and sync workflows, and securely synchronize files to and from an approved network location.

The solution consists of **three components**:

1. **Installer** – SYSTEM-level installer responsible for setup, upgrade, repair, and uninstall  
2. **Agent** – User logon-triggered background process  
3. **Sync** – On-demand worker process that performs file synchronization  

This document describes installation, upgrade behavior, configuration, and operational flow.

---

# 1. Components Overview

## Installer
- Runs elevated as **SYSTEM** or **Administrator**
- Installs binaries to:

`C:\Program Files\DataLockerWatcher`

Creates and maintains:
- Windows Event Log sources
- Start Menu shortcut with **AUMID** for toast activation
- **HKLM Run** entry to initialize the Agent at every user logon
- Local install copy of the installer for future repair or uninstall

If an interactive user session is already active, the installer attempts to launch the Agent immediately in that session.

The installer also handles:
- fresh installs
- upgrades
- repairs
- uninstall cleanup

---

## Agent

Runs in the **interactive user session**.

Responsibilities:
- Starts automatically at logon
- Registers a **WMI watcher** for USB insertion events
- Detects approved DataLocker devices
- Displays toast notifications guiding the user
- Launches the **Sync** worker when required

---

## Sync

The Sync component performs the actual synchronization.

Responsibilities:
- Locates the unlocked DataLocker USB drive
- Validates access to the configured source
- Synchronizes files to the USB device
- Reports status via toast notifications and logs

Sync supports **two operating modes**:

| Mode | Description |
|-----|-------------|
| 1 | Direct folder sync from a network share |
| 2 | Encrypted ZIP extraction followed by sync |

---

# 2. System Requirements

- Windows 10 1809 (build 17763) or later
- .NET 8 runtime if publishing framework-dependent binaries
- User must have access to the configured network share
- DataLocker USB device must be unlocked before sync
- Installer must run elevated

If you publish the applications as self-contained, a separate .NET runtime install is not required.

---

# 3. Build Output

Example release outputs:

- `Install.exe`
- `DataLockerWatcher-Agent.exe`
- `DataLockerWatcher-Sync.exe`
- `config.json`
- `DataLocker.ico` *(optional)*
- `DataLocker.png` *(optional)*

In the current packaging model, the installer is typically compiled or packaged as **`Install.exe`** for deployment convenience, while the installed Agent and Sync binaries retain their full names.

---

# 4. Installation and Command-Line Usage

## 4.1 Installer Command-Line Modes

The installer accepts exactly one required argument:

```text
Install.exe install
Install.exe repair
Install.exe uninstall
```

Valid modes:
- `install` – fresh install or upgrade
- `repair` – reinstall or refresh an existing install
- `uninstall` – remove the product

Any invalid or missing argument returns exit code `2`.

---

## 4.2 Install Mode

Command:

```text
Install.exe install
```

Install mode performs the following:
- Creates the install directory:
  - `C:\Program Files\DataLockerWatcher`
- Ensures Event Log sources exist:
  - `DataLockerWatcher-Install`
  - `DataLockerWatcher-Agent`
  - `DataLockerWatcher-Sync`
- Detects whether an existing installation is already present
- If an existing install is found:
  - checks for running `DataLockerWatcher-Sync.exe`
  - if Sync is running, waits up to **10 minutes**, checking every **5 seconds**
  - if Sync is still running after 10 minutes, exits with code `1`
  - if Sync is not running, or exits within that window, stops all running `DataLockerWatcher-Agent.exe` processes
  - if Agent processes do not stop within **30 seconds**, exits with code `1`
- Copies payload files into the install directory:
  - `Install.exe`
  - `DataLockerWatcher-Agent.exe`
  - `DataLockerWatcher-Sync.exe`
  - `config.json`
  - `DataLocker.ico` if present
  - `DataLocker.png` if present
- Creates the all-users Start Menu shortcut:
  - `DataLocker Watcher\DataLocker Watcher - Agent`
- Registers the HKLM Run value:
  - `DataLockerWatcher-Agent-Init`
- Configures the Run command to launch:

```text
"C:\Program Files\DataLockerWatcher\DataLockerWatcher-Agent.exe" --init
```

- Attempts to immediately start `DataLockerWatcher-Agent.exe --init` in the active user session

No reboot is required.

---

## 4.3 Repair Mode

Command:

```text
Install.exe repair
```

Repair mode uses the same operational flow as install, but is intended to refresh an existing deployment.

Typical use cases:
- restore missing binaries
- refresh `config.json`
- recreate the Start Menu shortcut
- recreate the HKLM Run registration
- replace binaries after a failed or partial deployment

---

## 4.4 Uninstall Mode

Command:

```text
Install.exe uninstall
```

Uninstall mode performs the following:
- Removes the HKLM Run value `DataLockerWatcher-Agent-Init`
- Removes the Start Menu folder `DataLocker Watcher`
- Stops running Agent processes on a best-effort basis
- Removes the active user HKCU Run value `DataLockerWatcher-Agent` on a best-effort basis
- Deletes:
  - `Install.exe`
  - `DataLockerWatcher-Agent.exe`
  - `DataLockerWatcher-Sync.exe`
  - `config.json`
  - `DataLocker.ico`
  - `DataLocker.png`
- Leaves `Install.log` in place for troubleshooting and forensic review unless manually removed
- Removes the install directory if it is empty

---

## 4.5 Exit Codes

| Exit Code | Meaning |
|---|---|
| 0 | Success |
| 1 | Runtime failure, timeout, or fatal installer error |
| 2 | Invalid usage or unsupported command-line argument |

---

# 5. Installation Behavior During Upgrade

When an existing installation is detected, the installer follows this sequence:

1. Check whether `DataLockerWatcher-Sync.exe` is running  
2. If Sync is running, wait up to **10 minutes** for it to finish  
3. If Sync does not exit within 10 minutes, abort and return exit code `1`  
4. If Sync is not running, or exits in time, stop all running Agent processes  
5. If Agent processes do not stop within **30 seconds**, abort and return exit code `1`  
6. Copy the new payload files  
7. Recreate shortcut and startup registration  
8. Start Agent initialization in any active interactive session  

This protects in-progress sync operations while still allowing in-place upgrades from Intune or another management platform.

---

# 6. Installed File Layout

Default install path:

`C:\Program Files\DataLockerWatcher`

Typical contents:

```text
C:\Program Files\DataLockerWatcher\
    Install.exe
    DataLockerWatcher-Agent.exe
    DataLockerWatcher-Sync.exe
    config.json
    DataLocker.ico
    DataLocker.png
    Install.log
```

---

# 7. Configuration

## 7.1 config.json

The configuration file must exist beside the installed binaries:

`C:\Program Files\DataLockerWatcher\config.json`

Example:

```json
{
  "Device": {
    "PnpDeviceIdContainsAny": [
      "VID_1234",
      "DATALOCKER"
    ]
  },
  "Unlock": {
    "ExeName": "SafeConsole.exe",
    "ToastOnDetected": true
  },
  "Sync": {
    "Source": "\\fileserver\\secure-share\\Data",
    "TargetFolder": "Data",
    "Mode": 1,
    "Key": ""
  }
}
```

### Configuration Notes

- `Device.PnpDeviceIdContainsAny` defines the approved USB device identifiers
- `Unlock.ExeName` identifies the DataLocker unlock application
- `Sync.Source` is the authoritative source path
- `Sync.TargetFolder` is the destination folder created or used on the DataLocker device
- `Sync.Mode` selects direct sync or encrypted ZIP extraction workflow
- `Sync.Key` is used when the selected sync mode requires a decryption key

---

# 8. Logging and Troubleshooting

## 8.1 Windows Event Log

Primary installer logging is written to the Windows **Application** log using the source:

- `DataLockerWatcher-Install`

The Agent and Sync components can also write to their own sources:
- `DataLockerWatcher-Agent`
- `DataLockerWatcher-Sync`

---

## 8.2 Install Log File

Installer file logging is written to:

`C:\Program Files\DataLockerWatcher\Install.log`

This log is used for troubleshooting and is retained during uninstall by default.

---

## 8.3 Common Upgrade Failure Conditions

The installer returns exit code `1` in these common cases:
- Sync is still running after the 10-minute wait period
- Agent is still running after the 30-second stop window
- a required payload file is missing from the install package
- a fatal exception occurs during install, repair, or uninstall

---

# 9. Operational Flow Summary

## 9.1 Logon Flow

1. User signs in  
2. HKLM Run launches `DataLockerWatcher-Agent.exe --init`  
3. Agent performs any per-user initialization  
4. Agent continues running in the interactive session  
5. Agent registers for USB insertion notifications  

---

## 9.2 Device Workflow

1. Approved DataLocker device is inserted  
2. Agent detects the device  
3. User is guided to unlock the device if required  
4. Once unlocked, Agent launches the Sync worker  
5. Sync validates configuration and source accessibility  
6. Sync copies or extracts content according to configured mode  
7. Status is reported through notifications and logs  

---

# 10. Deployment Notes

For managed deployment platforms such as **Intune**:
- run the installer elevated
- include all payload files in the package
- use `Install.exe install` for install and upgrade
- use `Install.exe uninstall` for removal
- detection can be based on the installed files, registry values, or a combination of both

Because the installer copies itself into the install folder, the locally installed `Install.exe` can be used later for repair or uninstall operations.
