# PST Merge Tool

Enterprise-grade Windows tool to merge multiple PST files into a single master PST. Designed for recovering large mailboxes.

![License](https://img.shields.io/badge/license-MIT-blue.svg)
![Platform](https://img.shields.io/badge/platform-Windows-lightgrey.svg)
![.NET](https://img.shields.io/badge/.NET-4.5+-purple.svg)

## Features

- ✅ **Merge unlimited PST files** into one master PST
- ✅ **2TB size limit support** (removes the default 50GB Outlook restriction)
- ✅ **Preserves folder structure** (Inbox, Sent Items, subfolders, etc.)
- ✅ **Handles all item types** (Emails, Contacts, Calendar, Tasks, Notes)
- ✅ **Graceful cancellation** - stop safely without corrupting data
- ✅ **Persistent logging** - every action saved to a log file
- ✅ **Disk space validation** - warns before running out of space
- ✅ **Error resilience** - continues processing even if individual items fail

## Requirements

- Windows 10/11 or Windows Server
- Microsoft Outlook installed (uses Outlook Interop)
- .NET Framework 4.5 or later

## Usage

1. **Download** the latest release from [Releases](../../releases)
2. **Run** `PstMerger.exe`
3. **Click "Fix PST Size Limits"** to remove the 50GB restriction (one-time setup)
4. **Select source folder** containing your PST files
5. **Select destination** for the merged PST
6. **Click "Start Merge"** and wait for completion

> ⚠️ **Important**: Close Outlook before running the merge. Do not use the computer for heavy tasks during the merge process.

## Screenshots

*Coming soon*

## Building from Source

```bash
# Clone the repository
git clone https://github.com/YourUsername/PstMergeTool.git
cd PstMergeTool

# Build with MSBuild
msbuild PstMerger.csproj /p:Configuration=Release
```

The compiled executable will be in `bin\Release\PstMerger.exe`.

## How It Works

The tool uses Microsoft Outlook Interop to:
1. Open each source PST file
2. Recursively traverse all folders
3. Copy items to the destination PST, preserving folder structure
4. Safely close and detach each PST after processing

This ensures maximum compatibility and data integrity, as Outlook handles all the low-level PST format details.

## Registry Fix Details

By default, Outlook limits PST files to ~50GB. The "Fix PST Size Limits" button modifies:

```
HKEY_CURRENT_USER\Software\Microsoft\Office\15.0\Outlook\PST
HKEY_CURRENT_USER\Software\Microsoft\Office\16.0\Outlook\PST
```

Setting `MaxLargeFileSize` and `WarnLargeFileSize` to ~2TB.

## License

MIT License - see [LICENSE](LICENSE) file.

## Author

**Mithun** - [DataGuardNXT](https://dataguardnxt.com)

© 2026 DataGuardNXT. All Rights Reserved.
