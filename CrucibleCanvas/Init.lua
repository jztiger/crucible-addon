-- Crucible Canvas: namespace, printing, the error guard, the three state sources (saved chunks, CVar stash,
-- Recovery.lua - highest revision wins, spec §5.5), events, combat queueing and the /canvas command.
-- Nothing here reloads the UI, touches a protected frame or reads another player.
local ADDON, ns = ...
local State, Geometry = ns.State, ns.Geometry

local PREFIX = "|cffe2b04aCrucible Canvas|r "
function ns.print(msg) print(PREFIX .. tostring(msg)) end

local reported = {}
-- One line per name until the next /canvas command, however often it fails.
function ns.report(name, err)
    if reported[name] then return end
    reported[name] = true
    ns.print(name .. " failed: " .. tostring(err))
end
function ns.guard(name, fn)
    return function(...)
        local ok, err = pcall(fn, ...)
        if not ok then ns.report(name, err) end
        return ok
    end
end
function ns.clearReports() reported = {} end

---------------------------------------------------------------------------------------------------
-- State: the document, the codec, the stash, saving
---------------------------------------------------------------------------------------------------
-- 11 pieces of 16384: the document is at most 128 KB of JSON, 4/3 of that as base64 (174763 <= 180224).
local STASH, STASH_REV, STASH_MAX = "crucibleCanvasState", "crucibleCanvasStateRevision", 11
-- Feature-detected: C_CVar, else the old globals. Every call goes through pcall, so with neither the stash is simply
-- empty and the addon still loads.
local cvar = C_CVar or { RegisterCVar = RegisterCVar, GetCVar = GetCVar, SetCVar = SetCVar }
ns.codec = {
    serialize = function(t) return C_EncodingUtil.SerializeJSON(t) end,
    deserialize = function(s) return C_EncodingUtil.DeserializeJSON(s) end,
    encode = function(s) return C_EncodingUtil.EncodeBase64(s) end,
    decode = function(s) return C_EncodingUtil.DecodeBase64(s) end,
}
ns.state = State.new()
ns.source = "new"
local revisions = { saved = 0, stash = 0, recovery = 0 }
-- Until ADDON_LOADED has read the three sources, ns.state is a placeholder: saving it would overwrite real data.
local loaded = false

local function registerStash()
    pcall(cvar.RegisterCVar, STASH_REV, "")
    for i = 1, STASH_MAX do pcall(cvar.RegisterCVar, STASH .. i, "") end
end

local function readStash()
    local ok, rev = pcall(cvar.GetCVar, STASH_REV)
    if not ok or type(rev) ~= "string" or rev == "" then return nil end
    local parts = {}
    for i = 1, STASH_MAX do
        local okP, p = pcall(cvar.GetCVar, STASH .. i)
        if not okP or type(p) ~= "string" or p == "" then break end
        parts[i] = p
    end
    local doc = State.decode(table.concat(parts), ns.codec)
    if doc and tostring(doc.revision) == rev then return doc end
    return nil
end

local function writeStash(b64, revision)
    for i = 1, STASH_MAX do
        local piece = b64:sub((i - 1) * State.CHUNK + 1, i * State.CHUNK)
        pcall(cvar.SetCVar, STASH .. i, piece)
    end
    pcall(cvar.SetCVar, STASH_REV, tostring(revision))
end

local function readSaved()
    local db = CrucibleCanvasDB
    if type(db) ~= "table" or type(db.chunks) ~= "table" then return nil end
    local b64 = State.fromChunks(db.chunks)
    return b64 and State.decode(b64, ns.codec) or nil
end

local function readRecovery()
    local r = CrucibleCanvasRecovery
    if type(r) ~= "table" or r.schema ~= 1 or type(r.state) ~= "string" then return nil end
    local doc = State.decode(r.state, ns.codec)
    if doc and doc.revision == r.revision then return doc end
    return nil
end

local refusalReported = false
function ns.saveNow()
    if not loaded then return false end
    local b64, why = State.encode(ns.state, ns.codec)
    if not b64 then
        -- nothing is written, so the last good save stays; said once a session, not on every change
        if not refusalReported then
            refusalReported = true
            ns.print("not saved: " .. tostring(why))
        end
        return false
    end
    CrucibleCanvasDB = CrucibleCanvasDB or {}
    CrucibleCanvasDB.chunks = State.chunks(b64, ns.state.revision)
    writeStash(b64, ns.state.revision)
    return true
end

