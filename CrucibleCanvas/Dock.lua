-- Docked panels (spec §5.4): a Blizzard frame that opens on the canvas, is dragged by its title, remembers where it
-- was (per character, as fractions of the canvas) and comes back there. Detached from the panel manager by clearing
-- its UIPanelLayout-area attribute (spike fact 4), kept on Blizzard's Escape list through UISpecialFrames.
-- Never a protected frame; a frame that is not there is skipped and listed in /canvas diag. Panels keep their own
-- strata (MEDIUM/HIGH), so they always draw above the BACKGROUND canvas; place() only raises them within it.
local ADDON, ns = ...
local Geometry, State = ns.Geometry, ns.State
local Dock = { attached = {}, missing = {}, protected = {} }
ns.Dock = Dock

-- size: the panel resizes itself on show (the map), so its size is remembered and re-applied. reopen = false: an
-- interaction panel (mail, a vendor, loot...) that means nothing without its NPC or object, never reopened at login.
Dock.PANELS = {
    { name = "WorldMapFrame", default = true, size = true }, { name = "CharacterFrame", default = true },
    { name = "PlayerSpellsFrame", default = true }, { name = "ProfessionsFrame", default = true },
    { name = "AchievementFrame", default = true }, { name = "CollectionsJournal", default = true },
    { name = "FriendsFrame", default = true }, { name = "GuildFrame", default = true },
    { name = "CommunitiesFrame", default = true }, { name = "MailFrame", default = true, reopen = false },
    { name = "SettingsPanel", default = true }, { name = "EncounterJournal", default = true },
    { name = "PVEFrame", default = true },
    { name = "MerchantFrame", default = false, reopen = false }, { name = "BankFrame", default = false, reopen = false },
    { name = "AuctionHouseFrame", default = false, reopen = false },
    { name = "ClassTrainerFrame", default = false, reopen = false },
    { name = "QuestFrame", default = false, reopen = false }, { name = "GossipFrame", default = false, reopen = false },
    { name = "TradeFrame", default = false, reopen = false }, { name = "LootFrame", default = false, reopen = false },
}
local defaults, sizes, noReopen = {}, {}, {}
for _, p in ipairs(Dock.PANELS) do
    defaults[p.name] = p.default
    sizes[p.name] = p.size == true
    noReopen[p.name] = p.reopen == false
end

local records = {}   -- name -> { frame, area, clamped, scale } while attached
local hooked = {}    -- name -> true once its scripts are hooked; hooks cannot be removed, so this outlives detach
local reopened = false

-- The map reports maximised while Blizzard has it full-screen: its saved (minimised) size is then neither applied
-- nor overwritten.
local function maximized(f) return f.IsMaximized and f:IsMaximized() and true or false end

function Dock.enabled(name)
    local o = ns.state.options.dock[name]
    if o ~= nil then return o == true end
    return defaults[name] == true
end

local function saved(name) return State.panel(ns.state, ns.charKey(), name) end

local function remember(name, fields)
    State.setPanel(ns.state, ns.charKey(), name, fields)
    ns.markChanged()
end

-- Centre of a frame in root units.
local function centre(f)
    local cx, cy = f:GetCenter()
    if not cx then return nil end
    local s = f:GetEffectiveScale()
    return cx * s, cy * s
end

-- A maximised map is fitted to the canvas by its SCALE, never its size (UAT row 19). Blizzard sizes it from UIParent
-- (UpdateMaximizedSize) and fits its art to that own-unit size only in OnFrameSizeChanged - the map has no
-- OnSizeChanged - so an outside SetSize would leave the art fitted to the old size, and calling Blizzard's map code from
-- here would run it on an addon path. Its cursor maths divide by its own effective scale (MapCanvas
-- GetCursorPosition), so a scaled map clicks true. The factor uses the parent's effective scale, so placing again does
-- not compound it.
local function fitToCanvas(f, r)
    local parent = f:GetParent()
    local ps = parent and parent:GetEffectiveScale() or 1
    local w, h = f:GetWidth(), f:GetHeight()
    if not (w and h and w > 0 and h > 0 and ps > 0) then return end
    f:SetScale(math.min((r.right - r.left) / (w * ps), (r.top - r.bottom) / (h * ps)))
end

-- A maximised map shows its BlackoutFrame, which Blizzard anchors over UIParent - with the canvas live, the whole
-- Mainhand. Docked, it covers the canvas instead (same two points, against the canvas frame); handed back, it gets
-- Blizzard's own anchors again (Blizzard_WorldMap.xml: TOPLEFT and BOTTOMRIGHT, relativeTo UIParent). Never hidden.
local function blackoutTo(rec, to)
    local b = rec.frame.BlackoutFrame
    if type(b) ~= "table" or type(b.SetPoint) ~= "function" then return end
    b:ClearAllPoints()
    b:SetPoint("TOPLEFT", to, "TOPLEFT", 0, 0)
    b:SetPoint("BOTTOMRIGHT", to, "BOTTOMRIGHT", 0, 0)
    rec.blackoutMoved = to ~= UIParent
