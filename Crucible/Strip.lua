-- Crucible: the auction house strip - Full scan / Quick scan / Stop / Sync, an auto-scan switch and one status line
-- beside the game's auction house window - and the one browse scan that may start itself when the house
-- opens (docs/decisions.md C7, revised by the owner 2026-09-23).
--
-- Display, and one decision that is not taken here: whether that scan may run is Logic.autoScanDecision,
-- which is pure and tested on its own. This file gathers the six fields it takes and obeys what it says.
-- The six limits C7 keeps, and the line here that keeps each:
--   1. The trigger to start is AUCTION_HOUSE_SHOW; if the client is busy at that instant the one query
--      goes out on its first ready signal, once (owner's amendment 2026-09-23). The handler at the foot of
--      this file is the only caller of startAutoScan(), and there is no timer, no repeat and no retry
--      anywhere in it. The wait is Scan.lua's (run.waitedOnce, its AUCTION_HOUSE_THROTTLED_SYSTEM_READY
--      handler): one attempt on the client's FIRST ready signal, and the scan is over either way - the
--      amendment was the owner's answer to the live finding that Blizzard's own window spends the query
--      allowance the instant the house opens, so the pre-check this file used to make refused every time.
--      Note what none of this claims: a browse scan is paged, so pages 2..N of a scan that HAS started
--      are asked for on later events (Scan.lua's browseMore, from the results and throttle handlers),
--      exactly as they are for a /crucible browse. That is the browse scan limit 4 names, and it is bounded
--      by that one scan's own paging - it starts nothing new.
--   2. One scan per opening: opening.scanned, set before the scan is asked for and cleared by
--      AUCTION_HOUSE_CLOSED (see the one exception at startAutoScan, which is a refusal, not a retry).
--   3. Nothing while the newest known scan - this session's own, or the pooled one the data file carries
--      back into the game - is younger than Logic.AUTO_SCAN_MIN_AGE: newestScanAt() feeds the decision.
--   4. The browse scan only: startAutoScan() calls ns.Scan.browse() and nothing else. The full scan stays
--      behind its own button and /crucible scan. The whole footprint of this scan is that ONE browse query
--      and its pages: it is marked `auto`, and Scan.lua's finishBrowse keeps the variant learner - up to
--      eight item searches - for the scans somebody pressed a button for.
--   5. Visible: paint() writes "waiting for the house to accept a query" while it waits and "scanning ..."
--      once the query is out, and shows the Stop button for as long as either lasts.
--   6. No second wait, no retry. The one wait of limit 1 is the whole allowance: a client that still will
--      not take the query at its own ready signal ends the scan for this opening - not on a later ready
--      signal, not on a second AUCTION_HOUSE_SHOW, not ever until the house has been closed and opened
--      again. Scan.lua enforces it (run.waitedOnce is raised before the send, so the handler cannot reach
--      the same run twice, and a run that did not send is ENDED there rather than left queued); this file
--      only records what it meant for the opening - opening.refused, on which the decision answers
--      "skip-cooldown". A /crucible browse still waits as long as the player likes: they asked for it.
-- The switch is ns.settings().autoScan, remembered by ns.setSetting like every other choice.
--
-- Nothing here posts, bids, buys or cancels, and nothing here asks the auction house for anything: the
-- buttons call ns.Scan, which is the one file that sends a request. Every widget is built under pcall,
-- and a client that has no auction house window gets no strip and no scan of its own accord - the
-- commands still work, and nothing the player cannot see ever starts.
--
-- Sync (0.9.3, called Send until 0.11.0) is a click; it and the other Sync buttons (the profession window,
-- the mail window) are the only things besides /crucible reload that reload the UI, and nothing reloads it
-- without one. 0.11.0 took the Tally 2.0 words: Full scan was Scan, Quick scan was Browse, Sync was Send.
--
-- B-M (spec 2026-10-04, section M): a second switch on its own row under auto-scan, "Take gold and items when I
-- open a mail", off by default and kept in ns.settings().autoTake. It has nothing to do with the auction house
-- beyond living in the same strip: a click on it writes the choice down and does nothing else, and Mail.lua reads
-- it when the player clicks a mail open.
--
-- B-P (spec 2026-10-04, section P): the posting row under it. "Undercut by 1c" - the rule the Sell tab's suggestion
-- follows; a click steps it (1c, 1% .. 10%) and writes it down, nothing more - and "Undercut?", shown only while the
-- game's Auctions tab is open: the cancel scan's one click, which Sell.lua carries out. Below notice v5 neither shows;
-- the row says "Posting help needs the new notice - open the website" instead. This file still posts nothing, cancels
-- nothing and asks the house for nothing.

local _, ns = ...
local Logic = ns.Logic

local Strip = {}
ns.Strip = Strip

local BUTTON_W, BUTTON_H = 50, 22
local SCAN_W = 76 -- room for "Quick scan" and "Full scan" (0.11.0)
-- four buttons wide since 0.9.3 (Full scan, Quick scan, Stop, Sync); B-M: a row taller; B-P: the posting row, taller again
local WIDTH, HEIGHT = 258, 126
local SWITCH_ROW = 24 -- one switch's row: a 24-high tick box
local POSTING_ROW = BUTTON_H + 4 -- B-P: "Undercut by 1c" and "Undercut?", or the notice line in their place
local TAKE_TEXT = "Take gold and items when I open a mail"
local NOTICE_TEXT = "Posting help needs the new notice - open the website"

-- What is true of THIS opening of the auction house. Either one is enough to stop a second scan, and only
-- AUCTION_HOUSE_CLOSED clears them - with one exception, in the two places marked "refused" below: a scan
-- that was asked for but whose query never went out puts `scanned` back down and `refused` up in the same
-- breath, so what the player is told is "the house is busy" rather than "already done". Both halves matter:
-- decide() weighs `scanned` BEFORE `refused`, so leaving `scanned` up would answer "skip-done" and the
-- status line would say nothing about a busy house. The opening is no less closed to a second scan for it -
-- both answers skip.
local opening = { scanned = false, refused = false }

local strip, cannotBuild

-- The game's auction house window, or nil on a client that has none.
local function window()
    if type(AuctionHouseFrame) == "table" then return AuctionHouseFrame end
    return nil
end

-- Runs a widget script under pcall: a failure in here must never reach the game's own window.
local function guarded(fn)
    return function(...)
        local ok, err = pcall(fn, ...)
        if not ok then ns.fail("strip", err) end
    end
end

local function setText(widget, text)
    if widget.label then widget.label:SetText(text) else widget:SetText(text) end
end

local function newLabel(parent, font, justify)
    local text = parent:CreateFontString(nil, "OVERLAY", font)
    text:SetJustifyH(justify or "LEFT")
    return text
end

-- A button with the game's own look where the client has the template, and a plain one carrying its own
-- text where it has not - the same fallback the Profit button uses, so no template is ever required.
local function newButton(parent, width, text, onClick)
    local ok, button = pcall(CreateFrame, "Button", nil, parent, "UIPanelButtonTemplate")
    if not ok then
        ok, button = pcall(CreateFrame, "Button", nil, parent)
        if not ok then return nil end
        button.label = newLabel(button, "GameFontNormalSmall", "CENTER")
        button.label:SetAllPoints()
    end
    button:SetSize(width, BUTTON_H)
    button:SetScript("OnClick", guarded(onClick))
    setText(button, text)
    return button
end

-- A plain hover tooltip, the same GameTooltip:SetText/Show/Hide a plain-text tooltip anywhere else in the
-- game uses. Guarded like everything else: a client missing any one piece of it just shows no tooltip.
local function withTooltip(widget, text)
    widget:SetScript("OnEnter", guarded(function(self)
        if type(GameTooltip) ~= "table" or type(GameTooltip.SetText) ~= "function"
            or type(GameTooltip_SetDefaultAnchor) ~= "function" then return end
        GameTooltip_SetDefaultAnchor(GameTooltip, self)
        GameTooltip:SetText(text)
        GameTooltip:Show()
    end))
    widget:SetScript("OnLeave", guarded(function()
        if type(GameTooltip) == "table" and type(GameTooltip.Hide) == "function" then GameTooltip:Hide() end
    end))
end

---------------------------------------------------------------------------------------------------
-- The decision (C7 as revised): gathered here, taken in Logic.autoScanDecision, obeyed below
---------------------------------------------------------------------------------------------------

-- The switch, as the player last left it: saved beats baked beats the default (which is on).
local function autoScanOn()
    return ns.settings().autoScan ~= false
end

-- B-M: the mail switch. Off unless the player turned it on (Logic.settings: the saved layer only).
local function autoTakeOn()
    return ns.settings().autoTake == true
end

-- Limit 3: the newest scan anybody knows of - this session's own (Scan.status) or the time of the prices
-- baked into the data file, whichever is later. 0 when neither is known, which reads as "old enough".
local function newestScanAt(status)
    local own = status.lastScanAt
    if type(own) ~= "number" or own ~= own or own < 0 then own = 0 end
    local pooled = type(ns.baked) == "table" and ns.baked.pricesAt or 0
    if type(pooled) ~= "number" or pooled ~= pooled or pooled < 0 then pooled = 0 end
    if own > pooled then return own end
    return pooled
end

local function decide(status)
    return Logic.autoScanDecision({
        enabled = autoScanOn(),
        ahOpen = ns.ahOpen and true or false,
        scannedThisOpening = opening.scanned,
        newestScanAt = newestScanAt(status),
        now = ns.serverTime(),
        cooldownRefused = opening.refused,
    })
end

---------------------------------------------------------------------------------------------------
-- The strip
---------------------------------------------------------------------------------------------------

-- Limit 5: one line that always says what is happening. A scan in progress wins over everything else;
-- otherwise the player is told why no scan started, or how old the prices they are looking at are - with
-- one suffix (0.9.3), appended to whatever the line would otherwise say: " - press Sync to upload" while
-- ns.pendingUpload is true (a scan or a newly learned recipe this session, nothing sent since). Not while
-- a scan is running: there is nothing yet to send from THIS scan, and the busy line matters more.
local function statusText(status)
    if status.busy then
        -- A browse run whose query the client has not taken yet: the single wait of limit 1, and the wait a
        -- clicked Quick scan has always been allowed. Either way there is something running to see and to stop.
        if status.waiting then return "waiting for the house to accept a query" end
        if status.kind == "browse" and type(status.pages) == "number" and status.pages > 0 then
            return string.format("scanning ... page %.0f", status.pages)
        end
        return "scanning ..."
    end
    local text
    local reason = decide(status)
    if reason == "skip-off" then
        text = "auto-scan is off"
    elseif reason == "skip-cooldown" then
        text = "the house is busy - try the button in a moment"
    else
        local newest = newestScanAt(status)
        if newest <= 0 then
            text = "no prices yet - press Quick scan"
        else
            text = "last scan " .. Logic.formatAge(ns.serverTime() - newest) .. " ago"
        end
    end
    if ns.pendingUpload then text = text .. " - press Sync to upload" end
    return text
end

-- A switch shows its state: the tick where it is a tick box, "[x] text" / "[ ] text" where it is a plain button.
local function paintSwitch(box, isCheckBox, on, text)
    if isCheckBox then
        box:SetChecked(on)
    else
        setText(box, (on and "[x] " or "[ ] ") .. text)
    end
end

-- B-P: the undercut rule as the button names it - "Undercut by 1c" or "Undercut by 3%".
local function undercutText()
    local pct = ns.undercutPct()
    if pct == 0 then return "Undercut by 1c" end
    return "Undercut by " .. string.format("%.0f", pct) .. "%"
end

-- B-P: the posting row. Below notice v5 nothing of the posting help shows - only the line that says why. From v5 the
-- undercut rule shows, and Undercut? only while the game's Auctions tab is open (Sell.lua says when it is).
local function paintPosting()
    local sell = ns.Sell
    local allowed = sell ~= nil and sell.allowed()
    if strip.noticeLine then strip.noticeLine:SetShown(not allowed) end
    if strip.undercutRule then
        strip.undercutRule:SetShown(allowed)
        setText(strip.undercutRule, undercutText())
    end
    if strip.undercutButton then strip.undercutButton:SetShown(allowed and sell.auctionsTabOpen()) end
end

local function paint()
    if not strip then return end
    local status = ns.Scan.status()
    paintSwitch(strip.autoBox, strip.autoIsCheckBox, autoScanOn(), "auto-scan")
    paintSwitch(strip.autoTakeBox, strip.autoTakeIsCheckBox, autoTakeOn(), TAKE_TEXT)
    strip.stopButton:SetShown(status.busy)
    strip.status:SetText(statusText(status))
    paintPosting()
end

-- B-P: Sell.lua repaints the strip when the game's tab changes (Undercut? follows the Auctions tab). Repaints only.
function Strip.repaint()
    paint()
end

-- Starts nothing by itself: it writes the choice down and repaints, and the next opening of the house
-- reads it (limit 1 - a click on the switch is not an AUCTION_HOUSE_SHOW).
local function toggleAuto()
    ns.setSetting("autoScan", not autoScanOn())
    paint()
end

-- B-M: the same for the mail switch - written down, nothing started. The mailbox reads it on a click.
local function toggleAutoTake()
    ns.setSetting("autoTake", not autoTakeOn())
    paint()
end

-- B-P: the undercut rule steps 1c -> 1% -> 2% ... -> 10% -> 1c. Written down, nothing started: the Sell tab reads it the
-- next time the player picks an item.
local function stepUndercut()
    local pct = ns.undercutPct() + 1
    if pct > ns.UNDERCUT_MAX_PCT then pct = 0 end
    ns.setSetting("undercutPct", pct)
    paint()
end

-- A switch: a real tick box where the client has the template, with its label beside it, else a plain button
-- carrying "[x] text" - the same fallback as the buttons. -> the switch, whether it is a tick box, its label
local function newSwitch(parent, text, fallbackWidth, onClick)
    local ok, box = pcall(CreateFrame, "CheckButton", nil, parent, "UICheckButtonTemplate")
    if ok then
        box:SetSize(24, 24)
        box:SetScript("OnClick", guarded(onClick))
        local label = newLabel(parent, "GameFontHighlightSmall", "LEFT")
        label:SetText(text)
        label:SetPoint("LEFT", box, "RIGHT", 2, 0)
        return box, true, label
    end
    box = newButton(parent, fallbackWidth, "[x] " .. text, onClick)
    if not box then error("this client could not build the " .. text .. " switch") end
    return box, false, nil
end

local function build(parent)
    local frame = CreateFrame("Frame", nil, parent)
    frame:SetSize(WIDTH, HEIGHT)
    -- Outside the window's right edge, like the Profit panel: it covers nothing of the game's own.
    frame:SetPoint("TOPLEFT", parent, "TOPRIGHT", 2, -28)

    frame.scanButton = newButton(frame, SCAN_W, "Full scan", function()
        ns.Scan.replicate()
        paint()
    end)
    frame.browseButton = newButton(frame, SCAN_W, "Quick scan", function()
        ns.Scan.browse()
        paint()
    end)
    frame.stopButton = newButton(frame, BUTTON_W, "Stop", function()
        ns.Scan.stop()
        paint()
    end)
    -- 0.9.3: the only other click that reloads the UI, through the SAME ns.reload as /crucible reload - see
    -- the file header. Always shown, unlike Stop: sending is never tied to a scan being in progress.
    frame.sendButton = newButton(frame, BUTTON_W, "Sync", function()
        ns.reload()
        paint()
    end)
    if not (frame.scanButton and frame.browseButton and frame.stopButton and frame.sendButton) then
        error("this client could not build a button")
    end
    withTooltip(frame.sendButton, "Sync - reloads the UI")
    frame.scanButton:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, 0)
    frame.browseButton:SetPoint("TOPLEFT", frame.scanButton, "TOPRIGHT", 2, 0)
    frame.stopButton:SetPoint("TOPLEFT", frame.browseButton, "TOPRIGHT", 2, 0)
    frame.sendButton:SetPoint("TOPLEFT", frame.stopButton, "TOPRIGHT", 2, 0)

    -- The switches. A real tick box where the client has the template, else a button reading "[x] auto-scan";
    -- which one it is is remembered rather than guessed from the widget, because only one has a tick.
    frame.autoBox, frame.autoIsCheckBox, frame.autoLabel = newSwitch(frame, "auto-scan", 110, toggleAuto)
    frame.autoBox:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, -BUTTON_H - 4)
    -- B-M: the mail switch, on its own row under auto-scan - its words are too long to share one.
    frame.autoTakeBox, frame.autoTakeIsCheckBox, frame.autoTakeLabel = newSwitch(frame, TAKE_TEXT, WIDTH, toggleAutoTake)
    frame.autoTakeBox:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, -BUTTON_H - 4 - SWITCH_ROW)

    -- Section L: the ledger window (Ledger.lua), the player's own click. Not needed for the strip to work, so a client
    -- that cannot build this one button still gets the rest.
    frame.ledgerButton = newButton(frame, BUTTON_W + 10, "Ledger", function()
        if ns.Ledger then ns.Ledger.toggle() end
    end)
    if frame.ledgerButton then
        frame.ledgerButton:SetPoint("TOPRIGHT", frame, "TOPRIGHT", 0, -BUTTON_H - 4)
        withTooltip(frame.ledgerButton, "Your sales and returns, last 30 days")
    end

    -- B-P: the posting row under the mail switch (spec 2026-10-04 section P). The undercut rule and Undercut? - the
    -- cancel scan's one click, which Sell.lua carries out - or, below notice v5, the line that says why neither is
    -- there. Like the Ledger button, not needed for the strip to work: a client that cannot build them still gets
    -- the rest.
    local postingY = -BUTTON_H - 4 - 2 * SWITCH_ROW - 2
    frame.undercutRule = newButton(frame, 110, "Undercut by 1c", stepUndercut)
    if frame.undercutRule then
        frame.undercutRule:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, postingY)
        withTooltip(frame.undercutRule, "How far under the lowest price the Sell tab's suggestion goes - click to change")
    end
    frame.undercutButton = newButton(frame, 80, "Undercut?", function()
        if ns.Sell then ns.Sell.cancelScan() end
        paint()
    end)
    if frame.undercutButton then
        frame.undercutButton:SetPoint("TOPLEFT", frame, "TOPLEFT", 114, postingY)
        withTooltip(frame.undercutButton, "Checks your auctions against the lowest prices now - one query at a time")
        frame.undercutButton:Hide()
    end
    frame.noticeLine = newLabel(frame, "GameFontDisableSmall", "LEFT")
    frame.noticeLine:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, postingY - 4)
    frame.noticeLine:SetWidth(WIDTH)
    frame.noticeLine:SetText(NOTICE_TEXT)

    frame.status = newLabel(frame, "GameFontHighlightSmall", "LEFT")
    frame.status:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, -BUTTON_H - 32 - SWITCH_ROW - POSTING_ROW)
    frame.status:SetWidth(WIDTH)
    return frame
