-- DCS Mission Wind Export (Unified - File and TCP modes)
--
-- PURPOSE
--   Exports the *mission/briefing* wind layers (Ground / 2000m / 8000m) to JSON for an external
--   wind correction calculator. Supports both file-based and TCP socket communication modes.
--
-- IMPORTANT (Multiplayer)
--   In multiplayer, LoGetWindAtPoint() at/near ground can jitter and is not suitable for CDU entry.
--   This script intentionally DOES NOT query live wind vectors.
--
-- INSTALL
--   1. Put this file here (create folders if missing):
--        %USERPROFILE%\Saved Games\DCS\Scripts\Hooks\dcs_wind_export.lua
--   2. Edit the CONFIGURATION section below to set mode, TCP port, and logging options
--
-- OUTPUT (File Mode)
--   JSON written to:
--     %USERPROFILE%\Saved Games\DCS\Scripts\Export\wind_data.json
--
-- OUTPUT (TCP Mode)
--   JSON sent via TCP to: <tcp_host>:<tcp_port> (configured in CONFIGURATION section)
--   JSON file is NOT written in TCP mode
--
-- LOG FILE
--   %USERPROFILE%\Saved Games\DCS\Logs\wind_export_debug.log
--
-- DATA NOTES
--   The mission file stores wind "dir" as NAV direction (direction wind is blowing TO).
--   DCS briefing shows both NAV and METEO (FROM). We export:
--     - direction     = METEO (FROM)  = (nav + 180) % 360
--     - navDirection  = NAV   (TO)    = raw mission dir
--
-- BEHAVIOR
--   - Attempts to export/send up to MAX_SEND_ATTEMPTS times once the simulation starts.
--   - Stops after first successful send, or after exhausting attempts, or when leaving the simulation.

local lfs = require("lfs")

-- ============================================================================
-- CONFIGURATION
-- ============================================================================

local config = {
    -- Communication mode: "file" or "tcp"
    mode = "tcp",

    -- TCP configuration (only used when mode = "tcp")
    tcp_host = "127.0.0.1",
    tcp_port = 10309,

    -- Logging configuration
    log_overwrite = true,  -- Overwrite log on restart (prevents large files)
    debug_mode = true,     -- Enable verbose debug logging
}

-- ============================================================================
-- END OF CONFIGURATION
-- ============================================================================

-- Paths
local outJson = lfs.writedir() .. [[Scripts\Export\wind_data.json]]
local logPath = lfs.writedir() .. [[Logs\wind_export_debug.log]]


-- If log_overwrite is true, truncate the log at script load and at mission/session resets.
local logInitialized = false

local function truncate_log(reason)
    if not config.log_overwrite then
        return
    end
    local f = io.open(logPath, "w")
    if f then
        f:write(os.date("[%Y-%m-%d %H:%M:%S] ") .. "LOG RESET: " .. tostring(reason) .. "\n")
        f:close()
    end
    -- After truncating once, all further writes should append.
    logInitialized = true
end

truncate_log("script load")


-- TCP state (only used in TCP mode)
local tcpClient = nil

-- Retry / lifecycle state
local MAX_SEND_ATTEMPTS = 10
local RETRY_INTERVAL_SEC = 1

local state = {
    inSimulation = false,   -- true between onSimulationStart and onSimulationStop
    sending = false,        -- retry loop active
    done = false,           -- terminal for current mission (success or failure)
    sent = false,           -- successfully exported once
    attempts = 0,
    nextTryAt = 0,
}

-- Logging function
local function log(msg)
    if not config.debug_mode and not msg:match("ERROR") and not msg:match("SUCCESS") and not msg:match("WARNING") then
        return -- Skip non-essential messages when debug_mode is false
    end

    local mode = logInitialized and "a" or (config.log_overwrite and "w" or "a")
    local f = io.open(logPath, mode)
    if f then
        if not logInitialized then
            logInitialized = true
        end
        f:write(os.date("[%Y-%m-%d %H:%M:%S] ") .. tostring(msg) .. "\n")
        f:close()
    end
