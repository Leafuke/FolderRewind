# Minecraft player storage and KnotLink repair evidence

Verified on 2026-10-02. Scope: Java Edition player-state preservation for ordinary Restore, with legacy and 26.1 layouts. This record separates official storage facts, selected product policy, automated validation and outstanding game acceptance.

## Primary sources

- [Minecraft Java Edition 26.1 release notes](https://www.minecraft.net/en-us/article/minecraft-java-edition-26-1), World Storage and Changes to level.dat: `playerdata` moves to `players/data`, `advancements` to `players/advancements`, `stats` to `players/stats`; the embedded `Player` becomes `singleplayer_uuid` and an independent player file.
- [26.1 official version metadata](https://piston-meta.mojang.com/v1/packages/84d0ff7bd4428695691af5a178edae22c7c83d89/26.1.json) and [official client JAR](https://piston-data.mojang.com/v1/objects/191771837687b766537a8c4607cb6fad79c533a1/client.jar), SHA-1 `191771837687b766537a8c4607cb6fad79c533a1`. The JAR's `version.json` identifies world data version **4786**. `PlayerStorageFileFix` implements the three moves; `LevelDatToSavedDataFileFix.extractPlayerDataToFile` extracts the embedded compound using the player's UUID and the player data fixer. `PrimaryLevelData` uses `UUIDUtil.CODEC` for `singleplayer_uuid`, represented by a four-element NBT int-array. `FileFixerUpper` performs a broader world layout migration: moving player paths alone is not a supported full-world version conversion.
- [1.21.11 official metadata](https://piston-meta.mojang.com/v1/packages/4f6bd9388f12e9d7adc2ded64acba66212d60521/1.21.11.json), [official mappings](https://piston-data.mojang.com/v1/objects/031a68bebf55d824f66d6573d8c752f0e1bf232a/client.txt), and [official client JAR](https://piston-data.mojang.com/v1/objects/ba2df812c2d12e0219c489c4cd9a5e1f0760f5bd/client.jar).

The 26.1 and mapped 1.21.11 save methods were examined read-only: `Entity.saveWithoutId`, `Player.addAdditionalSaveData`, `LivingEntity.addAdditionalSaveData`, `FoodData.addAdditionalSaveData`, and `ServerPlayer.addAdditionalSaveData`. They retain `Pos`, `Rotation`, `Dimension`, `Inventory`, `EnderItems`, `XpLevel`, `XpP`, `XpTotal`, `Score`, `playerGameType`, `Health`, `foodLevel`, `foodSaturationLevel`. Experience integers, score and food level use ints; `XpP`, **Health**, and saturation use floats. Position and rotation retain vector codecs; existing tags are cloned instead of rebuilding vector elements. The older `NbtShort`-only health extraction was incorrect for these releases. `playerGameType` uses the legacy GameType ID codec. Filename and NBT UUIDs are validated without using player names.

## Selected behavior

- Preserve these fields for every current UUID, including offline players. Other fields, advancements and statistics follow the backup.
- If the backup lacks a current UUID, preserve the complete current player compound as a deliberate exception. Players found only in the backup restore normally.
- Legacy embedded singleplayer fields are authoritative for that UUID and synchronize its file. A historical embedded compound without a UUID is preserved in-place; it is not guessed to belong to another player's file. UUIDMost/UUIDLeast decoding is supported, but all historical Minecraft versions have not been certified.
- Preserve a modern world's current `singleplayer_uuid` with its referenced player state. Do not map the current owner's fields onto the backup owner's different UUID.
- Canonical `.dat` files are player subjects; `.dat_old` is not used as another subject or an automatic corrupt-file fallback.
- Mixed layouts, invalid identities, missing referenced files, corrupt fields or proposal limits block the entire preservation preparation before target mutation. Effective preservation across the 26.1 layout boundary is blocked; no Mojang data-fixer or downgrade implementation is claimed.
- Preparation operates on the locked managed inventory only. It cannot read or write outside the effective source boundary. A server-root source uses the unique managed `level.dat` prefix; an ambiguous inventory fails rather than selecting a world.

## Validation and acceptance

The player-preservation, remote-parameter and restore-whitelist regression fixtures added with this change have been removed at the user's request. No new regression suite was added for the upgrade repair. The Host still validates the complete output before staging writes and maintains a Derived Workspace baseline when preserved content differs from the archive. Build and isolated package-installation verification are recorded in [the upgrade incident analysis](knotlink-bundled-upgrade-2026-10-02.md).

Actual Minecraft **1.21.11 and 26.1** singleplayer and multiplayer loading acceptance remains **pending**. In isolated copied worlds, restore while preserving all players; load the world and reconnect each online/offline UUID, checking location, inventory, ender chest, XP and health. Also verify selected fields survive while other fields and advancements/statistics rewind, and verify a preservation failure leaves the live world unchanged. Generated NBT tests are not evidence that a real game loaded the output successfully.
