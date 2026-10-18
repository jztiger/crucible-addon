-- Pure geometry for Crucible Canvas: no WoW API, loadable on its own (the tests do). Spec §4.1, §5.1.
-- Root units: the screen is 768 units high at scale 1 and 768*W/H wide; a frame's SetPoint offsets are in the
-- frame's OWN units, so a root distance is divided by that frame's effective scale.
local ADDON, ns = ...
local Geometry = {}
Geometry.ROOT_HEIGHT = 768

local function num(v) return type(v) == "number" and v == v and v ~= math.huge and v ~= -math.huge end

function Geometry.rootSize(physW, physH)
    return Geometry.ROOT_HEIGHT * physW / physH, Geometry.ROOT_HEIGHT
end

local function rectOk(r, window)
    if type(r) ~= "table" then return false, "rect missing" end
    if not (num(r.x) and num(r.y) and num(r.width) and num(r.height)) then return false, "rect not numbers" end
    if r.width <= 0 or r.height <= 0 then return false, "rect empty" end
    if r.x < 0 or r.y < 0 or r.x + r.width > window.width or r.y + r.height > window.height then return false, "rect outside window" end
    return true
end

function Geometry.validate(t)
    if type(t) ~= "table" or next(t) == nil then return false, "no topology" end
    if t.schema ~= 1 then return false, "schema " .. tostring(t.schema) .. " is not 1" end
    if t.mode ~= "DUAL" and t.mode ~= "SPLIT" then return false, "mode " .. tostring(t.mode) end
    local w = t.window
    if type(w) ~= "table" or not (num(w.width) and num(w.height)) or w.width <= 0 or w.height <= 0 then return false, "window not numbers" end
    local ok, why = rectOk(t.mainhand, w); if not ok then return false, "mainhand " .. why end
    ok, why = rectOk(t.workspace, w); if not ok then return false, "workspace " .. why end
    local a, b = t.mainhand, t.workspace
    local apart = a.x + a.width <= b.x or b.x + b.width <= a.x or a.y + a.height <= b.y or b.y + b.height <= a.y
    if not apart then return false, "mainhand and workspace overlap" end
    return true
end

function Geometry.isLive(t, physW, physH)
    local ok, why = Geometry.validate(t)
    if not ok then return false, why end
    if t.window.width ~= physW or t.window.height ~= physH then
        return false, string.format("topology is for %dx%d, window is %dx%d", t.window.width, t.window.height, physW, physH)
    end
    return true
end

local function toRoot(r, f)
    return { left = r.x * f, bottom = r.y * f, right = (r.x + r.width) * f, top = (r.y + r.height) * f }
end

function Geometry.rects(t, physH)
    local f = Geometry.ROOT_HEIGHT / physH
    return { mainhand = toRoot(t.mainhand, f), workspace = toRoot(t.workspace, f) }
end

-- TOPLEFT + BOTTOMRIGHT, as the spike's probe held. The TOPLEFT point matters: Blizzard's InputUtil.AnchorRegionToCursor
-- places every context menu against UIParent's BOTTOMLEFT and corrects a shifted UIParent only by subtracting
-- UIParent:GetPointByName("TOPLEFT")'s x. (The screen is ROOT_HEIGHT high, so the top edge is top - 768 from its top.)
function Geometry.anchors(r, scale)
    scale = scale or 1
    return {
        { "TOPLEFT", "TOPLEFT", r.left / scale, (r.top - Geometry.ROOT_HEIGHT) / scale },
        { "BOTTOMRIGHT", "BOTTOMLEFT", r.right / scale, r.bottom / scale },
    }
end

function Geometry.toFraction(r, x, y)
    return (x - r.left) / (r.right - r.left), (y - r.bottom) / (r.top - r.bottom)
end

function Geometry.fromFraction(r, fx, fy)
    return r.left + fx * (r.right - r.left), r.bottom + fy * (r.top - r.bottom)
end

function Geometry.clamp(r, cx, cy, w, h)
    local rw, rh = r.right - r.left, r.top - r.bottom
    if w >= rw then cx = r.left + rw / 2 else cx = math.max(r.left + w / 2, math.min(r.right - w / 2, cx)) end
    if h >= rh then cy = r.bottom + rh / 2 else cy = math.max(r.bottom + h / 2, math.min(r.top - h / 2, cy)) end
    return cx, cy
end

function Geometry.side(mainhand, workspace)
    if mainhand.right <= workspace.left then return "LEFT" end
    if mainhand.left >= workspace.right then return "RIGHT" end
    if mainhand.bottom >= workspace.top then return "TOP" end
    return "BOTTOM"
end

if ns then ns.Geometry = Geometry end
return Geometry