end

-- Minimal JSON encoder (objects only; enough for our export shape)
local function json_escape(s)
    return tostring(s)
        :gsub("\\", "\\\\")
        :gsub('"', '\\"')
        :gsub("\n", "\\n")
        :gsub("\r", "\\r")
        :gsub("\t", "\\t")
end

local function json_encode(v)
    local t = type(v)
    if t == "nil" then return "null" end
    if t == "number" then return tostring(v) end
    if t == "boolean" then return v and "true" or "false" end
    if t == "string" then return '"' .. json_escape(v) .. '"' end

    if t == "table" then
        local parts = {}
        for k, val in pairs(v) do
            table.insert(parts, '"' .. json_escape(k) .. '":' .. json_encode(val))
        end
        return "{" .. table.concat(parts, ",") .. "}"
    end

    return '"<unsupported>"'
end

local function norm360(deg)
    deg = (deg or 0) % 360
    if deg < 0 then deg = deg + 360 end
    return deg
end

local function round1(x)
    if x == nil then return nil end
    return math.floor(x * 10 + 0.5) / 10
end

local function ensure_export_dir()
    local base = lfs.writedir() .. [[Scripts]]
    if not lfs.attributes(base) then lfs.mkdir(base) end

    local dir = lfs.writedir() .. [[Scripts\Export]]
    if not lfs.attributes(dir) then lfs.mkdir(dir) end
end

local function get_current_mission_weather()
    local opts = LoGetMissionOptions and LoGetMissionOptions()
    if opts then
        if opts.weather and opts.weather.wind then
            return opts.weather, opts
        else
            if config.debug_mode then
                if opts.weather then
                    log("DEBUG: opts.weather exists but opts.weather.wind is missing")
                else
                    log("DEBUG: opts.weather is missing")
                end
            end
        end
    end

    if DCS and DCS.getCurrentMission then
        local cm = DCS.getCurrentMission()
        if cm and cm.mission then
            local m = cm.mission
            local weather = m.weather
            if weather and weather.wind then
                return weather, m
            end
        end
    end

    return nil, "mission wind data not available"
end

local function layer_from_mission(src)
    if not src then return nil, "layer table missing" end

    local speed = tonumber(src.speed)
    local navDir = tonumber(src.dir)

    if speed == nil or navDir == nil then
        return nil, "layer missing speed/dir"
    end

    navDir = norm360(navDir)
    local meteoDir = norm360(navDir + 180)

    return {
        speed = round1(speed),
        direction = round1(meteoDir),
        navDirection = round1(navDir),
    }
end

-- Initialize TCP socket (only in TCP mode)
local function init_tcp()
    if config.mode ~= "tcp" then
        return false
    end

    local success, socket = pcall(function()
        return require("socket")
    end)

    if not success or not socket then
        log("WARNING: LuaSocket not available. TCP communication disabled.")
        log("  To enable TCP, you may need to modify MissionScripting.lua")
        return false
    end

    success, tcpClient = pcall(function()
        local client = socket.tcp()
        client:settimeout(1) -- 1 second timeout for connect/send
        return client
    end)

    if not success or not tcpClient then
        log("ERROR: Failed to create TCP client: " .. tostring(tcpClient))
        return false
    end

    log("TCP client created, will connect to " .. config.tcp_host .. ":" .. tostring(config.tcp_port))
    return true
end

local function close_tcp()
    if tcpClient then
        pcall(function() tcpClient:close() end)
        tcpClient = nil
        if config.debug_mode then
            log("DEBUG: TCP client closed")
        end
    end
end

