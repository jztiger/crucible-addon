-- Crucible: the in-game ledger window (spec 2026-10-04 section L) - the member's OWN sales and returns for the last 30
-- days, as the server knew them at the last Sync: per day (gold received, units sold, units that came back) and per
-- item (units sold, gold, when it last sold, returns), newest first, with the footer "as of <age>; the website has the
-- full history". Opened by /cru ledger or the Ledger button on the strip and on the mailbox - both are the player's
-- own click on a window of ours.
--
-- The data is ns.baked.ledger (src/server/datafile.ts's memberLedger): whole copper figures from the member's own
-- sale rows and nobody else's (C17). This file only DISPLAYS it: it writes nothing anywhere (no saved state, no
-- upload, no setting), asks the auction house and the mailbox for nothing, owns no clock of any kind, and never
-- names a function that posts, buys, bids, cancels or takes mail. When the member switched "Record my sales" off on
-- the website the file carries no section and says captureSales = false, and the window says exactly that.
--
-- Every widget is built under pcall: a client that cannot build the window gets the same figures in chat instead.

local _, ns = ...
local Logic = ns.Logic

local Ledger = {}
ns.Ledger = Ledger

local MAX_DAYS, MAX_ITEMS = 30, 200 -- the server's caps; a longer table is cut here too
local WIDTH, ROW_H, VISIBLE = 548, 16, 14
local TOP = 52 -- where the rows start, below the title and the column headers
local DAY_COLUMNS = {
    { "day", "Day", 10, 62, "LEFT" },
    { "gold", "Received", 74, 84, "RIGHT" },
    { "sold", "Sold", 160, 36, "RIGHT" },
    { "back", "Back", 198, 36, "RIGHT" },
}
local ITEM_COLUMNS = {
    { "name", "Item", 250, 124, "LEFT" },
    { "units", "Sold", 376, 30, "RIGHT" },
    { "gold", "Received", 408, 70, "RIGHT" },
    { "last", "Last", 480, 28, "RIGHT" },
    { "back", "Back", 510, 30, "RIGHT" },
}
local OFF_TEXT = "Record my sales is off on the website - this window has nothing to show."
local NONE_TEXT = "no sales yet"
local FOOTER_TAIL = "the website has the full history"

local window, cannotBuild
local offsets = { days = 0, items = 0 } -- how far each list is scrolled, in rows

---------------------------------------------------------------------------------------------------
-- The data: what Data.lua carries, made safe to show
---------------------------------------------------------------------------------------------------

local function whole(value)
    return type(value) == "number" and value == value and value >= 0 and value < 2 ^ 53 and value % 1 == 0
end

local function cleanList(list, cap, fields)
    local out = {}
    if type(list) ~= "table" then return out end
    for i = 1, #list do
        if #out >= cap then break end
        local row = list[i]
        local ok = type(row) == "table"
        if ok then
            for f = 1, #fields do
                if not whole(row[fields[f]]) then ok = false end
            end
        end
        if ok then out[#out + 1] = row end
    end
    return out
end

-- -> { state = "off" | "none" | "rows", days = {...}, items = {...}, asOf = number|nil }
--   off   no section and the member's own switch is known off (Data.lua's captureSales = false)
--   none  switch on (or not known to be off) and nothing to show yet - no section, or an empty one
--   rows  at least one day or item
function Ledger.view(baked)
    local ledger = type(baked) == "table" and baked.ledger or nil
    if type(ledger) ~= "table" then
        if not Logic.capturesSales(baked) then return { state = "off", days = {}, items = {} } end
        return { state = "none", days = {}, items = {} }
    end
    local days = cleanList(ledger.days, MAX_DAYS, { "day", "soldCopper", "soldUnits", "returnedUnits" })
    local items = cleanList(ledger.items, MAX_ITEMS, { "itemID", "units", "copper", "lastAt", "returned" })
    local asOf = whole(ledger.asOf) and ledger.asOf or nil
    if #days == 0 and #items == 0 then return { state = "none", days = days, items = items, asOf = asOf } end
    return { state = "rows", days = days, items = items, asOf = asOf }
end

local function money(copper)
    return ns.UI.money(copper)
end

local function count(n)
    return string.format("%.0f", n)
end

-- "today", "yesterday", "3d ago": the server's days are UTC days, so this counts whole UTC days to the game's clock.
local function dayLabel(day, now)
    local n = math.floor((now - day) / 86400)
    if n <= 0 then return "today" end
    if n == 1 then return "yesterday" end
    return count(n) .. "d ago"
end

local function footer(view, now)
    if not view.asOf then return FOOTER_TAIL end
    return "as of " .. Logic.formatAge(now - view.asOf) .. "; " .. FOOTER_TAIL
