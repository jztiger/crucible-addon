-- The Crucible Canvas state document, pure: one table, a revision, base64-JSON encoding through a codec the caller
-- hands in (the client's C_EncodingUtil; JS in the tests), the saved-file chunk format the tray reads by regex
-- (spec §4.2), and the "highest revision wins" choice among sources (spec §5.5).
local ADDON, ns = ...
local State = {}
State.SCHEMA = 1
State.MAX_BYTES = 131072
State.CHUNK = 16384

local function defaultOptions()
    return { theme = "forge", splitSide = "left", reopen = true, wizardDone = false, dock = {} }
end

function State.new()
    return { revision = 0, schema = State.SCHEMA, options = defaultOptions(), characters = {} }
end

function State.isDoc(v)
    return type(v) == "table" and v.schema == State.SCHEMA and type(v.revision) == "number"
        and v.revision >= 0 and v.revision == math.floor(v.revision)
        and type(v.options) == "table" and type(v.characters) == "table"
end

function State.bump(doc)
    doc.revision = doc.revision + 1
    return doc
end

function State.encode(doc, codec)
    if not State.isDoc(doc) then return nil, "not a document" end
    local ok, json = pcall(codec.serialize, doc)
    if not ok or type(json) ~= "string" then return nil, "serialize failed" end
    if #json > State.MAX_BYTES then return nil, "document too big: " .. #json end
    local okB, b64 = pcall(codec.encode, json)
    if not okB or type(b64) ~= "string" then return nil, "encode failed" end
    return b64
end

function State.decode(b64, codec)
    if type(b64) ~= "string" or b64 == "" or b64:find("[^%w%+/=]") then return nil, "not base64" end
    if #b64 > State.MAX_BYTES * 2 then return nil, "too big" end
    local ok, json = pcall(codec.decode, b64)
    if not ok or type(json) ~= "string" then return nil, "decode failed" end
    if #json > State.MAX_BYTES then return nil, "document too big: " .. #json end
    local okD, doc = pcall(codec.deserialize, json)
    if not okD or type(doc) ~= "table" then return nil, "not JSON" end
    if doc.schema ~= State.SCHEMA then return nil, "schema " .. tostring(doc.schema) end
    if type(doc.revision) ~= "number" then return nil, "no revision" end
    if type(doc.options) ~= "table" then doc.options = {} end
    local defaults = defaultOptions()
    for k, v in pairs(defaults) do
        if type(doc.options[k]) ~= type(v) then doc.options[k] = v end
    end
    if type(doc.characters) ~= "table" then doc.characters = {} end
    if not State.isDoc(doc) then return nil, "not a document" end
    return doc
end

function State.chunks(b64, revision)
    local out, n = {}, math.ceil(#b64 / State.CHUNK)
    if n == 0 then n = 1 end
    for i = 1, n do
        out[i] = string.format("cc1:%d:%d/%d:%s", revision, i, n, b64:sub((i - 1) * State.CHUNK + 1, i * State.CHUNK))
    end
    out[n + 1] = string.format("cc1end:%d:%d", revision, n)
    return out
end

function State.fromChunks(list)
    if type(list) ~= "table" or #list == 0 then return nil, "no chunks" end
    local sentinelRev, count, sentinels = nil, nil, 0
    for _, s in ipairs(list) do
        if type(s) == "string" and s:sub(1, 7) == "cc1end:" then
            sentinels = sentinels + 1
            local r, n = s:match("^cc1end:(%d+):(%d+)$")
            if not r then return nil, "bad sentinel" end
            sentinelRev, count = tonumber(r), tonumber(n)
        end
    end
    if sentinels == 0 then return nil, "no sentinel" end
    if sentinels > 1 then return nil, "two sentinels" end
    if count == 0 then return nil, "no chunks" end
    local parts, seen = {}, 0
    for _, s in ipairs(list) do
        if type(s) == "string" then
            local r, i, n, piece = s:match("^cc1:(%d+):(%d+)/(%d+):([%w%+/=]*)$")
            if r then
                if tonumber(r) ~= sentinelRev then return nil, "mixed revisions" end
                if tonumber(n) ~= count then return nil, "count disagrees" end
                i = tonumber(i)
                if i < 1 or i > count then return nil, "part out of range" end
                if not parts[i] then seen = seen + 1 end
                parts[i] = piece
            elseif not s:match("^cc1end:") then
                return nil, "stray string"
            end
        end
    end
    if seen ~= count then return nil, "missing parts" end
    return table.concat(parts, "", 1, count), sentinelRev
end

function State.choose(candidates)
    local best, name
    for _, c in ipairs(candidates) do
        if State.isDoc(c.doc) and (best == nil or c.doc.revision > best.revision) then best, name = c.doc, c.name end
    end
    if not best then return State.new(), "new" end
    return best, name
end

function State.panel(doc, charKey, name)
    local ch = doc.characters[charKey]
    return ch and ch.panels and ch.panels[name] or nil
end

function State.setPanel(doc, charKey, name, fields)
    local ch = doc.characters[charKey]
    if not ch then ch = { panels = {} }; doc.characters[charKey] = ch end
    ch.panels = ch.panels or {}
    local p = ch.panels[name] or {}
    for k, v in pairs(fields) do p[k] = v end
    ch.panels[name] = p
    return p
end

if ns then ns.State = State end
return State
