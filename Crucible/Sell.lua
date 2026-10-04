-- Crucible: posting help at the auction house (B-P, spec 2026-10-04 section P; the compliance position is
-- docs/research/2026-10-04-posting-position.md, whose section 5 this file is built to).
--
-- Two things, both behind the member's accepted notice version (Data.lua's acceptedNotice, from the server): below
-- notice v5 none of this exists - no hook is installed, no line is drawn, and the strip says why (Strip.lua).
--
-- 1. The Sell tab. When the player picks an item to sell, the game's own SetItem puts its default price in the price
--    field. A hook on that SetItem (hooksecurefunc, on each of the two sell frames) then draws one Crucible line under
--    the game's inputs - "Crucible: lowest 12s 40c · market 13s 10c · 38 listed · yours would be 12s 39c" - and puts
--    that suggestion in the price field ONCE, through the game's own PriceInput:SetAmount, the call the game uses for
--    its own default. The game's UpdatePostState then runs as it always does. Once per SetItem: a repaint (a scan that
--    finished, say) redraws the line and never touches the price; a price the player typed is never overwritten. The
--    suggestion (Sell.suggest, below): the pooled lowest less 1c, or less N% (the strip's "Undercut by"); never under
--    what a vendor pays plus the house's cut on the suggested price (the cut is Data.lua's ahCutPercent - the market's
--    own - through Logic.AH_CUT_PERCENT); no fresh lowest -> the market value; neither -> no suggestion, "no group price
--    yet", and the game's default stands.
--
-- 2. The cancel scan - "Undercut?" on the strip, shown while the game's Auctions tab is open. One click: ONE query for
--    the player's own auctions, sent only when the client's ready flag says yes; then, for each distinct item among
--    them, one item search through Scan.lua's read walker (ns.Walker: one in flight, each on the ready flag, a Stop,
--    "7 of 19", nothing retried). Crucible's own panel then lists the player's auctions whose price per unit is above
--    the lowest listing that is not theirs - the search result's own containsOwnerItem flag, the one thing read about
--    whose a listing is; never a name. A click on a row is the game's own AuctionsFrame:SelectAuction, which enables
--    the game's own Cancel Auction button; the player presses it, and confirms in the game's own popup. After a cancel
--    the row greys out. Nothing is queried again on its own, after the cancel or after the scan.
--
-- What this file never does: post, cancel, bid or buy. It calls none of the write APIs or their confirms - a comment
-- may name them, the code may not (syntax.test.ts, gray-1c): the game's Post button and Cancel Auction button are the
-- only ways an auction is posted or cancelled, one press each, the player's. No clock of any kind lives here (the same
-- test holds it to that): the price is set inside the SetItem hook, the cancel scan moves on the client's own ready
-- event and the walker's, and every selection is inside a row's OnClick. Nothing read here is saved or uploaded.

local _, ns = ...
local Logic = ns.Logic

local Sell = {}
ns.Sell = Sell

Sell.NOTICE_MIN = 5 -- notice v5 says, in plain words, what posting and cancelling do (posting position, section 6)

local PANEL_W, ROW_H, TOP = 330, 20, 50
local MAX_OWNED = 500     -- the most of the player's own auctions read from one answer
local MAX_LISTINGS = 500  -- the most search results read from one answer
local SOLD_FALLBACK = 1   -- Enum.AuctionStatus.Sold where the client does not say
local GREY = { 0.5, 0.5, 0.5 }
local CAPTION = "Click a row, then the game's Cancel Auction button: one cancel per press, confirmed in the game's "
    .. "own popup. Nothing here is checked again until you press Undercut? again."

---------------------------------------------------------------------------------------------------
-- Small helpers
---------------------------------------------------------------------------------------------------

local function window()
    if type(AuctionHouseFrame) == "table" then return AuctionHouseFrame end
    return nil
end

-- Runs a script or a hook under pcall: a failure in here must never reach the game's own window.
local function guarded(fn)
    return function(...)
        local ok, err = pcall(fn, ...)
        if not ok then ns.fail("posting help", err) end
    end
end

-- A plain number, never a secret or anything else.
local function plain(v)
    if ns.isSecret(v) or type(v) ~= "number" or v ~= v then return nil end
    return v
end

