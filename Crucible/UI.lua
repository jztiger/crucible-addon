-- Crucible: what the player sees on item tooltips, and the /crucible status lines.
--
--   Lowest now: 45s (7m)                                    cheapest listing, from the last quick scan
--   Market value: 52s · sells ~3.2 a day                   what the server worked out, from Data.lua (M3)
--   Stack of 20: 9g lowest now · 10g 40s market value      on a stack in the bags or the bank only: both x its size
--   Vendor: 10c                                             when a visited vendor sells it
--   Craft cost: 4s 90c                     Profit: 37s 85c  green, or "Profit: -..." in red: crafting to sell at
--     4 x Medium Leather @ 1s 20c                   4s 80c  market value (else Lowest now), less the house's cut.
--     1 x Coarse Thread (vendor)                       10c  Left out when a guess (Logic.craftingProfit).
--   Disenchant: ~14s 2c                                     on green, blue and purple armor and weapons: what it
--     75%  2-5 x Soul Dust                      4s 24c each  most likely disenchants into, and the average worth
--     20%  1-2 x Lesser Mystic Essence          7s 70c each  (an estimate: Logic.disenchant's table).
--
-- 0.11.0: the Tally 2.0 words (docs/crucible-2.0/README.md) - Lowest now was Min AH Price, Market value was
-- Market, Craft cost was Crafting Cost, and a loss is a red Profit with a minus sign rather than "Loss".
--
-- Craft cost = the mats of the cheapest known recipe, each at its vendor price when a vendor the player has
-- visited sells it, otherwise its auction price (last browse scan), times the quantity (Logic.cheapestRecipe).
-- Recipes and vendor prices are learned by Craft.lua from windows the player opened.
--
-- The tooltip price comes from the last complete browse scan held in memory (CrucibleDB.prices).
-- That table may be empty: saved state does not survive a full client restart on this client, so
-- no line is simply no line until the next /crucible browse. Nothing here asks the server anything.

local _, ns = ...
local Logic = ns.Logic

local UI = {}
ns.UI = UI

local LABEL = "|cff33ff99Lowest now|r: "
local CRAFT_LABEL = "|cff33ff99Craft cost|r: "
local VENDOR_LABEL = "|cff33ff99Vendor|r: "
local MARKET_LABEL = "|cff33ff99Market value|r: "
local STACK_LABEL = "|cff33ff99Stack of "
local DISENCHANT_LABEL = "|cff33ff99Disenchant|r: "
local PROFIT, LOSS, DIM, CLOSE = "|cff00ff00Profit: ", "|cffff2020Profit: -", "|cff808080", "|r"

---------------------------------------------------------------------------------------------------
-- Money
---------------------------------------------------------------------------------------------------

local function money(copper)
    if type(C_CurrencyInfo) == "table" and type(C_CurrencyInfo.GetCoinTextureString) == "function" then
        local ok, text = pcall(C_CurrencyInfo.GetCoinTextureString, copper)
        if ok and type(text) == "string" and text ~= "" then return text end
    end
    if type(GetCoinTextureString) == "function" then
        local ok, text = pcall(GetCoinTextureString, copper)
        if ok and type(text) == "string" and text ~= "" then return text end
    end
    return Logic.formatMoney(copper)
end
UI.money = money

---------------------------------------------------------------------------------------------------
-- Tooltip line: "Crucible: <min price> (<age>)"
---------------------------------------------------------------------------------------------------

-- Where the prices came from when not from this session's own scan: "saved" (the player's own earlier scan, handed
-- back by the local bake) or "shared" (the newest scan anyone uploaded, from the server). "" for the player's own.
function UI.priceSource(before, after)
    local db = CrucibleDB
    local from = type(db) == "table" and db.pricesFrom or nil
    if from ~= "saved" and from ~= "shared" then return "" end
    return (before or "") .. from .. (after or "")
end