-- Send data via TCP (only in TCP mode)
local function send_tcp_data(data)
    if config.mode ~= "tcp" or not tcpClient then
        if config.debug_mode then
            log("DEBUG: send_tcp_data skipped - mode=" .. tostring(config.mode) .. ", tcpClient=" .. tostring(tcpClient))
        end
        return false
    end

    local json = json_encode(data)
    local success, result = pcall(function()
        -- Check if connected, connect if not
        if not tcpClient:getpeername() then
            local ok, err = tcpClient:connect(config.tcp_host, config.tcp_port)
            if not ok then
                return nil, "connect failed: " .. tostring(err)
            end
            if config.debug_mode then
                log("DEBUG: TCP connected to " .. config.tcp_host .. ":" .. tostring(config.tcp_port))
            end
        end

        -- Send data (newline-delimited)
        local bytes, err = tcpClient:send(json .. "\n")
        if not bytes then
            -- Connection might be broken, try to reconnect once
            close_tcp()
            local socket = require("socket")
            tcpClient = socket.tcp()
            tcpClient:settimeout(1)

            local ok2, err2 = tcpClient:connect(config.tcp_host, config.tcp_port)
            if not ok2 then
                return nil, "reconnect failed: " .. tostring(err2)
            end

            bytes, err = tcpClient:send(json .. "\n")
            if not bytes then
                return nil, "send after reconnect failed: " .. tostring(err)
            end
        end

        return true
    end)

    if not success then
        log("ERROR: TCP send error: " .. tostring(result))
        return false
    end

    if result ~= true then
        log("ERROR: TCP send failed: " .. tostring(result))
        return false
    end

    if config.debug_mode then
        log("DEBUG: TCP data sent successfully to " .. config.tcp_host .. ":" .. tostring(config.tcp_port))
    end

    return true
end

local function export_mission_wind(reason)
    local weather, mOrErr = get_current_mission_weather()
    if not weather then
        return nil, mOrErr
    end

    local wind = weather.wind

    local g, ge = layer_from_mission(wind.atGround)
    local w2, w2e = layer_from_mission(wind.at2000)
    local w8, w8e = layer_from_mission(wind.at8000)

    if not g then return nil, "atGround: " .. tostring(ge) end
    if not w2 then return nil, "at2000: " .. tostring(w2e) end
    if not w8 then return nil, "at8000: " .. tostring(w8e) end

    local data = {
        timestamp = os.time(),
        source = "mission",
        ground = g,
        at2000m = w2,
        at8000m = w8,
    }

    if type(mOrErr) == "table" then
        data.mission = {
            theatre = mOrErr.theatre or mOrErr.Theatre,
            sortie = mOrErr.sortie or mOrErr.Sortie,
            start_time = mOrErr.start_time or mOrErr.StartTime,
        }
    end

    local temp = weather.season and weather.season.temperature
    if temp ~= nil then
        data.groundTemp = math.floor(tonumber(temp) + 0.5)
    end

    -- Export based on mode
    if config.mode == "tcp" then
        local sent = send_tcp_data(data)
        if not sent then
            return nil, "TCP send failed"
        end
    else
        ensure_export_dir()
        local f = io.open(outJson, "w")
        if not f then
            return nil, "cannot write " .. outJson
        end
        f:write(json_encode(data))
        f:close()
    end

    log(string.format(
        "SUCCESS (%s): ground=%0.1f m/s | NAV=%0.1f° METEO=%0.1f° | 2000m NAV=%0.1f° METEO=%0.1f° | 8000m NAV=%0.1f° METEO=%0.1f°",
        tostring(reason),
        data.ground.speed, data.ground.navDirection, data.ground.direction,
        data.at2000m.navDirection, data.at2000m.direction,
        data.at8000m.navDirection, data.at8000m.direction
    ))

    return data, nil
end

-- -----------------------------------------------------------------------------
-- Retry / lifecycle helpers
-- -----------------------------------------------------------------------------

local function reset_state(reason)
    truncate_log(reason)
    state.inSimulation = false
    state.sending = false
    state.done = false
    state.sent = false
    state.attempts = 0
    state.nextTryAt = 0
    close_tcp()

    if config.debug_mode then
        log("STATE: reset (" .. tostring(reason) .. ")")
    end

    if config.mode == "tcp" then
        -- re-create TCP client for the next mission/session
        init_tcp()
    end