-- A whole number of at least `min`, or nil.
local function whole(v, min)
    v = plain(v)
    if v == nil or v % 1 ~= 0 or v < min or v >= 2 ^ 53 then return nil end
    return v
end

local function count(n)
    return string.format("%.0f", n)
end

local function money(copper)
    return ns.UI.money(copper)
end

local function newLabel(parent, font, justify)
    local text = parent:CreateFontString(nil, "OVERLAY", font)
    text:SetJustifyH(justify or "LEFT")
    return text
end

local function newButton(parent, text, width)
    local ok, button = pcall(CreateFrame, "Button", nil, parent, "UIPanelButtonTemplate")
    if not ok then
        button = CreateFrame("Button", nil, parent)
        button.label = newLabel(button, "GameFontNormal", "CENTER")
        button.label:SetAllPoints()
        button.label:SetText(text)
    end
    button:SetSize(width, 22)
    if button.SetText then button:SetText(text) end
    return button
end

---------------------------------------------------------------------------------------------------
-- The gate: notice v5
---------------------------------------------------------------------------------------------------

-- True when the member has accepted notice v5 or later (Data.lua's acceptedNotice, written by the server from what
-- their own tray said). A file without one - an older server, the local bake, no tray at all - is "not accepted".
function Sell.allowed()
    local baked = ns.baked
    local v = type(baked) == "table" and whole(baked.acceptedNotice, 1) or nil
    return v ~= nil and v >= Sell.NOTICE_MIN
end

-- True while the game's Auctions tab is the one showing. The game shows and hides its own tabs; this only asks.
function Sell.auctionsTabOpen()
    local house = window()
    local frame = house and house.AuctionsFrame
    if type(frame) ~= "table" or type(frame.IsShown) ~= "function" then return false end
    local ok, shown = pcall(frame.IsShown, frame)
    return ok and shown == true
end

---------------------------------------------------------------------------------------------------
-- The suggestion: pure arithmetic
---------------------------------------------------------------------------------------------------

-- o = { lowest, market, vendor, cut, pct, copper }, all optional:
--   lowest  the pooled lowest price per unit (already known to be fresh), copper
--   market  the market value per unit, copper - used only when there is no lowest
--   vendor  what a vendor pays for one; the floor is the price that still leaves this after the house's cut
--   cut     the house's cut in percent (default Logic.AH_CUT_PERCENT, which Data.lua's ahCutPercent sets); a cut of
--           100% or more leaves no floor at all
--   pct     0 = undercut by 1c; 1-10 = by that many percent, rounded down; anything else is 1c
--   copper  false on a house that takes whole silver only: the undercut rounds down, the floor up, never under 1s
-- -> price, "lowest" | "market", floored (true when the vendor floor raised it); nothing at all with no group price.
-- The deposit is not a floor.
function Sell.suggest(o)
    if type(o) ~= "table" then return end
    local lowest, market = whole(o.lowest, 1), whole(o.market, 1)
    local price, from
    if lowest then
        local pct = whole(o.pct, 1)
        if pct and pct <= ns.UNDERCUT_MAX_PCT then
            price = math.floor(lowest * (100 - pct) / 100)
        else
            price = lowest - 1
        end
        from = "lowest"
    elseif market then
        price, from = market, "market"
    else
        return
    end
    local silver = o.copper == false
    if silver then price = math.floor(price / 100) * 100 end
    local cut = plain(o.cut)
    if cut == nil then cut = plain(Logic.AH_CUT_PERCENT) or 0 end
    local floored = false
    local vendor = whole(o.vendor, 1)
    if vendor and cut >= 0 and cut < 100 then
        -- in basis points, so the division is exact whenever the answer is a whole number
        local bps = math.floor(cut * 100 + 0.5)
        local floor = math.ceil(vendor * 10000 / (10000 - bps))
        if silver then floor = math.ceil(floor / 100) * 100 end
        if price < floor then price, floored = floor, true end
    end
    local least = silver and 100 or 1
    if price < least then price = least end
    return price, from, floored
end

-- Whether this house takes copper (C_AuctionHouse.SupportsCopperValues; true where the client cannot say).
local function copperValues()
    local api = C_AuctionHouse
    if type(api) ~= "table" or type(api.SupportsCopperValues) ~= "function" then return true end
    local ok, v = pcall(api.SupportsCopperValues)
    if ok and v == false then return false end
    return true
end