-- -> the line for this item, or nil when there is no price for it, THEN (M3) the market line, or nil when
-- the data file has no market value for it. Two separate answers: an item nobody is selling today can
-- still have a market value, and a market value is not "what it costs right now".
function UI.priceLine(itemID)
    if ns.isSecret(itemID) or type(itemID) ~= "number" then return nil end
    local db = CrucibleDB
    if type(db) ~= "table" or type(db.prices) ~= "table" then return nil end
    local line
    local price = db.prices[itemID]
    if type(price) == "number" and price > 0 then
        line = LABEL .. money(price)
        if type(db.pricesAt) == "number" and db.pricesAt > 0 then
            line = line .. " (" .. Logic.formatAge(ns.serverTime() - db.pricesAt) .. UI.priceSource(", ") .. ")"
        end
    end
    return line, UI.marketLine(itemID)
end

-- -> "Market value: <value> · sells ~N a day", the two figures the server works out and sends back in
-- Data.lua (spec 2026-09-24 section 5). The sale rate travels x100 as a whole number, so it is divided here;
-- it is left off entirely when the server has none, and 0.0 a day is a real answer rather than a missing one.
-- 0.11.0: at 100 a day and more it is a whole number, below that one decimal - the site's perDay rule
-- (src/dashboard/money.ts), compared on the one-decimal text so 99.96 reads "100", never "100.0".
local function perDay(rate)
    local oneDecimal = string.format("%.1f", rate)
    if tonumber(oneDecimal) >= 100 then return string.format("%.0f", math.floor(rate + 0.5)) end
    return oneDecimal
end

function UI.marketLine(itemID)
    if ns.isSecret(itemID) or type(itemID) ~= "number" then return nil end
    local db = CrucibleDB
    if type(db) ~= "table" or type(db.market) ~= "table" then return nil end
    -- M87: a market scanned for under 3 days has no values at all yet; say why, rather than nothing.
    if Logic.marketNotYet(db) then return MARKET_LABEL .. "not yet - needs " .. Logic.MARKET_VALUE_DAYS .. " days of scans" end
    local value = db.market[itemID]
    if type(value) ~= "number" or value <= 0 then return nil end
    local line = MARKET_LABEL .. money(value)
    local sells = type(db.sells) == "table" and db.sells[itemID] or nil
    if type(sells) == "number" and sells >= 0 then
        line = line .. " · sells ~" .. perDay(sells / 100) .. " a day"
    end
    -- M1: how old that value is, as the lowest-now line shows its own; orange once past PRICES_MAX_AGE.
    local age, stale = Logic.marketValueAge(db, ns.serverTime())
    if age then
        local text = "(" .. Logic.formatAge(age) .. ")"
        line = line .. " " .. (stale and ("|cffff8000" .. text .. "|r") or text)
    end
    return line
end