end

-- Built once, the first time the auction house is opened on a client that has a window to hang it off.
-- Safe to call again: a client on which it cannot be built is remembered and never tried twice, and the
-- /crucible commands are the way in either way.
function Strip.attach()
    if strip or cannotBuild or not window() then return end
    local ok, built = pcall(build, window())
    if not ok then
        cannotBuild = true
        return
    end
    strip = built
    Strip.frame = built
    Strip.scanButton = built.scanButton
    Strip.browseButton = built.browseButton
    Strip.stopButton = built.stopButton
    Strip.sendButton = built.sendButton
    Strip.autoBox = built.autoBox
    Strip.autoTakeBox = built.autoTakeBox
    Strip.ledgerButton = built.ledgerButton
    Strip.undercutRule = built.undercutRule
    Strip.undercutButton = built.undercutButton
    Strip.noticeLine = built.noticeLine
    Strip.status = built.status
end

---------------------------------------------------------------------------------------------------
-- The one scan that starts itself
---------------------------------------------------------------------------------------------------

-- Starts the one scan C7 allows without a click - not to be read as autoScanOn(), which is only the
-- switch this consults. Called from the AUCTION_HOUSE_SHOW handler below and from nowhere else. It asks
-- once and it never repeats: whatever comes of this one attempt - the query now, the query on the client's
-- first ready signal, or nothing at all - stands until the house is closed and opened again. A strip that
-- could not be built means no auto-scan at all: nothing the player cannot see and cannot stop may start on
-- its own.
local function startAutoScan()
    local status = ns.Scan.status()
    if decide(status) ~= "scan" then return end
    opening.scanned = true -- limit 2: written before the scan is asked for, so nothing can ask twice
    -- Limit 4: the browse scan, through the very function the Browse button and /crucible browse call, so it
    -- passes the same checks they do. It answers true when a run of its own making is live - the query
    -- already out, or waiting for the client to take it, which limit 1 as amended allows exactly once and
    -- Scan.lua alone carries out. false is the one case where nothing of ours exists at all: the scan was
    -- refused outright, most often because a full scan the player started is still reading its list (that
    -- outlives the house closing, so an opening can find one going). Nothing to stop and nothing to wait
    -- for, so the opening has had its attempt - refused, not taken.
    if ns.Scan.browse({ auto = true }) ~= true then
        opening.scanned = false -- "refused": see the note on `opening` above - both halves, together
        opening.refused = true
    end
