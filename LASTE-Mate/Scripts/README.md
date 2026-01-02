# DCS Wind Export Script Installation

This script exports wind data from DCS World to a JSON file or via TCP socket for LASTE-Mate.

## Installation

1. Locate your DCS Saved Games folder:
   - **DCS OpenBeta**: `%USERPROFILE%\Saved Games\DCS.openbeta\`
   - **DCS Stable**: `%USERPROFILE%\Saved Games\DCS\`

2. Navigate to the `Scripts\Hooks\` folder (create it if it doesn't exist)

3. Copy `dcs_wind_export.lua` to this folder:
   - The full path should be:
     - `%USERPROFILE%\Saved Games\DCS.openbeta\Scripts\Hooks\dcs_wind_export.lua`
     - or `%USERPROFILE%\Saved Games\DCS\Scripts\Hooks\dcs_wind_export.lua`

4. Edit `dcs_wind_export.lua` and find the **CONFIGURATION** section at the top of the file (after the header comments) to configure the script (see Configuration section below)

## Configuration

Edit the `config` table in the **CONFIGURATION** section of `dcs_wind_export.lua` to change script behavior:

### Communication Mode

```lua
mode = "file",  -- or "tcp"
```

- **"file"**: Write JSON to `Scripts\Export\wind_data.json` (for file-based communication)
- **"tcp"**: Send JSON via TCP socket (for real-time communication, no file written)

### TCP Settings (only used when mode = "tcp")

```lua
tcp_host = "127.0.0.1",  -- TCP target host (localhost)
tcp_port = 10309,        -- TCP target port
```

### Logging Settings

```lua
log_overwrite = true,   -- true = overwrite log on restart, false = append
debug_mode = false,     -- true = verbose logging, false = essential only
```

- **log_overwrite**: Set to `true` to overwrite the log file each time DCS starts (prevents large log files). Set to `false` to append to existing log.
- **debug_mode**: Set to `true` for verbose debug logging (useful for troubleshooting). Set to `false` to only log errors, warnings, and successes.

## File Mode (Default)

When `mode = "file"`:
- JSON is written to: `%USERPROFILE%\Saved Games\DCS\Scripts\Export\wind_data.json`
- File updates when mission loads
- Use this mode with LASTE-Mate's "File-based (Read-only)" connection mode

## TCP Mode

When `mode = "tcp"`:
- JSON is sent via TCP to `<tcp_host>:<tcp_port>` every second
- **No JSON file is written** (only TCP communication)
- Receives and executes button press commands from LASTE-Mate
- Use this mode with LASTE-Mate's "TCP Socket (Real-time)" connection mode
- **Note**: TCP mode requires LuaSocket. You may need to modify `MissionScripting.lua` to allow socket access (see Troubleshooting)

## Multiplayer Setup

### Client-Side (Recommended)
- Place `dcs_wind_export.lua` in your client's `Scripts\Hooks\` folder
- The script will export wind data based on your client's view of the mission

### Server-Side
- Place `dcs_wind_export.lua` in the server's `Scripts\Hooks\` folder
- The script will export wind data from the server's perspective
- In file mode, all clients can read the same export file if it's on a shared location

## Verification

### File Mode
1. Start DCS and load a mission
2. Check that `wind_data.json` is created in the `Scripts\Export\` folder
3. The file should update when the mission loads
4. Open the Wind Correction Calculator and configure it to read from this file path

### TCP Mode
1. Start DCS and load a mission
2. Check the log file for "TCP client created" message
3. In LASTE-Mate, select "TCP Socket (Real-time)" connection mode and start the TCP server
4. The connection status should show as connected

## Troubleshooting

### File Mode Issues
- **File not updating**: Make sure DCS is running and a mission is loaded
- **Script errors**: Check DCS log files for Lua errors
- **Path issues**: Ensure the `Scripts\Hooks\` and `Scripts\Export\` folders exist

### TCP Mode Issues
- **"LuaSocket not available"**: You need to enable LuaSocket in DCS:
  1. Navigate to your DCS installation directory: `C:\Program Files\Eagle Dynamics\DCS World\Scripts\`
  2. Open `MissionScripting.lua` in a text editor
  3. Comment out or remove the line: `sanitizeModule('socket')`
  4. Save the file (note: DCS updates may overwrite this, requiring re-editing)
- **Connection not working**: 
  - Verify the TCP port matches in both the script config and LASTE-Mate
  - Make sure LASTE-Mate's TCP server is started
  - Check Windows Firewall isn't blocking the connection
  - Enable `debug_mode = true` in the script config to see detailed logs

### Log File Issues
- **Log file too large**: Set `log_overwrite = true` in the config file
- **Want to keep all logs**: Set `log_overwrite = false` in the config file

### General
- **Script not loading**: Ensure the file is in `Scripts\Hooks\` folder (not `Scripts\Export\`)
- **Config changes not taking effect**: Restart DCS after editing the configuration in the script file