-- What a vendor pays for one: the item's own sell price, the 11th return of GetItemInfo - the same figure the game's
-- own default price starts from. Not Data.lua's vendor table, which is what a vendor ASKS for the mats it sells.
local function vendorPays(itemID)
    local info = nil
    if type(C_Item) == "table" and type(C_Item.GetItemInfo) == "function" then
        info = C_Item.GetItemInfo
    elseif type(GetItemInfo) == "function" then
        info = GetItemInfo
    end
    if not info then return nil end
    local ok, price = pcall(function() return (select(11, info(itemID))) end)
    if not ok then return nil end
    return whole(price, 1)
end

-- Everything the line says about one item, and the suggestion: { lowest, market, listed, price, from, floored }.
-- The lowest and the listed count come from the same scan (Data.lua's pooled prices, or the player's own newer scan),
-- and count only while it is younger than the data file's own age limit; the market value comes from Data.lua's stats.
function Sell.quote(itemID)
    local db = ns.db()
    local q = {}
    local at = whole(db.pricesAt, 1)
    local now = ns.serverTime()
    if at and (type(now) ~= "number" or now - at <= Logic.PRICES_MAX_AGE) then
        q.lowest = whole(type(db.prices) == "table" and db.prices[itemID] or nil, 1)
        if q.lowest then q.listed = whole(type(db.listed) == "table" and db.listed[itemID] or nil, 1) end
    end
    q.market = whole(type(db.market) == "table" and db.market[itemID] or nil, 1)
    q.price, q.from, q.floored = Sell.suggest({
        lowest = q.lowest,
        market = q.market,
        vendor = vendorPays(itemID),
        pct = ns.undercutPct(),
        copper = copperValues(),
    })
    return q
end

