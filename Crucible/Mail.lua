-- Crucible: the player's OWN auction outcomes, read out of the mailbox they opened.
--
-- What this file is for (design spec 2026-09-24, section 4): an auction that sold leaves an invoice in the
-- mail saying what it fetched, what the deposit was and what the auction house kept; one that expired or was
-- cancelled comes back with the item. Those are the only numbers anyone has for what things really sell for,
-- as opposed to what they are listed at, and they are about the player's own auctions.
--
-- Rules this file keeps (docs/decisions.md C7, C11; spec section 6):
--   * The mailbox is read only while the player has it open, and only because they opened it. MAIL_SHOW
--     reads it once; MAIL_INBOX_UPDATE reads it only while it is still open; MAIL_CLOSED ends that. There is
--     no timer in this file, no other event, and nothing at all happens when the mailbox is shut.
--   * GetInboxInvoiceInfo's third return is the OTHER party to the auction - the one field in the whole
--     mailbox that is about somebody else. It is discarded in the destructuring below and never bound to a
--     name, the same discipline Scan.lua applies to the replicate list's four name positions. The compliance
--     test in addon/tests/syntax.test.ts holds it.
--   * The reader takes nothing, returns nothing, deletes nothing; nothing here posts, buys or cancels an
--     auction. It reads what is already on screen. The ONE thing in this file that takes anything out of the
--     mailbox is auto-take (B-M, at the foot of the file): off by default, and then only the one mail the
--     player has just clicked open, with the game's own one-call take, in that click's own event chain.
--   * Every client function is feature-detected and called under pcall, and every value that comes back is
--     checked with ns.isSecret before it is used, exactly as Scan.lua and Craft.lua do.
--
-- What is read lands in CrucibleDB.sales (Logic.noteSale, which owns the dedup key); it leaves in the
-- reference document on the next Sync or /crucible reload, like everything else.
--
-- M72: a member who switched "Record my sales" off on Your PCs gets captureSales = false in their own Data.lua
-- (Logic.capturesSales), and then nothing here reads the inbox at all. The server keeps none of their sales
-- either way, so a copy of the addon from before this line changes nothing they keep.
--
-- 0.11.0 (D15): a Sync button on the mail window while the mailbox is open, with the "press Sync to upload"
-- hint while something is waiting. It is a click, and it calls the same ns.reload as /crucible reload and the
-- strip's Sync (Core.lua) - the one place the UI is ever reloaded. Nothing else here is new: no timer, no
-- mail action, and the button does nothing until somebody presses it.
--
-- Review M11/M19 (reference document schema 4): ONE ROW PER MAIL PER SESSION, and a return says why it came
-- back. The server counts the rows of one document that fold together as DIFFERENT mails (sale.times,
-- src/server/ingest/reference.ts MULTIPLICITY), so this file must never write one mail twice in a session -
-- see "readings" below for how a mail is told apart from its own later readings, and from a stale one.
-- "Record on the first MAIL_INBOX_UPDATE after MAIL_SHOW" is realised by those rules, not by special-casing
-- an event: MAIL_SHOW's walk is the EARLY reading (whatever snapshot the client had cached - kept, because
-- another mail addon's OpenAll can take a mail before the update ever shows it, review M44), the first update
-- after it is the client's fresh fetch, and a fresh reading of a mail supersedes a stale one.

local _, ns = ...
local Logic = ns.Logic

local Mail = {}
ns.Mail = Mail

-- An auction that did not sell comes back with its subject saying so and the item attached. 0.11.0 (D13):
-- the client's own format strings come first (AUCTION_REMOVED_MAIL_SUBJECT, AUCTION_EXPIRED_MAIL_SUBJECT,
-- e.g. "Auction cancelled: %s"), used only when one ends in %s so the part before it is a plain prefix;
-- then the English subjects in both spellings as fallbacks. The owner's UAT (2026-09-24) found Forever
-- writing "Auction canceled: " with ONE L, which the 0.10.0 list did not have, so no cancelled auction was
-- ever captured. Review M19: each prefix also says WHY the auction came back - a cancel is the player's
-- choice, an expiry a failed sale - and the row carries it.
local FALLBACK_PREFIXES = {
    { "Auction expired: ", "expired" },
    { "Auction cancelled: ", "cancelled" },
    { "Auction canceled: ", "cancelled" },
}
-- The invoice type the game gives the OTHER side of a sale - the person who bought it. Everything else is
-- the player's own sale-side invoice. Tested this way round on purpose: the word for the other type is one
-- the compliance test bans from the addon's code, and nothing here needs it.
local BUYER = "buyer"