end

function Dock.place(name)
    local rec = records[name]
    local r = ns.Canvas.rect()
    if not rec or not r then return end
    local f = rec.frame
    local p = saved(name) or {}
    if sizes[name] then
        if maximized(f) then
            fitToCanvas(f, r)
            blackoutTo(rec, ns.Canvas.frame())
        else
            if f:GetScale() ~= rec.scale then f:SetScale(rec.scale) end
            if p.w and p.h then f:SetSize(p.w, p.h) end
        end
    end
    local s = f:GetEffectiveScale()
    local w, h = f:GetWidth() * s, f:GetHeight() * s
    local cx, cy = Geometry.fromFraction(r, p.x or 0.5, p.y or 0.5)
    cx, cy = Geometry.clamp(r, cx, cy, w, h)
    f:ClearAllPoints()
    -- CENTER against the canvas's BOTTOMLEFT. SetPoint offsets are in the anchored frame's own units, so the root
    -- distance from the canvas corner is divided by the frame's effective scale.
    f:SetPoint("CENTER", ns.Canvas.frame(), "BOTTOMLEFT", (cx - r.left) / s, (cy - r.bottom) / s)
    f:Raise()
end

local function onShow(f)
    local name = f:GetName()
    if not records[name] then return end
    Dock.place(name)
    remember(name, { open = true })
end

local function onHide(f)
    local name = f:GetName()
    if not records[name] then return end
    remember(name, { open = false })
end

local function onDragStart(f)
    if records[f:GetName()] and f:IsMovable() then f:StartMoving() end
end

local function onDragStop(f)
    local name = f:GetName()
    if not records[name] then return end
    f:StopMovingOrSizing()
    if f.SetUserPlaced then f:SetUserPlaced(false) end -- our drags stay out of the client's own layout cache
    -- R30: a maximised map's centre is not a spot to remember; it goes back on the canvas, and Minimize finds the old one
    if maximized(f) then Dock.place(name) return end
    local r = ns.Canvas.rect()
    if not r then return end
    local cx, cy = centre(f)
    if not cx then return end
    local s = f:GetEffectiveScale()
    cx, cy = Geometry.clamp(r, cx, cy, f:GetWidth() * s, f:GetHeight() * s)
    local fx, fy = Geometry.toFraction(r, cx, cy)
    local fields = { x = fx, y = fy }
    if sizes[name] and not maximized(f) then fields.w, fields.h = f:GetWidth(), f:GetHeight() end
    remember(name, fields)
    Dock.place(name)
end

-- Always HookScript, never SetScript: the frame's own script (Blizzard's resize, say) runs first, ours after. Each
-- hook is guarded, so a failure is one printed line, not a Lua error inside Blizzard's own show or drag.
-- The map's Maximize/Minimize go through the panel manager, which re-anchors it to UIParent's TOP (the Mainhand)
-- without an OnShow: placed again on the next frame, after Blizzard's own re-anchor (spec §11 risk 2). Its
-- UpdateMaximizedSize (called by Maximize, and on DISPLAY_SIZE_CHANGED / UI_SCALE_CHANGED) re-sizes it from UIParent:
-- fitted to the canvas again after it. SynchronizeDisplayState (Maximize, Minimize, and every open by M through
-- SetDisplayState) calls MaximizeUIPanel while maximised, which anchors the map by its maximizePoint ("TOP") to
-- UIParent after our OnShow placement - the panel manager itself does nothing more, the map having no area: placed
-- again after it too.
local function hookOnce(name, f)
    if hooked[name] then return end
    f:HookScript("OnShow", ns.guard("dock show", onShow))
    f:HookScript("OnHide", ns.guard("dock hide", onHide))
    f:HookScript("OnDragStart", ns.guard("dock drag", onDragStart))
    f:HookScript("OnDragStop", ns.guard("dock drag", onDragStop))
    if sizes[name] and type(hooksecurefunc) == "function" then
        local again = ns.guard("dock maximise", function() Dock.place(name) end)
        for _, method in ipairs({ "Maximize", "Minimize", "UpdateMaximizedSize", "SynchronizeDisplayState" }) do
            if type(f[method]) == "function" then
                hooksecurefunc(f, method, function() C_Timer.After(0, again) end)
            end
        end
    end
    hooked[name] = true
end

local function setArea(name, f, area)
    if UIPanelWindows and UIPanelWindows[name] then
        SetUIPanelAttribute(f, "area", area)
    else
        f:SetAttributeNoHandler("UIPanelLayout-area", area)
    end
end