end

local function stop_sending(reason)
    state.sending = false
    state.done = true
    if config.debug_mode then
        log("STATE: stop_sending (" .. tostring(reason) .. ")")
    end
end

local function start_sending(reason)
    if state.sent or state.done then
        return
    end
    state.sending = true
    state.attempts = 0
    state.nextTryAt = 0
    if config.debug_mode then
        log("STATE: start_sending (" .. tostring(reason) .. ")")
    end
end

local function try_send(reason, countAttempt)
    if state.sent or state.done then
        return
    end

    local data, err = export_mission_wind(reason)
    if data then
        state.sent = true
        state.sending = false
        state.done = true
        return
    end

    if countAttempt then
        state.attempts = state.attempts + 1
        if config.debug_mode then
            log(string.format("INFO: export/send failed (%s): %s (attempt %d/%d)",
                tostring(reason), tostring(err), state.attempts, MAX_SEND_ATTEMPTS))
        end

        if state.attempts >= MAX_SEND_ATTEMPTS then
            log("ERROR: Could not export mission wind after " .. tostring(MAX_SEND_ATTEMPTS) .. " attempts")
            stop_sending("maxAttemptsReached")
        end
    else
        if config.debug_mode then
            log(string.format("INFO: wind not available yet (%s): %s (will retry after sim start)",
                tostring(reason), tostring(err)))
        end
    end
end

-- -----------------------------------------------------------------------------
-- Callback wiring (Hooks environment)
-- -----------------------------------------------------------------------------

-- Initialize TCP on script load (if in TCP mode)
local tcpInitSuccess = false
if config.mode == "tcp" then
    tcpInitSuccess = init_tcp()
    if not tcpInitSuccess then
        log("ERROR: Failed to initialize TCP socket. Check LuaSocket availability and MissionScripting.lua")
    end
end

log("wind export hook loaded (mode: " .. config.mode .. ", tcp_init: " .. tostring(tcpInitSuccess) .. ", port: " .. tostring(config.tcp_port) .. ")")

local callbacks = {}

callbacks.onMissionLoadBegin = function()
    reset_state("onMissionLoadBegin")
end

callbacks.onNetMissionChanged = function()
    reset_state("onNetMissionChanged")
end

callbacks.onNetDisconnect = function()
    reset_state("onNetDisconnect")
end

callbacks.onMissionLoadEnd = function()
    -- Best-effort single try while still in the UI (does not consume attempts).
    -- If it isn't available yet (common in MP before a slot is picked), we'll retry once simulation starts.
    try_send("onMissionLoadEnd", false)
end

callbacks.onSimulationStart = function()
    state.inSimulation = true
    start_sending("onSimulationStart")
    -- immediate first attempt (counts)
    try_send("onSimulationStart", true)
end

callbacks.onPlayerStart = function()
    -- Some MP flows are more reliably "ready" here; start sending if not already started.
    start_sending("onPlayerStart")
    try_send("onPlayerStart", true)
end

callbacks.onPlayerChangeSlot = function()
    -- If the player changes slots, re-arm sending if we haven't succeeded yet.
    if not state.sent then
        start_sending("onPlayerChangeSlot")
        try_send("onPlayerChangeSlot", true)
    end
end

callbacks.onPlayerStop = function()
    -- Player left the unit; stop to avoid background work.
    stop_sending("onPlayerStop")
end

callbacks.onSimulationStop = function()
    -- Exiting the 3D world back to UI; stop and close sockets.
    state.inSimulation = false
    stop_sending("onSimulationStop")
    close_tcp()
end

callbacks.onSimulationFrame = function()
    if state.done or not state.sending then
        return
    end

    local now = os.time()
    if now >= state.nextTryAt then
        state.nextTryAt = now + RETRY_INTERVAL_SEC
        try_send("retry", true)
    end
end

DCS.setUserCallbacks(callbacks)
