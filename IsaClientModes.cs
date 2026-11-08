using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;

namespace IsaClientModes;

public enum IsaMode : byte
{
    None = 0,
    Detect = 1,
    NoCheats = 2,
    Vanilla = 3
}

public enum PopupStyle : byte
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Danger = 3
}

public sealed class WelcomeCard
{
    public bool Enabled { get; set; } = false;
    public string Title { get; set; } = "Welcome!";
    public string Body { get; set; } = "- Read /rules\n- Have fun";
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public PopupStyle Style { get; set; } = PopupStyle.Info;
    public int Icon { get; set; } = 0;
    public int Seconds { get; set; } = 0;
}

public sealed class ModesConfig
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public IsaMode Mode { get; set; } = IsaMode.Detect;

    public string Message { get; set; } = "";

    public string BypassPermission { get; set; } = "isaclient.bypass";

    public Dictionary<string, string> GroupModes { get; set; } = new();

    public bool KickIfNotConfirmed { get; set; } = false;

    public int AckSeconds { get; set; } = 10;

    public bool TellStaff { get; set; } = true;

    public WelcomeCard Welcome { get; set; } = new();
}

[ApiVersion(2, 1)]
public sealed class IsaClientModesPlugin : TerrariaPlugin
{
    public override string Name => "IsaClientModes";
    public override string Author => "IsaClient";
    public override string Description => "Detects IsaClient players and limits what the client can do on this server.";
    public override Version Version => new(1, 0, 0);

    private const ushort Module = 0xC1A5;
    private static readonly byte[] Magic = { 0x49, 0x53, 0x4D, 0x01 };
    private const byte Ask = 1, SetMode = 2, Popup = 3, Hello = 10, Ack = 11;

    private const string AdminPermission = "isaclient.admin";

    private sealed class Seen
    {
        public string Version = "";
        public IsaMode Sent;
        public IsaMode? Confirmed;
        public DateTime SentAt;
    }

    private readonly Seen[] _seen = new Seen[Main.maxPlayers];
    private readonly DateTime?[] _askAgainAt = new DateTime?[Main.maxPlayers];
    private readonly int[] _asks = new int[Main.maxPlayers];
    private readonly Dictionary<string, IsaMode> _playerModes = new(StringComparer.OrdinalIgnoreCase);
    private ModesConfig _config = new();

    private static string ConfigPath => Path.Combine(TShock.SavePath, "IsaClientModes.json");

    private static IsaClientModesPlugin _instance;

    #region API for other plugins

    public static event Action<TSPlayer, string> IsaClientJoined;

    public static bool IsIsaClient(TSPlayer player) => SeenOf(player) != null;

    public static string ClientVersion(TSPlayer player) => SeenOf(player)?.Version;

    public static IsaMode ModeOf(TSPlayer player) => SeenOf(player)?.Sent ?? IsaMode.None;

    public static bool ShowPopup(TSPlayer player, string title, string body, PopupStyle style = PopupStyle.Info, int icon = 0, int seconds = 0)
    {
        if (player == null || !player.Active || !IsIsaClient(player))
        {
            return false;
        }
        title ??= "";
        body ??= "";
        player.SendRawData(Packet(Popup, w =>
        {
            w.Write(title.Length > 80 ? title[..80] : title);
            w.Write(body.Length > 700 ? body[..700] : body);
            w.Write((byte)style);
            w.Write((short)Math.Clamp(icon, 0, short.MaxValue));
            w.Write((ushort)Math.Clamp(seconds, 0, 600));
        }));
        return true;
    }

    public static int ShowPopupToAll(string title, string body, PopupStyle style = PopupStyle.Info, int icon = 0, int seconds = 0)
    {
        int count = 0;
        foreach (TSPlayer player in TShock.Players)
        {
            if (ShowPopup(player, title, body, style, icon, seconds))
            {
                count++;
            }
        }
        return count;
    }

    private static Seen SeenOf(TSPlayer player)
    {
        if (_instance == null || player == null || player.Index < 0 || player.Index >= Main.maxPlayers)
        {
            return null;
        }
        return _instance._seen[player.Index];
    }

    #endregion

    public IsaClientModesPlugin(Main game) : base(game)
    {
        _instance = this;
        Order = -10;
    }

