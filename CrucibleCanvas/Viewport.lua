-- Confines UIParent and WorldFrame to the Mainhand rectangle of a live topology (spike facts 1-2, spec §5.2).
-- Re-applied on size/scale events, after Blizzard's own UpdateUIParentPosition, and by a one-second drift watcher.
-- Never in combat: everything goes through ns.afterCombat. A stale or absent topology restores the full window.
local ADDON, ns = ...
local Geometry = ns.Geometry
local Viewport = { drift = 0, driftLog = {}, reason = "no topology" }
ns.Viewport = Viewport

local rects          -- the root-unit rects in force, or nil
local listeners = {}
local applied        -- what we last set, for the watcher: { cx, cy } of UIParent in root units
local everApplied = false

function Viewport.live() return rects end
-- Each listener runs guarded, so one that raises is reported once and cannot stop the ones after it.
function Viewport.onChange(fn)
    listeners[#listeners + 1] = ns.guard("viewport listener " .. (#listeners + 1), fn)
end

local function notify()
    for _, fn in ipairs(listeners) do fn(rects) end
end

local function setPoints(f, anchors)
    f:ClearAllPoints()
    for _, a in ipairs(anchors) do f:SetPoint(a[1], nil, a[2], a[3], a[4]) end
end

local function full(f)
    f:ClearAllPoints()
    f:SetPoint("TOPLEFT", nil, "TOPLEFT", 0, 0)
    f:SetPoint("BOTTOMRIGHT", nil, "BOTTOMRIGHT", 0, 0)
end

local function confine()
    local r = rects.mainhand
    setPoints(UIParent, Geometry.anchors(r, UIParent:GetEffectiveScale()))
    setPoints(WorldFrame, Geometry.anchors(r, 1))
    applied = { cx = (r.left + r.right) / 2, cy = (r.bottom + r.top) / 2 }
    everApplied = true
end

function Viewport.restore()
    rects, applied = nil, nil
    if everApplied then
        full(UIParent)
        full(WorldFrame)
    end
    notify()
end

function Viewport.apply()
    ns.afterCombat(function()
        local t = CrucibleCanvasTopology
        local w, h = GetPhysicalScreenSize()
        local ok, why = Geometry.isLive(t, w, h)
        if not ok then
            Viewport.reason = why
            if rects or everApplied then
                -- stale now, or stale after being live: back to the full window, once (and once more for a new
                -- reason, so a changed-size window is also restored)
                Viewport.restore()
            elseif type(t) == "table" and t.schema ~= nil then
                full(UIParent); full(WorldFrame); everApplied = true; notify()
            else
                notify()
            end
            return
        end
        Viewport.reason = "live"
        rects = Geometry.rects(t, h)
        confine()
        notify()
    end)
end

-- Something else moved UIParent? Re-apply and count it: /canvas diag shows the count and the last five (time, the
-- centre it was found at, root units). Out of combat only: in combat the watcher does nothing at all.
local DRIFT_KEEP = 5
local function watch()
    if not rects or not applied or ns.inCombat() then return end
    local cx, cy = UIParent:GetCenter()
    if not cx or not cy then return end
    local s = UIParent:GetEffectiveScale()
    cx, cy = cx * s, cy * s
    if math.abs(cx - applied.cx) > 1 or math.abs(cy - applied.cy) > 1 then
        Viewport.drift = Viewport.drift + 1
        local log = Viewport.driftLog
        log[#log + 1] = { time = date("%H:%M:%S"), cx = cx, cy = cy }
        if #log > DRIFT_KEEP then table.remove(log, 1) end
        confine()
    end
end

ns.onEvent("PLAYER_ENTERING_WORLD", function() Viewport.apply() end)
ns.onEvent("DISPLAY_SIZE_CHANGED", function() Viewport.apply() end)
ns.onEvent("UI_SCALE_CHANGED", function() Viewport.apply() end)
if type(hooksecurefunc) == "function" and type(UpdateUIParentPosition) == "function" then
    hooksecurefunc("UpdateUIParentPosition", function() if rects then Viewport.apply() end end)
end
C_Timer.NewTicker(1, ns.guard("viewport watch", watch))

-- UIParent's actual edges in root units; any edge the client does not report yet prints as "unknown".
local function edges(f)
    local l, b, r, t = f:GetLeft(), f:GetBottom(), f:GetRight(), f:GetTop()
    if not (l and b and r and t) then return "unknown" end
    local s = f:GetEffectiveScale()
    return string.format("%.0f,%.0f-%.0f,%.0f", l * s, b * s, r * s, t * s)
end

table.insert(ns.diagExtra, function()
    local r = rects and rects.mainhand
    local intended
    if r then
        intended = string.format("intended mainhand %.0f,%.0f-%.0f,%.0f", r.left, r.bottom, r.right, r.top)
    else
        local rw, rh = Geometry.rootSize(GetPhysicalScreenSize())
        intended = string.format("intended full window 0,0-%.0f,%.0f", rw, rh)
    end
    local lines = {
        "viewport: " .. Viewport.reason,
        "UIParent root " .. edges(UIParent) .. "; " .. intended,
        "drift events: " .. Viewport.drift,
    }
    if #Viewport.driftLog > 0 then
        local seen = {}
        for _, d in ipairs(Viewport.driftLog) do seen[#seen + 1] = string.format("%s at %.0f,%.0f", tostring(d.time), d.cx, d.cy) end
        lines[#lines + 1] = "last drifts: " .. table.concat(seen, "; ")
    end
    return lines
end)
