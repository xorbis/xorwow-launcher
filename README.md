# XorWoW Launcher

The launcher for **XorWoW**, a private World of Warcraft 3.3.5a (build 12340) realm running on
[AzerothCore](https://www.azerothcore.org/). Players download only `XorWoW.exe`, and it handles the rest:

- **Login**: SRP6 against the realm's authserver (port 3724), so the account is verified before the game starts.
  "Remember me" stores only a DPAPI-protected `SHA1(USER:PASS)`. The game's own login screen needs the
  password itself, so if game auto-login is on, the password is also stored, DPAPI-protected.
- **Install and update the client**: the game goes into `client\` next to the exe. Files are compared
  against a signed manifest and only changed files are downloaded.
- **Realm addons** are kept up to date, and other addons can be browsed and installed from the
  [Warperia](https://warperia.com/) catalogue.
- **Release notes** for the client, addons and server; click one for its full write-up.
- **Game auto-login**: types the already-verified login into the game's login screen.
- **Self-update** to newer launcher builds listed in the manifest.
- **Extras**: an in-game radio helper (`--radio`), guild hall banner painting, and a Windows uninstall entry.

## Security model

Everything the launcher writes into a game folder comes from `manifest.json` on the realm's file
server. That manifest lists every file with its size and SHA-256, and it is signed with an RSA-3072 key
(`manifest.sig`). The public half is embedded in the exe (`Assets/signing-key.public.xml`). The private
key never leaves the realm's build machine. A tampered web host can't push files or a launcher
update to players, because every download is checked against the signed hashes.

## Building

Requirements: Windows and the .NET 8 SDK (`winget install Microsoft.DotNet.SDK.8`). The target is
.NET Framework 4.8, so players get a single exe with nothing to install.

```
dotnet build XorWoWLauncher.csproj -c Release -p:XorWoWServer=your.realm.host
```

The output is `bin\Release\net48\XorWoW.exe`. `XorWoWServer` is the realm the launcher logs into
by default (players can change it in the launcher). It defaults to `127.0.0.1`, and it can also be
set in a `Directory.Build.props` above this folder. The file server (`Settings.DefaultFiles`) points at
XorWoW's downloads. To use the launcher for another realm, change that URL, replace
`Assets/signing-key.public.xml` with your own key's public half, and publish your own signed manifest.

## Runtime data

Settings, the log and per-game-folder state are stored in `%APPDATA%\XorWoW Launcher\`.

## License

[MIT](LICENSE). The bundled Cinzel font is under the SIL Open Font License (`Assets/Cinzel-OFL.txt`).
World of Warcraft is a trademark of Blizzard Entertainment. This project is not affiliated with Blizzard.
