#!/usr/bin/env python3
"""
sts2-cli launcher: start a new game or load a save, then hand off to python/play.py.
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
from datetime import datetime

ROOT = os.path.dirname(os.path.abspath(__file__))
PLAY_PY = os.path.join(ROOT, "python", "play.py")
SAVE_DIR = os.path.join(ROOT, "saves")

CLI_CHARACTERS = ["Ironclad", "Silent", "Defect", "Regent", "Necrobinder"]


def _prompt_line(prompt: str) -> str:
    try:
        return input(prompt).strip()
    except (EOFError, KeyboardInterrupt):
        print()
        raise SystemExit(0) from None


def _pick_int(prompt: str, lo: int, hi: int, default: int | None = None) -> int:
    while True:
        raw = _prompt_line(prompt)
        if not raw and default is not None:
            return default
        try:
            value = int(raw)
        except ValueError:
            print(f"  Enter an integer between {lo} and {hi}.")
            continue
        if lo <= value <= hi:
            return value
        print(f"  Enter an integer between {lo} and {hi}.")


def _collect_save_entries() -> list[dict]:
    if not os.path.isdir(SAVE_DIR):
        return []
    entries: list[dict] = []
    for name in os.listdir(SAVE_DIR):
        path = os.path.join(SAVE_DIR, name)
        if not os.path.isfile(path):
            continue
        st = os.stat(path)
        if name.endswith(".json"):
            try:
                with open(path, encoding="utf-8") as f:
                    data = json.load(f)
                entries.append(
                    {
                        "kind": "replay",
                        "path": path,
                        "name": name,
                        "mtime": st.st_mtime,
                        "character": data.get("character", "?"),
                        "seed": data.get("seed", "?"),
                        "actions": len(data.get("actions", [])),
                    }
                )
            except (json.JSONDecodeError, OSError):
                pass
        elif name.endswith(".save"):
            try:
                with open(path, encoding="utf-8") as f:
                    data = json.load(f)
                seed = data.get("rng", {}).get("seed", "?")
                ascension = data.get("ascension", 0)
                character_id = "?"
                players = data.get("players", [])
                if players:
                    character_id = players[0].get("character_id", "?")
                entries.append(
                    {
                        "kind": "native",
                        "path": path,
                        "name": name,
                        "mtime": st.st_mtime,
                        "seed": seed,
                        "ascension": ascension,
                        "character_id": character_id,
                    }
                )
            except (json.JSONDecodeError, OSError):
                entries.append(
                    {
                        "kind": "native",
                        "path": path,
                        "name": name,
                        "mtime": st.st_mtime,
                        "broken": True,
                    }
                )
    entries.sort(key=lambda item: -item["mtime"])
    return entries


def _format_entry(entry: dict) -> str:
    ts = datetime.fromtimestamp(entry["mtime"]).strftime("%Y-%m-%d %H:%M")
    if entry["kind"] == "replay":
        return (
            f"{entry['name']}  |  {entry.get('character', '?')}  |  "
            f"Seed {entry['seed']}  |  {entry['actions']} actions  |  {ts}"
        )
    if entry.get("broken"):
        return f"{entry['name']}  |  unreadable or corrupted file  |  {ts}"
    return (
        f"{entry['name']}  |  {entry.get('character_id', '?')}  |  "
        f"Ascension {entry['ascension']}  |  Seed {entry['seed']}  |  {ts}"
    )


def _run_play(args: list[str]) -> int:
    cmd = [sys.executable, PLAY_PY, *args]
    result = subprocess.run(cmd, cwd=ROOT)
    return result.returncode


def _menu_new_game() -> None:
    print("\n-- Choose Character --")
    for index, cli_name in enumerate(CLI_CHARACTERS):
        print(f"  {index}  {cli_name}")
    idx = _pick_int("\nEnter a number (0-4): ", 0, 4)
    character = CLI_CHARACTERS[idx]
    ascension = _pick_int(
        "\nAscension 0-10 (press Enter for 0): ",
        0,
        10,
        default=0,
    )
    print(f"\nStarting: {character}  |  Ascension {ascension}\n")
    _run_play(["--character", character, "--ascension", str(ascension)])


def _menu_load_save() -> None:
    entries = _collect_save_entries()
    if not entries:
        print("\n  No .save or .json files found in saves/.\n")
        return

    print("\n-- Load Save (newest first) --")
    print("  [Continue] = native .save")
    print("  [Replay]   = action replay .json\n")
    for index, entry in enumerate(entries, 1):
        tag = "Continue" if entry["kind"] == "native" else "Replay"
        print(f"  {index:2}  [{tag}]  {_format_entry(entry)}")
    print("\n  0  Back")
    choice = _pick_int("\nEnter a number: ", 0, len(entries))
    if choice == 0:
        return
    selected = entries[choice - 1]
    rel = os.path.relpath(selected["path"], ROOT)
    if selected["kind"] == "native":
        print(f"\nLoading native save: {rel}\n")
        _run_play(["--continue", rel])
    else:
        print(f"\nLoading replay save: {rel}\n")
        _run_play(["--load", rel])


def _main_interactive() -> None:
    sys.path.insert(0, os.path.join(ROOT, "python"))
    import play as play_mod  # noqa: PLC0415

    play_mod.ensure_setup()

    while True:
        print(
            """
╔══════════════════════════════════════╗
║       Slay the Spire 2 CLI           ║
╚══════════════════════════════════════╝

  1  New Game
  2  Load Save
  0  Quit
"""
        )
        choice = _prompt_line("Choose (0-2): ").lower()
        if choice in ("0", "q", "quit", "exit", ""):
            print("Goodbye.")
            break
        if choice == "1":
            _menu_new_game()
        elif choice == "2":
            _menu_load_save()
        else:
            print("  Invalid input. Enter 0, 1, or 2.")


def main() -> None:
    argparse.ArgumentParser(description="sts2-cli interactive launcher").parse_args()
    _main_interactive()


if __name__ == "__main__":
    main()