end

ns.on("AUCTION_HOUSE_SHOW", function()
    Strip.attach()
    if not strip then return end
    startAutoScan()
    paint()
end)

-- The single wait of limit 1, once it is over. Scan.lua's own handler for this event ran first (the .toc
-- loads it first), so by now the scan has either sent its query or given up for good; this decides only
-- what that meant for the opening, which is this file's business and not Scan.lua's. Repainting is the
-- rest of it: a wait that ended has to stop showing as a wait. Registering for an event is not owning a
-- clock - nothing here is scheduled, and the player's own client is what decides when this arrives.
ns.on("AUCTION_HOUSE_THROTTLED_SYSTEM_READY", function()
    if strip and ns.Scan.status().autoGaveUp then
        opening.scanned = false -- "refused": see the note on `opening` above - both halves, together
        opening.refused = true
    end
    paint()
end)

ns.on("AUCTION_HOUSE_CLOSED", function()
    opening.scanned = false
    opening.refused = false
    paint()
end)

-- Repaints, and nothing else. Every one of these runs after Scan.lua's own handler for the same event, so
-- what they show is the scan as it stands; none of them decides anything and none of them starts anything.
ns.on("AUCTION_HOUSE_BROWSE_RESULTS_UPDATED", paint)
ns.on("AUCTION_HOUSE_BROWSE_RESULTS_ADDED", paint)
ns.on("AUCTION_HOUSE_BROWSE_FAILURE", paint)
ns.on("REPLICATE_ITEM_LIST_UPDATE", paint)
ns.onChange(paint)