    public override void Initialize()
    {
        LoadConfig();
        ServerApi.Hooks.NetGetData.Register(this, OnGetData, int.MaxValue);
        ServerApi.Hooks.NetGreetPlayer.Register(this, OnGreet);
        ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
        ServerApi.Hooks.GameUpdate.Register(this, OnUpdate);
        GeneralHooks.ReloadEvent += OnReload;
        Commands.ChatCommands.Add(new Command(AdminPermission, Cmd, "isaclient", "isamodes")
        {
            HelpText = "IsaClient players and modes: /isaclient list | mode <none|detect|nocheats|vanilla> [message] | set <player> <mode|default> | popup <player|all> <title> | <text> [| style [| icon [| seconds]]] | reload"
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ServerApi.Hooks.NetGetData.Deregister(this, OnGetData);
            ServerApi.Hooks.NetGreetPlayer.Deregister(this, OnGreet);
            ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
            ServerApi.Hooks.GameUpdate.Deregister(this, OnUpdate);
            GeneralHooks.ReloadEvent -= OnReload;
        }
        base.Dispose(disposing);
    }

    #region Config

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private void LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                _config = JsonSerializer.Deserialize<ModesConfig>(File.ReadAllText(ConfigPath), Json) ?? new ModesConfig();
            }
            SaveConfig();
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError("[IsaClientModes] could not read IsaClientModes.json: " + ex.Message);
        }
    }

    private void SaveConfig()
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(_config, Json));
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleError("[IsaClientModes] could not write IsaClientModes.json: " + ex.Message);
        }
    }

    private void OnReload(ReloadEventArgs e)
    {
        LoadConfig();
        SendModeToAll();
        e.Player?.SendSuccessMessage("[IsaClientModes] reloaded.");
    }

    private static bool TryParseMode(string text, out IsaMode mode)
    {
        mode = IsaMode.None;
        switch ((text ?? "").Trim().ToLowerInvariant())
        {
            case "none": case "off": case "0": mode = IsaMode.None; return true;
            case "detect": case "1": mode = IsaMode.Detect; return true;
            case "nocheats": case "no-cheats": case "cheats": case "2": mode = IsaMode.NoCheats; return true;
            case "vanilla": case "total": case "3": mode = IsaMode.Vanilla; return true;
        }
        return false;
    }

    private IsaMode ModeFor(TSPlayer player)
    {
        if (!string.IsNullOrEmpty(_config.BypassPermission) && player.HasPermission(_config.BypassPermission))
        {
            return IsaMode.None;
        }
        if (_playerModes.TryGetValue(player.Name, out IsaMode own))
        {
            return own;
        }
        if (player.Group != null && _config.GroupModes != null && _config.GroupModes.TryGetValue(player.Group.Name, out string text) && TryParseMode(text, out IsaMode group))
        {
            return group;
        }
        return _config.Mode;
    }

    #endregion

    #region Packets

    private static byte[] Packet(byte type, Action<BinaryWriter> body = null)
    {
        using MemoryStream stream = new();
        using (BinaryWriter w = new(stream, Encoding.UTF8, leaveOpen: true))
        {
            w.Write((ushort)0);
            w.Write((byte)82);
            w.Write(Module);
            w.Write(Magic);
            w.Write(type);
            body?.Invoke(w);
        }
        byte[] packet = stream.ToArray();
        BitConverter.GetBytes((ushort)packet.Length).CopyTo(packet, 0);
        return packet;
    }

    private void OnGetData(GetDataEventArgs e)
    {
        if (e.Handled || e.MsgID != PacketTypes.LoadNetModule || e.Length < 3)
        {
            return;
        }
        byte[] buffer = e.Msg.readBuffer;
        if (BitConverter.ToUInt16(buffer, e.Index) != Module)
        {
            return;
        }
        e.Handled = true;
        int who = e.Msg.whoAmI;
        TSPlayer player = (who >= 0 && who < Main.maxPlayers) ? TShock.Players[who] : null;
        if (player == null)
        {
            return;
        }
        try
        {
            using BinaryReader r = new(new MemoryStream(buffer, e.Index + 2, Math.Max(0, e.Length - 3)), Encoding.UTF8);
            for (int i = 0; i < Magic.Length; i++)
            {
                if (r.ReadByte() != Magic[i])
                {
                    return;
                }
            }
            byte type = r.ReadByte();
            if (type == Hello)
            {
                string version = r.ReadString();
                bool first = _seen[who] == null;
                _seen[who] = new Seen { Version = version.Length > 40 ? version[..40] : version };
                _askAgainAt[who] = null;
                SendMode(player);
                if (first)
                {
                    WelcomeCard welcome = _config.Welcome;
                    if (welcome != null && welcome.Enabled)
                    {
                        ShowPopup(player, welcome.Title, (welcome.Body ?? "").Replace("\\n", "\n"), welcome.Style, welcome.Icon, welcome.Seconds);
                    }
                    try
                    {
                        IsaClientJoined?.Invoke(player, _seen[who].Version);
                    }
                    catch (Exception ex)
                    {
                        TShock.Log.ConsoleError("[IsaClientModes] an IsaClientJoined handler failed: " + ex.Message);
                    }
                    string line = $"{player.Name} uses IsaClient {_seen[who].Version} (mode {_seen[who].Sent}).";
                    TShock.Log.ConsoleInfo("[IsaClientModes] " + line);
                    if (_config.TellStaff)
                    {
                        foreach (TSPlayer staff in TShock.Players)
                        {
                            if (staff != null && staff.Active && staff != player && staff.HasPermission(AdminPermission))
                            {
                                staff.SendInfoMessage("[IsaClient] " + line);
                            }
                        }
                    }
                }
            }
            else if (type == Ack && _seen[who] != null)
            {
                _seen[who].Confirmed = (IsaMode)Math.Min((byte)3, r.ReadByte());
            }
        }
        catch (Exception ex)
        {
            TShock.Log.ConsoleDebug("[IsaClientModes] bad packet from " + player.Name + ": " + ex.Message);
        }
    }

    private void SendMode(TSPlayer player)
    {
        Seen seen = _seen[player.Index];
        if (seen == null)
        {
            return;
        }
        IsaMode mode = ModeFor(player);
        seen.Sent = mode;
        seen.Confirmed = null;
        seen.SentAt = DateTime.UtcNow;
        string note = _config.Message ?? "";
        player.SendRawData(Packet(SetMode, w =>
        {
            w.Write((byte)mode);
            w.Write(note.Length > 200 ? note[..200] : note);
        }));
    }

    private void SendModeToAll()
    {
        foreach (TSPlayer player in TShock.Players)
        {
            if (player != null && player.Active && _seen[player.Index] != null)
            {
                SendMode(player);
            }
        }
    }

    private void OnGreet(GreetPlayerEventArgs e)
    {
        TSPlayer player = (e.Who >= 0 && e.Who < Main.maxPlayers) ? TShock.Players[e.Who] : null;
        if (player == null)
        {
            return;
        }
        _seen[e.Who] = null;
        _asks[e.Who] = 0;
        AskNow(player);
    }

    private void AskNow(TSPlayer player)
    {
        _asks[player.Index]++;
        _askAgainAt[player.Index] = (_asks[player.Index] < 3) ? DateTime.UtcNow.AddSeconds(5) : null;
        player.SendRawData(Packet(Ask));
    }

    private void OnLeave(LeaveEventArgs e)
    {
        if (e.Who >= 0 && e.Who < Main.maxPlayers)
        {
            _seen[e.Who] = null;
            _askAgainAt[e.Who] = null;
            _asks[e.Who] = 0;
        }
    }

    private int _tick;

    private void OnUpdate(EventArgs e)
    {
        if (++_tick % 30 != 0)
        {
            return;
        }
        DateTime now = DateTime.UtcNow;
        for (int i = 0; i < Main.maxPlayers; i++)
        {
            TSPlayer player = TShock.Players[i];
            if (player == null || !player.Active)
            {
                continue;
            }
            if (_seen[i] == null && _askAgainAt[i] is DateTime at && now >= at)
            {
                AskNow(player);
                continue;
            }
            Seen seen = _seen[i];
            if (_config.KickIfNotConfirmed && seen != null && seen.Sent >= IsaMode.NoCheats && seen.Confirmed != seen.Sent && (now - seen.SentAt).TotalSeconds > Math.Max(3, _config.AckSeconds))
            {
                TShock.Log.ConsoleInfo($"[IsaClientModes] kicking {player.Name}: IsaClient did not confirm mode {seen.Sent}");
                player.Kick("Your IsaClient did not accept this server's rules.", force: true, silent: false);
                _seen[i] = null;
            }
        }
    }

    #endregion

    #region Command

    private void Cmd(CommandArgs args)
    {
        string sub = args.Parameters.Count > 0 ? args.Parameters[0].ToLowerInvariant() : "list";
        switch (sub)
        {
            case "list":
            {
                List<string> lines = new();
                foreach (TSPlayer player in TShock.Players)
                {
                    if (player != null && player.Active && _seen[player.Index] is Seen seen)
                    {
                        string ok = seen.Confirmed == seen.Sent ? "confirmed" : "not confirmed yet";
                        lines.Add($"{player.Name}: IsaClient {seen.Version}, mode {seen.Sent} ({ok})");
                    }
                }
                args.Player.SendInfoMessage($"Server mode: {_config.Mode}. IsaClient players online: {lines.Count}");
                foreach (string line in lines)
                {
                    args.Player.SendInfoMessage("  " + line);
                }
                break;
            }
            case "mode":
            {
                if (args.Parameters.Count < 2 || !TryParseMode(args.Parameters[1], out IsaMode mode))
                {
                    args.Player.SendErrorMessage("Usage: /isaclient mode <none|detect|nocheats|vanilla> [message]");
                    return;
                }
                _config.Mode = mode;
                if (args.Parameters.Count > 2)
                {
                    _config.Message = string.Join(" ", args.Parameters.Skip(2));
                }
                SaveConfig();
                SendModeToAll();
                args.Player.SendSuccessMessage($"IsaClient mode is now {mode}.");
                break;
            }
            case "set":
            {
                if (args.Parameters.Count < 3)
                {
                    args.Player.SendErrorMessage("Usage: /isaclient set <player> <none|detect|nocheats|vanilla|default>");
                    return;
                }
                List<TSPlayer> found = TSPlayer.FindByNameOrID(args.Parameters[1]);
                if (found.Count != 1)
                {
                    args.Player.SendErrorMessage(found.Count == 0 ? "No player by that name." : "More than one player matches.");
                    return;
                }
                TSPlayer target = found[0];
                if (args.Parameters[2].Equals("default", StringComparison.OrdinalIgnoreCase))
                {
                    _playerModes.Remove(target.Name);
                }
                else if (TryParseMode(args.Parameters[2], out IsaMode own))
                {
                    _playerModes[target.Name] = own;
                }
                else
                {
                    args.Player.SendErrorMessage("Unknown mode.");
                    return;
                }
                SendMode(target);
                args.Player.SendSuccessMessage($"{target.Name}: mode {ModeFor(target)}" + (_seen[target.Index] == null ? " (not an IsaClient player right now)." : "."));
                break;
            }
            case "popup":
            {
                if (args.Parameters.Count < 3)
                {
                    args.Player.SendErrorMessage("Usage: /isaclient popup <player|all> <title> | <text> [| style [| icon [| seconds]]]  (\\n = new line, \"- \" = bullet)");
                    return;
                }
                string[] parts = string.Join(" ", args.Parameters.Skip(2)).Split('|').Select(s => s.Trim()).ToArray();
                string title = parts[0];
                string body = parts.Length > 1 ? parts[1].Replace("\\n", "\n") : "";
                PopupStyle style = PopupStyle.Info;
                if (parts.Length > 2 && !Enum.TryParse(parts[2], true, out style))
                {
                    style = PopupStyle.Info;
                }
                int icon = parts.Length > 3 && int.TryParse(parts[3], out int i) ? i : 0;
                int seconds = parts.Length > 4 && int.TryParse(parts[4], out int s) ? s : 0;
                if (args.Parameters[1].Equals("all", StringComparison.OrdinalIgnoreCase) || args.Parameters[1] == "*")
                {
                    int sent = ShowPopupToAll(title, body, style, icon, seconds);
                    args.Player.SendSuccessMessage($"Card sent to {sent} IsaClient player(s).");
                    return;
                }
                List<TSPlayer> who = TSPlayer.FindByNameOrID(args.Parameters[1]);
                if (who.Count != 1)
                {
                    args.Player.SendErrorMessage(who.Count == 0 ? "No player by that name." : "More than one player matches.");
                    return;
                }
                args.Player.SendSuccessMessage(ShowPopup(who[0], title, body, style, icon, seconds) ? $"Card sent to {who[0].Name}." : $"{who[0].Name} is not using IsaClient.");
                break;
            }
            case "reload":
                LoadConfig();
                SendModeToAll();
                args.Player.SendSuccessMessage("IsaClientModes.json reloaded.");
                break;
            default:
                args.Player.SendErrorMessage("Usage: /isaclient list | mode <mode> [message] | set <player> <mode|default> | popup <player|all> <title> | <text> | reload");
                break;
        }
    }

    #endregion
}
