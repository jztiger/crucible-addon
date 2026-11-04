# Crucible

An auction house tool for World of Warcraft: Forever, for a small group of friends. It was called Tallybook until
October 2026; `/tally` now only prints the new name.

- **Lowest now** and **Market value** on item tooltips, from the group's scans, with how many sell a day once there
  is enough history.
- **Craft cost** for anything you can craft - every mat with its price (vendor price when a vendor sells it) - and
  the **Profit** (or a red **Profit** with a minus sign) of crafting it to sell, after the auction house's cut.
- The cost or profit next to every recipe in the profession window, and a **Profit** panel for a whole profession.
- Posting help: a suggested price on the Sell tab, and an **Undercut?** list of your own auctions that have been
  undercut. You press the game's own Post and Cancel Auction buttons yourself, every time.
- A shopping list planned on the group's website, with **Search All** to price every mat in one go.
- Your own sales and returns, read from your mailbox, with a **Ledger** window of your last 30 days.
- A recorder for the group's own quest map, and your character's sheet for the group's DPS simulator.

## What it does and does not do

- Inside the game it may read your auction house scans, your mailbox, your professions and recipes, your bags, your
  own quest log, and your own characters (name, level, talents, spells, pet, gear, stats, and where you are when you
  talk to a quest giver, trainer, vendor, flight master or innkeeper). It never reads chat, other players' names or
  positions, your account name, or anything outside the game's own addon interface.
- It **never bids and never buys**, and it has no code that could.
- It **never posts or cancels anything on its own.** The Sell tab gets a suggested price, which you can change; the
  auction is posted only when you press the game's Post button, one auction per press. An undercut auction is
  cancelled only when you click it and then press the game's Cancel Auction button, confirmed in the game's own popup.
- Every scan starts from you: opening the auction house runs one quick scan (see "At the auction house" for the
  rule and the off switch), everything else is a button or a command. Nothing runs on a timer.
- It is free. There is nothing to buy and no way to pay.

`shared/risk-notice.txt` is the full statement of what the addon and the tray app may read, and the risk. Features
inside that scope are listed in `shared/CHANGELOG.txt`.

## Install

