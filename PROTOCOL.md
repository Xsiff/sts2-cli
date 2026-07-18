# STS2 CLI Protocol

This document describes the JSON protocol implemented by `src/Sts2Headless/Program.cs` and `src/Sts2Headless/simulation/RunSimulator*.cs`.

The simulator is line-oriented:

- send one JSON object per line on `stdin`
- receive one JSON object per line on `stdout`

All response keys are serialized in `snake_case`.

## Session Lifecycle

Start the simulator:

```bash
dotnet run --project src/Sts2Headless/Sts2Headless.csproj
```

On startup it emits:

```json
{"type":"ready","version":"0.2.0"}
```

After that, send one command object per line.

## Top-Level Commands

## `start_run`

Start a new run.

Request:

```json
{
  "cmd": "start_run",
  "character": "Ironclad",
  "ascension": 0,
  "seed": "test"
}
```

Fields:

- `character`: optional string, defaults to `"Ironclad"`
- `ascension`: optional integer, defaults to `0`
- `seed`: optional string; if omitted, a generated seed is used

Supported `character` values:

- `Ironclad`
- `Silent`
- `Defect`
- `Regent`
- `Necrobinder`

Response:

- usually a decision object such as `map_select`
- or `error`

## `action`

Execute an in-run action.

Request:

```json
{
  "cmd": "action",
  "action": "play_card",
  "args": {
    "card_index": 0,
    "target_index": 0
  }
}
```

Fields:

- `action`: required string
- `args`: optional object; meaning depends on `action`

Supported actions are documented in the Actions section below.

Response:

- usually the next decision object
- or `error`

## `load_save`

Load a serialized run save.

Request, inline JSON:

```json
{
  "cmd": "load_save",
  "json": "{...save json...}"
}
```

Request, from file:

```json
{
  "cmd": "load_save",
  "path": "/absolute/or/relative/path/save.json"
}
```

Fields:

- `json`: optional string containing the full save payload
- `path`: optional string path to a save file

Notes:

- at least one of `json` or `path` must be provided
- saves are schema-validated before loading

Response:

- usually a decision object
- or `error`

## `get_map`

Return the full current map state.

Request:

```json
{"cmd":"get_map"}
```

Response:

- `map`
- or `error`

## `set_player`

Mutate the current player state for testing/debugging.

Request:

```json
{
  "cmd": "set_player",
  "hp": 50,
  "max_hp": 80,
  "gold": 123,
  "relics": ["BURNING_BLOOD"],
  "deck": ["STRIKE_RED","DEFEND_RED","BASH"],
  "potions": ["FIRE_POTION", "WEAK_POTION"]
}
```

Supported fields:

- `hp`: integer
- `max_hp`: integer
- `gold`: integer
- `relics`: array of relic entry IDs
- `deck`: array of card entry IDs
- `potions`: array of potion entry IDs

Response:

```json
{
  "type": "ok",
  "player": { "...player summary..." : "..." }
}
```

## `enter_room`

Force entry into a room, mainly for testing/debugging.

Request:

```json
{
  "cmd": "enter_room",
  "type": "combat",
  "encounter": "SHRINKER_BEETLE_WEAK"
}
```

Fields:

- `type`: required string
- `encounter`: required for combat-like rooms if you want a specific encounter
- `event`: required for `type = "event"`

Supported room `type` values:

- `combat`
- `monster`
- `elite`
- `shop`
- `rest`
- `rest_site`
- `event`
- `treasure`

Notes:

- for `combat` / `monster` / `elite`, `encounter` defaults to `SHRINKER_BEETLE_WEAK`
- for `event`, `event` is required and must be an event entry ID

Response:

- usually a decision object
- or `error`

## `set_draw_order`

Reorder the current combat draw pile.

Request:

```json
{
  "cmd": "set_draw_order",
  "cards": ["BASH", "STRIKE_RED", "DEFEND_RED"]
}
```

Fields:

- `cards`: ordered array of card entry IDs

Notes:

- only works during combat
- unspecified cards remain after the listed cards in their existing relative order

Response:

```json
{
  "type": "ok",
  "draw_pile_count": 12,
  "top_cards": ["Bash", "Strike", "Defend"]
}
```

## `write_continue_save`

Write a save checkpoint to disk.

Request:

```json
{
  "cmd": "write_continue_save",
  "path": "saves/run1.json"
}
```

Fields:

- `path`: required string output path

Response:

- `save_result`
- or `error`

## `quit`

Terminate the session.

Request without saving:

```json
{"cmd":"quit"}
```

Request with save:

```json
{
  "cmd": "quit",
  "path": "saves/run1.json"
}
```

Fields:

- `path`: optional string; if present, the simulator tries to save before quitting

Response:

- `quit_result`
- or `save_error`

After a `quit_result`, the process exits.

## Actions

These are sent through:

```json
{"cmd":"action","action":"...","args":{...}}
```

## `select_map_node`

Move to a map node.

Args:

- `col`: required integer
- `row`: required integer

Example:

```json
{"cmd":"action","action":"select_map_node","args":{"col":3,"row":1}}
```

## `play_card`

Play a card from hand.

Args:

- `card_index`: required integer
- `target_index`: optional integer, used for `AnyEnemy` cards

Example:

```json
{"cmd":"action","action":"play_card","args":{"card_index":0,"target_index":0}}
```

Notes:

- if `target_index` is omitted for an enemy-targeted card, the first living enemy is auto-selected

## `end_turn`

End the current combat turn.

Args: none

Example:

```json
{"cmd":"action","action":"end_turn"}
```

## `choose_option`

Choose an event or rest-site option.

Args:

- `option_index`: required integer

Example:

```json
{"cmd":"action","action":"choose_option","args":{"option_index":1}}
```

## `select_card_reward`

Pick a card from a reward screen.

Args:

- `card_index`: required integer

Example:

```json
{"cmd":"action","action":"select_card_reward","args":{"card_index":2}}
```

Notes:

- used for both normal combat rewards and event-driven card rewards

## `skip_card_reward`

Skip the current card reward.

Args: none

Example:

```json
{"cmd":"action","action":"skip_card_reward"}
```

## `buy_card`

Buy a card in a shop.

Args:

- `card_index`: required integer

## `buy_relic`

Buy a relic in a shop.

Args:

- `relic_index`: required integer

## `buy_potion`

Buy a potion in a shop.

Args:

- `potion_index`: required integer

## `remove_card`

Buy card removal in a shop.

Args: none

Notes:

- often followed by a `card_select` decision

## `select_bundle`

Pick one bundle when the game offers bundles/packs.

Args:

- `bundle_index`: required integer

## `select_cards`

Resolve a pending card-selection prompt.

Args:

- `indices`: required string, comma-separated card indices such as `"0,2"` or `"1"`

Example:

```json
{"cmd":"action","action":"select_cards","args":{"indices":"0,2"}}
```

Notes:

- used for upgrade/remove/transform and similar effects

## `skip_select`

Skip an optional card-selection prompt.

Args: none

## `use_potion`

Use a potion.

Args:

- `potion_index`: required integer
- `target_index`: optional integer for enemy-targeted potions

Notes:

- self-targeting potions ignore `target_index`
- if an enemy-targeted potion omits `target_index`, the first living enemy is auto-selected

## `discard_potion`

Discard a potion.

Args:

- `potion_index`: required integer

## `leave_room`

Try to leave the current non-combat room.

Args: none

## `proceed`

Proceed from the current terminal state.

Args: none

Notes:

- used after rewards or boss transitions

## Response Types

## `ready`

Emitted once at startup.

Example:

```json
{"type":"ready","version":"0.2.0"}
```

## `decision`

The main state response type.

Every decision response includes:

- `type = "decision"`
- `decision`: the decision subtype
- usually `context`
- often `player`

Supported `decision` values:

- `map_select`
- `combat_play`
- `card_reward`
- `card_select`
- `bundle_select`
- `event_choice`
- `rest_site`
- `shop`
- `game_over`
- `unknown`

### Common `context`

Many decisions include:

```json
{
  "act": 1,
  "act_name": "Overgrowth",
  "floor": 0,
  "room_type": "Map"
}
```

It may also include:

- `boss`: `{ "id": "...", "name": "..." }`

### `map_select`

Fields:

- `choices`: array of map nodes
- `player`
- `act`
- `act_name`
- `floor`

Each choice contains:

- `col`
- `row`
- `type`

### `combat_play`

Fields:

- `round`
- `energy`
- `max_energy`
- `hand`
- `enemies`
- `player`
- `player_powers`
- `draw_pile_count`
- `discard_pile_count`
- `deck_state`

Possible extra fields:

- `orbs`
- `orb_slots`
- `stars`
- `osty`

