# NOMNOM entry

`NoMissileCam.json` is the entry for [NOMNOM](https://github.com/KopterBuzz/NOMNOM), the registry that
NOMM (Nuclear Option Mod Manager) installs mods from. It follows NOMNOM's `SCHEMA.md` and passes its
`ValidationSchema.json` and the content checks of `Validate-JsonContent.ps1` (file name equals `id`,
id not taken, https URLs, archive file names, numeric versions). Submitted for 0.3.3 on 2026-10-12 as
[KopterBuzz/NOMNOM#434](https://github.com/KopterBuzz/NOMNOM/pull/434), which is waiting for review.

## Choices

- `id` `NoMissileCam`: NOMNOM asks for the plugin's assembly name. It is also the folder a manual
  install uses (`BepInEx/plugins/NoMissileCam/`), so NOMM and a manual install share one folder.
  "MissileCamera" and "MissileCamNO" are other authors' mods.
- `isClientOrServer` `Client`: only the player who wants the camera installs it. It sends no network
  messages.
- `tags`: `mod`, `QoL` and `Camera`, as the other camera mods in NOMNOM use.
- `description`: leads with what this mod adds next to other missile cameras in NOMNOM (the `[` target
  view and in-game rebinding of keys and joystick buttons) without naming other mods. Keep the `\`
  key escaped as `\\` in the JSON.
- `imageUrl` and `imageHash`: the icon, `docs/media/icon-512.png` (PNG, 512x512, NOMNOM's limit), as a
  raw.githubusercontent.com URL pinned to a commit so the file behind it never changes. `imageHash` is
  the file's SHA-256 as plain lowercase hex with no `sha256:` prefix: NOMM's `PreviewImageLoader.kt`
  compares it with the downloaded bytes and shows no icon when they differ, and NOMNOM's image issue
  template writes the same format. To change the icon after the merge, use the "Update Mod Image URL"
  issue template (`HOWTO_UPDATE_MODIMAGE.md` in NOMNOM) with a new pinned URL.
- No `dependencies`: NOMM installs BepInEx 5 itself, and the plugin needs nothing else.
- `gameVersion` `0.34.2`: the version the 0.3.2 code was tested on (0.3.3 is the same code).

## How NOMM installs it

Checked against NOMM's `Installer.kt`, `RepoMods.kt` and `LocalMods.kt`:

- NOMM downloads the artifact's `downloadUrl`, checks it against `hash`, unpacks the whole archive into
  `BepInEx/disabledPlugins/<id>/`, writes `meta.json` there and moves the folder to
  `BepInEx/plugins/<id>/` when the mod is turned on.
- The archive is unpacked as it is, with no folder flattening. A zip with `BepInEx/plugins/...` inside
  (the game-folder zip) would therefore nest. That is why each release has two zips, and the
  `-plugins.zip` with its files at the root comes first.
- `autoUpdateArtifacts` `"True"`: NOMNOM's hourly job reads the **first** asset of each new GitHub
  release (`Update-ModArtifact.ps1`) and turns the tag (`v0.3.4` becomes `0.3.4`) into the artifact
  version. Keep the tag numeric, matching `Plugin.Version` and the csproj `<Version>`, and upload
  `NoMissileCam-<version>-plugins.zip` first (`scripts/package.py` builds both zips).
- NOMM treats a plugins folder without `meta.json` as the mod named like the folder, so a manual
  install shows up as this mod and an update from NOMM replaces it cleanly.
