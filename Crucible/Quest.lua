-- Crucible: the quest library's recorder (spec 2026-10-11-quest-library-design.md, section 3; what the client
-- actually does: docs/research/2026-10-11-quest-probe.md).
--
-- What this file is for: the group builds its own map of where quests and NPCs are from what each player sees on
-- their OWN actions - reading a quest offer, accepting, an objective ticking, turning in, opening a trainer, a
-- vendor, a flight master or an innkeeper. Each is one event the player caused; each writes one row into
-- CrucibleDB.quest (Logic.noteOffer and friends own the ceilings), which leaves as the "q1:" string on the next
-- Sync or logout (Export.saveQuest). Nothing is read back.
--
-- Rules this file keeps (docs/decisions.md C7, C11, C17; spec section 3):
--   * No timer, no polling, no combat log. Every row is written inside the event the player's click caused.
--   * Nothing is read while InCombatLockdown() is true.
--   * Only the NPC whose window is open is named (UnitName("npc")); never a player, never a GUID. The compliance
--     test in addon/tests/syntax.test.ts holds the event list and the forbidden names.
--   * Positions: C_Map.GetBestMapForUnit("player") and C_Map.GetPlayerMapPosition(mapID, "player"), feature-detected
--     and under pcall, every value checked with ns.isSecret. No position, no row.
--   * A member who switched "Record my quests" off on Your PCs gets recordQuests = false in their own Data.lua
--     (Logic.recordsQuests), and then this file registers nothing at all.
--
-- Recording needs notice v6 accepted and the switch on (Logic.recordsQuests); below that this file registers nothing.
--
-- Probe facts this file leans on (2026-10-11): QUEST_DETAIL's argument is the starting item (0), the quest id is
-- GetQuestID() and the log does not know the quest yet, so the title is GetTitleText()'s; QUEST_ACCEPTED carries the
-- quest id as its one argument; QUEST_TURNED_IN carries (questID, xp, money); UNIT_QUEST_LOG_CHANGED("player")
-- fires on every objective tick with the position readable and never secret out of combat. A kill objective ticks
-- while the player is still IN combat (in-game 2026-10-12), so that change is only remembered, and the diff runs
-- when PLAYER_REGEN_ENABLED says the fight is over - the player is still standing where it was.

local _, ns = ...
local Logic = ns.Logic

if not Logic.recordsQuests(ns.baked) then return end

local function q() return ns.db().quest end

local function clean(v)
    if ns.isSecret(v) then return nil end
    return v
end

-- -> mapID, x, y   or nil
local function here()
    if type(C_Map) ~= "table" or type(C_Map.GetBestMapForUnit) ~= "function"
        or type(C_Map.GetPlayerMapPosition) ~= "function" then
        return nil
    end
    if type(InCombatLockdown) == "function" and InCombatLockdown() then return nil end
    local ok, mapID = pcall(C_Map.GetBestMapForUnit, "player")
    mapID = ok and clean(mapID) or nil
    if type(mapID) ~= "number" then return nil end
    local okP, p = pcall(C_Map.GetPlayerMapPosition, mapID, "player")
    if not okP or type(p) ~= "table" or type(p.GetXY) ~= "function" then return nil end
    local okXY, fx, fy = pcall(p.GetXY, p)
    if not okXY then return nil end
    local x, y = Logic.toCoord(clean(fx)), Logic.toCoord(clean(fy))
    if x == nil or y == nil then return nil end
    return mapID, x, y
end

-- The NPC whose window is open - and only an NPC: a quest shared by a party member, or one started from an item,
-- can leave a PLAYER in that unit, and a player's name never enters the document (C11).
local function npcName()
    if type(UnitName) ~= "function" or type(UnitIsPlayer) ~= "function" then return nil end
    local okP, isPlayer = pcall(UnitIsPlayer, "npc")
    if not okP or ns.isSecret(isPlayer) or isPlayer then return nil end
    local ok, name = pcall(UnitName, "npc")
    name = ok and clean(name) or nil
    if type(name) ~= "string" then return nil end
    return name
end

-- -> array of { text, fulfilled } for a quest, or {} when the client has no objectives API
local function objectivesOf(questID)
    local out = {}
    if type(C_QuestLog) ~= "table" or type(C_QuestLog.GetQuestObjectives) ~= "function" then return out end
    local ok, list = pcall(C_QuestLog.GetQuestObjectives, questID)
    if not ok or type(list) ~= "table" then return out end
    for i = 1, #list do
        local o = list[i]
        local text = type(o) == "table" and clean(o.text) or nil
        local done = type(o) == "table" and clean(o.numFulfilled) or nil
        out[i] = { text = type(text) == "string" and text or "", fulfilled = type(done) == "number" and done or 0 }
    end
    return out
end

-- The title of the offer on screen (the log does not hold the quest yet at QUEST_DETAIL).
local function offerTitle()
    if type(GetTitleText) ~= "function" then return nil end
    local ok, t = pcall(GetTitleText)
    t = ok and clean(t) or nil
    return type(t) == "string" and t or nil
