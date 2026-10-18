-- Bags on the canvas (spec §5.4.1). Blizzard re-anchors its bag frames in UpdateContainerFrameAnchors every time one
-- opens; hooked (never replaced), the bags are then placed at the remembered canvas spot. Third-party bags: a fixed
-- list of known root frame names, each opt-in and only when that addon is loaded. Nothing is discovered by scanning.
local ADDON, ns = ...
local Geometry, State = ns.Geometry, ns.State
local Bags = {}
ns.Bags = Bags

Bags.THIRD_PARTY = {
    { addon = "Baganator", root = "Baganator_BackpackViewFrame" },
    { addon = "Bagnon", root = "BagnonInventoryFrame1" },
    { addon = "AdiBags", root = "AdiBagsContainer1" },
}

local hooked = false
local third = {}   -- addon -> { frame, movable } once attached; hooks cannot be removed, so this outlives the switch

local function loaded(addon)
    local f = (C_AddOns and C_AddOns.IsAddOnLoaded) or IsAddOnLoaded
    if type(f) ~= "function" then return false end
    local ok, r = pcall(f, addon)
    return ok and r == true
end

function Bags.enabled(addon) return ns.state.options.dock["bags:" .. addon] == true end

-- Blizzard anchors ContainerFrameSettingsManager:GetBagsShown()[1] bottom-right and chains the rest to it, so that is
-- the frame to move: the combined bag when shown, else the first shown bag, else ContainerFrame1.
local function blizzardRoot()
    if ContainerFrameCombinedBags and ContainerFrameCombinedBags:IsShown() then return ContainerFrameCombinedBags end
    local m = ContainerFrameSettingsManager
    if type(m) == "table" and type(m.GetBagsShown) == "function" then
        local ok, shown = pcall(m.GetBagsShown, m)
        local first = ok and type(shown) == "table" and shown[1] or nil
        if type(first) == "table" and first.GetCenter then return first end
    end
    return ContainerFrame1
end

-- Where a bag opens before it is ever dragged: Blizzard's bags low on the right (a chain grows upward from the
-- first, inside the canvas), a third-party root halfway up.
local function placeFrame(f, key, defaultY)
    local r = ns.Canvas.rect()
    if not r or not f then return end
    local p = State.panel(ns.state, ns.charKey(), key) or {}
    local s = f:GetEffectiveScale()
    local w, h = f:GetWidth() * s, f:GetHeight() * s
    local cx, cy = Geometry.fromFraction(r, p.x or 0.85, p.y or defaultY or 0.5)
    cx, cy = Geometry.clamp(r, cx, cy, w, h)
    f:ClearAllPoints()
    -- SetPoint offsets are in the anchored frame's own units: the root distance is divided by its effective scale.
    f:SetPoint("CENTER", ns.Canvas.frame(), "BOTTOMLEFT", (cx - r.left) / s, (cy - r.bottom) / s)
end

local function always() return true end

-- active(): false for a third-party root whose switch is off, so it neither starts a drag nor saves a position.
local function dragStop(f, key, active, defaultY)
    if not active() then return end
    f:StopMovingOrSizing()
    local r = ns.Canvas.rect()
    if not r then return end -- not live: the frame keeps its own layout-cache behaviour
    if f.SetUserPlaced then f:SetUserPlaced(false) end -- our drags stay out of the client's own layout cache
    local cx, cy = f:GetCenter()
    if not cx then return end
    local s = f:GetEffectiveScale()
    cx, cy = Geometry.clamp(r, cx * s, cy * s, f:GetWidth() * s, f:GetHeight() * s)
    local fx, fy = Geometry.toFraction(r, cx, cy)
    State.setPanel(ns.state, ns.charKey(), key, { x = fx, y = fy })
    ns.markChanged()
    placeFrame(f, key, defaultY)
end

-- True when the drag really started.
local function dragStart(f, active)
    if ns.Canvas.rect() and active() and f:IsMovable() then
        f:StartMoving()
        return true
    end
    return false
end

-- Once per frame: hooks cannot be removed, so a third-party root switched off keeps them and active() quiets them.
-- Always HookScript, never SetScript: the frame's own drag script (a third-party bag's, say) runs first, ours after.
-- Guarded: a failure is one printed line, not a Lua error in the middle of the bag's own drag.
-- OnSizeChanged: a bag that grows after it was placed (Blizzard's own layout, a bag addon's sections) is clamped
-- again at its new size from the remembered spot; placeFrame only sets points, so it does not resize (no recursion).
-- A Blizzard bag is re-placed only while it is the first shown bag: once another is first, Blizzard chains it to that
-- one, and a resize must not pull it off the chain. A third-party root is always its own root.
local BLIZZARD_Y = 0.15
local function placesItself(f, key) return key ~= "Bags" or f == blizzardRoot() end
local function makeDraggable(f, key, active, defaultY)
    if f.crucibleCanvasDrag then return end
    f.crucibleCanvasDrag = true
    f:SetMovable(true)
    f:EnableMouse(true)
    f:RegisterForDrag("LeftButton")
    f:HookScript("OnDragStart", ns.guard("bags drag", function(frame) dragStart(frame, active) end))
    f:HookScript("OnDragStop", ns.guard("bags drag", function(frame) dragStop(frame, key, active, defaultY) end))
    f:HookScript("OnSizeChanged", ns.guard("bags size", function(frame)
        if ns.Canvas.rect() and active() and frame:IsShown() and placesItself(frame, key) then
            placeFrame(frame, key, defaultY)
        end
    end))
