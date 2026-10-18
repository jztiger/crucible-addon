-- Edit Mode with the canvas (owner decision 2026-10-16, UAT row 9). With the canvas live, UIParent is the Mainhand,
-- not the screen, and Blizzard_EditMode mixes the two:
-- 1. A drop: the client anchors a moved frame to the SCREEN (relativeTo nil); EditModeManagerFrameMixin:
--    UpdateSystemAnchorInfo re-targets the same point and offsets to UIParent and corrects Y only, so the frame lands
--    by the distance between the screen's point and UIParent's (a LEFT point: UIParent's left offset).
-- 2. Arrow keys and every save (ProcessMovementKey, PrepareForSave): EditModeSystemMixin:BreakFrameSnap uses the
--    frame's screen-relative edges as offsets from UIParent's corners, wrong by UIParent's left and bottom.
-- Nothing is replaced: Blizzard places and stores first; then the frame is put back where the player had it and Edit
-- Mode stores that (OnSystemPositionChange). Every hook returns at once without a live canvas or in combat.
-- The frames are Blizzard's own list (EditModeManagerFrame.registeredSystemFrames), read when Edit Mode opens.
-- Their drag scripts sit on the Selection child, which calls the frame's OnDragStart/OnDragStop METHODS
-- (EditModeSystemTemplates.xml/.lua), so the methods are hooked (hooksecurefunc), not scripts.
local ADDON, ns = ...
local EditMode = {}
ns.EditMode = EditMode

local JUMP = 50 -- frame units; a snap moves a frame a few pixels, the defect by UIParent's offset from the screen

local function active() return ns.Canvas.rect() ~= nil and not ns.inCombat() end

-- A point's place on one axis of a rect: low, middle or high.
local function onAxis(point, low, high, lo, hi)
    if point:find(low, 1, true) then return lo end
    if point:find(high, 1, true) then return hi end
    return (lo + hi) / 2
end

-- UIParent units -> the frame's own units (1 / the frame's scale for a UIParent child).
local function toFrame(f)
    local s, e = UIParent:GetEffectiveScale(), f:GetEffectiveScale()
    if type(s) ~= "number" or type(e) ~= "number" or e <= 0 then return nil end
    return s / e
end

-- The frame's single point, when it is against UIParent.
local function uiParentPoint(f)
    if f:GetNumPoints() ~= 1 then return nil end
    local point, rel, relPoint, x, y = f:GetPoint(1)
    if rel ~= UIParent or type(point) ~= "string" or type(relPoint) ~= "string" then return nil end
    if type(x) ~= "number" or type(y) ~= "number" then return nil end
    return point, relPoint, x, y
end

local function put(f, point, relPoint, x, y)
    f:ClearAllPoints()
    f:SetPoint(point, UIParent, relPoint, x, y)
    if type(f.OnSystemPositionChange) == "function" then f:OnSystemPositionChange() end
end

---------------------------------------------------------------------------------------------------
-- Drops: where the frame was while it moved, sampled every client frame by our own watcher.
---------------------------------------------------------------------------------------------------
local watcher = CreateFrame("Frame")
watcher:Hide()
local dragged, lastLeft, lastTop

local function sample()
    if not dragged or not dragged.isDragging then
        dragged = nil
        watcher:Hide()
        return
    end
    local l, t = dragged:GetLeft(), dragged:GetTop()
    if l and t then lastLeft, lastTop = l, t end
end
watcher:SetScript("OnUpdate", ns.guard("edit mode watch", sample))

local function dragStart(f)
    if not active() or not f.isDragging then return end
    dragged, lastLeft, lastTop = f, nil, nil
    sample()
    watcher:Show()
end

-- The top-left corner the frame's anchor gives it, in its own units. Not GetLeft/GetTop: Edit Mode frames are
-- clamped to the screen, so after a jump past the screen's edge those report the clamped spot, not the anchor's.
local function anchoredTopLeft(f, point, relPoint, x, y)
    local k = toFrame(f)
    local l, b, r, t = UIParent:GetLeft(), UIParent:GetBottom(), UIParent:GetRight(), UIParent:GetTop()
    local w, h = f:GetWidth(), f:GetHeight()
    if not (k and l and b and r and t and w and h) then return nil end
    local ax = onAxis(relPoint, "LEFT", "RIGHT", l, r) * k + x
    local ay = onAxis(relPoint, "BOTTOM", "TOP", b, t) * k + y
    return ax - onAxis(point, "LEFT", "RIGHT", 0, w), ay - onAxis(point, "BOTTOM", "TOP", 0, h) + h
end

-- After Blizzard's OnDragStop (the client's anchor, a snap if on, UpdateSystemAnchorInfo): a frame its anchor puts
-- far from where it was dropped is put back, by the same point.
local function dragStop(f)
    if dragged ~= f then return end
    local l0, t0 = lastLeft, lastTop
    dragged, lastLeft, lastTop = nil, nil, nil
    watcher:Hide()
    if not active() or not l0 then return end
    local point, relPoint, x, y = uiParentPoint(f)
    if not point then return end
    local left, top = anchoredTopLeft(f, point, relPoint, x, y)
    if not left then return end
    local dx, dy = left - l0, top - t0
    if math.abs(dx) <= JUMP and math.abs(dy) <= JUMP then return end
    put(f, point, relPoint, x - dx, y - dy)
end

---------------------------------------------------------------------------------------------------
-- Arrow keys and saves: BreakFrameSnap measured from the screen's left and bottom; UIParent's are added back.
---------------------------------------------------------------------------------------------------
local function snapBroken(f)
    if not active() or not f:GetTop() then return end
    local l, b = UIParent:GetLeft(), UIParent:GetBottom()
    if not l or not b or (math.abs(l) < 0.5 and math.abs(b) < 0.5) then return end
    local point, relPoint, x, y = uiParentPoint(f)
    if not point or point ~= relPoint or (point ~= "TOPLEFT" and point ~= "TOPRIGHT") then return end
    local k = toFrame(f)
    if not k then return end
    put(f, point, relPoint, x - l * k, y - b * k)
end

---------------------------------------------------------------------------------------------------
-- Hooks: once per frame (a flag on it), when Edit Mode opens and at load if it is already open.
---------------------------------------------------------------------------------------------------
local function hookFrame(f)
    if type(f) ~= "table" or f.crucibleCanvasEditMode then return end
    f.crucibleCanvasEditMode = true
    if type(f.OnDragStart) == "function" then hooksecurefunc(f, "OnDragStart", ns.guard("edit mode drag", dragStart)) end
    if type(f.OnDragStop) == "function" then hooksecurefunc(f, "OnDragStop", ns.guard("edit mode drag", dragStop)) end
    if type(f.BreakFrameSnap) == "function" then
        hooksecurefunc(f, "BreakFrameSnap", ns.guard("edit mode nudge", snapBroken))
    end
end

local function hookAll()
    local list = EditModeManagerFrame.registeredSystemFrames
    if type(list) ~= "table" then return end
    for _, f in ipairs(list) do hookFrame(f) end
end

local managerHooked = false
local function hookManager()
    local m = EditModeManagerFrame
    if managerHooked or type(m) ~= "table" or type(m.HookScript) ~= "function" then return end
    managerHooked = true
    m:HookScript("OnShow", ns.guard("edit mode open", hookAll))
    if m:IsShown() then ns.guard("edit mode open", hookAll)() end
end

if type(hooksecurefunc) == "function" then
    hookManager()
    ns.onEvent("ADDON_LOADED", hookManager)
end