end

local function levelOf(questID)
    if type(C_QuestLog) ~= "table" or type(C_QuestLog.GetQuestDifficultyLevel) ~= "function" then return 0 end
    local ok, l = pcall(C_QuestLog.GetQuestDifficultyLevel, questID)
    l = ok and clean(l) or nil
    return type(l) == "number" and l or 0
end

local function currentQuestID()
    if type(GetQuestID) ~= "function" then return nil end
    local ok, id = pcall(GetQuestID)
    id = ok and clean(id) or nil
    if type(id) == "number" and id > 0 then return id end
    return nil
end

local function questIDArg(v)
    v = clean(v)
    if type(v) == "number" and v > 0 then return v end
    return nil
end

-- The last known objective counts per quest in the log, for the tick diff. Session only.
local known = {}

ns.on("QUEST_DETAIL", function()
    local mapID, x, y = here() -- first: nothing at all is read in combat
    if not mapID then return end
    local questID = currentQuestID()
    if not questID then return end
    if Logic.noteOffer(q(), questID, offerTitle(), npcName(), mapID, x, y, ns.serverTime()) then
        ns.pendingUpload = true
    end
end)

ns.on("QUEST_ACCEPTED", function(a, b)
    -- This client passes (questID); retail passes (questLogIndex, questID). Take the second when there is one.
    local questID = questIDArg(b) or questIDArg(a)
    local mapID, x, y = here()
    if not questID or not mapID then return end
    local objs = objectivesOf(questID)
    known[questID] = objs
    local lines = {}
    for i = 1, #objs do lines[i] = objs[i].text end
    if Logic.noteAccept(q(), questID, levelOf(questID), lines, mapID, x, y, ns.serverTime()) then
        ns.pendingUpload = true
    end
end)

-- Set when the log changed in combat; the diff is owed at the end of the fight.
local logChangedInCombat = false

-- Diffs every quest in the log against `known`: a rise becomes a tick row when the position is readable; with no
-- readable position (an instance) the counts are still re-baselined, so the rise is never credited to wherever the
-- player stands at the NEXT change (review 2026-10-11, Critical 1).
local function diffQuestLog()
    if type(C_QuestLog) ~= "table" or type(C_QuestLog.GetNumQuestLogEntries) ~= "function"
        or type(C_QuestLog.GetInfo) ~= "function" then
        return
    end
    local mapID, x, y = here()
    local okN, n = pcall(C_QuestLog.GetNumQuestLogEntries)
    n = okN and clean(n) or nil
    if type(n) ~= "number" then return end
    local t = ns.serverTime()
    for i = 1, n do
        local okI, info = pcall(C_QuestLog.GetInfo, i)
        if okI and type(info) == "table" and not info.isHeader then
            local questID = questIDArg(info.questID)
            if questID then
                local after = objectivesOf(questID)
                if mapID and Logic.noteTicks(q(), questID, known[questID], after, mapID, x, y, t) > 0 then
                    ns.pendingUpload = true
                end
                known[questID] = after
            end
        end
    end
end

-- Login and /reload: every quest already in the log gets its baseline now, so its first change is a tick and not
-- just a baseline (a change with no baseline never counts - review 2026-10-11, Critical 1).
ns.on("PLAYER_ENTERING_WORLD", function()
    if type(InCombatLockdown) == "function" and InCombatLockdown() then
        logChangedInCombat = true
        return
    end
    diffQuestLog()
end)

ns.on("UNIT_QUEST_LOG_CHANGED", function(unit)
    if unit ~= "player" then return end
    -- Nothing is read in combat (spec section 3): the change is noted and the diff waits for the fight's end.
    if type(InCombatLockdown) == "function" and InCombatLockdown() then
        logChangedInCombat = true
        return
    end
    diffQuestLog()
end)

ns.on("PLAYER_REGEN_ENABLED", function()
    if not logChangedInCombat then return end
    logChangedInCombat = false
    diffQuestLog()
end)

ns.on("QUEST_TURNED_IN", function(questID)
    questID = questIDArg(questID)
    if not questID then return end
    -- The completion is kept whatever the position says; the turn-in row needs the NPC and a readable position.
    local mapID, x, y = here()
    Logic.noteTurnin(q(), questID, mapID and npcName() or nil, mapID, x, y, ns.serverTime())
    ns.pendingUpload = true
    known[questID] = nil
end)

local function noteNpc(kind)
    return function()
        local mapID, x, y = here()
        if not mapID then return end
        if Logic.noteNpc(q(), npcName(), kind, mapID, x, y, ns.serverTime()) then ns.pendingUpload = true end
    end
end
ns.on("TRAINER_SHOW", noteNpc("trainer"))
ns.on("MERCHANT_SHOW", noteNpc("vendor"))
ns.on("TAXIMAP_OPENED", noteNpc("flight"))
ns.on("CONFIRM_BINDER", noteNpc("inn"))
