-- The /canvas window (spec §5.6): theme, per-panel dock switches, reopen, reset, status. The Mainhand side for SPLIT
-- is chosen in the tray's Displays choice, not here (options.splitSide stays in the document, unused).
-- Plain frames and the game's own button/check templates; no Blizzard art, no rounded corners.
local ADDON, ns = ...
local Options = {}
ns.Options = Options

local frame
local ROW, COLUMN = 22, 250
local STATUS_ROOM = 220   -- the status card: about fifteen small lines, the docked/missing/protected lists wrapping
-- Switch labels where the frame name is not what a player calls it; the rest drop a trailing "Frame".
local LABELS = { PlayerSpellsFrame = "Spellbook", PVEFrame = "Group finder", SettingsPanel = "Settings" }

local function button(parent, text, w, x, y, onClick)
    local b = CreateFrame("Button", nil, parent, "UIPanelButtonTemplate")
    b:SetSize(w, 24)
    b:SetPoint("TOPLEFT", parent, "TOPLEFT", x, y)
    b:SetText(text)
    b:SetScript("OnClick", ns.guard("options click", onClick))
    return b
end

-- get is kept on the box so refresh() can re-read every switch the same way.
local function check(parent, text, x, y, get, set)
    local cb = CreateFrame("CheckButton", nil, parent, "UICheckButtonTemplate")
    cb:SetSize(24, 24)
    cb:SetPoint("TOPLEFT", parent, "TOPLEFT", x, y)
    cb.label = cb:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    cb.label:SetPoint("LEFT", cb, "RIGHT", 4, 0)
    cb.label:SetText(text)
    cb.get = get
    cb:SetChecked(get())
    cb:SetScript("OnClick", ns.guard("options check", function(self) set(self:GetChecked()) end))
    return cb
end

-- Two columns of switches from the top of y; returns the y below the last row.
local function grid(parent, y, items)
    for i, item in ipairs(items) do
        local col, row = (i - 1) % 2, math.floor((i - 1) / 2)
        parent.dockChecks[item.key] = check(parent, item.text, 16 + col * COLUMN, y - row * ROW, item.get, item.set)
    end
    return y - math.ceil(#items / 2) * ROW
end

function Options.frame()
    if frame then return frame end
    frame = CreateFrame("Frame", "CrucibleCanvasOptions", UIParent)
    frame:SetFrameStrata("DIALOG")
    frame:EnableMouse(true)
    frame:SetMovable(true)
    frame:RegisterForDrag("LeftButton")
    frame:SetScript("OnDragStart", function(f) f:StartMoving() end)
    frame:SetScript("OnDragStop", function(f) f:StopMovingOrSizing() end)
    frame.bg = frame:CreateTexture(nil, "BACKGROUND")
    frame.bg:SetAllPoints(frame)
    frame.bg:SetColorTexture(0.078, 0.071, 0.067, 0.96)
    frame.edge = frame:CreateTexture(nil, "BORDER")
    frame.edge:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, 0)
    frame.edge:SetPoint("TOPRIGHT", frame, "TOPRIGHT", 0, 0)
    frame.edge:SetHeight(2)
    frame.edge:SetColorTexture(0.886, 0.690, 0.290, 1)
    frame.title = frame:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
    frame.title:SetPoint("TOPLEFT", frame, "TOPLEFT", 16, -14)
    frame.title:SetText("CRUCIBLE CANVAS")
    frame.close = button(frame, "Close", 70, 434, -10, function() frame:Hide() end)

    local y = -52
    local themes = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    themes:SetPoint("TOPLEFT", frame, "TOPLEFT", 16, y)
    themes:SetText("Theme")
    frame.themeButtons = {}
    local x = 80
    for _, name in ipairs({ "forge", "ember", "slate", "parchment" }) do
        frame.themeButtons[name] = button(frame, name, 90, x, y + 4, function() ns.Canvas.applyTheme(name) end)
        x = x + 96
    end

    y = y - 36
    frame.reopen = check(frame, "Reopen the panels I left open at login", 16, y,
        function() return ns.state.options.reopen end,
        function(on) ns.state.options.reopen = on; ns.markChanged() end)

    y = y - 36
    local docks = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    docks:SetPoint("TOPLEFT", frame, "TOPLEFT", 16, y)
    docks:SetText("Open these on the canvas")
    y = y - 22
    frame.dockChecks = {}
    local panels = {}
    for _, p in ipairs(ns.Dock.PANELS) do
        local name = p.name
        panels[#panels + 1] = { key = name, text = LABELS[name] or (name:gsub("Frame$", "")),
            get = function() return ns.Dock.enabled(name) end,
            set = function(on) ns.Dock.setEnabled(name, on) end }
    end
    y = grid(frame, y, panels) - 8
    local bags = {}
    for _, t in ipairs(ns.Bags.THIRD_PARTY) do
        local addon = t.addon
        bags[#bags + 1] = { key = "bags:" .. addon, text = addon .. " bags",
            get = function() return ns.Bags.enabled(addon) end,
            set = function(on) ns.Bags.setEnabled(addon, on) end }
    end
    y = grid(frame, y, bags) - 12

    frame.resetButton = button(frame, "Reset this character's layout", 220, 16, y, function() Options.resetLayout() end)
    frame.gatherButton = button(frame, "Gather panels", 120, 244, y, function() ns.Dock.gather() end)
    frame.wizardButton = button(frame, "Guide", 70, 372, y, function() ns.Wizard.show() end)
    y = y - 36
    frame.status = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    frame.status:SetPoint("TOPLEFT", frame, "TOPLEFT", 16, y)
    frame.status:SetWidth(488)
    frame.status:SetJustifyH("LEFT")
    frame:SetSize(520, STATUS_ROOM - y)

    table.insert(UISpecialFrames, "CrucibleCanvasOptions")
    frame:Hide()
    return frame
end

function Options.refresh()
    local f = Options.frame()
    f.reopen:SetChecked(f.reopen.get())
    for _, cb in pairs(f.dockChecks) do cb:SetChecked(cb.get()) end
    f.status:SetText(table.concat(ns.diagLines(), "\n"))
end

-- Gather runs first: it saves the centre for every open panel, which the clear then discards. Dock.place already
-- puts a panel with no saved entry at the centre, so the open panels end up in the same place either way.
function Options.resetLayout()
    ns.Dock.gather()
    ns.state.characters[ns.charKey()] = nil
    ns.markChanged()
    Options.refresh()
end

function Options.show()
    local f = Options.frame()
    f:ClearAllPoints()
    if ns.Canvas.rect() then f:SetPoint("CENTER", ns.Canvas.frame(), "CENTER", 0, 0) else f:SetPoint("CENTER", UIParent, "CENTER", 0, 0) end
    Options.refresh()
    f:Show()
end

function Options.toggle()
    if frame and frame:IsShown() then frame:Hide() else Options.show() end
end

ns.commands[""] = function() Options.toggle() end
