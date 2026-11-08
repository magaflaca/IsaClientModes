# IsaClientModes

TShock plugin to control what IsaClient can do on your server.

## Install

Drop `IsaClientModes.dll` (from releases) in `ServerPlugins` and restart. Config is created at `tshock/IsaClientModes.json`.

## Modes

- `None` - nothing, but you can still see who uses it with `/isaclient list`
- `Detect` - staff get told who uses IsaClient
- `NoCheats` - client cheats are off on your server (god, noclip, tp, item/world tools, plugins, etc)
- `Vanilla` - the whole client is off, plays like vanilla

Players get everything back when they leave.

## Config

```json
{
  "Mode": "Detect",
  "Message": "",
  "BypassPermission": "isaclient.bypass",
  "GroupModes": { "vip": "Detect", "default": "NoCheats" },
  "KickIfNotConfirmed": false,
  "AckSeconds": 10,
  "TellStaff": true,
  "Welcome": { "Enabled": false, "Title": "Welcome!", "Body": "- Read /rules\\n- Have fun", "Style": "Info", "Icon": 0, "Seconds": 0 }
}
```

`Message` is shown on the mode popup. `Welcome` is a popup shown to IsaClient players when they join.

## Commands

Permission: `isaclient.admin`

```
/isaclient list
/isaclient mode <none|detect|nocheats|vanilla> [message]
/isaclient set <player> <mode|default>
/isaclient popup <player|all> <title> | <text> [| info|success|warning|danger [| icon [| seconds]]]
/isaclient reload
```

Popup text: `\n` for new lines, lines starting with `- ` are bullets, chat tags like `[c/ff0000:red]` and `[i:29]` work.

## API

Reference the dll from your plugin:

```csharp
using IsaClientModes;

IsaClientModesPlugin.IsaClientJoined += (player, version) =>
    IsaClientModesPlugin.ShowPopup(player, "Hey!", "- New season started\n- Check /kit", PopupStyle.Success, icon: 29);

IsaClientModesPlugin.ShowPopupToAll("Restart in 5 min", "Save your stuff.", PopupStyle.Warning, seconds: 30);

IsaClientModesPlugin.IsIsaClient(player);
IsaClientModesPlugin.ClientVersion(player);
IsaClientModesPlugin.ModeOf(player);
```

## Build

Put `TShockAPI.dll`, `OTAPI.dll`, `TerrariaServer.dll` and `ModFramework.dll` from your server in a `ref` folder (or pass `-p:TShockRef=<folder>`), then:

```
dotnet build -c Release
```

## Notes

Uses packet 82 with a net module id vanilla doesn't use, so vanilla clients/servers just ignore it.
It only works because the client cooperates, it's not an anticheat.

## License

PolyForm Noncommercial 1.0.0, same as IsaClient. See LICENSE.md.
