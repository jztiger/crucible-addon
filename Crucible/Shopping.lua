-- Crucible: the shopping list (0.12.0) - a panel under the Crucible bar at the auction house, listing the mats of
-- a batch the member planned on the web (the Workbench drawer's "Send list to the game"). The owner's ruling of
-- 2026-09-25 (Tally 2.0 open decision 3, inside C17): the member's OWN list travels through their own Data.lua, as
-- ns.baked.shopping; Logic.shoppingList reads it, so a damaged one is simply no list.
--
-- Each row: the mat, how many are in the bags (C_Item.GetItemCount, repainted on the game's own bag event), how
-- many are in the bank (0.12.1 - C_Item.GetItemCount(id, true) minus the bags count, read live on every paint,
-- never stored: the owner's probe of 2026-09-25 found includeBank answers correctly even on a session that never
-- opened the bank), how many are still to buy (bags and bank both taken off), the average price the web walked
-- for the batch, and "pay up to" - the price per unit where the whole craft stops paying. A click on a row asks
-- Scan.lua for ONE search in the game's own window (Scan.search): the player's click, one query (C7); the
-- results and the buying stay in Blizzard's window. A vendor's mat is not searched for.
--
-- What this file does NOT do: it sends nothing to the auction house itself (Scan.lua is the one file that does),
-- owns no clock of any kind (the compliance test holds it to that), never repeats or retries a search, and
-- never buys, posts or cancels. It shows itself when the house opens - showing is not asking the house for
-- anything - and the Close button hides it until the next visit; /crucible shopping brings it back. Every widget is
-- built under pcall: a client that cannot build it gets no panel, and the list stays readable in /crucible shopping.
--
-- Done (0.15.2, "Clear the shopping list"): unlike Close, it hides the panel for the rest of the SESSION - every
-- later AUCTION_HOUSE_SHOW stays quiet for this exact list, by identity (recipe id + sent-at), until Data.lua
-- carries a different one - and records that identity (Logic.noteShoppingDone) so the next upload's reference
-- document tells the server to clear THAT list, never a newer one sent since. /crucible shopping still forces it
-- open by hand, Done or not.
--
-- Search All (B-S, spec 2026-10-04 section S): one click asks Scan.lua's read walker (ns.Walker) for one item search
-- per row that is not a vendor's - one in flight, each only when the house says it is ready, a Stop, "1 of 12", and
-- a query the house refuses is marked "throttled - try again" and never sent again by itself. What comes back is
-- read into Crucible's own results panel under the list - per row the lowest per-unit price, how many are listed at
-- or under Pay up to, the cheapest stacks that cover what is wanted, and "under target" / "over by 3%" - with the
-- row's own filters from the web applied (item level band, minimum quality, wantQty; Logic.shoppingResult). A click
-- on a result row is the same one game search a list row's click is: the player buys in Blizzard's window, with
-- Blizzard's buttons. Nothing read here is stored or uploaded; the results go when the house closes (or the list's
-- Close or Done is pressed, which also stop a walk still running - no walk runs unseen). Everything that moves the
-- walk forward is the walker's own; this file still owns no clock.

local _, ns = ...
local Logic = ns.Logic

local Shopping = {}
ns.Shopping = Shopping

local WIDTH, ROW_H = 458, 20
local TOP = 62 -- where the rows start, below the title, the heading and the column headers
-- columns: key, header, left edge, width, alignment
local COLUMNS = {
    { "name", "Mat", 10, 150, "LEFT" },
    { "bags", "Bags", 162, 36, "RIGHT" },
    { "bank", "Bank", 200, 36, "RIGHT" },
    { "buy", "Buy", 238, 36, "RIGHT" },
    { "each", "Avg", 276, 60, "RIGHT" },
    { "payUpTo", "Pay up to", 338, 70, "RIGHT" },
    { "action", "", 410, 44, "RIGHT" },
}
local CAPTION = "Pay up to is the price where the whole craft stops paying. Each Search sends one auction house query; "
    .. "buying stays in Blizzard's window."
local GREEN, RED, CLOSE = "|cff00ff00", "|cffff2020", "|r"

-- Search All's results panel (B-S): columns as above.
local RESULTS_TOP = 62
local RESULT_COLUMNS = {
    { "name", "Mat", 10, 120, "LEFT" },
    { "lowest", "Lowest", 132, 62, "RIGHT" },
    { "under", "Under", 196, 40, "RIGHT" },
    { "stacks", "Cheapest", 242, 148, "LEFT" },
    { "verdict", "Target", 392, 62, "RIGHT" },
}
local RESULTS_CAPTION = "Under: how many are listed at or under Pay up to. One query per row, one at a time, when the "
    .. "house is ready; nothing is kept. Click a row to search for it in Blizzard's window - buying stays there."
local MAX_LISTINGS = 500 -- the most result rows read from one answer

local panel, cannotBuild
local results -- Search All's own panel, built with the list's
-- This visit's Search All, or nil: { run, entries = { { itemID, label, named, row, state, result } }, settled,
-- total, over, why }. state: "pending" / "answered" / "refused" / "timeout". Never saved anywhere.
local search = nil
-- Closed with its button during THIS visit to the house; the next AUCTION_HOUSE_SHOW shows it again.
local closedThisVisit = false
-- The Done button (0.15.2): the identity ({ recipeID, at }) of whichever list was marked done this SESSION -
-- unlike Close, this stays hidden across every later AUCTION_HOUSE_SHOW, until Data.lua carries a different
-- list (a different recipeID or at). /crucible shopping still forces it open (Shopping.command's own `force`).
local doneList = nil

-- Whether `list` is the exact one Done was pressed on - never a newer one sent since, which carries a
-- different `at`.
local function isDone(list)
    return doneList ~= nil and list ~= nil and doneList.recipeID == list.recipeID and doneList.at == list.at
end

local function window()
    if type(AuctionHouseFrame) == "table" then return AuctionHouseFrame end
    return nil
end

-- Runs a widget script under pcall: a failure in here must never reach the game's own window.
local function guarded(fn)
    return function(...)
        local ok, err = pcall(fn, ...)
        if not ok then ns.fail("shopping list", err) end
    end
end

local function newLabel(parent, font, justify)
    local text = parent:CreateFontString(nil, "OVERLAY", font)
    text:SetJustifyH(justify or "LEFT")
    return text
end

local function money(copper)
    if copper == nil then return "-" end
    return ns.UI.money(copper)
end

-- The list the data file carried, or nil.
local function currentList()
    return Logic.shoppingList(ns.baked)
end

-- A mat's name: the list's, else the game's own, else its id.
local function nameOf(itemID, name)
    if name then return name, true end
    if type(C_Item) == "table" and type(C_Item.GetItemNameByID) == "function" then
        local ok, known = pcall(C_Item.GetItemNameByID, itemID)
        if ok and not ns.isSecret(known) and type(known) == "string" and known ~= "" then return known, true end
    end
    return "item " .. string.format("%.0f", itemID), false
end

-- { [itemID] = count in the bags } for the list's mats, or nil on a client that cannot count them.
local function bags(list)
    if type(C_Item) ~= "table" or type(C_Item.GetItemCount) ~= "function" then return nil end
    local have = {}
    for i = 1, #list.mats do
        local itemID = list.mats[i].itemID
        local ok, count = pcall(C_Item.GetItemCount, itemID)
        if ok and not ns.isSecret(count) and type(count) == "number" then have[itemID] = count else have[itemID] = 0 end
    end
    return have
end

-- { [itemID] = count in the bank } for the list's mats: C_Item.GetItemCount(id, true) counts bags AND bank, so
-- the bank alone is that minus the plain (bags-only) call - never below 0. Read live on every paint (0.12.1); no
-- snapshot, nothing stored (the owner's ruling of 2026-09-25: alts' banks are a separate, unbuilt feature). A mat
-- whose count cannot be read BOTH ways - no C_Item.GetItemCount at all, or either call answering anything but a
-- plain number - is left out of the table entirely: unknown, never guessed as 0.
local function bank(list)
    if type(C_Item) ~= "table" or type(C_Item.GetItemCount) ~= "function" then return nil end
    local have = {}
    for i = 1, #list.mats do
        local itemID = list.mats[i].itemID
        local okBags, inBags = pcall(C_Item.GetItemCount, itemID)
        local okTotal, total = pcall(C_Item.GetItemCount, itemID, true)
        if okBags and okTotal and not ns.isSecret(inBags) and not ns.isSecret(total)
            and type(inBags) == "number" and type(total) == "number" then
            local diff = total - inBags
            have[itemID] = diff > 0 and diff or 0
        end
    end
    return have
end

---------------------------------------------------------------------------------------------------
-- The panel
---------------------------------------------------------------------------------------------------

-- The one game search a row click asks for - a list row's or a Search All result row's alike: one search, through
-- Scan.lua, for this mat - or, for a vendor's mat, nothing but saying so.
local function searchFor(data)
    if not data then return end
    if data.vendor then
        ns.print(data.label .. " is sold by a vendor")
        return
    end
    if not data.named then
        ns.print("no name known for " .. data.label .. " yet - search for it by hand")
        return
    end
    ns.Scan.search(data.label)
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

local function newRow(p, i)
    local row = CreateFrame("Button", nil, p)
    row:SetSize(WIDTH - 2, ROW_H)
    row:SetPoint("TOPLEFT", p, "TOPLEFT", 1, -(TOP + (i - 1) * ROW_H))
    if i % 2 == 0 then
        row.stripe = row:CreateTexture(nil, "BACKGROUND")
        row.stripe:SetAllPoints()
        row.stripe:SetColorTexture(1, 1, 1, 0.05)
    end
    row.glow = row:CreateTexture(nil, "BORDER")
    row.glow:SetAllPoints()
    row.glow:SetColorTexture(1, 0.82, 0, 0.14)
    row.glow:Hide()
    for c = 1, #COLUMNS do
        local text = newLabel(row, "GameFontHighlightSmall", COLUMNS[c][5])
        text:SetWidth(COLUMNS[c][4])
        text:SetPoint("LEFT", row, "LEFT", COLUMNS[c][3], 0)
        row[COLUMNS[c][1]] = text
    end
    -- The one thing a click does: one search, through Scan.lua, for this mat - or, for a vendor's mat, nothing but
    -- saying so.
    row:SetScript("OnClick", guarded(function(self) searchFor(self.data) end))
    row:SetScript("OnEnter", guarded(function(self) self.glow:Show() end))
    row:SetScript("OnLeave", guarded(function(self) self.glow:Hide() end))
    return row
end

---------------------------------------------------------------------------------------------------
-- Search All's results panel (B-S)
---------------------------------------------------------------------------------------------------

-- A frame with the list panel's own look: the dark fill and the thin border.
local function dress(p)
    p:SetFrameStrata("HIGH")
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

local function newResultRow(r, i)
    local row = CreateFrame("Button", nil, r)
    row:SetSize(WIDTH - 2, ROW_H)
    row:SetPoint("TOPLEFT", r, "TOPLEFT", 1, -(RESULTS_TOP + (i - 1) * ROW_H))
    if i % 2 == 0 then
        row.stripe = row:CreateTexture(nil, "BACKGROUND")
        row.stripe:SetAllPoints()
        row.stripe:SetColorTexture(1, 1, 1, 0.05)
    end
    row.glow = row:CreateTexture(nil, "BORDER")
    row.glow:SetAllPoints()
    row.glow:SetColorTexture(1, 0.82, 0, 0.14)
    row.glow:Hide()
    for c = 1, #RESULT_COLUMNS do
        local text = newLabel(row, "GameFontHighlightSmall", RESULT_COLUMNS[c][5])
        text:SetWidth(RESULT_COLUMNS[c][4])
        text:SetPoint("LEFT", row, "LEFT", RESULT_COLUMNS[c][3], 0)
        row[RESULT_COLUMNS[c][1]] = text
    end
    -- The same one game search a list row's click is: the player buys in Blizzard's window.
    row:SetScript("OnClick", guarded(function(self) searchFor(self.data) end))
    row:SetScript("OnEnter", guarded(function(self) self.glow:Show() end))
    row:SetScript("OnLeave", guarded(function(self) self.glow:Hide() end))
    return row
end

-- Built with the list's panel, hidden until Search All is pressed; under the list, outside the house's window.
local function buildResults(parent, p)
    local r = CreateFrame("Frame", nil, parent)
    r:SetSize(WIDTH, RESULTS_TOP + 10)
    r:SetPoint("TOPLEFT", p, "BOTTOMLEFT", 0, -4)
    dress(r)
    r.title = newLabel(r, "GameFontNormal")
    r.title:SetPoint("TOPLEFT", r, "TOPLEFT", 10, -8)
    r.title:SetText("Search All")
    r.status = newLabel(r, "GameFontHighlightSmall")
    r.status:SetPoint("TOPLEFT", r, "TOPLEFT", 10, -24)
    r.status:SetWidth(WIDTH - 80)
    r.stopButton = newButton(r, "Stop", 60)
    r.stopButton:SetPoint("TOPRIGHT", r, "TOPRIGHT", -4, -4)
    r.stopButton:SetScript("OnClick", guarded(function()
        if search and search.run then ns.Walker.stop(search.run) end
    end))
    r.headers = {}
    for c = 1, #RESULT_COLUMNS do
        local header = newLabel(r, "GameFontDisableSmall", RESULT_COLUMNS[c][5])
        header:SetWidth(RESULT_COLUMNS[c][4])
        header:SetPoint("TOPLEFT", r, "TOPLEFT", RESULT_COLUMNS[c][3] + 1, -44)
        header:SetText(RESULT_COLUMNS[c][2])
        r.headers[RESULT_COLUMNS[c][1]] = header
    end
    r.rows = {}
    r.caption = newLabel(r, "GameFontDisableSmall")
    r.caption:SetWidth(WIDTH - 20)
    if type(r.caption.SetWordWrap) == "function" then r.caption:SetWordWrap(true) end
    r.caption:SetText(RESULTS_CAPTION)
    r:Hide()
    return r
end

local function whole(n)
    return string.format("%.0f", n)
end

-- One entry's four texts: lowest, under, cheapest, target.
local function resultTexts(e, over)
    if e.state == "pending" then
        if over then return "-", "", "not searched", "" end
        return "…", "", "", ""
    end
    if e.state == "refused" then return "-", "", "throttled - try again", "" end
    if e.state == "timeout" then return "-", "", "no answer", "" end
    local r = e.result
    local under = r.underQty == nil and "-" or whole(r.underQty)
    if r.lowest == nil then
        if r.dropped > 0 then return "-", under, "none match this row's filters (" .. whole(r.dropped) .. " left out)", "" end
        return "-", under, "none listed", ""
    end
    local stacks = Logic.stacksText(r.stacks, r.more, money)
    if r.short then stacks = stacks .. " · only " .. whole(r.total) .. " listed" end
    if r.dropped > 0 then stacks = stacks .. " · " .. whole(r.dropped) .. " left out by this row's filters" end
    local verdict = Logic.verdictText(r)
    if r.verdict == "under" then
        verdict = GREEN .. verdict .. CLOSE
    elseif r.verdict == "over" then
        verdict = RED .. verdict .. CLOSE
    end
    return money(r.lowest), under, stacks, verdict
end

-- Repaints the results from `search`; hides them when there is none. Called by the walk's own callbacks only.
local function paintResults()
    if not results then return end
    local s = search
    if not s then
        for i = 1, #results.rows do
            results.rows[i].data = nil
            results.rows[i]:Hide()
        end
        results:Hide()
        return
    end
    if not s.over then
        results.status:SetText("Searching " .. whole(math.min(s.settled + 1, s.total)) .. " of " .. whole(s.total)
            .. " - one query at a time, when the house is ready")
    elseif s.why == nil then
        results.status:SetText("Searched " .. whole(s.settled) .. " of " .. whole(s.total)
            .. ". Click a row to search it in Blizzard's window.")
    else
        results.status:SetText("Search All stopped (" .. tostring(s.why) .. "): " .. whole(s.settled) .. " of "
            .. whole(s.total) .. " searched.")
    end
    results.stopButton:SetShown(not s.over)
    for i = 1, #s.entries do
        local e = s.entries[i]
        local row = results.rows[i]
        if not row then
            row = newResultRow(results, i)
            results.rows[i] = row
        end
        row.data = { label = e.label, named = e.named }
        row.name:SetText(e.label)
        local lowest, under, stacks, verdict = resultTexts(e, s.over)
        row.lowest:SetText(lowest)
        row.under:SetText(under)
        row.stacks:SetText(stacks)
        row.verdict:SetText(verdict)
        row:Show()
    end
    for i = #s.entries + 1, #results.rows do
        results.rows[i].data = nil
        results.rows[i]:Hide()
    end
    local bottom = RESULTS_TOP + #s.entries * ROW_H
    results.caption:ClearAllPoints()
    results.caption:SetPoint("TOPLEFT", results, "TOPLEFT", 10, -(bottom + 8))
    results:SetHeight(bottom + 44)
    results:Show()
end

-- Ends a walk still running and forgets the results: Close, Done and the house closing. No walk runs unseen.
local function stopSearch()
    local s = search
    search = nil
    if s and s.run and not s.over then ns.Walker.stop(s.run) end
    paintResults()
end

-- A plain number, never a secret or anything else.
local function plain(v)
    if ns.isSecret(v) or type(v) ~= "number" then return nil end
    return v
end

-- The item's own quality, for a listing whose link does not say: the client's, else the one a quick scan noted.
local function itemQuality(itemID)
    if type(C_Item) == "table" and type(C_Item.GetItemQualityByID) == "function" then
        local ok, q = pcall(C_Item.GetItemQualityByID, itemID)
        if ok and plain(q) then return q end
    end
    local items = ns.db().items
    local known = type(items) == "table" and items[itemID] or nil
    if type(known) == "table" then return plain(known[2]) end
    return nil
end

-- One walker answer -> Logic.shoppingResult's listings. Only prices, quantities, the item key's level and the link's
-- quality are read - never who is selling. An item auction's buyout is for its whole stack: per unit, rounded up.
local function listingsOf(answer, itemID)
    local out = {}
    local quality = nil
    local function fallbackQuality()
        if quality == nil then quality = itemQuality(itemID) or false end
        return quality or nil
    end
    for i = 1, math.min(answer.count, MAX_LISTINGS) do
        local r = answer.info(i)
        if r then
            if answer.kind == "commodity" then
                out[#out + 1] = { unit = plain(r.unitPrice), qty = plain(r.quantity), quality = fallbackQuality() }
            else
                local qty, buyout = plain(r.quantity), plain(r.buyoutAmount)
                local unit = (qty and buyout and qty > 0) and math.ceil(buyout / qty) or nil
                local key = (not ns.isSecret(r.itemKey) and type(r.itemKey) == "table") and r.itemKey or nil
                local level = key and plain(key.itemLevel) or nil
                local q = Logic.linkQuality((not ns.isSecret(r.itemLink)) and r.itemLink or nil)
                if q == nil then q = fallbackQuality() end
                out[#out + 1] = { unit = unit, qty = qty, level = level, quality = q }
            end
        end
    end
    return out
end

local function build(parent)
    local p = CreateFrame("Frame", nil, parent)
    p:SetSize(WIDTH, TOP + 10)
    -- Under the Crucible bar when there is one, outside the house's right edge like the bar: it covers nothing of
    -- the game's own window.
    local strip = ns.Strip and ns.Strip.frame
    if strip then
        p:SetPoint("TOPLEFT", strip, "BOTTOMLEFT", 0, -8)
    else
        p:SetPoint("TOPLEFT", parent, "TOPRIGHT", 2, -28)
    end
    dress(p)

    p.title = newLabel(p, "GameFontNormal")
    p.title:SetPoint("TOPLEFT", p, "TOPLEFT", 10, -8)
    p.title:SetText("Shopping list")
    p.heading = newLabel(p, "GameFontHighlightSmall")
    p.heading:SetPoint("TOPLEFT", p, "TOPLEFT", 10, -24)
    p.heading:SetWidth(WIDTH - 44)

    local ok, close = pcall(CreateFrame, "Button", nil, p, "UIPanelCloseButton")
    if not ok then
        close = CreateFrame("Button", nil, p)
        close.label = newLabel(close, "GameFontNormal", "CENTER")
        close.label:SetAllPoints()
        close.label:SetText("x")
    end
    close:SetSize(24, 24)
    close:SetPoint("TOPRIGHT", p, "TOPRIGHT", -2, -2)
    close:SetScript("OnClick", guarded(function()
        closedThisVisit = true
        stopSearch()
        p:Hide()
    end))
    p.closeButton = close

    -- Done (0.15.2): the list just used, marked finished - hidden for the rest of the SESSION (not just this
    -- visit, like Close), and its identity recorded so the next upload tells the server to clear THAT list.
    local okDone, done = pcall(CreateFrame, "Button", nil, p, "UIPanelButtonTemplate")
    if not okDone then
        done = CreateFrame("Button", nil, p)
        done.label = newLabel(done, "GameFontNormal", "CENTER")
        done.label:SetAllPoints()
        done.label:SetText("Done")
    end
    done:SetSize(50, 22)
    done:SetPoint("TOPRIGHT", close, "TOPLEFT", -2, 0)
    if done.SetText then done:SetText("Done") end
    done:SetScript("OnClick", guarded(function()
        local list = currentList()
        if not list then return end
        doneList = { recipeID = list.recipeID, at = list.at }
        Logic.noteShoppingDone(ns.db(), list)
        stopSearch()
        p:Hide()
    end))
    p.doneButton = done

    -- Search All (B-S): one click, one item search per row that is not a vendor's, walked by Scan.lua.
    local all = newButton(p, "Search All", 84)
    all:SetPoint("TOPRIGHT", done, "TOPLEFT", -4, 0)
    all:SetScript("OnClick", guarded(function() Shopping.searchAll() end))
    p.searchAllButton = all

    p.headers = {}
    for c = 1, #COLUMNS do
        local header = newLabel(p, "GameFontDisableSmall", COLUMNS[c][5])
        header:SetWidth(COLUMNS[c][4])
        header:SetPoint("TOPLEFT", p, "TOPLEFT", COLUMNS[c][3] + 1, -44)
        header:SetText(COLUMNS[c][2])
        p.headers[COLUMNS[c][1]] = header
    end

    p.rows = {}
    p.cost = newLabel(p, "GameFontHighlightSmall")
    p.profit = newLabel(p, "GameFontHighlightSmall", "RIGHT")
    p.caption = newLabel(p, "GameFontDisableSmall")
    p.caption:SetWidth(WIDTH - 20)
    if type(p.caption.SetWordWrap) == "function" then p.caption:SetWordWrap(true) end
    p.caption:SetText(CAPTION)
    p:Hide()
    results = buildResults(parent, p)
    Shopping.results = results
    return p
end

-- Fills the panel from the list and the bags. Called on every show and on every bag event; asks nothing of the
-- house.
local function paint(list)
    if not panel then return end
    list = list or currentList()
    if not list then
        panel:Hide()
        return
    end
    panel.heading:SetText(Logic.shoppingHeading(list, ns.serverTime()))
    local rows = Logic.shoppingRows(list, bags(list), bank(list))
    for i = 1, #rows do
        local r = rows[i]
        local row = panel.rows[i]
        if not row then
            row = newRow(panel, i)
            panel.rows[i] = row
        end
        local label, named = nameOf(r.itemID, r.name)
        row.data = { label = label, named = named, vendor = r.vendor }
        row.name:SetText(label)
        local have = r.have == nil and "-" or string.format("%.0f", r.have)
        row.bags:SetText(r.enough and (GREEN .. have .. CLOSE) or have)
        row.bank:SetText(r.bank == nil and "—" or string.format("%.0f", r.bank))
        row.buy:SetText(string.format("%.0f", r.buy))
        row.each:SetText(money(r.each))
        row.payUpTo:SetText(money(r.payUpTo))
        row.action:SetText(r.vendor and "vendor" or "Search")
        row:Show()
    end
    for i = #rows + 1, #panel.rows do
        panel.rows[i].data = nil
        panel.rows[i]:Hide()
    end
    local bottom = TOP + #rows * ROW_H
    panel.cost:ClearAllPoints()
    panel.cost:SetPoint("TOPLEFT", panel, "TOPLEFT", 10, -(bottom + 8))
    panel.cost:SetText("Buy for about " .. money(list.cost))
    panel.profit:ClearAllPoints()
    panel.profit:SetPoint("TOPRIGHT", panel, "TOPRIGHT", -10, -(bottom + 8))
    local profit = "-"
    if list.profit ~= nil then
        profit = list.profit < 0 and (RED .. "-" .. money(-list.profit) .. CLOSE)
            or (GREEN .. "+" .. money(list.profit) .. CLOSE)
    end
    panel.profit:SetText("Profit at market " .. profit)
    panel.caption:ClearAllPoints()
    panel.caption:SetPoint("TOPLEFT", panel, "TOPLEFT", 10, -(bottom + 26))
    panel:SetHeight(bottom + 60)
end

-- Builds the panel once, the first time it is needed, on a client with a window to hang it off.
local function attach()
    if panel or cannotBuild or not window() then return panel end
    local ok, built = pcall(build, window())
    if not ok then
        cannotBuild = true
        return nil
    end
    panel = built
    Shopping.panel = built
    return panel
end

-- Shows the list if there is one - unless it is the exact one Done was pressed on this session, and `force`
-- is not set (/crucible shopping's own override). -> true when it is on screen.
local function show(force)
    local list = currentList()
    if not list or (not force and isDone(list)) or not attach() then return false end
    paint(list)
    panel:Show()
    return true
end

---------------------------------------------------------------------------------------------------
-- Search All (B-S): the button's one click
---------------------------------------------------------------------------------------------------

-- One item search per row that is not a vendor's (each item once), walked by Scan.lua's ns.Walker: it sends, paces,
-- stops and refuses; this only reads each answer into its row and paints. Refused - with the walker's own reason -
-- while anything else of ours is running or the house is closed; the last results then stay as they were.
function Shopping.searchAll()
    local list = currentList()
    if not list or not panel then return end
    local rows = Logic.shoppingRows(list, bags(list), bank(list))
    local entries, keys, seen = {}, {}, {}
    for i = 1, #rows do
        local r = rows[i]
        if not r.vendor and not seen[r.itemID] then
            local key = ns.Walker.itemKey(r.itemID)
            if key then
                seen[r.itemID] = true
                local label, named = nameOf(r.itemID, r.name)
                entries[#entries + 1] = { itemID = r.itemID, label = label, named = named, row = r, state = "pending" }
                keys[#keys + 1] = key
            end
        end
    end
    if #keys == 0 then
        ns.print("nothing on this list is sold at the auction house")
        return
    end
    local s = { entries = entries, settled = 0, total = #keys, over = false }
    local function mine() return search == s end
    local previous = search
    search = s -- the walk's first callbacks come back before start returns
    local run = ns.Walker.start({
        kind = "shopping",
        keys = keys,
        onAnswer = function(index, _, answer)
            local e = s.entries[index]
            e.state = "answered"
            e.result = Logic.shoppingResult(e.row, listingsOf(answer, e.itemID))
            if mine() then paintResults() end
        end,
        onMissed = function(index, _, why)
            s.entries[index].state = why
            if mine() then paintResults() end
        end,
        onProgress = function(done)
            s.settled = done
            if mine() then paintResults() end
        end,
        onDone = function(why)
            s.over, s.why = true, why
            if mine() then paintResults() end
        end,
    })
    if not run then
        search = previous
        return
    end
    s.run = run
    paintResults()
end

---------------------------------------------------------------------------------------------------
-- /crucible shopping
---------------------------------------------------------------------------------------------------

function Shopping.command()
    local list = currentList()
    if not list then
        ns.print("no shopping list yet - plan one on the web (Workbench, Send list to the game), then Sync")
        return
    end
    if not ns.ahOpen then
        ns.print("open the auction house - the shopping list shows there")
        return
    end
    closedThisVisit = false
    if not show(true) then
        -- No panel on this client: the list in chat instead, one mat a line.
        ns.print(Logic.shoppingHeading(list, ns.serverTime()))
        local rows = Logic.shoppingRows(list, bags(list), bank(list))
        for i = 1, #rows do
            local label = nameOf(rows[i].itemID, rows[i].name)
            ns.print("  " .. string.format("%.0f", rows[i].buy) .. " x " .. label)
        end
    end
end

---------------------------------------------------------------------------------------------------
-- Events: showing and repainting only
---------------------------------------------------------------------------------------------------

-- Loads after Strip.lua, so the bar is built (and the panel can sit under it) by the time this runs.
ns.on("AUCTION_HOUSE_SHOW", function()
    closedThisVisit = false
    show()
end)

ns.on("AUCTION_HOUSE_CLOSED", function()
    if panel then panel:Hide() end
    stopSearch() -- Scan.lua has already ended the walk itself; its results go with the house (B-S)
end)

-- The bags changed (a loot, a purchase, the mailbox): the counts follow. The game's own event, not a clock.
local function repaint()
    if panel and panel:IsShown() and not closedThisVisit then paint() end
end
if not ns.on("BAG_UPDATE_DELAYED", repaint) then ns.on("BAG_UPDATE", repaint) end
