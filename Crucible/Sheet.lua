-- Crucible: the character sheet for the simulator (spec 2026-10-17-dps-simulator-design.md, sections 5 and 11; what the
-- client actually says: docs/research/2026-10-18-sheet-probe.md).
--
-- What this file is for: the group's DPS simulator needs to know who the logged-in character is - its name, class, race and
-- level, what it wears and carries, the spells and talents it has learned, its pet and its professions - and the stats the
-- game itself reports, which let the site check its own sums against the game's. This file keeps one such snapshot in
-- memory (Sheet.last), refreshed on the events that say the sheet changed. Export.saveChar writes the last complete one as
-- the "c1:" string beside the reference and quest strings when the client is about to write the saved file (logout,
-- /crucible reload). Logic.charDoc builds the document and owns its ceilings; src/shared/char-doc.ts is the contract on the
-- other side. Nothing is read back.
--
-- Rules this file keeps (docs/decisions.md C7, C11; spec sections 5 and 11):
--   * No timer, no polling, no combat log, no server query. A capture runs inside an event the game sent because the sheet
--     changed, and reads the player's own state.
--   * Nothing is read while InCombatLockdown() is true. An event that comes in combat only marks a capture owed, and
--     PLAYER_REGEN_ENABLED pays it. What is written at a logout in combat is the last capture taken out of combat.
--     A /reload in combat lifts the lockdown before its write (UAT 2026-10-18, row 4), so the owed capture is paid
--     then and the write carries the sheet as it is at that moment.
--   * Only the member's own character and its pet: every unit handed to the client is "player" or "pet" (the compliance
--     test in addon/tests/syntax.test.ts holds this, and holds UnitName("player") to this file). Another player's identity
--     never enters: a crafted item's link carries its crafter's GUID, and Logic.itemString cuts it out at once.
--   * Every client call is under pcall, and every value passes ns.isSecret before anything else is done with it - compared,
--     counted, indexed or stored. A value that reads secret is a value that is missing.
--   * Only a COMPLETE capture is kept: one the builder can make a document of (an identity and every stat but the weapons)
--     and whose spellbook gave at least one spell (PLAYER_ENTERING_WORLD can fire before the spellbook has loaded, and an
--     empty list would say the character knows nothing). The simulator must never run from half a sheet, so an incomplete
--     capture never replaces Sheet.last: the one before it stays.
--
-- Collecting needs the scope notice (version 6) accepted and the "Use the simulator" switch not off (Logic.collectsChar);
-- below that this file registers nothing at all.
--
-- Probe facts this file leans on (2026-10-18, the owner's level 30 hunter on build 70450): UnitName answers a second value
-- that is not nil, so one return is taken; an empty gear slot answers no value at all; links carry a named colour and the
-- crafter's GUID on crafted pieces; UnitStat's first two returns are equal and its four are kept as they come; weapon
-- speeds are UnitAttackSpeed's, the ranged one UnitRangedDamage's first return; percentages and damage are floats, rounded
-- to hundredths; flyouts have no spellID and pet actions carry an actionID above 2^31, so only spellIDs are kept;
-- GetProfessions answers seven slots with nil holes and its numbers are skill indexes; a talent's rank is activeRank and
-- its spell is entry -> definition -> spellID. Ammo (slot 0) answers nothing on this client, so it is not asked for.

local _, ns = ...
local Logic = ns.Logic

if not Logic.collectsChar(ns.baked) then return end

local Sheet = {}
ns.Sheet = Sheet

-- The newest COMPLETE capture, and what Export.saveChar needs to write it without asking the client anything - it runs in
-- the middle of a fight when the player logs out in one: when the capture was taken (the document is dated by it, as the
-- server keeps the newest by that time), the hash of the character's GUID, and the realm, faction and region ns.meta()
-- gave then, each one checked with ns.isSecret (a secret or unreadable one is missing). Session only.
Sheet.last = nil
Sheet.lastAt = nil
Sheet.lastHash = nil
Sheet.lastMeta = nil

local floor = math.floor

---------------------------------------------------------------------------------------------------
-- Reading what the client hands back
---------------------------------------------------------------------------------------------------

-- The value, or nil when it is secret. The first look at anything the client returns.
local function clean(v)
    if ns.isSecret(v) then return nil end
    return v
end

-- A finite number, else nil (a secret one included).
local function num(v)
    v = clean(v)
    if type(v) ~= "number" or v ~= v or v == math.huge or v == -math.huge then return nil end
    return v
end

-- A non-empty string, else nil.
local function text(v)
    v = clean(v)
    if type(v) ~= "string" or v == "" then return nil end
    return v
end

-- A table, else nil.
local function tbl(v)
    v = clean(v)
    if type(v) ~= "table" then return nil end
    return v
end

-- floor(v * scale + 0.5): the document holds whole numbers only - percentages and damage in hundredths, speeds in
-- milliseconds. nil when v is no usable number.
local function scaled(v, scale)
    v = num(v)
    if v == nil then return nil end
    return floor(v * scale + 0.5)
end

-- A function of a client namespace, or nil when the namespace or the function is missing on this client.
local function api(namespace, name)
    if type(namespace) == "table" then return namespace[name] end
    return nil
end

-- The first return of a client function; nil when it is missing, throws, or answers a secret value.
local function first(fn, ...)
    local ok, v = pcall(fn, ...)
    if not ok then return nil end
    return clean(v)
end

-- The first n returns of a client call as one row of whole numbers, each times `scale`; `ok` is the call's pcall result
-- and the call is the rest. nil when it failed or any of the n values is missing or secret.
local function wholeRow(n, scale, ok, ...)
    if not ok then return nil end
    local out = {}
    for i = 1, n do
        local v = scaled((select(i, ...)), scale)
        if v == nil then return nil end
        out[i] = v
    end
    return out
end

-- A value of the game's own Enum, or the number the probe saw when this client has no such table.
local function enumValue(group, key, probed)
    local g = type(Enum) == "table" and Enum[group] or nil
    local v = type(g) == "table" and g[key] or nil
    if type(v) == "number" then return v end
    return probed
end
local PLAYER_BANK = enumValue("SpellBookSpellBank", "Player", 0)
local PET_BANK = enumValue("SpellBookSpellBank", "Pet", 1)
local FUTURE_SPELL = enumValue("SpellBookItemType", "FutureSpell", 2)

local MANA = 0 -- UnitPowerMax's power type for mana
local LAST_BAG = 4 -- the backpack (0) and the four bag slots, which is what the probe read
local STAT_KEYS = { "str", "agi", "sta", "int", "spi" } -- UnitStat's ids 1 to 5

local function inCombat()
    if type(InCombatLockdown) ~= "function" then return false end
    local ok, v = pcall(InCombatLockdown)
    if not ok or ns.isSecret(v) then return true end -- cannot tell: then nothing is read
    return v and true or false
end

---------------------------------------------------------------------------------------------------
-- The reads. Each takes what its section holds, or nothing; none raises.
---------------------------------------------------------------------------------------------------

local function readIdentity(snap)
    -- UnitName's second return is a realm-like string and not nil: only the first is taken.
    local okName, name = pcall(UnitName, "player")
    snap.name = okName and text(name) or nil
    local okClass, _, _, classId = pcall(UnitClass, "player")
    snap.classId = okClass and num(classId) or nil
    local okRace, _, _, raceId = pcall(UnitRace, "player")
    snap.raceId = okRace and num(raceId) or nil
    snap.level = num(first(UnitLevel, "player"))
    local okFaction, faction = pcall(UnitFactionGroup, "player")
    snap.faction = okFaction and text(faction) or nil
end

-- [slot, item string] for every slot that holds something. An empty slot answers no value at all. The link goes through
-- Logic.itemString here, so the crafter's GUID of a crafted piece never sits in memory either.
local function readGear()
    local rows = {}
    for slot = 1, Logic.MAX_GEAR_SLOT do
        local item = Logic.itemString(first(GetInventoryItemLink, "player", slot))
        if item then rows[#rows + 1] = { slot, item } end
    end
    return rows
end

-- [bag, slot, item string] for the equippable items in the bags - what the member's notice names. A client that cannot
-- say which items are equippable collects none.
local function readBags()
    local rows = {}
    local slotsOf = api(C_Container, "GetContainerNumSlots")
    local linkAt = api(C_Container, "GetContainerItemLink")
    local equippable = api(C_Item, "IsEquippableItem")
    if not (slotsOf and linkAt and equippable) then return rows end
    for bag = 0, LAST_BAG do
        local slots = num(first(slotsOf, bag)) or 0
        for slot = 1, math.min(slots, 255) do
            local link = text(first(linkAt, bag, slot))
            if link and first(equippable, link) == true then
                local item = Logic.itemString(link)
                if item then rows[#rows + 1] = { bag, slot, item } end
            end
        end
    end
    return rows
end

-- One item of a spellbook bank, as the client describes it, or nil.
local function bookItem(index, bank)
    return tbl(first(api(C_SpellBook, "GetSpellBookItemInfo"), index, bank))
end

-- [spellID, lowRank 0 or 1] for every learned spell. A flyout has no spellID, and a spell the character has not learned yet
-- is no spell it may cast; lower ranks are spells of their own and the client says which are low. A spell whose low-rank
-- answer is missing is left out, rather than guessed at.
local function readSpells()
    local rows = {}
    local lines = num(first(api(C_SpellBook, "GetNumSpellBookSkillLines")))
    if not lines then return rows end
    for line = 1, lines do
        local info = tbl(first(api(C_SpellBook, "GetSpellBookSkillLineInfo"), line))
        local offset = info and num(info.itemIndexOffset)
        local count = info and num(info.numSpellBookItems)
        if offset and count then
            for index = offset + 1, offset + count do
                if #rows >= Logic.MAX_SPELLS then return rows end
                local item = bookItem(index, PLAYER_BANK)
                local spellID = item and num(item.spellID)
                if spellID and spellID >= 1 and num(item.itemType) ~= FUTURE_SPELL then
                    local low = first(api(C_SpellBook, "IsSpellBookItemLowRank"), index, PLAYER_BANK)
                    if type(low) == "boolean" then rows[#rows + 1] = { spellID, low and 1 or 0 } end
                end
            end
        end
    end
    return rows
end

-- The pet: { family, level, spells }, or nil when there is none out (or it cannot be described). Its abilities are the
-- spellIDs of its own bank; an item's actionID is a packed number, above 2^31 for some, and is never kept.
local function readPet()
    if first(UnitExists, "pet") ~= true then return nil end
    local okFamily, _, family = pcall(UnitCreatureFamily, "pet") -- the family's name, then its numeric id
    family = okFamily and num(family) or nil
    local level = num(first(UnitLevel, "pet"))
    if not family or not level then return nil end
    local spells = {}
    local count = num(first(api(C_SpellBook, "HasPetSpells"))) or 0
    for index = 1, math.min(count, Logic.MAX_SPELLS) do
        local item = bookItem(index, PET_BANK)
        local spellID = item and num(item.spellID)
        if spellID and spellID >= 1 then spells[#spells + 1] = spellID end
    end
    return { family = family, level = level, spells = spells }
end

-- [skillLine, rank, maxRank] for each profession. GetProfessions answers seven slots - the two primary and five secondary
-- ones - with nil holes anywhere, so each slot is looked at on its own; its numbers are skill indexes, and
-- GetProfessionInfo turns one into the classic skill line id (its seventh return) with the rank and the cap (third, fourth).
local function readProfessions()
    local rows = {}
    local got = { pcall(GetProfessions) } -- got[1] is pcall's ok; the seven slots follow
    if got[1] ~= true then return rows end
    for slot = 2, 8 do
        local index = num(got[slot])
        if index then
            local ok, _, _, rank, maxRank, _, _, skillLine = pcall(GetProfessionInfo, index)
            if ok then
                skillLine, rank, maxRank = num(skillLine), num(rank), num(maxRank)
                if skillLine and rank and maxRank then rows[#rows + 1] = { skillLine, rank, maxRank } end
            end
        end
    end
    return rows
end

-- [nodeId, entryId, rank, spellId] for one bought talent node, or nil when it is not bought or a link of its chain cannot
-- be read. The rank is activeRank - what Forever's own talent buttons light a node by - and the spell is the one the
-- entry's definition names.
local function talentRow(configID, nodeID)
    local node = tbl(first(api(C_Traits, "GetNodeInfo"), configID, nodeID))
    local rank = node and num(node.activeRank)
    if not rank or rank < 1 then return nil end
    local entry = tbl(node.activeEntry)
    local entryID = entry and num(entry.entryID)
    if not entryID then return nil end
    local entryInfo = tbl(first(api(C_Traits, "GetEntryInfo"), configID, entryID))
    local definitionID = entryInfo and num(entryInfo.definitionID)
    if not definitionID then return nil end
    local definition = tbl(first(api(C_Traits, "GetDefinitionInfo"), definitionID))
    local spellID = definition and num(definition.spellID)
    if not spellID then return nil end
    return { nodeID, entryID, rank, spellID }
end

-- -> the bought nodes, and the talent import string. Traits is the only route on this client (the classic talent calls are
-- gone); a tree that is not there to read gives no rows and no string, and the sheet is complete without them.
local function readTalents()
    local rows = {}
    local configID = num(first(api(C_ClassTalents, "GetActiveConfigID")))
    if not configID then return rows, nil end
    local loadout = text(first(api(C_Traits, "GenerateImportString"), configID))
    local config = tbl(first(api(C_Traits, "GetConfigInfo"), configID))
    local trees = config and tbl(config.treeIDs)
    if trees then
        for t = 1, #trees do
            local treeID = num(trees[t])
            local nodes = treeID and tbl(first(api(C_Traits, "GetTreeNodes"), treeID))
            if nodes then
                for n = 1, #nodes do
                    if #rows >= Logic.MAX_TALENT_NODES then return rows, loadout end
                    local nodeID = num(nodes[n])
                    local row = nodeID and talentRow(configID, nodeID)
                    if row then rows[#rows + 1] = row end
                end
            end
        end
    end
    return rows, loadout
end

-- One hand's weapon as the contract has it: [damage min and max in hundredths, speed in milliseconds]. A hand that holds
-- nothing answers no speed, or 0, and is left out: nil - never false and never an empty row. A hand that does hold a weapon
-- but whose numbers cannot all be read (one is missing or secret, or the call failed) comes back as a row with a hole in
-- it, which the builder refuses whole: the sheet is then incomplete and the last complete one stays, instead of a weapon
-- being written as "no weapon".
local function weapon(ok, lo, hi, speed)
    if ok and not ns.isSecret(speed) and (speed == nil or (type(speed) == "number" and speed <= 0)) then
        return nil
    end
    return { scaled(lo, 100), scaled(hi, 100), scaled(speed, 1000) }
end

-- The stats the game itself reports. Whole numbers stay whole; percentages are times 100 and rounded; UnitStat's four
-- returns and the attack power triples are kept as they come, the base of an attribute being derived where it is used.
local function readStats()
    local s = {}
    s.hp = scaled(first(UnitHealthMax, "player"), 1)
    s.mana = scaled(first(UnitPowerMax, "player", MANA), 1)
    for i = 1, #STAT_KEYS do
        s[STAT_KEYS[i]] = wholeRow(4, 1, pcall(UnitStat, "player", i))
    end
    s.ap = wholeRow(3, 1, pcall(UnitAttackPower, "player"))
    s.rap = wholeRow(3, 1, pcall(UnitRangedAttackPower, "player"))
    s.crit = scaled(first(GetCritChance), 100)
    s.rangedCrit = scaled(first(GetRangedCritChance), 100)
    s.spellCrit = scaled(first(GetSpellCritChance), 100)
    s.hit = scaled(first(GetHitModifier), 100)
    s.spellHit = scaled(first(GetSpellHitModifier), 100)
    -- UnitDamage has the damage of both hands and no speed; UnitAttackSpeed has their speeds; the ranged weapon has both
    -- in UnitRangedDamage, speed first.
    local okDamage, mhLo, mhHi, ohLo, ohHi = pcall(UnitDamage, "player")
    local okSpeed, mhSpeed, ohSpeed = pcall(UnitAttackSpeed, "player")
    local okRanged, rangedSpeed, rangedLo, rangedHi = pcall(UnitRangedDamage, "player")
    s.mh = weapon(okDamage and okSpeed, mhLo, mhHi, mhSpeed)
    s.oh = weapon(okDamage and okSpeed, ohLo, ohHi, ohSpeed)
    s.ranged = weapon(okRanged, rangedLo, rangedHi, rangedSpeed)
    return s
end

---------------------------------------------------------------------------------------------------
-- A capture
---------------------------------------------------------------------------------------------------

-- The character's GUID is hashed here and goes no further (Logic.charHash says why). -> 16 hex characters, or nil
local function characterHash()
    local guid = first(UnitGUID, "player")
    if type(guid) ~= "string" then return nil end
    return Logic.charHash(guid)
end

-- Keeps the capture as Sheet.last when it is complete: the builder makes a document of it, with the real identity and time,
-- and the spellbook gave at least one spell. The builder is the only definition of "complete" there is.
local function keepIfComplete(snap)
    local at, hash = ns.serverTime(), characterHash()
    -- ns.meta() calls the client without pcall and hands its values back unchecked: it is asked under pcall here, and the
    -- three fields the document takes pass num/text (so ns.isSecret) before the builder compares or measures them.
    local okMeta, m = pcall(ns.meta)
    if not okMeta or type(m) ~= "table" then m = {} end
    local meta = { realm = text(m.realm), faction = text(m.faction), region = num(m.region) }
    local doc = Logic.charDoc(snap, at, hash, meta)
    if not doc or #doc.spells == 0 then return false end
    Sheet.last, Sheet.lastAt, Sheet.lastHash, Sheet.lastMeta = snap, at, hash, meta
    return true
end

-- Reads the character now and returns the snapshot Logic.charDoc takes - { name, classId, raceId, level, faction, gear,
-- bags, spells, talents, loadout, pet, professions, stats } - or nil in combat, when nothing is read. Whether it replaces
-- Sheet.last is decided here: only a complete one does.
function Sheet.capture()
    if inCombat() then return nil end
    local snap = {}
    readIdentity(snap)
    snap.gear = readGear()
    snap.bags = readBags()
    snap.spells = readSpells()
    snap.talents, snap.loadout = readTalents()
    snap.pet = readPet()
    snap.professions = readProfessions()
    snap.stats = readStats()
    keepIfComplete(snap)
    return snap
end

---------------------------------------------------------------------------------------------------
-- When
---------------------------------------------------------------------------------------------------

-- Set when an event came in combat; the capture is owed at the end of the fight.
local pending = false

-- The capture runs under pcall: an error that left this handler would reach Core.lua's dispatcher, whose ns.fail resets a
-- running scan - and BAG_UPDATE_DELAYED, which the player's own Post click fires, comes while one runs. A capture that
-- fails keeps nothing, so Sheet.last stays the last complete one.
local function refresh()
    if inCombat() then
        pending = true
        return
    end
    pending = false
    pcall(Sheet.capture)
end

ns.on("PLAYER_ENTERING_WORLD", refresh)
ns.on("PLAYER_EQUIPMENT_CHANGED", refresh)
ns.on("PLAYER_LEVEL_UP", refresh)
ns.on("SPELLS_CHANGED", refresh)
ns.on("TRAIT_CONFIG_UPDATED", refresh)
ns.on("UNIT_PET", function(unit)
    if not ns.isSecret(unit) and unit == "player" then refresh() end
end)
ns.on("BAG_UPDATE_DELAYED", refresh)
ns.on("PLAYER_REGEN_ENABLED", function()
    if pending then refresh() end
end)
