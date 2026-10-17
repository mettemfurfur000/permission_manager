using CounterStrikeSharp.API.Core;

namespace permission_manager.src;

/// <summary>
/// Plugin level configuration. CounterStrikeSharp generates
/// <c>configs/plugins/permission_manager/permission_manager.json</c> from this class on
/// first launch. Storage paths default to the folder holding that file.
/// </summary>
public class PermissionManagerConfig : IBasePluginConfig
{
    /// <summary>
    /// Folder holding <c>permissions.json</c> and <c>commands.json</c>.
    /// Leave empty to use <c>addons/counterstrikesharp/configs/plugins/permission_manager</c>.
    /// </summary>
    public string DataDirectory { get; set; } = string.Empty;

    /// <summary>Group applied to every user, including users with no explicit groups.</summary>
    public string DefaultGroup { get; set; } = "default";

    /// <summary>How long to wait between disk writes. Writes are batched, not made per change.</summary>
    public float SaveDebounceSeconds { get; set; } = 5f;

    // ------------------------------------------------------------ access policy

    /// <summary>Server console / RCON passes every node check. Turn off to gate scripted commands too.</summary>
    public bool AllowConsole { get; set; } = true;

    /// <summary>Bots pass every node check.</summary>
    public bool AllowBots { get; set; } = true;

    /// <summary>
    /// What a node check answers when no pattern matched anywhere. <c>false</c> means
    /// "unmapped means allowed", which is why enabling this plugin can never lock the
    /// server out. Set <c>true</c> for a strict LuckPerms style whitelist.
    /// </summary>
    public bool DefaultDeny { get; set; } = false;

    /// <summary>Let the console run <c>css_perm_*</c> even when <see cref="AllowConsole"/> is off.</summary>
    public bool AllowConsoleAdmin { get; set; } = true;

    /// <summary>
    /// Also honour CounterStrikeSharp's own admin flags (<c>@css/root</c>) when checking
    /// nodes. Keeps server owners working without having to grant them anything here.
    /// </summary>
    public bool UseCounterStrikeSharpAdmins { get; set; } = true;

    // ---------------------------------------------------------- command gating

    /// <summary>
    /// Opt in to gating every command listed in <c>commands.json</c>, including commands
    /// owned by other plugins. Off by default: with it off, <c>commands.json</c> is left
    /// alone and only gates registered through <c>RegisterGatedCommand</c> are enforced.
    /// Turning it on installs a global command listener in front of every command.
    /// </summary>
    public bool EnableCommandMap { get; set; } = false;

    /// <summary>
    /// Message sent to the player whose command was blocked. Supports <c>{node}</c>,
    /// <c>{command}</c>, <c>{group}</c>, <c>{player}</c> and the colour tags <c>{red}</c>,
    /// <c>{lightred}</c> (an alias for <c>{red}</c>), <c>{green}</c>, <c>{yellow}</c>,
    /// <c>{lightblue}</c>, <c>{grey}</c>, <c>{default}</c> and friends. See <c>ChatFormat</c>
    /// for the full list.
    /// </summary>
    public string DenialMessage { get; set; } = "{red}You do not have permission to use {command} (needs {node}).";

    /// <summary>Send <see cref="DenialMessage"/> to chat.</summary>
    public bool DenyToChat { get; set; } = true;

    /// <summary>Print <see cref="DenialMessage"/> to the server console as well.</summary>
    public bool DenyToConsole { get; set; } = true;

    /// <summary>Log every denial to the plugin log. Chat already tells the player.</summary>
    public bool LogDenials { get; set; } = false;

    /// <summary>CounterStrikeSharp rewrites the config file when this changes.</summary>
    public int Version { get; set; } = 1;
}