"""Tests for English output."""



class TestEnglish:
    def test_explicit_english_returns_english(self, game):
        state = game.start(seed="english_explicit1")
        player = state.get("player", {})
        deck = player.get("deck", [])
        assert any("Strike" in str(c.get("name", "")) for c in deck), (
            f"Expected English card names, got: {[c['name'] for c in deck[:3]]}"
        )

    def test_default_output_is_english(self, game):
        """Without extra output options, names should be English."""
        state = game.send({"cmd": "start_run", "character": "Ironclad", "seed": "english_default1"})
        player = state.get("player", {})
        deck = player.get("deck", [])
        names = [c.get("name", "") for c in deck]
        assert any("Strike" in str(name) for name in names), (
            f"Expected English by default, got: {names[:3]}"
        )