end

-- An item's name: the game's own, else its id.
local function nameOf(itemID)
    if type(C_Item) == "table" and type(C_Item.GetItemNameByID) == "function" then
        local ok, known = pcall(C_Item.GetItemNameByID, itemID)
        if ok and not ns.isSecret(known) and type(known) == "string" and known ~= "" then return known end
    end
    return "item " .. count(itemID)
end

---------------------------------------------------------------------------------------------------
-- The window
---------------------------------------------------------------------------------------------------

local function guarded(fn)
    return function(...)
        local ok, err = pcall(fn, ...)
        if not ok then ns.fail("ledger", err) end
    end
end

local function newLabel(parent, font, justify)
    local text = parent:CreateFontString(nil, "OVERLAY", font)
    text:SetJustifyH(justify or "LEFT")
    return text
end

local function newRow(parent, columns, x, width, i)
    local row = CreateFrame("Button", nil, parent)
    row:SetSize(width, ROW_H)
    row:SetPoint("TOPLEFT", parent, "TOPLEFT", x, -(TOP + (i - 1) * ROW_H))
    if i % 2 == 0 then
        row.stripe = row:CreateTexture(nil, "BACKGROUND")
        row.stripe:SetAllPoints()
        row.stripe:SetColorTexture(1, 1, 1, 0.05)
    end
    for c = 1, #columns do
        local text = newLabel(row, "GameFontHighlightSmall", columns[c][5])
        text:SetWidth(columns[c][4])
        text:SetPoint("LEFT", row, "LEFT", columns[c][3] - x, 0)
        row[columns[c][1]] = text
    end
    return row
end

local function build()
    local p = CreateFrame("Frame", nil, UIParent)
    p:SetSize(WIDTH, TOP + VISIBLE * ROW_H + 42)
    p:SetPoint("CENTER", UIParent, "CENTER", 0, 0)
    p:SetFrameStrata("HIGH")
    p:EnableMouse(true)
    p:SetMovable(true)
    p:SetClampedToScreen(true)
    p:RegisterForDrag("LeftButton")
    p:SetScript("OnDragStart", guarded(function(self) self:StartMoving() end))
    p:SetScript("OnDragStop", guarded(function(self) self:StopMovingOrSizing() end))
    p.background = p:CreateTexture(nil, "BACKGROUND")
    p.background:SetAllPoints()
    p.background:SetColorTexture(0.035, 0.035, 0.055, 0.96)

    p.title = newLabel(p, "GameFontNormal")
    p.title:SetPoint("TOPLEFT", p, "TOPLEFT", 10, -8)
    p.title:SetText("Crucible ledger - your last 30 days")

    local ok, close = pcall(CreateFrame, "Button", nil, p, "UIPanelCloseButton")
    if not ok then
        close = CreateFrame("Button", nil, p)
        close.label = newLabel(close, "GameFontNormal", "CENTER")
        close.label:SetAllPoints()
        close.label:SetText("x")
    end
    close:SetSize(24, 24)
    close:SetPoint("TOPRIGHT", p, "TOPRIGHT", -2, -2)
    close:SetScript("OnClick", guarded(function() p:Hide() end))
    p.closeButton = close

    p.headers = {}
    for _, set in ipairs({ { "days", DAY_COLUMNS }, { "items", ITEM_COLUMNS } }) do
        for c = 1, #set[2] do
            local col = set[2][c]
            local header = newLabel(p, "GameFontDisableSmall", col[5])
            header:SetWidth(col[4])
            header:SetPoint("TOPLEFT", p, "TOPLEFT", col[3] + 1, -34)
            header:SetText(col[2])
            p.headers[set[1] .. "." .. col[1]] = header
        end
    end

    p.dayRows, p.itemRows = {}, {}
    for i = 1, VISIBLE do
        p.dayRows[i] = newRow(p, DAY_COLUMNS, 4, 236, i)
        local row = newRow(p, ITEM_COLUMNS, 244, WIDTH - 248, i)
        row.glow = row:CreateTexture(nil, "BORDER")
        row.glow:SetAllPoints()
        row.glow:SetColorTexture(1, 0.82, 0, 0.14)
        row.glow:Hide()
        -- The item's own tooltip (UI.lua adds our price lines to every item tooltip).
        row:SetScript("OnEnter", guarded(function(self)
            self.glow:Show()
            if not self.data or type(GameTooltip) ~= "table" or type(GameTooltip.SetItemByID) ~= "function"
                or type(GameTooltip_SetDefaultAnchor) ~= "function" then return end
            GameTooltip_SetDefaultAnchor(GameTooltip, self)
            GameTooltip:SetItemByID(self.data.itemID)
            GameTooltip:Show()
        end))
        row:SetScript("OnLeave", guarded(function(self)
            self.glow:Hide()
            if type(GameTooltip) == "table" and type(GameTooltip.Hide) == "function" then GameTooltip:Hide() end
        end))
        p.itemRows[i] = row
    end

    p.message = newLabel(p, "GameFontHighlight", "CENTER")
    p.message:SetPoint("TOPLEFT", p, "TOPLEFT", 20, -(TOP + 60))
    p.message:SetWidth(WIDTH - 40)
    p.footer = newLabel(p, "GameFontDisableSmall")
    p.footer:SetPoint("BOTTOMLEFT", p, "BOTTOMLEFT", 10, 10)
    p.footer:SetWidth(WIDTH - 20)
    p:Hide()
    return p
