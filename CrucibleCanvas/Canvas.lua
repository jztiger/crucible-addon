-- The Offhand canvas: one PARENTLESS frame over the workspace rectangle (spike fact 3), the brand theme, a 2 px
-- gold rule on the edge that faces the Mainhand, a small wordmark, and the tooltip edge guard (spec §5.3).
local ADDON, ns = ...
local Geometry = ns.Geometry
local Canvas = {}
ns.Canvas = Canvas

Canvas.THEMES = {
    forge     = { ground = { 0.078, 0.071, 0.067 }, rule = { 0.886, 0.690, 0.290 }, ink = { 0.937, 0.922, 0.914 } },
    ember     = { ground = { 0.090, 0.055, 0.045 }, rule = { 0.886, 0.690, 0.290 }, ink = { 0.937, 0.922, 0.914 } },
    slate     = { ground = { 0.071, 0.078, 0.090 }, rule = { 0.373, 0.722, 0.831 }, ink = { 0.937, 0.922, 0.914 } },
    parchment = { ground = { 0.902, 0.871, 0.816 }, rule = { 0.420, 0.337, 0.157 }, ink = { 0.078, 0.071, 0.067 } },
}

local frame, rect

function Canvas.frame()
    if frame then return frame end
    frame = CreateFrame("Frame", "CrucibleCanvasFrame", nil)
    frame:SetFrameStrata("BACKGROUND")
    frame:EnableMouse(true)                 -- clicks on the bare canvas go nowhere, not to the world beneath
    frame.ground = frame:CreateTexture(nil, "BACKGROUND")
    frame.ground:SetAllPoints(frame)
    frame.rule = frame:CreateTexture(nil, "BORDER")
    frame.mark = frame:CreateFontString(nil, "OVERLAY", "GameFontNormalLarge")
    frame.mark:SetPoint("TOPLEFT", frame, "TOPLEFT", 16, -12)
    frame.mark:SetText("CRUCIBLE")
    frame:Hide()
    return frame
end

function Canvas.rect() return rect end

-- On the canvas: a descendant of the canvas frame (the cheap check), or a frame whose centre lies inside the canvas
-- rect. Docked panels are UIParent children anchored to the canvas, so for them only the position tells.
function Canvas.isOn(f)
    local c = Canvas.frame()
    local g = f
    while g do
        if g == c then return true end
        g = g.GetParent and g:GetParent() or nil
    end
    if not rect or type(f) ~= "table" or not f.GetCenter then return false end
    local cx, cy = f:GetCenter()
    if not cx or not cy then return false end
    local s = f:GetEffectiveScale()
    cx, cy = cx * s, cy * s
    return cx >= rect.left and cx <= rect.right and cy >= rect.bottom and cy <= rect.top
end

local function colour(tex, c) tex:SetColorTexture(c[1], c[2], c[3], 1) end

function Canvas.applyTheme(name)
    local t = Canvas.THEMES[name]
    if not t then return false end
    local f = Canvas.frame()
    colour(f.ground, t.ground)
    colour(f.rule, t.rule)
    f.mark:SetTextColor(t.ink[1], t.ink[2], t.ink[3])
    if ns.state.options.theme ~= name then
        ns.state.options.theme = name
        ns.markChanged()
    end
    return true
end

local function placeRule(side)
    local f = Canvas.frame()
    f.rule:ClearAllPoints()
    if side == "LEFT" or side == "RIGHT" then
        local p = side == "LEFT" and "LEFT" or "RIGHT"
        f.rule:SetPoint("TOP" .. p, f, "TOP" .. p, 0, 0)
        f.rule:SetPoint("BOTTOM" .. p, f, "BOTTOM" .. p, 0, 0)
        f.rule:SetWidth(2)
    else
        local p = side == "TOP" and "TOP" or "BOTTOM"
        f.rule:SetPoint(p .. "LEFT", f, p .. "LEFT", 0, 0)
        f.rule:SetPoint(p .. "RIGHT", f, p .. "RIGHT", 0, 0)
        f.rule:SetHeight(2)
    end
end

local function layout(rects)
    local f = Canvas.frame()
    if not rects then
        rect = nil
        f:Hide()
        return
    end
    rect = rects.workspace
    f:ClearAllPoints()
    for _, a in ipairs(Geometry.anchors(rect, 1)) do f:SetPoint(a[1], nil, a[2], a[3], a[4]) end
    placeRule(Geometry.side(rects.mainhand, rects.workspace))
    local theme = Canvas.THEMES[ns.state.options.theme] and ns.state.options.theme or "forge"
    local t = Canvas.THEMES[theme]
    colour(f.ground, t.ground); colour(f.rule, t.rule); f.mark:SetTextColor(t.ink[1], t.ink[2], t.ink[3])
    f:Show()
end

-- Blizzard places GameTooltip against UIParent's edges, which are now the Mainhand's. When its owner is on the
-- canvas and the tooltip would stick out of the canvas on any edge, pin it to the matching canvas corner, 8 units
-- in. The corner takes the edge (or edges) it overflows; on an axis that fits, the half of the canvas it is in.
local INSET = 8
function Canvas.guardTooltip()
    if not rect then return end
    local owner = GameTooltip:GetOwner()
    if not owner or not Canvas.isOn(owner) then return end
    local s = GameTooltip:GetEffectiveScale()
    local left, right, bottom, top = GameTooltip:GetLeft(), GameTooltip:GetRight(), GameTooltip:GetBottom(), GameTooltip:GetTop()
    if not (left and right and bottom and top) then return end
    left, right, bottom, top = left * s, right * s, bottom * s, top * s
    local h = (right > rect.right and "RIGHT") or (left < rect.left and "LEFT") or nil
    local v = (top > rect.top and "TOP") or (bottom < rect.bottom and "BOTTOM") or nil
    if not h and not v then return end
    h = h or ((left + right) / 2 > (rect.left + rect.right) / 2 and "RIGHT" or "LEFT")
    v = v or ((bottom + top) / 2 > (rect.bottom + rect.top) / 2 and "TOP" or "BOTTOM")
    local corner = v .. h
    GameTooltip:ClearAllPoints()
    GameTooltip:SetPoint(corner, Canvas.frame(), corner, h == "LEFT" and INSET or -INSET, v == "BOTTOM" and INSET or -INSET)
end

-- A tooltip is not at its final size at OnShow: other addons' lines (Crucible's prices, an item-id addon) and the
-- client's deferred layout widen it afterwards (UAT row 6). So the guard runs again whenever a SHOWN tooltip changes
-- size. A hidden one is being rebuilt between SetOwner and Show: Blizzard's anchoring at show stands, and OnShow
-- checks it then. Once pinned, a growth either fits or resolves to the same corner; SetPoint does not resize it.
local function guardResized()
    if GameTooltip:IsShown() then Canvas.guardTooltip() end
end

ns.Viewport.onChange(layout)
GameTooltip:HookScript("OnShow", ns.guard("tooltip guard", Canvas.guardTooltip))
GameTooltip:HookScript("OnSizeChanged", ns.guard("tooltip guard", guardResized))

table.insert(ns.diagExtra, function()
    return { "canvas: " .. (rect and string.format("shown, workspace root %.0f,%.0f-%.0f,%.0f, theme %s", rect.left, rect.bottom, rect.right, rect.top, ns.state.options.theme) or "hidden") }
end)