Each hand/deck card may include:

- `index`
- `id`
- `name`
- `cost`
- `type`
- `rarity`
- `target_type`
- `upgraded`
- `description`
- `stats`
- `keywords`
- `can_play` for hand cards
- `star_cost` when relevant
- `enchantment`
- `enchantment_amount`
- `affliction`
- `affliction_amount`

Each enemy may include:

- `index`
- `name`
- `hp`
- `max_hp`
- `block`
- `intents`
- `intends_attack`
- `powers`

`deck_state` contains:

- `hand_count`
- `draw_pile_count`
- `discard_pile_count`
- `exhaust_pile_count`
- `hand`
- `draw_pile`
- `discard_pile`
- `exhaust_pile`

### `card_reward`

Fields:

- `cards`
- `can_skip`
- `player`

Possible extra fields:

- `gold_earned`
- `from_event`

Each card may include:

- `index`
- `id`
- `name`
- `cost`
- `type`
- `rarity`
- `description`
- `stats`
- `keywords`
- `after_upgrade`

### `card_select`

Fields:

- `cards`
- `min_select`
- `max_select`
- `player`

### `bundle_select`

Fields:

- `bundles`
- `player`

Each bundle contains:

- `index`
- `cards`

### `event_choice`

Fields:

- `event_name`
- `description`
- `options`
- `player`

Each option may include:

- `index`
- `title`
- `description`
- `text_key`
- `is_locked`
- `vars`

### `rest_site`

Fields:

- `options`
- `player`

Each option contains:

- `index`
- `option_id`
- `name`
- `is_enabled`

### `shop`

Fields:

- `cards`
- `relics`
- `potions`
- `card_removal_cost`
- `player`

Shop card entries may include:

- `index`
- `name`
- `type`
- `rarity`
- `card_cost`
- `description`
- `stats`
- `keywords`
- `after_upgrade`
- `cost`
- `is_stocked`
- `on_sale`

Shop relic/potion entries include:

- `index`
- `name`
- `description`
- `cost`
- `is_stocked`

### `game_over`

Fields:

- `victory`: boolean
- `player`
- `act`
- `floor`

### `unknown`

Fallback shape:

- `room_type`
- `message`

## `ok`

Returned by helper/debug commands such as `set_player` and `set_draw_order`.

Known shapes:

```json
{"type":"ok","player":{...}}
```

```json
{"type":"ok","draw_pile_count":12,"top_cards":["Bash","Strike","Defend"]}
```

## `map`

Returned by `get_map`.

Fields:

- `type = "map"`
- `context`
- `rows`
- `boss`
- `current_coord`

Each row contains nodes with:

- `col`
- `row`
- `type`
- `children`
- `visited`
- `current`

`boss` includes:

- `col`
- `row`
- `type`
- optionally `id`
- optionally `name`

## `save_result`

Returned by `write_continue_save` and also nested inside `quit_result`.

Fields:

- `type = "save_result"`
- `success`
- `path`
- `size`
- `room_type`

## `quit_result`

Returned by `quit`.

Fields:

- `type = "quit_result"`
- `success`
- `save`: `null` or a nested `save_result`

## `save_error`

Returned by `quit` when a requested save fails.

Fields:

- `type = "save_error"`
- `save`: nested error/save result object

## `error`

Generic error response.

Common fields:

- `type = "error"`
- `message`

Some errors also include:

- `stack_trace`

Examples:

```json
{"type":"error","message":"Unknown command: foo"}
```

```json
{"type":"error","message":"play_card requires 'card_index'"}
```

## Notes and Quirks

- Numeric values inside `action.args` are parsed as integers.
- `select_cards.args.indices` is a comma-separated string, not a JSON array.
- `load_save` may reject saves with schema mismatches.
- `quit` with a `path` first attempts a save; if that save fails, the simulator does not clean up and returns `save_error`.
- Some debug/testing commands like `set_player`, `enter_room`, and `set_draw_order` are intended for testing harnesses and may bypass normal game flow.

## Minimal Example Session

```json
{"cmd":"start_run","character":"Ironclad","seed":"test","ascension":0}
{"cmd":"action","action":"select_map_node","args":{"col":3,"row":1}}
{"cmd":"action","action":"play_card","args":{"card_index":0,"target_index":0}}
{"cmd":"action","action":"end_turn"}
{"cmd":"action","action":"skip_card_reward"}
{"cmd":"quit"}
```