end

local view -- the one painted last

-- Fills the window from ns.baked. Reads only what Data.lua already holds.
local function paint()
    if not window then return end
    view = Ledger.view(ns.baked)
    local now = ns.serverTime()
    local showRows = view.state == "rows"
    window.message:SetText(view.state == "off" and OFF_TEXT or (view.state == "none" and NONE_TEXT or ""))
    for _, header in pairs(window.headers) do header:SetShown(showRows) end
    for i = 1, VISIBLE do
        local d = showRows and view.days[offsets.days + i] or nil
        local row = window.dayRows[i]
        if d then
            row.day:SetText(dayLabel(d.day, now))
            row.gold:SetText(money(d.soldCopper))
            row.sold:SetText(count(d.soldUnits))
            row.back:SetText(count(d.returnedUnits))
            row:Show()
        else
            row:Hide()
        end
        local it = showRows and view.items[offsets.items + i] or nil
        local irow = window.itemRows[i]
        if it then
            irow.data = { itemID = it.itemID }
            irow.name:SetText(nameOf(it.itemID))
            irow.units:SetText(count(it.units))
            irow.gold:SetText(money(it.copper))
            irow.last:SetText(Logic.formatAge(now - it.lastAt))
            irow.back:SetText(count(it.returned))
            irow:Show()
        else
            irow.data = nil
            irow:Hide()
        end
    end
    window.footer:SetText(footer(view, now))
end

-- The wheel scrolls the list under the mouse: a window of rows over the longer list, never past its ends.
local function scroller(which, list)
    return guarded(function(_, delta)
        local rows = view and view[list] or {}
        local most = math.max(0, #rows - VISIBLE)
        local target = offsets[which] - (delta > 0 and 1 or -1) * 3
        offsets[which] = math.max(0, math.min(most, target))
        paint()
    end)
end

local function attach()
    if window or cannotBuild then return window end
    local ok, built = pcall(build)
    if not ok then
        cannotBuild = true
        return nil
    end
    window = built
    Ledger.window = built
    for i = 1, VISIBLE do
        built.dayRows[i]:EnableMouseWheel(true)
        built.dayRows[i]:SetScript("OnMouseWheel", scroller("days", "days"))
        built.itemRows[i]:EnableMouseWheel(true)
        built.itemRows[i]:SetScript("OnMouseWheel", scroller("items", "items"))
    end
    return window
end

---------------------------------------------------------------------------------------------------
-- Opening it
---------------------------------------------------------------------------------------------------

-- The same figures as chat lines, for a client that cannot build the window.
local function say(v, now)
    if v.state == "off" then
        ns.print(OFF_TEXT)
        return
    end
    if v.state == "none" then
        ns.print(NONE_TEXT)
        return
    end
    ns.print("ledger, last 30 days (" .. footer(v, now) .. ")")
    for i = 1, math.min(#v.days, 7) do
        local d = v.days[i]
        ns.print("  " .. dayLabel(d.day, now) .. ": " .. money(d.soldCopper) .. " from " .. count(d.soldUnits)
            .. " sold, " .. count(d.returnedUnits) .. " back")
    end
    for i = 1, math.min(#v.items, 10) do
        local it = v.items[i]
        ns.print("  " .. nameOf(it.itemID) .. ": " .. count(it.units) .. " sold for " .. money(it.copper)
            .. ", " .. count(it.returned) .. " back")
    end
end

function Ledger.show()
    if not attach() then
        say(Ledger.view(ns.baked), ns.serverTime())
        return false
    end
    offsets.days, offsets.items = 0, 0
    paint()
    window:Show()
    return true
end

function Ledger.toggle()
    if window and window:IsShown() then
        window:Hide()
        return false
    end
    return Ledger.show()
end