Read `shared/risk-notice.txt` first. Then copy the `Crucible` folder into `Interface\AddOns\` inside your Forever
folder (`_forever_`, the one that holds `Wow.exe` for Forever; the beta's `_classic_beta_` was shut down on
2026-11-03), start the game, and type `/crucible`. Members of our group do not copy anything: the tray app
(below) installs the addon and keeps it up to date.

## At the auction house

A strip appears beside the auction house window:

- **Full scan** runs a full market snapshot (15 minute cooldown); **Quick scan** runs a quicker price scan.
- **Stop** shows up only while a scan is running, and ends it right there.
- An **auto-scan** checkbox, on by default: while it's ticked, opening the auction house runs one Quick scan by
  itself - once per visit, and never while the newest scan anyone in the group has made is under 30 minutes old,
  so a handful of people visiting an auctioneer make a handful of scans a day, not dozens. The scan may start a
  second or two after the house opens: the game's own auction house window usually spends the client's one query
  allowance first, so the scan waits for the client to say it is free - once. Untick it and the automatic scan
  stops; the buttons keep working either way.
- One status line says what's happening: `waiting for the house to accept a query`, `scanning ... page 3`,
  `last scan 6h ago`, `the house is busy - try the button in a moment` (then nothing is tried again until you close
  and reopen the house), or `no prices yet - press Quick scan` the first time. It adds `- press Sync to upload`
  whenever there is something new to send.
- **Sync** uploads what you have learned - it reloads the UI, which is what writes the addon's saved file.
- **Take gold and items when I open a mail**, off by default: see "Your mailbox".
- **Undercut by 1c**: the rule the Sell tab's suggestion follows. A click steps it (1c, then 1% to 10%).
- **Undercut?**, shown while the game's Auctions tab is open: see "Selling".
- **Ledger** opens your last 30 days of sales and returns, as the group's server last knew them.
- The **shopping list** panel under the strip lists the mats of a batch you planned on the website, with how many
  are in your bags and your bank. A click on a row searches that item; **Search All** runs one search per row, one
  at a time, and **Stop** ends it.

## Selling

When you pick an item on the Sell tab, one Crucible line under the price shows the group's lowest price and
market value for it, and the price field gets a suggestion: the lowest less the undercut rule, never under what a
vendor pays plus the house's cut. No fresh lowest means the market value; neither means `no group price yet` and
the game's default stands. A price you typed is never overwritten. Posting is the game's own Post button.

**Undercut?** makes one query for your own auctions and then one search per distinct item among them, and lists
the auctions whose price per unit is above the lowest listing that is not yours - it reads the search result's own
"this one is yours" flag, never a name. Click a row, then press the game's Cancel Auction button and confirm in the
game's popup; the row greys out. Nothing is queried again on its own, after a cancel or after the scan.

## Professions

Opening a profession window reads its recipes, every time, and a line beside the **Profit** button confirms it:
`✓ learned 41 recipes, 3 new`, with its own **Sync** button. The **Profit** panel lists the whole profession
sorted by profit, with cost, what it sells for, and profit or loss per recipe. Profit is worked out from the market
value when there is one, falling back to Lowest now when there isn't yet enough history - the same order the tooltip
uses, so hovering an item and opening the panel never disagree.

## Your mailbox

When you open your mailbox, Crucible reads what it's already showing you: for a sold auction, the item, the count,
the price you received, the deposit and the auction house's cut; for one that expired or was cancelled, the item and
the count. It also reads how many days the mail has left, only so it can tell the same mail apart from a new one
later. It reads while the mailbox is open, and only because you opened it; nothing goes anywhere until you press
**Sync** (on the strip, on the mail window itself, or `/crucible reload`). It never reads or stores who bought
anything, who sent a mail, or any other player's name. A mail titled `Auction expired: <name>` or
`Auction canceled: <name>` (either spelling) is filed as a returned auction under that name, on your own Sales
page only - it never enters the group's figures.

**Take gold and items when I open a mail** (the switch on the strip, off by default): with it on, clicking a mail
open makes one call, the game's own take of that whole mail - the same thing the game's shift-click on a mail
does - and nothing else. Never another mail, never a loop over the inbox, never on a timer. C.O.D. mail is never
taken from, a GM's mail is left for you, and a mail whose attachments would not fit in your bags is not taken at
all. The **Open All** button is the game's own; Crucible only reads what it opens.

With the tray app and a membership, your own sales and returns show up in detail on the website - to you alone.
The group only ever sees a median price and how many sold, per item, never whose. In game, the tooltip's
`Market value` line gains a sale rate (`Market value: 16g 95s · sells ~3.2 a day`).

## Your characters and your quests

For the group's DPS simulator, the addon keeps one snapshot of the logged-in character - name, class, race, level,
gear, the equippable items in your bags, spells, talents, pet, professions and the stats the game shows - and
sends it with your other data. Only you see your characters on the website; switch it off with "Use the simulator"
on the website's Your PCs page.

For the group's own quest map, the addon notes quest givers, objectives, turn-ins, trainers, vendors, flight
masters and innkeepers with your map position, each on your own action - reading a quest offer, accepting, an
objective ticking, turning in, opening a trainer. Switch it off with "Record my quests" on the same page. Nothing
comes back into the game from it yet.

## Commands

`/crucible` or `/cru`:

| Command | What it does |
|---|---|
| `/crucible` | status |
| `/crucible browse` | price scan, about 6 seconds (the strip's Quick scan) |
| `/crucible scan` | full market snapshot, 15 minute cooldown (the strip's Full scan) |
| `/crucible profit` | the profit panel for the profession you have open |
| `/crucible basket <N>` | prices `N` crafts of the recipe you last clicked in the Profit panel (or shift-click an item) against real auction house listings |
| `/crucible shopping` | the shopping list planned on the website |
| `/crucible ledger` | your last 30 days of sales and returns; a second one closes it |
| `/crucible list profit` or `/crucible list cost` | whether the profession window's recipe list shows profit or plain cost next to each recipe |
| `/crucible selftest` | a small synthetic scan for checking the upload path - made-up prices, labelled so they can never mix into a real market |
| `/crucible reload` | reloads the UI so the game writes the addon's saved file |
| `/crucible debug` | timing and progress detail in chat, on or off for this session |

Known limit of the client, seen through the whole beta: the game does not read addon saved data back after a
restart, so a fresh session needs one scan - automatic on opening the auction house, or by hand. `Data.lua` is where
the group's prices, your learned recipes and vendor prices, your shopping list and your settings are handed back as
code; the copy here is empty, and the tray app fills it.

## The tray app (`tray/`)

Optional. A small Windows program (C#, .NET Framework 4.8) for members of our group: it sends the addon's saved
file to the group's private server and writes one `Data.lua`, made for you alone, back into the addon's folder,
so the game has the group's prices without scanning ("Bring data back" in its Settings). It reads `Crucible.lua`
and `Crucible.lua.bak` under `_forever_\WTF\Account\*\SavedVariables\` inside the World of Warcraft folder **you**
pick, and writes only inside the addon's own folder. It installs the addon and replaces it when a new version is
published ("Keep the Crucible addon installed and up to date" in Settings; "Reinstall the addon from GitHub" in
its menu puts the addon back from this repository without asking our server). It keeps itself up to date too:
only a build signed by the same publisher is ever taken, checked by Windows before it runs. Help > About Crucible
shows the versions installed and newest, the release notes and the notice. On the owner's install alone, and only
because the server asks for it there, it also sends the item rows of the game's own item cache (`DBCache.bin`);
nobody else's copy reads that file. It does not look at the game while it runs, except to span the window for
Crucible Canvas when you have turned that on. `shared/risk-notice.txt` is what every member reads before
downloading it. It is useless without a membership - the server is private - but the source is here so anyone can
read what it does.
Build: `dotnet build tray/Crucible.Tray -c Release`; tests: `dotnet test tray/Crucible.Tray.Tests`.

## Crucible Canvas (`CrucibleCanvas/`)

**Crucible Canvas** is a second, optional addon in this repository: it keeps the game and HUD on one screen and
opens the map, bags and panels on the other. The tray app spans the WoW window across the two screens you choose
(it moves and resizes that window and does nothing else to the game) and writes two small generated files into the
addon's folder, `Topology.lua` and `Recovery.lua`. It is off until you turn it on in the tray's Displays window.

## About this repository

This is the code of what our group is handed - the two addons, and the optional tray app - published so that anyone
can read it, as Blizzard's UI Add-On Development Policy requires of addons. It is developed elsewhere and mirrored
here per release, which is why the history is one commit per version; `shared/CHANGELOG.txt` says what each one
changed. Comments that mention design documents or rule numbers (C7, C11...) refer to notes kept with the
development copy.

Not affiliated with or endorsed by Blizzard Entertainment. World of Warcraft is a trademark or registered trademark of
Blizzard Entertainment, Inc.