-- -> "Stack of N: <N x lowest> lowest now · <N x market> market value" for N of the item, or nil for one item or
-- when it has neither price. Lowest now and Market value are the price of ONE whatever is hovered, while the game's
-- own Sell Price line right above them is the whole stack's - so a stack of 20 read as if a vendor paid about what
-- the auction house does (owner, 2026-11-12). The stack's figures are the same two prices times its size.
function UI.stackLine(itemID, count)
    if ns.isSecret(itemID) or type(itemID) ~= "number" then return nil end
    if ns.isSecret(count) or type(count) ~= "number" or count < 2 then return nil end
    local db = CrucibleDB
    if type(db) ~= "table" then return nil end
    local parts = {}
    local price = type(db.prices) == "table" and db.prices[itemID] or nil
    if type(price) == "number" and price > 0 then parts[#parts + 1] = money(price * count) .. " lowest now" end
    local value = type(db.market) == "table" and not Logic.marketNotYet(db) and db.market[itemID] or nil
    if type(value) == "number" and value > 0 then parts[#parts + 1] = money(value * count) .. " market value" end
    if #parts == 0 then return nil end
    return STACK_LABEL .. string.format("%.0f", count) .. "|r: " .. table.concat(parts, " · ")
end

-- How many the bag or bank slot behind this tooltip holds, or nil when the tooltip is not a slot's (a link, an
-- auction, a reward). The game fills a slot's tooltip through SetBagItem(bag, slot), for the bags and the bank
-- alike, and keeps that as the info it is processing while it calls us (Blizzard's TooltipDataHandler.lua); the
-- slot then says which item it holds and how many - so the size is the hovered stack's own, 5 or 20. Two reads of
-- the player's own bags, nothing written and nothing kept.
local function stackBehind(tooltip, itemID)
    if type(tooltip.GetProcessingTooltipInfo) ~= "function" then return nil end
    local info = tooltip:GetProcessingTooltipInfo()
    if ns.isSecret(info) or type(info) ~= "table" then return nil end
    local getter, args = info.getterName, info.getterArgs
    if ns.isSecret(getter) or getter ~= "GetBagItem" or ns.isSecret(args) or type(args) ~= "table" then return nil end
    local bag, slot = args[1], args[2]
    if ns.isSecret(bag) or ns.isSecret(slot) or type(bag) ~= "number" or type(slot) ~= "number" then return nil end
    if type(C_Container) ~= "table" or type(C_Container.GetContainerItemInfo) ~= "function" then return nil end
    local held = C_Container.GetContainerItemInfo(bag, slot)
    if ns.isSecret(held) or type(held) ~= "table" then return nil end
    if ns.isSecret(held.itemID) or held.itemID ~= itemID then return nil end
    return held.stackCount
end

-- -> "Vendor: <unit price>" for an item a visited vendor sells for gold in unlimited supply, else nil.
-- This is the price Craft cost uses for that mat, whatever the auction house says.
function UI.vendorLine(itemID)
    if ns.isSecret(itemID) or type(itemID) ~= "number" then return nil end
    local db = CrucibleDB
    if type(db) ~= "table" or type(db.vendor) ~= "table" then return nil end
    local price = db.vendor[itemID]
    if type(price) ~= "number" or price <= 0 then return nil end
    return VENDOR_LABEL .. money(price)
end

local function unpriced(n)
    if n == 1 then return "1 mat with no price" end
    return string.format("%.0f", n) .. " mats with no price"
end

-- The name as far as the client has it; an item it has not loaded yet is shown by its number (the hover itself
-- makes the client fetch it, so the next hover has the name).
local function itemName(itemID)
    if type(C_Item) == "table" and type(C_Item.GetItemNameByID) == "function" then
        local ok, name = pcall(C_Item.GetItemNameByID, itemID)
        if ok and not ns.isSecret(name) and type(name) == "string" and name ~= "" then return name end
    end
    return "item " .. string.format("%.0f", itemID)
end
UI.itemName = itemName

-- "Cannot Sell" (2026-09-25, owner-approved): whether the client currently knows this item binds ON PICKUP -
-- true, false for anything else it knows, nil when the client hasn't cached this item's info yet (so the
-- caller should treat it as unknown and show the ordinary figure, never a false "sellable"). Feature-detected,
-- the same pattern Scan.lua's own base-name read already uses: C_Item.GetItemInfo where the client has it,
-- else the classic global GetItemInfo Forever's client still exposes. Both return the same 17 values in the
-- same order; the 14th is bindType, and Blizzard's own enum for "binds when picked up" is 1.
local BIND_ON_PICKUP = 1
function UI.bindsOnPickup(itemID)
    if ns.isSecret(itemID) or type(itemID) ~= "number" then return nil end
    local fn
    if type(C_Item) == "table" and type(C_Item.GetItemInfo) == "function" then
        fn = C_Item.GetItemInfo
    elseif type(GetItemInfo) == "function" then
        fn = GetItemInfo
    else
        return nil
    end
    -- select(14, ...) rather than a run of placeholder locals before pcall's own `ok`, which is exactly the
    -- kind of position that is trivial to miscount by one and get no error for, only a silently wrong item
    -- (caught in review: an earlier version of this line landed on subclassID, one short of bindType).
    local ok, bindType = pcall(function() return (select(14, fn(itemID))) end)
    if not ok or ns.isSecret(bindType) or type(bindType) ~= "number" then return nil end
    return bindType == BIND_ON_PICKUP
end

-- -> "Craft cost: <total> (<one> each) + N mats with no price", then "Profit: <n>" / "Profit: -<n>" or nil, then
-- the chosen recipe's mats as { {left, right}, ... }; nil when no recipe makes this item
function UI.craftLine(itemID)
    if ns.isSecret(itemID) or type(itemID) ~= "number" then return nil end
    local db = CrucibleDB
    if type(db) ~= "table" or type(db.recipes) ~= "table" then return nil end
    local _, total, missing, each, recipe = Logic.cheapestRecipe(db.recipes[itemID], db.prices, db.vendor)
    if not total then return nil end

    local mats = {}
    local rows = Logic.costBreakdown(recipe, db.prices, db.vendor)
    for i = 1, #rows do
        local row = rows[i]
        local left = "  " .. string.format("%.0f", row.qty) .. " x " .. itemName(row.itemID)
        if row.source == "vendor" then left = left .. " (vendor)" end
        if row.unit and row.qty > 1 then left = left .. " @ " .. money(row.unit) end
        mats[i] = { left, row.total and money(row.total) or (DIM .. "no price" .. CLOSE) }
    end

    if total == 0 and missing > 0 then return CRAFT_LABEL .. "unknown (" .. unpriced(missing) .. ")", nil, mats end
    local line = CRAFT_LABEL .. money(total)
    if each ~= total then line = line .. " (" .. money(each) .. " each)" end
    if missing > 0 then line = line .. " + " .. unpriced(missing) end

    -- "Cannot Sell" (2026-09-25): it never sells, whatever the numbers would say, so there is nothing to
    -- compute - checked before market/prices are even read. A client that hasn't cached this item yet
    -- (bindsOnPickup returns nil) is treated as unknown, exactly like any other missing fact here: the
    -- ordinary profit line shows, not a false claim either way.
    local result
    if UI.bindsOnPickup(itemID) then
        result = DIM .. "Cannot sell" .. CLOSE
    else
        -- M3, fix round 1: what one sells for is the server's market value when there is one, today's cheapest
        -- listing when there is not - the same order Logic.profitSummary uses for the Profit panel. Hovering a
        -- row of that panel shows this tooltip, so the two must never quote different profits for one recipe.
        local market = type(db.market) == "table" and db.market[itemID] or nil
        local price = (type(market) == "number" and market > 0) and market
            or (type(db.prices) == "table" and db.prices[itemID] or nil)
        local profit = Logic.craftingProfit(total, missing, recipe.qty, price)
        if profit then
            result = (profit >= 0 and (PROFIT .. money(profit)) or (LOSS .. money(-profit))) .. CLOSE
        end
    end
    return line, result, mats
end

-- The quality, item level and class of an item as far as the client has it loaded: GetItemInfo's 3rd, 4th and
-- 12th returns, read by number out of a table so no run of placeholders can be miscounted (see bindsOnPickup).
local function gearFacts(itemID)
    local fn
    if type(C_Item) == "table" and type(C_Item.GetItemInfo) == "function" then
        fn = C_Item.GetItemInfo
    elseif type(GetItemInfo) == "function" then
        fn = GetItemInfo
    else
        return nil
    end
    local ok, info = pcall(function() return { fn(itemID) } end)
    if not ok then return nil end
    local quality, level, classID = info[3], info[4], info[12]
    if ns.isSecret(quality) or ns.isSecret(level) or ns.isSecret(classID) then return nil end
    return quality, classID, level
end

-- -> "Disenchant: ~<average>", then one { left, right } per result, most likely first:
--      "  75%  2-5 x Soul Dust"     "4s 24c each"
-- nil for anything that is not green, blue or purple armor or a weapon. The odds are Logic.disenchant's table,
-- an estimate, hence the "~"; each result is priced at its market value, else at Lowest now, and the average is
-- over many disenchants. Shown on a tooltip only: never a sort, a filter or a highlight on auction listings (C13).
function UI.disenchantLines(itemID)
    if ns.isSecret(itemID) or type(itemID) ~= "number" then return nil end
    local db = CrucibleDB
    if type(db) ~= "table" then return nil end
    local results = Logic.disenchant(gearFacts(itemID))
    if not results then return nil end
    local market = type(db.market) == "table" and not Logic.marketNotYet(db) and db.market or nil
    local total, missing, units = Logic.disenchantValue(results, market, db.prices)
    local rows = {}
    for i = 1, #results do
        local r = results[i]
        local count = string.format("%.0f", r[3])
        if r[4] ~= r[3] then count = count .. "-" .. string.format("%.0f", r[4]) end
        rows[i] = {
            "  " .. string.format("%.0f", r[2]) .. "%  " .. count .. " x " .. itemName(r[1]),
            units[i] and (money(units[i]) .. " each") or (DIM .. "no price" .. CLOSE),
        }
    end
    if not total then return DISENCHANT_LABEL .. "no price yet", rows end
    local line = DISENCHANT_LABEL .. "~" .. money(total)
    if missing > 0 then line = line .. " + " .. string.format("%.0f", missing) .. " with no price" end
    return line, rows
end

-- Two columns where the tooltip has them, the same words on one line where it does not. soft: the quieter
-- off-white of the mat lines, so the cost line above them stays the headline.
local function addPair(tooltip, left, right, soft)
    if type(tooltip.AddDoubleLine) == "function" then
        if soft then
            tooltip:AddDoubleLine(left, right, 0.85, 0.85, 0.85, 0.85, 0.85, 0.85)
        else
            tooltip:AddDoubleLine(left, right)
        end
    else
        tooltip:AddLine(left .. "  " .. right)
    end
end

local function addLine(tooltip, itemID)
    if type(tooltip) ~= "table" or type(tooltip.AddLine) ~= "function" then return end
    local line, market = UI.priceLine(itemID)
    if line then tooltip:AddLine(line) end
    if market then tooltip:AddLine(market) end
    -- Under its own pcall: whatever a client does with the slot behind a tooltip, the lines below still arrive.
    local okStack, count = pcall(stackBehind, tooltip, itemID)
    local stack = okStack and UI.stackLine(itemID, count) or nil
    if stack then tooltip:AddLine(stack) end
    local vendor = UI.vendorLine(itemID)
    if vendor then tooltip:AddLine(vendor) end
    local craft, result, mats = UI.craftLine(itemID)
    if craft then
        if result then addPair(tooltip, craft, result) else tooltip:AddLine(craft) end
        for i = 1, #mats do addPair(tooltip, mats[i][1], mats[i][2], true) end
    end
    local disenchant, results = UI.disenchantLines(itemID)
    if not disenchant then return end
    tooltip:AddLine(disenchant)
    for i = 1, #results do addPair(tooltip, results[i][1], results[i][2], true) end
end

local function fromTooltipData(tooltip, data)
    if ns.isSecret(data) or type(data) ~= "table" then return end
    addLine(tooltip, data.id)
end

local function fromItemLink(tooltip)
    if type(tooltip) ~= "table" or type(tooltip.GetItem) ~= "function" then return end
    local _, link = tooltip:GetItem()
    if ns.isSecret(link) or type(link) ~= "string" then return end
    addLine(tooltip, tonumber(string.match(link, "item:(%d+)")))
end

-- The whole handler runs under pcall, and a tooltip failure is swallowed: a broken price line
-- must never break the game's tooltips or fill the chat on every mouse-over.
local hooked = false
if type(TooltipDataProcessor) == "table" and type(TooltipDataProcessor.AddTooltipPostCall) == "function"
    and type(Enum) == "table" and type(Enum.TooltipDataType) == "table" and Enum.TooltipDataType.Item ~= nil then
    hooked = pcall(TooltipDataProcessor.AddTooltipPostCall, Enum.TooltipDataType.Item, function(tooltip, data)
        pcall(fromTooltipData, tooltip, data)
    end)
end
if not hooked and type(GameTooltip) == "table" and type(GameTooltip.HookScript) == "function"
    and type(GameTooltip.HasScript) == "function" then
    local okHas, has = pcall(GameTooltip.HasScript, GameTooltip, "OnTooltipSetItem")
    if okHas and has then
        hooked = pcall(GameTooltip.HookScript, GameTooltip, "OnTooltipSetItem", function(tooltip)
            pcall(fromItemLink, tooltip)
        end)
    end
end
UI.tooltipHooked = hooked

---------------------------------------------------------------------------------------------------
-- /crucible : status
---------------------------------------------------------------------------------------------------

local function age(at, now)
    -- "never" may only mean "not since the client was restarted": saved state does not survive that
    if type(at) ~= "number" or at <= 0 then return "never, or not since the last client restart" end
    return Logic.formatAge(now - at) .. " ago"
end

function UI.status()
    local db = ns.db()
    local now = ns.serverTime()
    local scans, bytes = Logic.ringStats(db.chunks)
    local allowed, remaining = Logic.canReplicate(now, db.state.lastReplicateAt, Logic.REPLICATE_COOLDOWN)

    ns.print("v" .. Logic.VERSION .. " - auction house " .. (ns.ahOpen and "open" or "closed")
        .. (ns.Scan.busy() and ", a scan is running" or ""))
    ns.print("last full scan: " .. age(db.state.lastReplicateAt, now)
        .. (allowed and ", ready" or (", cooldown " .. Logic.formatAge(remaining) .. " left")))
    ns.print("last quick scan: " .. age(db.state.lastBrowseAt, now))
    ns.print(string.format("held in memory: %.0f %s (room for %.0f), %.1f KB of %.0f MB - Sync writes them to disk",
        scans, scans == 1 and "scan" or "scans", Logic.RING_MAX_SCANS, bytes / 1024, Logic.RING_MAX_BYTES / 1048576))

    local priced = 0
    for _ in pairs(db.prices) do priced = priced + 1 end
    if priced > 0 then
        ns.print(string.format("tooltip prices: %.0f items, from %s", priced, age(db.pricesAt, now)) .. UI.priceSource(" (", ")"))
    else
        ns.print("tooltip prices: none yet - Quick scan fills them")
    end
    local recipes, vendor = 0, 0
    for _, list in pairs(db.recipes) do
        if type(list) == "table" then recipes = recipes + #list end
    end
    for _ in pairs(db.vendor) do vendor = vendor + 1 end
    if recipes > 0 or vendor > 0 then
        ns.print(string.format("recipes known: %.0f - vendor prices: %.0f", recipes, vendor))
    else
        ns.print("recipes known: none yet - open a profession window, and a vendor for thread and dyes")
    end
    local baked = ns.baked
    if type(baked) == "table" and type(baked.builtAt) == "number" and baked.builtAt > 0 then
        local bakedVendor, bakedRecipes = 0, 0
        if type(baked.vendor) == "table" then
            for _ in pairs(baked.vendor) do bakedVendor = bakedVendor + 1 end
        end
        if type(baked.recipes) == "table" then
            for _, list in pairs(baked.recipes) do
                if type(list) == "table" then bakedRecipes = bakedRecipes + #list end
            end
        end
        ns.print(string.format("built-in data: %.0f vendor %s, %.0f %s, baked %s", bakedVendor,
            bakedVendor == 1 and "price" or "prices", bakedRecipes, bakedRecipes == 1 and "recipe" or "recipes",
            age(baked.builtAt, now)))
    else
        -- The empty Data.lua the addon ships with: nothing has replaced it yet. This is what a friend sees in the
        -- minutes after installing, and saying nothing at all left them wondering whether it was working.
        ns.print("built-in data: none yet - it arrives on its own once you have scanned and reloaded")
    end
    local ok, why = ns.Export.available()
    if not ok then ns.print(why) end
end