-- Returns true when attached, else false and why ("missing" or "protected").
local function attach(name)
    if records[name] then return true end
    local f = _G[name]
    if type(f) ~= "table" or not f.GetName then return false, "missing" end
    if f.IsProtected and f:IsProtected() then return false, "protected" end
    local rec = { frame = f, area = f:GetAttribute("UIPanelLayout-area"), clamped = f:IsClampedToScreen(), scale = f:GetScale() }
    if not rec.area and UIPanelWindows and UIPanelWindows[name] then rec.area = UIPanelWindows[name].area end
    setArea(name, f, nil)
    f:SetClampedToScreen(false)
    f:SetMovable(true)
    f:EnableMouse(true)
    f:RegisterForDrag("LeftButton")
    hookOnce(name, f)
    local listed = false
    for _, n in ipairs(UISpecialFrames) do if n == name then listed = true end end
    if not listed then table.insert(UISpecialFrames, name) end
    records[name] = rec
    Dock.attached[name] = true
    if f:IsShown() then Dock.place(name) end
    return true
end

-- A shown panel is hidden once it is handed back, so it is not left on a canvas that is about to hide; Blizzard
-- shows it through the panel manager on the next open. The record goes first, so our OnHide hook keeps 'open' as it
-- was (reopen still works when the canvas is live again). The manager does not track a panel we showed (it had no
-- area), so its HideUIPanel ends in a plain Hide() (UIParentPanelManager.lua, HideUIPanelImplementation's last
-- branch); in combat HideUIPanel refuses addon code with "Interface action blocked", so there it is that plain Hide.
local function detach(name)
    local rec = records[name]
    if not rec then return end
    local f = rec.frame
    setArea(name, f, rec.area)
    f:SetClampedToScreen(rec.clamped)
    f:SetMovable(false)
    if f:GetScale() ~= rec.scale then f:SetScale(rec.scale) end -- a map fitted to the canvas gets its own scale back
    if rec.blackoutMoved then blackoutTo(rec, UIParent) end
    records[name] = nil
    Dock.attached[name] = nil
    if f:IsShown() then
        if ns.inCombat() then f:Hide() else HideUIPanel(f) end
    end
    -- the hooks stay (a hook cannot be removed) but do nothing without a record
end

-- Each attach runs protected: one panel that throws is reported once ("attach <name>") and the rest still attach.
function Dock.attachAll()
    if not ns.Canvas.rect() then return end
    Dock.missing, Dock.protected = {}, {}
    for _, p in ipairs(Dock.PANELS) do
        if Dock.enabled(p.name) then
            local ok, done, why = pcall(attach, p.name)
            if not ok then
                ns.report("attach " .. p.name, done)
            elseif not done then
                local list = why == "protected" and Dock.protected or Dock.missing
                list[#list + 1] = p.name
            end
        end
    end
end

-- A topology that is not live leaves everything as Blizzard left it (spec §4.1): every panel goes back to the panel
-- manager. Saved options and positions are kept, so a live topology again re-attaches them where they were.
function Dock.detachAll()
    for name in pairs(records) do detach(name) end
    Dock.missing, Dock.protected = {}, {}
end

-- The option is saved either way; a panel is only attached while the canvas is live (attachAll picks it up later).
function Dock.setEnabled(name, on)
    if defaults[name] == nil then return end
    ns.state.options.dock[name] = on and true or false
    ns.markChanged()
    if not on then detach(name) elseif ns.Canvas.rect() then attach(name) end
end

function Dock.gather()
    for name, rec in pairs(records) do
        if rec.frame:IsShown() then
            remember(name, { x = 0.5, y = 0.5 })
            Dock.place(name)
        end
    end
end

-- Once per session: what was left open comes back, out of combat (spec §5.5). Called on the first live change, so a
-- login mid-fight reopens when the layout applies after combat, not at a later zone-in.
function Dock.reopenOnce()
    if reopened then return end
    reopened = true
    if not ns.state.options.reopen then return end
    ns.afterCombat(function()
        for name, rec in pairs(records) do
            local p = saved(name)
            if p and p.open and not noReopen[name] and not rec.frame:IsShown() then ShowUIPanel(rec.frame) end
        end
    end)
end

ns.Viewport.onChange(function(rects)
    if not rects then Dock.detachAll() return end
    Dock.attachAll()
    for name, rec in pairs(records) do if rec.frame:IsShown() then Dock.place(name) end end
    Dock.reopenOnce()
end)
-- A load-on-demand Blizzard addon (spellbook, professions, achievements...) creates its panel later (spec §5.4).
ns.onEvent("ADDON_LOADED", function() if ns.Canvas.rect() then Dock.attachAll() end end)
ns.commands.gather = function() Dock.gather(); ns.print("gathered every open panel to the canvas centre") end

table.insert(ns.diagExtra, function()
    local names = {}
    for name in pairs(records) do names[#names + 1] = name end
    table.sort(names)
    return {
        "docked: " .. table.concat(names, ", "),
        "missing on this client: " .. table.concat(Dock.missing, ", "),
        "protected on this client: " .. table.concat(Dock.protected, ", "),
    }
end)
