-- The first-run guide (spec §5.6): three cards, shown once, available again from /canvas wizard.
local ADDON, ns = ...
local Wizard = {}
ns.Wizard = Wizard

local CARDS = {
    { title = "What goes where", text = "Crucible Canvas keeps the 3D world and your action bars, unit frames and chat on your Mainhand screen. Your other screen becomes the canvas: the map, your bags, the character sheet and other panels open there and stay open while you play." },
    { title = "Spanning the window", text = "The Crucible tray app does the spanning. Open the Displays window (Displays... in the tray menu or in Settings), tick your two screens, choose the Mainhand one and press Span WoW now. If the game was already open, type /reload once so the addon sees the new window. Then type /canvas wizard to finish this guide." },
    { title = "Placing panels", text = "Drag any panel on the canvas by its title to put it where you like; it comes back there next time. /canvas opens the options (themes, which panels dock, reset). /canvas gather pulls every open panel back to the middle if one gets lost." },
}

local frame

function Wizard.frame()
    if frame then return frame end
    frame = CreateFrame("Frame", "CrucibleCanvasWizard", UIParent)
    frame:SetSize(460, 220)
    frame:SetFrameStrata("DIALOG")
    frame:EnableMouse(true)
    frame.bg = frame:CreateTexture(nil, "BACKGROUND")
    frame.bg:SetAllPoints(frame)
    frame.bg:SetColorTexture(0.078, 0.071, 0.067, 0.96)
    frame.edge = frame:CreateTexture(nil, "BORDER")
    frame.edge:SetPoint("TOPLEFT", frame, "TOPLEFT", 0, 0)
    frame.edge:SetPoint("BOTTOMLEFT", frame, "BOTTOMLEFT", 0, 0)
    frame.edge:SetWidth(2)
    frame.edge:SetColorTexture(0.886, 0.690, 0.290, 1)
    frame.title = frame:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
    frame.title:SetPoint("TOPLEFT", frame, "TOPLEFT", 18, -14)
    frame.text = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlight")
    frame.text:SetPoint("TOPLEFT", frame, "TOPLEFT", 18, -44)
    frame.text:SetWidth(424)
    frame.text:SetJustifyH("LEFT")
    frame.step = frame:CreateFontString(nil, "OVERLAY", "GameFontHighlightSmall")
    frame.step:SetPoint("BOTTOMLEFT", frame, "BOTTOMLEFT", 18, 16)
    frame.next = CreateFrame("Button", nil, frame, "UIPanelButtonTemplate")
    frame.next:SetSize(90, 24)
    frame.next:SetPoint("BOTTOMRIGHT", frame, "BOTTOMRIGHT", -14, 12)
    frame.next:SetScript("OnClick", ns.guard("wizard next", function() Wizard.advance() end))
    frame.card = 1
    frame:Hide()
    table.insert(UISpecialFrames, "CrucibleCanvasWizard")
    return frame
end

local function render()
    local f = Wizard.frame()
    local c = CARDS[f.card]
    f.title:SetText(c.title)
    f.text:SetText(c.text)
    f.step:SetText(string.format("Crucible Canvas guide, %d of %d", f.card, #CARDS))
    f.next:SetText(f.card == #CARDS and "Done" or "Next")
end

function Wizard.advance()
    local f = Wizard.frame()
    if f.card >= #CARDS then
        f:Hide()
        if not ns.state.options.wizardDone then
            ns.state.options.wizardDone = true
            ns.markChanged()
        end
        return
    end
    f.card = f.card + 1
    render()
end

function Wizard.show()
    local f = Wizard.frame()
    f.card = 1
    f:ClearAllPoints()
    if ns.Canvas.rect() then f:SetPoint("CENTER", ns.Canvas.frame(), "CENTER", 0, 60) else f:SetPoint("CENTER", UIParent, "CENTER", 0, 60) end
    render()
    f:Show()
end

-- The automatic show counts as seen at once: card 2 asks for a /reload, and the stashed flag
-- keeps that reload from starting the guide over at card 1. The flag is set after show() returns,
-- so a show that throws does not mark the guide seen.
local shownThisSession = false
function Wizard.maybeShow()
    if shownThisSession or ns.state.options.wizardDone then return end
    shownThisSession = true
    Wizard.show()
    ns.state.options.wizardDone = true
    ns.markChanged()
end

ns.onEvent("PLAYER_ENTERING_WORLD", function() Wizard.maybeShow() end)
ns.commands.wizard = function() Wizard.show() end
