namespace GDShrapt.TestHelpers;

/// <summary>
/// Scene-signal fixture: player.tscn instanced in game.tscn, HUD callback, Timer, unresolved emitter scene.
/// Shared (linked) by Semantics.Tests and LSP.Tests.
/// </summary>
public static class GDSceneSignalFixture
{
    public const string PlayerGd =
        "class_name Player\nextends CharacterBody2D\n\nconst WALK_SPEED = 300.0\n\nfunc _physics_process(delta: float) -> void:\n\tvar s := WALK_SPEED\n\ttry_jump()\n\nfunc try_jump() -> void:\n\tpass\n" +
        "\nsignal coin_collected\n\nfunc collect() -> void:\n\tcoin_collected.emit()\n";

    public const string HudGd =
        "extends Control\n\nfunc _on_coin_collected() -> void:\n\tpass\n\nfunc bind(p: Player) -> void:\n\tp.coin_collected.connect(_on_coin_collected)\n";

    public const string PlayerTscn =
        "[gd_scene load_steps=2 format=3]\n\n[ext_resource type=\"Script\" path=\"res://player.gd\" id=\"1\"]\n\n[node name=\"Player\" type=\"CharacterBody2D\"]\nscript = ExtResource(\"1\")\n";

    // 0-based lines: 14 = coin_collected connection, 15 = timeout connection
    public const string GameTscn =
        "[gd_scene load_steps=4 format=3]\n\n" +
        "[ext_resource type=\"PackedScene\" path=\"res://player.tscn\" id=\"1\"]\n" +
        "[ext_resource type=\"Script\" path=\"res://hud.gd\" id=\"2\"]\n\n" +
        "[node name=\"Game\" type=\"Node\"]\n\n" +
        "[node name=\"Player\" parent=\".\" instance=ExtResource(\"1\")]\n\n" +
        "[node name=\"HUD\" type=\"Control\" parent=\".\"]\nscript = ExtResource(\"2\")\n\n" +
        "[node name=\"T\" type=\"Timer\" parent=\".\"]\n\n" +
        "[connection signal=\"coin_collected\" from=\"Player\" to=\"HUD\" method=\"_on_coin_collected\"]\n" +
        "[connection signal=\"timeout\" from=\"T\" to=\"HUD\" method=\"_on_coin_collected\"]\n";

    public const int GameCoinConnectionLine = 14;
    public const int GameTimeoutConnectionLine = 15;

    // 0-based line 4: emitter node "Missing" does not exist → type unknown
    public const string OtherTscn =
        "[gd_scene format=3]\n\n[node name=\"Other\" type=\"Node\"]\n\n" +
        "[connection signal=\"coin_collected\" from=\"Missing\" to=\".\" method=\"noop\"]\n";

    public const int OtherConnectionLine = 4;

    public static (string, string)[] Files =>
    [
        ("player.gd", PlayerGd), ("hud.gd", HudGd), ("player.tscn", PlayerTscn),
        ("game.tscn", GameTscn), ("other.tscn", OtherTscn)
    ];
}
