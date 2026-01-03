# DCS Wind Export Script Installation

This script exports wind data from DCS World via TCP socket for LASTE-Mate.

## Installation

1. Locate your DCS Saved Games folder:
   - **DCS Stable**: `%USERPROFILE%\Saved Games\DCS\`
   - **DCS OpenBeta**: `%USERPROFILE%\Saved Games\DCS.openbeta\`

2. Navigate to the `Scripts\Hooks\` folder (create it if it doesn't exist)

3. Copy `dcs_wind_export.lua` to this folder:
   - The full path should be:
     - `%USERPROFILE%\Saved Games\DCS.openbeta\Scripts\Hooks\dcs_wind_export.lua`
     - or `%USERPROFILE%\Saved Games\DCS.openbeta\Scripts\Hooks\dcs_wind_export.lua`

4. Edit `dcs_wind_export.lua` and find the **CONFIGURATION** section at the top of the file (after the header comments) to configure the script (see Configuration section below)

## Configuration

Edit the `config` table in the **CONFIGURATION** section of `dcs_wind_export.lua` to change script behavior:

### TCP Settings

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

## How It Works

- JSON is sent via TCP to `<tcp_host>:<tcp_port>` when the mission loads
- The script attempts to send the data up to 10 times with 1-second intervals until successful
- **Note**: Requires LuaSocket. You may need to modify `MissionScripting.lua` to allow socket access (see Troubleshooting)

## Multiplayer Setup

### Client-Side (Recommended)
- Place `dcs_wind_export.lua` in your client's `Scripts\Hooks\` folder
- The script will export wind data based on your client's view of the mission

### Server-Side
- Place `dcs_wind_export.lua` in the server's `Scripts\Hooks\` folder
- The script will export wind data from the server's perspective

## Verification

1. Start DCS and load a mission
2. Check the log file for "TCP client created" message
3. Ensure LASTE-Mate's TCP server is running (it starts automatically)
4. The connection status should show as connected after the mission loads

## Troubleshooting

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
- **Script errors**: Check DCS log files for Lua errors

### Log File Issues
- **Log file too large**: Set `log_overwrite = true` in the config file
- **Want to keep all logs**: Set `log_overwrite = false` in the config file

### General
- **Script not loading**: Ensure the file is in `Scripts\Hooks\` folder (not `Scripts\Export\`)
- **Config changes not taking effect**: Restart DCS after editing the configuration in the script file