-- A whole number of seconds; nothing else can key a mail (see Logic.noteSale on the dedup rule).
local function isWholeSecond(v)
    return type(v) == "number" and v == v and v >= 1 and v < 4102444800 and v % 1 == 0
end

-- The prefix a client format string gives, or nil. Compared as text, never as a pattern: in a Lua pattern
-- "%s" means whitespace. A string that is nothing but "%s" would make every mail a return, so it is refused.
local function clientPrefix(format)
    if ns.isSecret(format) or type(format) ~= "string" then return nil end
    if #format < 3 or string.sub(format, -2) ~= "%s" then return nil end
    return string.sub(format, 1, -3)
end

-- The client's prefixes first, then the fallbacks, each with its reason. Read on every walk rather than once
-- at load: the globals are the client's, and reading two of them costs nothing.
local function returnPrefixes()
    local list = {}
    local removed, expired = clientPrefix(AUCTION_REMOVED_MAIL_SUBJECT), clientPrefix(AUCTION_EXPIRED_MAIL_SUBJECT)
    if removed then list[#list + 1] = { removed, "cancelled" } end
    if expired then list[#list + 1] = { expired, "expired" } end
    for i = 1, #FALLBACK_PREFIXES do list[#list + 1] = FALLBACK_PREFIXES[i] end
    return list
end

-- The item's name and why it came back, out of "Auction expired: Linen Cloth", or nil when this is not a
-- returned auction.
local function returnedName(subject, prefixes)
    if ns.isSecret(subject) or type(subject) ~= "string" then return nil end
    for i = 1, #prefixes do
        local prefix, reason = prefixes[i][1], prefixes[i][2]
        if string.sub(subject, 1, #prefix) == prefix then
            local name = string.sub(subject, #prefix + 1)
            if #name >= 1 and #name <= Logic.MAX_NAME_CHARS then return name, reason end
        end
    end
    return nil
end

-- How many mail the inbox holds. 0 on any client that cannot say.
local function inboxCount()
    if type(GetInboxNumItems) ~= "function" then return 0 end
    local ok, n = pcall(GetInboxNumItems)
    if not ok or ns.isSecret(n) or type(n) ~= "number" or n ~= n or n < 1 then return 0 end
    return math.floor(n)
end

-- The subject, and daysLeft exactly as the client gave it (nil when it cannot say one).
--
-- GetInboxHeaderInfo's returns, in the client's order:
--   packageIcon, stationeryIcon, sender, subject, money, CODAmount, daysLeft, itemCount, wasRead,
--   wasReturned, textCreated, canReply, isGM
-- The sender is position 3 and is discarded with the rest: auction mail is recognised by its invoice and by
-- the subject, so there is never a reason to hold a name that, on ordinary mail, is another player's.
-- (The owner's UAT dump of a cancelled auction, 2026-09-24: [3] "Horde Auction House", [4] "Auction
-- canceled: Skinning Knife", [7] 29.999559402466, [8] 1.)
local function header(index)
    if type(GetInboxHeaderInfo) ~= "function" then return nil end
    local ok, _, _, _, subject, _, _, daysLeft = pcall(GetInboxHeaderInfo, index)
    if not ok then return nil end
    if ns.isSecret(subject) or type(subject) ~= "string" then subject = nil end
    if ns.isSecret(daysLeft) or type(daysLeft) ~= "number" or daysLeft ~= daysLeft or daysLeft < 0 then
        return subject, nil
    end
    return subject, daysLeft
end

-- A whole number of at least 1, from a value the client handed back; nil otherwise.
local function positiveWhole(v)
    if ns.isSecret(v) or type(v) ~= "number" or v ~= v or v < 1 or v >= 4294967296 then return nil end
    return math.floor(v)
end

-- The first attachment: its item id and how many of it. GetInboxItem(index, attachment) -> name, itemID,
-- texture, count, quality, canUse.
--
-- 0.11.0 (D13): the owner's UAT dump on Forever (2026-09-24) proved this is the modern shape there -
-- "Skinning Knife", 7005 (the real item id), 135637 (a texture), 1 (the count). So [2] is taken as the
-- item id only when it is a positive whole number AND [3] is a texture FILE id (>= TEXTURE_ID_MIN), else 0
-- as before; the server matches a 0 by name like every other name-only row. The old layout - name,
-- texture, count, quality, ... - also has two positive numbers there, but its [3] is a stack count, never
-- that large, so a texture id is never written down as an item id.
-- The count is 1 when the client cannot say (a mail with nothing on it is still one auction).
local TEXTURE_ID_MIN = 100000
local function attachment(index)
    if type(GetInboxItem) ~= "function" then return 0, 1 end
    local ok, _, itemID, texture, count = pcall(GetInboxItem, index, 1)
    if not ok then return 0, 1 end
    local id = positiveWhole(itemID)
    local textureID = positiveWhole(texture)
    if not id or not textureID or textureID < TEXTURE_ID_MIN then id = 0 end
    return id, positiveWhole(count) or 1
end

-- One mail -> what it says, or nil when it is not one of the player's own auction outcomes:
--   { outcome, name, count, price, deposit, cut, itemID, reason, days, minute, raw }
-- `days` is daysLeft exactly as the client gave it; `raw` is the moment it says the mail expires, by THIS
-- reading's clock (now + daysLeft), and `minute` that moment rounded DOWN to the minute - the expiry a NEW
-- reading of this mail would be filed under (see readings below for when it is not new).
--
-- GetInboxInvoiceInfo's returns, from a live /dump on Forever (2026-09-24) with one sold auction in the box:
--   invoiceType, itemName, <the other party>, bid, buyout, deposit, consignment, moneyDelay, etaHour,
--   etaMin, count, itemID
-- On that client [3] came back "" and [12] false - the invoice names no item, so the row travels by name and
-- the server matches it. [4] bid is the amount the player received and [7] consignment is the house's cut.
local function readMail(index, now, prefixes)
    local subject, daysLeft = header(index)
    if daysLeft == nil then return nil end
    local raw = now + daysLeft * 86400
    local minute = math.floor(raw / 60) * 60
    if not isWholeSecond(minute) then return nil end
    local m = { days = daysLeft, raw = raw, minute = minute }

    if type(GetInboxInvoiceInfo) == "function" then
        local ok, invoiceType, itemName, _, bid, _, deposit, consignment, _, _, _, count, itemID =
            pcall(GetInboxInvoiceInfo, index)
        if ok and not ns.isSecret(invoiceType) and type(invoiceType) == "string" then
            -- A mail with an invoice is an auction house mail either way; whether it is OURS is the type.
            if invoiceType == BUYER then return nil end
            if ns.isSecret(itemName) or type(itemName) ~= "string" then return nil end
            if ns.isSecret(bid) or type(bid) ~= "number" or bid ~= bid then return nil end
            if ns.isSecret(deposit) or type(deposit) ~= "number" then deposit = 0 end
            if ns.isSecret(consignment) or type(consignment) ~= "number" then consignment = 0 end
            if ns.isSecret(count) or type(count) ~= "number" or count < 1 then count = 1 end
            if ns.isSecret(itemID) or type(itemID) ~= "number" then itemID = 0 end
            m.outcome, m.name, m.count, m.price = "sold", itemName, math.floor(count), math.floor(bid)
            m.deposit, m.cut, m.itemID = math.floor(deposit), math.floor(consignment), math.floor(itemID)
            return m
        end
    end

    -- No invoice: an auction that expired or was cancelled, which the subject says and the item proves.
    -- Nothing comes back but the item, so there is no price and the deposit is not returned either.
    local name, reason = returnedName(subject, prefixes)
    if not name then return nil end
    local itemID, count = attachment(index)
    m.outcome, m.name, m.count, m.price, m.deposit, m.cut = "returned", name, count, 0, 0, 0
    m.itemID, m.reason = itemID, reason
    return m
end

---------------------------------------------------------------------------------------------------
-- Readings: one row per mail per session (review M11; 0.11.0 D16 before it)
---------------------------------------------------------------------------------------------------
--
-- This API gives a mail no id, and daysLeft is not a live countdown: it is a snapshot from the client's last
-- fetch of the inbox, so `now + daysLeft` drifts by however old that snapshot is (the owner opened the mailbox
-- twice about 4 minutes apart and every sale became two rows exactly 240 s apart). So the session keeps, per
-- mail IDENTITY (outcome, name, count, price and a return's reason), the mails it has read: each one's expiry
-- minute (its row's key), every exact daysLeft value it has been read with (the key into it is the NUMBER,
-- never tostring, which rounds to 14 digits), and how many mails of one reading it stands for (`n`, the row's
-- `times`: identical mails in one walk - a buyer who took two identical stacks at once - share one row).
--
-- Each walk of the inbox settles every mail it reads, in this order, against the mails already known:
--   1. THE SAME READING: its exact daysLeft is one a known mail was read with - the same snapshot read again,
--      however long ago (D16). Its expiry is reused.
--   2. THE SAME MAIL, COUNTED DOWN: a fresh snapshot whose expiry minute is within the server's own one-bucket
--      match (Logic.SALE_MATCH_TOLERANCE) of a known mail with room for it - an honest countdown, give or take
--      the second of jitter that crosses a minute boundary. Its first expiry is kept: one mail, one row.
--   3. A STALE READING SUPERSEDED: what is left, against known mails nothing in this walk has claimed, whose
--      expiry is more than that match and at most a day (Logic.SNAPSHOT_FOLD_WINDOW) LATER - the earlier
--      reading's daysLeft moved more than the clock did, so it was stale by the difference. The fresher reading
--      wins: the stale one stands for that many fewer mails, and its row goes when it stands for none. A mail
--      the fresh reading no longer shows (taken in between, M44) keeps its early row.
--   4. NEW: anything still left is a mail not seen before - filed at its own minute, or, when a known mail of
--      the same identity already sits on that minute, counted into it (multiplicity).
-- Steps 1 and 2 come first, one known mail to as many readings as it stands for, so that a DIFFERENT mail of
-- the same item with more time left - which looks exactly like a stale reading of this one - is claimed by its
-- own reading before step 3 could ever take it. A row's `n` only grows by what one walk sees at once; a later
-- walk seeing fewer (one collected) never lowers it - only step 3 does.
--
-- Session state only: a reload starts it empty, and it picks up the rows CrucibleDB.sales still holds (the
-- first session after an install, when saved data does read back), each as one known mail. When saved data
-- did not come back, neither did the rows: the next document carries a re-read with the same raw daysLeft, and
-- the server folds those by it (src/server/ingest/reference.ts, D16).
local readings = {}
local picked = false

local function identityOf(m)
    return m.outcome .. "\n" .. m.name .. "\n" .. string.format("%d", m.count) .. "\n"
        .. string.format("%d", m.price) .. "\n" .. (m.reason or "")
end

local function knownFor(identity)
    local list = readings[identity]
    if not list then
        list = {}
        readings[identity] = list
    end
    return list
end

local function whole(v, min)
    return type(v) == "number" and v == v and v >= min and v % 1 == 0
end

-- The rows this session's saved data already holds, each as one known mail (see above).
local function pickUp(db)
    if picked then return end
    picked = true
    if type(db.sales) ~= "table" then return end
    for _, row in pairs(db.sales) do
        if type(row) == "table" and whole(row[1], 0) and type(row[2]) == "string" and whole(row[3], 1)
            and whole(row[4], 0) and (row[7] == "sold" or row[7] == "returned") and isWholeSecond(row[8]) then
            local extras = Logic.saleExtras(row) or {}
            local m = { outcome = row[7], name = row[2], count = row[3], price = row[4], itemID = row[1],
                deposit = whole(row[5], 0) and row[5] or 0, cut = whole(row[6], 0) and row[6] or 0,
                reason = row[7] == "returned" and type(extras.reason) == "string" and extras.reason or nil }
            local known = { expiresAt = row[8], days = {}, n = whole(extras.times, 1) and extras.times or 1, mail = m }
            if type(row[9]) == "number" then
                known.days[row[9]] = true
                known.firstDays = row[9]
            end
            local list = knownFor(identityOf(m))
            list[#list + 1] = known
        end
    end
end

local function keyOf(known)
    local m = known.mail
    return Logic.saleKey(m.name, m.count, m.price, known.expiresAt, m.outcome, m.reason)
end

-- The row a known mail stands for, written as it now is. -> 1 when that changed the saved data, else 0.
local function write(db, known)
    local m = known.mail
    local times = math.min(known.n, Logic.MAX_SALE_TIMES)
    if Logic.noteSale(db, m.itemID, m.name, m.count, m.price, m.deposit, m.cut, m.outcome, known.expiresAt,
        known.firstDays, m.reason, times) then
        return 1
    end
    return 0
end

-- Settles one identity's mails from this walk (steps 1-4 above). -> how many changes it made to the saved data
local function settle(db, identity, mails)
    local tolerance, window = Logic.SALE_MATCH_TOLERANCE, Logic.SNAPSHOT_FOLD_WINDOW
    local known = knownFor(identity)
    -- This walk's mails by exact daysLeft: identical numbers are one snapshot's identical mails, a group of `c`.
    local groups, byDays = {}, {}
    for i = 1, #mails do
        local m = mails[i]
        local g = byDays[m.days]
        if g then
            g.c = g.c + 1
        else
            g = { days = m.days, c = 1, mail = m, minute = m.minute, raw = m.raw }
            byDays[m.days] = g
            groups[#groups + 1] = g
        end
    end
    table.sort(groups, function(a, b) return a.raw < b.raw end)

    local claimed, touched = {}, {}
    local function claim(k, g)
        if claimed[k] == nil then
            claimed[k] = 0
            touched[#touched + 1] = k
        end
        claimed[k] = claimed[k] + g.c
        k.days[g.days] = true
    end

    -- 1. the same reading
    local pending = {}
    for i = 1, #groups do
        local g, found = groups[i], nil
        for j = 1, #known do
            if not known[j].gone and known[j].days[g.days] then
                found = known[j]
                break
            end
        end
        if found then claim(found, g) else pending[#pending + 1] = g end
    end

    -- 2. the same mail, counted down
    local left = {}
    for i = 1, #pending do
        local g, best, bestGap = pending[i], nil, nil
        for j = 1, #known do
            local k = known[j]
            local gap = math.abs(k.expiresAt - g.minute)
            if not k.gone and gap <= tolerance and (claimed[k] or 0) + g.c <= k.n
                and (best == nil or gap < bestGap) then
                best, bestGap = k, gap
            end
        end
        if best then claim(best, g) else left[#left + 1] = g end
    end

    -- 3. a stale reading superseded
    local changed, lowered = 0, {}
    for i = 1, #left do
        local g, best, bestAhead = left[i], nil, nil
        for j = 1, #known do
            local k = known[j]
            local ahead = k.expiresAt - g.minute
            if not k.gone and claimed[k] == nil and ahead > tolerance and ahead <= window
                and (best == nil or ahead < bestAhead) then
                best, bestAhead = k, ahead
            end
        end
        if best then
            best.n = best.n - g.c
            if best.n <= 0 then
                best.gone = true
                if Logic.dropSale(db, keyOf(best)) then changed = changed + 1 end
            else
                lowered[#lowered + 1] = best
            end
        end
    end

    -- 4. new: at its own minute, or counted into the known mail already on it
    for i = 1, #left do
        local g, at = left[i], nil
        for j = 1, #known do
            if not known[j].gone and known[j].expiresAt == g.minute then
                at = known[j]
                break
            end
        end
        if not at then
            at = { expiresAt = g.minute, days = {}, n = 0, mail = g.mail, firstDays = g.days }
            known[#known + 1] = at
        end
        claim(at, g)
    end

    for i = 1, #touched do
        local k = touched[i]
        if claimed[k] > k.n then k.n = claimed[k] end
        changed = changed + write(db, k)
    end
    for i = 1, #lowered do
        if claimed[lowered[i]] == nil and not lowered[i].gone then changed = changed + write(db, lowered[i]) end
    end
    return changed
end

-- Walks the open inbox once. Called on MAIL_SHOW, and on each MAIL_INBOX_UPDATE while it is still open -
-- both of which the player caused. A mail already noted changes nothing (the readings above), so a second
-- walk of the same inbox records nothing and costs a few table lookups per mail. -> how many changes it made
function Mail.readInbox()
    if not ns.mailOpen then return 0 end
    -- M72: the member switched "Record my sales" off on Your PCs; their own Data.lua says so. Nothing is read.
    if not Logic.capturesSales(ns.baked) then return 0 end
    local db = ns.db()
    pickUp(db)
    local now = ns.serverTime()
    local prefixes = returnPrefixes()
    local order, byIdentity = {}, {}
    for index = 1, inboxCount() do
        local m = readMail(index, now, prefixes)
        if m then
            local identity = identityOf(m)
            local mails = byIdentity[identity]
            if not mails then
                mails = {}
                byIdentity[identity] = mails
                order[#order + 1] = identity
            end
            mails[#mails + 1] = m
        end
    end
    local changed = 0
    for i = 1, #order do changed = changed + settle(db, order[i], byIdentity[order[i]]) end
    if changed > 0 then
        ns.pendingUpload = true -- something is now waiting on a Sync / /crucible reload to go out
        ns.changed()
    end
    return changed
end

---------------------------------------------------------------------------------------------------
-- 0.11.0 (D15): Sync on the mail window
---------------------------------------------------------------------------------------------------

-- The game's mail window, or nil on a client that has none.
local function mailWindow()
    if type(MailFrame) == "table" then return MailFrame end
    return nil
end

-- Runs a widget script under pcall: a failure in here must never reach the game's own window.
local function guarded(fn)
    return function(...)
        local ok, err = pcall(fn, ...)
        if not ok then ns.fail("mail", err) end
    end
end

-- The hint beside the button: "press Sync to upload" while something is waiting, nothing otherwise.
local function paint()
    if not Mail.syncHint then return end
    Mail.syncHint:SetText(ns.pendingUpload and "press Sync to upload" or "")
end

-- Built once, the first time the mailbox opens on a client that has a mail window to hang it off - the
-- same game-look-or-plain fallback as the strip's buttons. A client on which it cannot be built is
-- remembered and never tried twice; /crucible reload is the way in either way.
local cannotBuild = false
local function build(parent)
    local ok, button = pcall(CreateFrame, "Button", nil, parent, "UIPanelButtonTemplate")
    if not ok then
        button = CreateFrame("Button", nil, parent)
        button.label = button:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
        button.label:SetJustifyH("CENTER")
        button.label:SetAllPoints()
    end
    button:SetSize(60, 22)
    -- Outside the window's right edge, like the strip beside the auction house: it covers nothing of the game's.
    button:SetPoint("TOPLEFT", parent, "TOPRIGHT", 2, -28)
    if button.label then button.label:SetText("Sync") else button:SetText("Sync") end
    button:SetScript("OnClick", guarded(function()
        ns.reload()
        paint()
    end))
    local hint = button:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    hint:SetJustifyH("LEFT")
    hint:SetPoint("TOPLEFT", button, "BOTTOMLEFT", 0, -4)
    hint:SetWidth(160)
    -- Section L: the ledger window (Ledger.lua), beside Sync - a child of it, so it shows and hides with it. The
    -- player's own click; a client that cannot build it still gets Sync.
    local ledger
    local okLedger, built = pcall(CreateFrame, "Button", nil, button, "UIPanelButtonTemplate")
    if not okLedger then
        okLedger, built = pcall(CreateFrame, "Button", nil, button)
        if okLedger then
            built.label = built:CreateFontString(nil, "OVERLAY", "GameFontNormalSmall")
            built.label:SetJustifyH("CENTER")
            built.label:SetAllPoints()
        end
    end
    if okLedger then
        ledger = built
        ledger:SetSize(60, 22)
        ledger:SetPoint("TOPLEFT", button, "TOPRIGHT", 2, 0)
        if ledger.label then ledger.label:SetText("Ledger") else ledger:SetText("Ledger") end
        ledger:SetScript("OnClick", guarded(function()
            if ns.Ledger then ns.Ledger.toggle() end
        end))
    end
    return button, hint, ledger
end

local function attach()
    if Mail.syncButton or cannotBuild or not mailWindow() then return end
    local ok, button, hint, ledger = pcall(build, mailWindow())
    if not ok then
        cannotBuild = true
        return
    end
    Mail.syncButton, Mail.syncHint, Mail.ledgerButton = button, hint, ledger
end

---------------------------------------------------------------------------------------------------
-- B-M (spec 2026-10-04, section M): auto-take on a single open
---------------------------------------------------------------------------------------------------
--
-- "Take gold and items when I open a mail" - one switch in the auction house strip, OFF by default. With it on,
-- a click that opens ONE mail in the game's inbox makes ONE call, AutoLootMailItem(index) - the game's own take of
-- a whole mail, exactly what its shift-click on that row does - in that same click's event chain. The client
-- sequences the money and the attachments itself, so nothing is lost to a command still in flight (fix round 1:
-- the owner's ruling, over a sequence of our own money and slot takes). The hook is a post-hook on the game's own
-- InboxFrame_OnClick, which is what the inbox row's OnClick calls (Blizzard_MailFrame, branch forever).
--   * One mail, the clicked one: the index the click handed the game, and only once the game has made it the
--     open mail (InboxFrame.openMailID) - a click that closes a mail takes nothing.
--   * Never another mail, never a loop over the inbox, never on MAIL_INBOX_UPDATE, never on a timer. The
--     game's Open All (OpenAllMailMixin) takes mail without InboxFrame_OnClick, so it never reaches the hook;
--     a click made while it is still working (its button disabled) is left to it as well.
--   * The game's own shift-click (IsModifiedClick("MAILAUTOLOOTTOGGLE")) already took the mail before the
--     hook runs: nothing is added to it. A mail command still in flight (C_Mail.IsCommandPending): nothing
--     is taken, and one line says so.
--   * C.O.D. mail is never taken from - taking it is paying for it, and we never pay. A GM's mail is left for
--     the player, as the game's own Open All leaves it.
--   * A mail whose attachments cannot all fit in the free bag slots (the same
--     C_Container.CalculateTotalNumberOfFreeBagSlots the game's Open All asks) is not taken at all, and one line
--     says so. Money needs no bag space; a letter with neither money nor an attachment is only read.
-- The reader above is untouched: the walk at MAIL_SHOW / MAIL_INBOX_UPDATE saw this mail before any click
-- could reach it, and a mail its next reading no longer shows keeps its row (review M44).

-- How many attachment slots a received mail has: the game's own constant where it has one (16 on this client).
local function attachmentSlots()
    local n = ATTACHMENTS_MAX_RECEIVE
    if ns.isSecret(n) or type(n) ~= "number" or n ~= n or n < 1 or n > 64 then return 16 end
    return math.floor(n)
end

-- True while the game's Open All is working through the inbox: it disables its own button for exactly that long.
local function openingAll()
    local button = type(InboxFrame) == "table" and InboxFrame.OpenAllMail or nil
    if type(button) ~= "table" or type(button.IsEnabled) ~= "function" then return false end
    local ok, enabled = pcall(button.IsEnabled, button)
    return ok and enabled == false
end

-- True when the game says a mail command is still in flight; false when it says not, or cannot say.
local function commandPending()
    if type(C_Mail) ~= "table" or type(C_Mail.IsCommandPending) ~= "function" then return false end
    local ok, pending = pcall(C_Mail.IsCommandPending)
    return ok and pending == true
end

-- Free bag slots, or nil when the client cannot say (then the game's own "inventory is full" is the limit).
local function freeBagSlots()
    if type(C_Container) ~= "table" or type(C_Container.CalculateTotalNumberOfFreeBagSlots) ~= "function" then
        return nil
    end
    local ok, n = pcall(C_Container.CalculateTotalNumberOfFreeBagSlots)
    if not ok or ns.isSecret(n) or type(n) ~= "number" or n ~= n then return nil end
    return n
end

-- How many attachments the mail at `index` carries: the slots GetInboxItem reports an item in.
local function attachmentCount(index)
    local count = 0
    for slot = 1, attachmentSlots() do
        local got, name = pcall(GetInboxItem, index, slot)
        if got and (ns.isSecret(name) or name ~= nil) then count = count + 1 end
    end
    return count
end

-- The one function in the addon that takes anything out of the mailbox (addon/tests/syntax.test.ts holds that,
-- and that the hook below is its only caller). `index` is the mail the player's click just opened.
local function takeOpened(index)
    if not ns.mailOpen or ns.settings().autoTake ~= true then return end
    if type(index) ~= "number" or ns.isSecret(index) then return end
    if type(InboxFrame) ~= "table" or InboxFrame.openMailID ~= index then return end
    if type(IsModifiedClick) == "function" and IsModifiedClick("MAILAUTOLOOTTOGGLE") then return end
    if openingAll() then return end
    if type(AutoLootMailItem) ~= "function" then return end
    if type(GetInboxHeaderInfo) ~= "function" or type(GetInboxItem) ~= "function" then return end
    if commandPending() then
        ns.print("the mailbox is busy - nothing taken from that mail; open it again in a moment")
        return
    end

    -- The header's money, C.O.D. charge and GM flag; a mail whose header cannot be read for certain is left alone.
    local ok, _, _, _, _, money, cod, _, _, _, _, _, _, isGM = pcall(GetInboxHeaderInfo, index)
    if not ok or ns.isSecret(money) or ns.isSecret(cod) or ns.isSecret(isGM) then return end
    if type(cod) ~= "number" or cod ~= cod or cod > 0 then return end
    if isGM == true then return end

    local items = attachmentCount(index)
    local hasMoney = type(money) == "number" and money > 0
    if items == 0 and not hasMoney then return end -- a letter: nothing to take
    local free = freeBagSlots()
    if free ~= nil and items > free then
        ns.print("not enough bag space for that mail's items - nothing taken")
        return
    end
    pcall(AutoLootMailItem, index)
end

-- Installed once, on the first MAIL_SHOW of a client that has the game's inbox: by then Blizzard_MailFrame is
-- loaded. A post-hook runs after the game has opened (or closed) the mail, in the same click.
local hooked = false
local function hookInbox()
    if hooked or type(hooksecurefunc) ~= "function" or type(InboxFrame_OnClick) ~= "function" then return end
    hooked = true
    pcall(hooksecurefunc, "InboxFrame_OnClick", function(_, index)
        local ok, err = pcall(takeOpened, index)
        if not ok then ns.fail("mail", err) end
    end)
end

ns.mailOpen = false

ns.on("MAIL_SHOW", function()
    ns.mailOpen = true
    Mail.readInbox()
    attach()
    hookInbox()
    if Mail.syncButton then Mail.syncButton:Show() end
    paint()
end)

-- The inbox refreshes as pages load and as the player takes things out of it. Read again only while the
-- mailbox is still open: after MAIL_CLOSED this event does nothing at all.
ns.on("MAIL_INBOX_UPDATE", function()
    if not ns.mailOpen then return end
    Mail.readInbox()
    paint()
end)

ns.on("MAIL_CLOSED", function()
    ns.mailOpen = false
    if Mail.syncButton then Mail.syncButton:Hide() end
end)

ns.onChange(paint)
