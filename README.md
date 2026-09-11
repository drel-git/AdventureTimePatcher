# AdventureTimePatcher

Installer/updater for [sebbun123/Adventuretime](https://github.com/sebbun123/Adventuretime).

It installs AdventureTime into a MacroQuest root:

```text
<MQ root>/lua/adventuretime/
```

## Windows usage

Download and run:

```text
AdventureTimePatcher-win-x64.exe
```

Pick your MacroQuest folder, then click **Check for Updates** or **Update Now**.

The patcher also checks for newer patcher releases. If one is available, use **Update Patcher** first, then continue updating AdventureTime.

## Linux usage

```bash
chmod +x AdventureTimePatcher-linux-x64
./AdventureTimePatcher-linux-x64 check --mq "/path/to/MQ/root"
./AdventureTimePatcher-linux-x64 update --mq "/path/to/MQ/root"
./AdventureTimePatcher-linux-x64 self-check
./AdventureTimePatcher-linux-x64 self-update
```

The MQ root is the folder that contains `lua/` and `config/`. Under Lutris/Wine, this is usually inside the game/MacroQuest install folder, not the Wine prefix root.

## What update does

- Downloads the latest `main` branch from `sebbun123/Adventuretime`.
- Only installs/updates these managed files:
  - `lua/adventuretime/init.lua`
  - `lua/adventuretime/README.md`
- Preserves an existing `lua/adventuretime/AdventureTime_targets.ini`.
- If targets already exist, writes the repo copy as `AdventureTime_targets.ini.example`.
- Does not delete or replace the full `lua/adventuretime/` folder.
- Leaves extra user files and folders alone, including settings and logs.
- Backs up replaced files under:

```text
<MQ root>/config/AdventureTimePatcher_backup/<timestamp>/
```

- Writes install state to:

```text
<MQ root>/config/adventuretime_install.json
```

## Build locally

Windows GUI:

```powershell
dotnet publish src/AdventureTimePatcher.Gui/AdventureTimePatcher.Gui.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishTrimmed=false `
  -p:EnableCompressionInSingleFile=true `
  -o publish/win-x64
```

Linux CLI:

```bash
dotnet publish src/AdventureTimePatcher.Cli/AdventureTimePatcher.Cli.csproj \
  -c Release \
  -r linux-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:PublishTrimmed=false \
  -o publish/linux-x64
```

The binary will be:

```text
publish/linux-x64/AdventureTimePatcher
```

Rename it to `AdventureTimePatcher-linux-x64` for release.