local savePending = false
function ns.markChanged()
    if not loaded then return end
    State.bump(ns.state)
    if savePending then return end
    savePending = true
    C_Timer.After(1, ns.guard("save", function() savePending = false; ns.saveNow() end))
end

function ns.charKey() return tostring(UnitName("player")) .. "-" .. tostring(GetRealmName()) end

---------------------------------------------------------------------------------------------------
-- Combat: nothing moves while InCombatLockdown(); callers queue through afterCombat.
---------------------------------------------------------------------------------------------------
local queued = {}
function ns.inCombat() return InCombatLockdown() end
function ns.afterCombat(fn)
    if not InCombatLockdown() then fn() return true end
    queued[#queued + 1] = fn
    return false
end

---------------------------------------------------------------------------------------------------
-- Events
---------------------------------------------------------------------------------------------------
local frame = CreateFrame("Frame")
local handlers = {}
-- Guard names are the event and the handler's place, so a second module failing on the same event is reported too.
function ns.onEvent(event, fn)
    handlers[event] = handlers[event] or {}
    table.insert(handlers[event], ns.guard(event .. " " .. (#handlers[event] + 1), fn))
    frame:RegisterEvent(event)
end
frame:SetScript("OnEvent", function(_, event, ...)
    for _, fn in ipairs(handlers[event] or {}) do fn(...) end
end)

ns.onEvent("ADDON_LOADED", function(name)
    if name ~= ADDON then return end
    registerStash()
    local saved, stash, recovery = readSaved(), readStash(), readRecovery()
    revisions.saved, revisions.stash, revisions.recovery = saved and saved.revision or 0, stash and stash.revision or 0, recovery and recovery.revision or 0
    -- A tie goes to the first listed: saved wins only when it is strictly the highest (ruling R26).
    ns.state, ns.source = State.choose({ { name = "stash", doc = stash }, { name = "recovery", doc = recovery }, { name = "saved", doc = saved } })
    loaded = true
end)
ns.onEvent("PLAYER_LOGOUT", function() ns.saveNow() end)
ns.onEvent("PLAYER_REGEN_ENABLED", function()
    local run = queued
    queued = {}
    for _, fn in ipairs(run) do ns.guard("after combat", fn)() end -- one failure never drops the rest
end)

---------------------------------------------------------------------------------------------------
-- Diag and the command
---------------------------------------------------------------------------------------------------
ns.diagExtra = {}   -- later modules push functions returning lines

-- What the tray wrote, whether or not it is live: mode, when, which Companion, and the window size it was made for.
local function topologyLine()
    local t = CrucibleCanvasTopology
    if type(t) ~= "table" or t.schema == nil then return "no topology" end
    local win = type(t.window) == "table" and t.window or {}
    return string.format("topology: %s, generated %s by Crucible %s, window %sx%s", tostring(t.mode),
        tostring(t.generatedAt), tostring(t.companionVersion), tostring(win.width), tostring(win.height))
end

function ns.diagLines()
    local lines = {}
    local w, h = GetPhysicalScreenSize()
    local rw, rh = Geometry.rootSize(w, h)
    lines[#lines + 1] = string.format("Crucible Canvas on build %s, interface %s", tostring((select(1, GetBuildInfo()))), tostring((select(4, GetBuildInfo()))))
    lines[#lines + 1] = string.format("window %dx%d px, root %.1fx%.1f, UIParent scale %.3f", w, h, rw, rh, UIParent:GetEffectiveScale())
    lines[#lines + 1] = topologyLine()
    lines[#lines + 1] = string.format("source: %s; revisions: saved %d, stash %d, recovery %d; now %d", ns.source, revisions.saved, revisions.stash, revisions.recovery, ns.state.revision)
    for _, fn in ipairs(ns.diagExtra) do for _, l in ipairs(fn()) do lines[#lines + 1] = l end end
    return lines
end

ns.commands = {}
function ns.command(cmd, msg)
    if cmd == "diag" then
        for _, l in ipairs(ns.diagLines()) do ns.print(l) end
        return true
    end
    local fn = ns.commands[cmd]
    if fn then fn(msg) return true end
    return false
end

SLASH_CRUCIBLECANVAS1 = "/canvas"
SlashCmdList.CRUCIBLECANVAS = function(msg)
    ns.clearReports()
    local cmd = ((msg or ""):match("^(%S*)") or ""):lower()
    if ns.command(cmd, msg) then return end
    ns.print("commands: /canvas (options), /canvas wizard, /canvas gather, /canvas diag")
end