end

-- Blizzard's title strip (ContainerFrame.xml): the combined bag and ContainerFrameTemplate each hold an unnamed
-- ContainerFramePortraitButtonRouterTemplate Button over the title, keyed routeToSibling = "PortraitButton". A Button
-- takes the mouse, so a press on the title is never the bag's: each router is made a drag handle that moves the ROOT
-- and saves the ROOT's centre. Only the root's direct children, matched by Blizzard's own key; nothing deeper. Its own
-- OnMouseDown (the dropdown proxy) is kept: HookScript adds, never replaces. That OnMouseDown toggles the menu of its
-- sibling (root[routeToSibling], the PortraitButton) on the press, unless Shift is held (DropdownButton.lua); a drag
-- that really starts closes the menu the press just opened. A plain click still toggles it, as Blizzard has it.
local function closeMenu(root, b)
    local pb = root[b.routeToSibling]
    if type(pb) == "table" and type(pb.IsMenuOpen) == "function" and type(pb.CloseMenu) == "function"
        and pb:IsMenuOpen() then
        pb:CloseMenu()
    end
end
local function titleHandles(root, key, active, defaultY)
    for _, b in ipairs({ root:GetChildren() }) do
        if not b.crucibleCanvasDrag and b.routeToSibling == "PortraitButton" and b:GetObjectType() == "Button" then
            b.crucibleCanvasDrag = true
            b:RegisterForDrag("LeftButton")
            b:HookScript("OnDragStart", ns.guard("bags drag", function()
                if dragStart(root, active) then closeMenu(root, b) end
            end))
            b:HookScript("OnDragStop", ns.guard("bags drag", function() dragStop(root, key, active, defaultY) end))
        end
    end
end

-- The handle lookup runs on every place (the flag makes a second run add nothing); third-party roots get none.
function Bags.place()
    if not ns.Canvas.rect() then return end
    local f = blizzardRoot()
    if f and f:IsShown() then
        makeDraggable(f, "Bags", always, BLIZZARD_Y)
        titleHandles(f, "Bags", always, BLIZZARD_Y)
        placeFrame(f, "Bags", BLIZZARD_Y)
    end
end

local function placeThirdParty(addon)
    local t = third[addon]
    if t and Bags.enabled(addon) and t.frame:IsShown() then placeFrame(t.frame, "bags:" .. addon) end
end

-- A known root switched on again (or still on when the canvas comes back): movable, and at its remembered spot.
local function resume(addon)
    if not Bags.enabled(addon) then return end
    third[addon].frame:SetMovable(true)
    placeThirdParty(addon)
end

-- Only while the canvas is live; a root that appears later is picked up by the next live onChange or ADDON_LOADED.
function Bags.attachThirdParty()
    if not ns.Canvas.rect() then return end
    for _, t in ipairs(Bags.THIRD_PARTY) do
        local f = _G[t.root]
        if not third[t.addon] and Bags.enabled(t.addon) and loaded(t.addon) and type(f) == "table" and f.HookScript then
            local addon = t.addon
            third[addon] = { frame = f, movable = f:IsMovable() and true or false }
            makeDraggable(f, "bags:" .. addon, function() return Bags.enabled(addon) end)
            f:HookScript("OnShow", ns.guard("bags show", function() placeThirdParty(addon) end))
            placeThirdParty(addon)
        end
    end
end

-- The option is saved either way. Off: the root gets its own movable state back and our hooks go quiet (they cannot
-- be removed). On again: movable, and placed at its remembered spot if it is open.
function Bags.setEnabled(addon, on)
    ns.state.options.dock["bags:" .. addon] = on and true or false
    ns.markChanged()
    local t = third[addon]
    if not on then
        if t then t.frame:SetMovable(t.movable) end
    elseif t then
        if ns.Canvas.rect() then resume(addon) end
    else
        Bags.attachThirdParty()
    end
end

-- The anchor hook goes in once; the third-party roots are looked for on every call (they can appear later).
function Bags.attach()
    if not hooked and type(hooksecurefunc) == "function" and type(UpdateContainerFrameAnchors) == "function" then
        hooksecurefunc("UpdateContainerFrameAnchors", ns.guard("bags place", Bags.place))
        hooked = true
    end
    Bags.attachThirdParty()
end

ns.Viewport.onChange(function(rects)
    if rects then
        Bags.attach()
        Bags.place()
        for addon in pairs(third) do resume(addon) end
    end
end)
ns.onEvent("ADDON_LOADED", function() if ns.Canvas.rect() then Bags.attachThirdParty() end end)