-- The line's words: "Crucible: lowest 12s 40c · market 13s 10c · 38 listed · yours would be 12s 39c".
function Sell.lineText(q)
    local parts = {}
    if q.lowest then parts[#parts + 1] = "lowest " .. money(q.lowest) end
    if q.market then parts[#parts + 1] = "market " .. money(q.market) end
    if q.listed then parts[#parts + 1] = count(q.listed) .. " listed" end
    if q.price then
        parts[#parts + 1] = "yours would be " .. money(q.price) .. (q.floored and " (vendor floor)" or "")
    else
        parts[#parts + 1] = "no group price yet"
    end
    return "Crucible: " .. table.concat(parts, " · ")
end

---------------------------------------------------------------------------------------------------
-- The Sell tab: the line, and the one pre-fill per SetItem
---------------------------------------------------------------------------------------------------

-- One per sell frame: { line, itemID, filled, typed, ours, filling }. Kept here, not on the game's frames.
local states = {}

-- The item the frame holds now, by id - read from the frame itself, never from the hook's arguments.
local function itemOf(frame)
    if type(frame.GetItem) ~= "function" then return nil end
    local ok, location = pcall(frame.GetItem, frame)
    if not ok or location == nil or ns.isSecret(location) then return nil end
    if type(C_Item) ~= "table" or type(C_Item.GetItemID) ~= "function" then return nil end
    local okID, itemID = pcall(C_Item.GetItemID, location)
    if not okID then return nil end
    return whole(itemID, 1)
end

-- Repaints the line from what is known now. Never touches the price.
local function paintLine(state)
    if not state.itemID then
        state.line:SetText("")
        state.line:Hide()
        return nil
    end
    local q = Sell.quote(state.itemID)
    state.line:SetText(Sell.lineText(q))
    state.line:Show()
    return q
end

local function amountOf(frame)
    local input = frame.PriceInput
    if type(input) ~= "table" or type(input.GetAmount) ~= "function" then return nil end
    local ok, amount = pcall(input.GetAmount, input)
    if ok then return plain(amount) end
    return nil
end

local function attachSellFrame(frame)
    if type(frame) ~= "table" or type(frame.SetItem) ~= "function" or type(frame.CreateFontString) ~= "function"
        or type(frame.PriceInput) ~= "table" then
        return
    end
    local state = { frame = frame }
    state.line = newLabel(frame, "GameFontHighlightSmall", "LEFT")
    -- Under the game's inputs: below its Post button, which is the last of them.
    state.line:SetPoint("TOP", frame.PostButton or frame, "BOTTOM", 0, -6)
    local width = frame:GetWidth()
    state.line:SetWidth((type(width) == "number" and width > 40) and (width - 24) or 280)
    if type(state.line.SetWordWrap) == "function" then state.line:SetWordWrap(true) end
    state.line:Hide()
    states[frame] = state

    -- The player's typing. Every money box's OnTextChanged reaches OnAmountChanged - the game's own default, our one
    -- fill, and the player's keys alike. Ours is told apart by the `filling` flag; a change to anything but the number
    -- we put there is the player's (or the game's own, after the player clicked a listing), and from then on this
    -- item's price is theirs.
    local moneyFrame = frame.PriceInput.MoneyInputFrame
    if type(moneyFrame) == "table" and type(moneyFrame.OnAmountChanged) == "function" then
        hooksecurefunc(moneyFrame, "OnAmountChanged", guarded(function()
            if state.filling or not state.itemID then return end
            if amountOf(frame) ~= state.ours then state.typed = true end
        end))
    end

    -- The one place a price is ever put into the game's field: right after the game's own SetItem has put its
    -- default there, once per SetItem, and only while the player has not typed.
    hooksecurefunc(frame, "SetItem", guarded(function(self)
        state.itemID = itemOf(self)
        state.filled, state.typed, state.ours = false, false, nil
        local q = paintLine(state)
        if not q or not q.price or state.filled or state.typed then return end
        local input = self.PriceInput
        if type(input.SetAmount) ~= "function" then return end
        state.filled = true
        state.filling = true
        local ok = pcall(input.SetAmount, input, q.price)
        state.filling = false
        if ok then state.ours = q.price end
    end))
end

-- The line under a sell frame, or nil (tests, and nothing else).
function Sell.lineOf(frame)
    local state = states[frame]
    return state and state.line or nil
end

-- Whether the player has changed the price since this item was set.
function Sell.typed(frame)
    local state = states[frame]
    return state ~= nil and state.typed == true
end

-- A scan finished, or anything else was learned: each line redraws. The price stays as it is.
ns.onChange(function()
    for _, state in pairs(states) do
        if state.itemID then pcall(paintLine, state) end
    end
end)

---------------------------------------------------------------------------------------------------
-- The cancel scan's panel
---------------------------------------------------------------------------------------------------

local panel, cannotBuild
-- This visit's check, or nil: { phase = "waiting" | "querying" | "walking" | "done", entries, keys, settled, total,
-- over, why, run, selected }. Entries are the player's own active auctions with a buyout: { auctionID, status, itemID,
-- level, quantity, buyout, key (index into keys), state, mine, lowest, undercut, cancelled }. Never saved anywhere.
local check = nil

-- A frame with the shopping panel's look: the dark fill and the thin border.
local function dress(p)
    p:SetFrameStrata("DIALOG")
    p:EnableMouse(true)
    p.background = p:CreateTexture(nil, "BACKGROUND")
    p.background:SetAllPoints()
    p.background:SetColorTexture(0.035, 0.035, 0.055, 0.96)
    for _, edge in ipairs({ { "TOPLEFT", "TOPRIGHT", nil, 1 }, { "BOTTOMLEFT", "BOTTOMRIGHT", nil, 1 },
        { "TOPLEFT", "BOTTOMLEFT", 1, nil }, { "TOPRIGHT", "BOTTOMRIGHT", 1, nil } }) do
        local line = p:CreateTexture(nil, "BORDER")
        line:SetColorTexture(0.35, 0.35, 0.39, 1)
        line:SetPoint(edge[1], p, edge[1], 0, 0)
        line:SetPoint(edge[2], p, edge[2], 0, 0)
        if edge[3] then line:SetWidth(edge[3]) end
        if edge[4] then line:SetHeight(edge[4]) end
    end
end

local paintPanel -- the row's click repaints

local function newRow(p, i)
    local row = CreateFrame("Button", nil, p)
    row:SetSize(PANEL_W - 2, ROW_H)
    row:SetPoint("TOPLEFT", p, "TOPLEFT", 1, -(TOP + (i - 1) * ROW_H))
    row.glow = row:CreateTexture(nil, "BORDER")
    row.glow:SetAllPoints()
    row.glow:SetColorTexture(1, 0.82, 0, 0.14)
    row.glow:Hide()
    row.name = newLabel(row, "GameFontHighlightSmall", "LEFT")
    row.name:SetWidth(120)
    row.name:SetPoint("LEFT", row, "LEFT", 8, 0)
    row.prices = newLabel(row, "GameFontHighlightSmall", "LEFT")
    row.prices:SetWidth(PANEL_W - 140)
    row.prices:SetPoint("LEFT", row, "LEFT", 132, 0)
    -- The one thing a click does: the game's own selection of this auction in its Auctions tab, which enables the
    -- game's own Cancel Auction button. Pressing that, and confirming in the game's popup, is the player's.
    row:SetScript("OnClick", guarded(function(self)
        local e = self.entry
        if not e or e.cancelled then return end
        local house = window()
        local auctions = house and house.AuctionsFrame
        if type(auctions) ~= "table" or type(auctions.SelectAuction) ~= "function" then
            ns.print("find it in the Auctions tab - this client cannot select it from here")
            return
        end
        if not Sell.auctionsTabOpen() then
            ns.print("open the Auctions tab first")
            return
        end
        auctions:SelectAuction({ auctionID = e.auctionID, status = e.status })
        if check then check.selected = e end
        paintPanel()
    end))
    row:SetScript("OnEnter", guarded(function(self) self.glow:Show() end))
    row:SetScript("OnLeave", guarded(function(self)
        if not (check and self.entry and check.selected == self.entry) then self.glow:Hide() end
    end))
    return row
end

local stopCheck -- the panel's Stop and Close, defined with the check below

local function build(parent)
    local p = CreateFrame("Frame", nil, parent)
    p:SetSize(PANEL_W, TOP + 40)
    local strip = ns.Strip and ns.Strip.frame
    if strip then
        p:SetPoint("TOPLEFT", strip, "BOTTOMLEFT", 0, -8)
    else
        p:SetPoint("TOPLEFT", parent, "TOPRIGHT", 2, -28)
    end
    dress(p)
    p.title = newLabel(p, "GameFontNormal")
    p.title:SetPoint("TOPLEFT", p, "TOPLEFT", 10, -8)
    p.title:SetText("Undercut?")
    p.status = newLabel(p, "GameFontHighlightSmall")
    p.status:SetPoint("TOPLEFT", p, "TOPLEFT", 10, -26)
    p.status:SetWidth(PANEL_W - 90)
    if type(p.status.SetWordWrap) == "function" then p.status:SetWordWrap(true) end
    p.stopButton = newButton(p, "Stop", 56)
    p.stopButton:SetPoint("TOPRIGHT", p, "TOPRIGHT", -28, -4)
    p.stopButton:SetScript("OnClick", guarded(function() stopCheck("you stopped it") end))
    local ok, close = pcall(CreateFrame, "Button", nil, p, "UIPanelCloseButton")
    if not ok then
        close = CreateFrame("Button", nil, p)
        close.label = newLabel(close, "GameFontNormal", "CENTER")
        close.label:SetAllPoints()
        close.label:SetText("x")
    end
    close:SetSize(24, 24)
    close:SetPoint("TOPRIGHT", p, "TOPRIGHT", -2, -2)
    -- Close ends a check still running (nothing runs unseen) and puts the panel away until the next click.
    close:SetScript("OnClick", guarded(function()
        stopCheck("you closed it")
        check = nil
        p:Hide()
    end))
    p.closeButton = close
    p.rows = {}
    p.caption = newLabel(p, "GameFontDisableSmall")
    p.caption:SetWidth(PANEL_W - 20)
    if type(p.caption.SetWordWrap) == "function" then p.caption:SetWordWrap(true) end
    p.caption:SetText(CAPTION)
    p:Hide()
    return p
end

local function ensurePanel()
    if panel or cannotBuild then return panel end
    local house = window()
    if not house then return nil end
    local ok, built = pcall(build, house)
    if not ok then
        cannotBuild = true
        return nil
    end
    panel = built
    Sell.panel = built
    return panel
end

local function nameOf(itemID)
    if type(C_Item) == "table" and type(C_Item.GetItemNameByID) == "function" then
        local ok, name = pcall(C_Item.GetItemNameByID, itemID)
        if ok and not ns.isSecret(name) and type(name) == "string" and name ~= "" then return name end
    end
    return "item " .. count(itemID)
end

local function undercutEntries(c)
    local out = {}
    for i = 1, #c.entries do
        if c.entries[i].undercut then out[#out + 1] = c.entries[i] end
    end
    return out
end

local function statusText(c)
    local found = #undercutEntries(c)
    if c.phase == "waiting" and not c.over then return "Waiting for the house to take the query ..." end
    if c.phase == "querying" and not c.over then return "Reading your auctions ..." end
    if not c.over then
        return "Checking " .. count(math.min(c.settled + 1, c.total)) .. " of " .. count(c.total)
            .. " - one query at a time, when the house is ready"
    end
    if c.why ~= nil then
        if c.phase ~= "walking" and c.phase ~= "done" then return "Undercut? stopped (" .. tostring(c.why) .. ")." end
        return "Undercut? stopped (" .. tostring(c.why) .. "): " .. count(c.settled) .. " of " .. count(c.total)
            .. " checked. " .. count(found) .. " undercut so far."
    end
    if #c.entries == 0 then return "You have no auctions up with a buyout to check." end
    local text
    if found > 0 then
        text = count(found) .. " of " .. count(#c.entries) .. " auctions undercut. Click one, then the game's Cancel "
            .. "Auction button."
    else
        text = "None of your " .. count(#c.entries) .. " auctions is undercut."
    end
    if c.missed and c.missed > 0 then
        text = text .. " " .. count(c.missed) .. " could not be checked - the house was busy."
    end
    return text
end

function paintPanel()
    if not panel then return end
    local c = check
    if not c then
        for i = 1, #panel.rows do
            panel.rows[i].entry = nil
            panel.rows[i]:Hide()
        end
        panel:Hide()
        return
    end
    panel.status:SetText(statusText(c))
    panel.stopButton:SetShown(not c.over)
    local shown = undercutEntries(c)
    for i = 1, #shown do
        local e = shown[i]
        local row = panel.rows[i]
        if not row then
            row = newRow(panel, i)
            panel.rows[i] = row
        end
        row.entry = e
        row.name:SetText(nameOf(e.itemID) .. (e.quantity > 1 and (" x" .. count(e.quantity)) or ""))
        local prices = "yours " .. money(e.mine) .. " · lowest now " .. money(e.lowest)
        if e.cancelled then prices = prices .. " · cancelled" end
        row.prices:SetText(prices)
        row.greyed = e.cancelled == true
        if e.cancelled then
            row.name:SetTextColor(GREY[1], GREY[2], GREY[3])
            row.prices:SetTextColor(GREY[1], GREY[2], GREY[3])
        else
            row.name:SetTextColor(1, 1, 1)
            row.prices:SetTextColor(1, 1, 1)
        end
        row.glow:SetShown(c.selected == e and not e.cancelled)
        row:Show()
    end
    for i = #shown + 1, #panel.rows do
        panel.rows[i].entry = nil
        panel.rows[i]:Hide()
    end
    local bottom = TOP + #shown * ROW_H
    panel.caption:ClearAllPoints()
    panel.caption:SetPoint("TOPLEFT", panel, "TOPLEFT", 10, -(bottom + 6))
    panel:SetHeight(bottom + 40)
    panel:Show()
end

---------------------------------------------------------------------------------------------------
-- The cancel scan: one click, then reads only
---------------------------------------------------------------------------------------------------

local function finish(c, why)
    if c.over then return end
    c.over, c.why = true, why
    if check == c then paintPanel() end
end

-- Ends the check that is running, whatever it is waiting for. The walk is ended through the walker, which says
-- why in its own onDone; a check still waiting for the house just ends.
function stopCheck(why)
    local c = check
    if not c or c.over then return end
    if c.run then
        ns.Walker.stop(c.run)
    else
        finish(c, why)
    end
end

-- One auction's price per unit: a commodity's own buyout is already per unit (the game's own list shows it so); an
-- item auction's buyout is for its whole stack, so per unit is that over the quantity, rounded up.
local function perUnit(e, kind)
    if kind == "commodity" then return e.buyout end
    return math.ceil(e.buyout / e.quantity)
end

-- The lowest price per unit among the listings an answer holds that are NOT the player's own - the result's own
-- containsOwnerItem flag, nothing else about whose a listing is. An item auction's level must match the player's
-- (the game's own best-price pick does the same); nil when every listing is the player's, or there are none.
local function lowestOther(answer, key)
    local lowest = nil
    for i = 1, math.min(answer.count, MAX_LISTINGS) do
        local r = answer.info(i)
        if r and r.containsOwnerItem ~= true then
            local unit = nil
            if answer.kind == "commodity" then
                unit = whole(r.unitPrice, 1)
            else
                local qty, buyout = whole(r.quantity, 1), whole(r.buyoutAmount, 1)
                local rk = (not ns.isSecret(r.itemKey) and type(r.itemKey) == "table") and r.itemKey or nil
                local level = rk and whole(rk.itemLevel, 1) or nil
                local sameLevel = key.itemLevel == 0 or level == nil or level == key.itemLevel
                if qty and buyout and sameLevel then unit = math.ceil(buyout / qty) end
            end
            if unit and (lowest == nil or unit < lowest) then lowest = unit end
        end
    end
    return lowest
end

-- The player's own active auctions with a buyout, and the distinct item keys among them. Only the auction's id, item
-- key, status, quantity and buyout are read.
local function readOwned()
    local api = C_AuctionHouse
    local SOLD = type(Enum) == "table" and type(Enum.AuctionStatus) == "table" and plain(Enum.AuctionStatus.Sold)
        or SOLD_FALLBACK
    local okN, n = pcall(api.GetNumOwnedAuctions)
    n = okN and whole(n, 0) or 0
    local entries, keys, byKey = {}, {}, {}
    for i = 1, math.min(n, MAX_OWNED) do
        local ok, info = pcall(api.GetOwnedAuctionInfo, i)
        if ok and not ns.isSecret(info) and type(info) == "table" then
            local auctionID, status = whole(info.auctionID, 1), plain(info.status)
            local quantity, buyout = whole(info.quantity, 1), whole(info.buyoutAmount, 1)
            local k = (not ns.isSecret(info.itemKey) and type(info.itemKey) == "table") and info.itemKey or nil
            local itemID = k and whole(k.itemID, 1) or nil
            if auctionID and status ~= SOLD and quantity and buyout and itemID then
                local key = { itemID = itemID, itemLevel = whole(k.itemLevel, 0) or 0, itemSuffix = whole(k.itemSuffix, 0) or 0,
                    battlePetSpeciesID = whole(k.battlePetSpeciesID, 0) or 0 }
                local id = count(key.itemID) .. ":" .. count(key.itemSuffix) .. ":" .. count(key.itemLevel) .. ":"
                    .. count(key.battlePetSpeciesID)
                if not byKey[id] then
                    keys[#keys + 1] = key
                    byKey[id] = #keys
                end
                entries[#entries + 1] = { auctionID = auctionID, status = status, itemID = itemID, quantity = quantity,
                    buyout = buyout, key = byKey[id], state = "pending" }
            end
        end
    end
    return entries, keys
end

local function startWalk(c)
    local entries, keys = readOwned()
    c.entries, c.keys, c.total, c.missed = entries, keys, #keys, 0
    if #keys == 0 then
        c.phase = "done"
        finish(c, nil)
        return
    end
    c.phase = "walking"
    local function mine() return check == c end
    local run = ns.Walker.start({
        kind = "undercut",
        keys = keys,
        onAnswer = function(index, key, answer)
            local lowest = lowestOther(answer, key)
            for i = 1, #c.entries do
                local e = c.entries[i]
                if e.key == index then
                    e.state = "answered"
                    e.mine = perUnit(e, answer.kind)
                    e.lowest = lowest
                    e.undercut = lowest ~= nil and e.mine > lowest
                end
            end
            if mine() then paintPanel() end
        end,
        onMissed = function(index)
            c.missed = c.missed + 1
            for i = 1, #c.entries do
                if c.entries[i].key == index then c.entries[i].state = "missed" end
            end
            if mine() then paintPanel() end
        end,
        onProgress = function(done)
            c.settled = done
            if mine() then paintPanel() end
        end,
        onDone = function(why)
            c.phase = "done"
            c.run = nil
            finish(c, why)
        end,
    })
    if run then
        c.run = run
    else
        finish(c, "the search could not start")
    end
    if mine() then paintPanel() end
end

-- The one query for the player's own auctions: only when the house's ready flag is up, and only once per check. The
-- click sends it, or - when the house was busy at the click - the client's own ready event does, once.
local function sendOwned(c)
    if check ~= c or c.over or c.phase ~= "waiting" then return end
    local api = C_AuctionHouse
    if not api.IsThrottledMessageSystemReady() then return end -- the ready event brings us back
    local sorts = {}
    local order = type(Enum) == "table" and type(Enum.AuctionHouseSortOrder) == "table" and Enum.AuctionHouseSortOrder
    if order and order.Price ~= nil then sorts[1] = { sortOrder = order.Price, reverseSort = false } end
    c.phase = "querying"
    if not pcall(api.QueryOwnedAuctions, sorts) then
        finish(c, "the house would not take the query")
        return
    end
    paintPanel()
end

-- Undercut?'s one click (the strip's button). Refused - saying why - while anything else of ours is running, while
-- one is already going, with the house closed, or below notice v5.
function Sell.cancelScan()
    if not Sell.allowed() then return end
    if not ns.ahOpen then
        ns.print("open the auction house first")
        return
    end
    if check and not check.over then
        ns.print("Undercut? is already checking - Stop ends it")
        return
    end
    if ns.Scan.busy() then
        ns.print("a scan is already running - wait for it, or press Stop")
        return
    end
    local api = C_AuctionHouse
    if type(api) ~= "table" or type(api.QueryOwnedAuctions) ~= "function" or type(api.GetNumOwnedAuctions) ~= "function"
        or type(api.GetOwnedAuctionInfo) ~= "function" or type(api.IsThrottledMessageSystemReady) ~= "function" then
        ns.print("this client cannot list your auctions")
        return
    end
    if not ensurePanel() then
        ns.print("this client cannot show the Undercut? panel")
        return
    end
    check = { phase = "waiting", entries = {}, keys = {}, settled = 0, total = 0, over = false, missed = 0 }
    paintPanel()
    sendOwned(check)
end

---------------------------------------------------------------------------------------------------
-- Events: they carry a running check forward and repaint, and start nothing
---------------------------------------------------------------------------------------------------

-- Scan.lua's own handler for this event ran first (the .toc loads it first) and moved its walker on; this one only
-- sends the owned query a check is still waiting to send.
ns.on("AUCTION_HOUSE_THROTTLED_SYSTEM_READY", function()
    if check and check.phase == "waiting" and not check.over then sendOwned(check) end
end)

-- The answer to the owned query. Only a check that sent one reads it: the game re-queries its own list after a
-- cancel, and that answer starts nothing here.
ns.on("OWNED_AUCTIONS_UPDATED", function()
    if check and check.phase == "querying" and not check.over then startWalk(check) end
end)

-- A query the house dropped after taking it: the owned query was the one in flight, so the check ends. Never retried.
ns.on("AUCTION_HOUSE_THROTTLED_MESSAGE_DROPPED", function()
    if check and check.phase == "querying" and not check.over then finish(check, "the house dropped the query") end
end)

-- The game's own cancel went through: that row greys out. Nothing is asked of the house.
ns.on("AUCTION_CANCELED", function(auctionID)
    local c = check
    if not c then return end
    local id = whole(auctionID, 1)
    for i = 1, #c.entries do
        local e = c.entries[i]
        if (id and e.auctionID == id) or (not id and c.selected == e) then e.cancelled = true end
    end
    paintPanel()
end)

ns.on("AUCTION_HOUSE_CLOSED", function()
    local c = check
    check = nil
    if c and not c.over then
        if c.run then ns.Walker.stop(c.run) else finish(c, "the auction house was closed") end
    end
    paintPanel()
end)

-- The tabs: Undercut? follows the Auctions tab on the strip. Hooks installed once, the first time the house opens,
-- and only for a member who has accepted notice v5.
local attached = false
function Sell.attach()
    if attached or not Sell.allowed() then return end
    local house = window()
    if not house then return end
    attached = true
    pcall(attachSellFrame, house.ItemSellFrame)
    pcall(attachSellFrame, house.CommoditiesSellFrame)
    if type(house.SetDisplayMode) == "function" then
        hooksecurefunc(house, "SetDisplayMode", guarded(function()
            if ns.Strip and ns.Strip.repaint then ns.Strip.repaint() end
        end))
    end
end

-- Loads after Strip.lua, so the strip is built (and the panel can sit under it) by the time this runs.
ns.on("AUCTION_HOUSE_SHOW", function()
    Sell.attach()
    if ns.Strip and ns.Strip.repaint then ns.Strip.repaint() end
end)
