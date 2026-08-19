using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace TheUnknowing
{
    // Owns every active/collapsing storm for the mod's lifetime. Not a ModSystem itself -
    // TheUnknowingModSystem creates one and drives it (tick listener, command handler), same
    // single-entry-point shape Caveshrooms uses.
    public class UnknowingStormManager
    {
        private const string SaveDataKey = "theunknowing:storms";

        private readonly ICoreServerAPI api;
        private readonly UnknowingConfig config;
        private readonly IServerNetworkChannel channel;
        private readonly List<UnknowingStorm> storms;

        // Player UIDs currently believed (server-side) to be inside some storm's chunk bounds -
        // not persisted, rebuilt from scratch (as empty) on every server start, since it's purely
        // a "did this change since last check" cache driving InStormPacket, not real state.
        private readonly HashSet<string> playersInStorm = new();

        public UnknowingStormManager(ICoreServerAPI api, UnknowingConfig config, IServerNetworkChannel channel)
        {
            this.api = api;
            this.config = config;
            this.channel = channel;
            storms = api.WorldManager.SaveGame.GetData(SaveDataKey, new List<UnknowingStorm>());
        }

        // Resolves the name through PlayerData (the persistent per-player record, populated at
        // first join and kept regardless of online status) rather than matching directly against
        // LandClaim.LastKnownOwnerName - confirmed live that field is NOT populated at claim
        // creation time (a freshly claimed area on a still-online player came back empty), so it
        // can't be trusted as the primary lookup. OwnedByPlayerUid is the field LandClaim
        // actually keys ownership on.
        public TextCommandResult StartStorm(string playerName)
        {
            IServerPlayerData? playerData = api.PlayerData.GetPlayerDataByLastKnownName(playerName);
            if (playerData == null)
            {
                return TextCommandResult.Error($"No player has ever been known by the name '{playerName}'.");
            }

            List<LandClaim> claims = api.World.Claims.All
                .Where(claim => claim.OwnedByPlayerUid == playerData.PlayerUID)
                .ToList();

            if (claims.Count == 0)
            {
                return TextCommandResult.Error($"'{playerName}' has no land claims.");
            }

            HashSet<(int ChunkX, int ChunkZ)> columns = ClaimChunkMath.GetCoveredChunkColumns(claims);

            // A claim can exist with zero Areas (e.g. every area removed via /land removearea but
            // the claim record itself left behind) - claims.Count > 0 above doesn't rule that out.
            // Must bail before any destructive side effect below: letting a zero-column storm
            // through would remove the player's claim(s) and persist a storm whose ChunkColumns is
            // empty, which crashes every later ChunkColumns[Count / 2] indexing (this method's own
            // centerColumn below, and previously SpawnSmokeColumn) on every subsequent tick until
            // the corrupt storm is cleared from save data.
            if (columns.Count == 0)
            {
                return TextCommandResult.Error($"'{playerName}' has land claim(s) but they cover zero chunk columns (no Areas) - nothing to storm.");
            }

            // Suppress immediately - the moment it's summoned, the claim is forgotten and the
            // base is lootable. No need to restore this later; the land gets regenerated from
            // scratch once the storm collapses (0.4).
            foreach (LandClaim claim in claims)
            {
                api.World.Claims.Remove(claim);
            }

            var storm = new UnknowingStorm
            {
                TargetPlayerName = playerName,
                StartTotalHours = api.World.Calendar.TotalHours,
                DurationHours = config.StormDurationHours,
                Status = UnknowingStormStatus.Active,
                ChunkColumns = columns.Select(c => new ChunkColumn(c.ChunkX, c.ChunkZ)).ToList()
            };

            storms.Add(storm);
            Persist();

            ChunkColumn centerColumn = storm.ChunkColumns[storm.ChunkColumns.Count / 2];
            double centerX = centerColumn.ChunkX * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;
            double centerZ = centerColumn.ChunkZ * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;
            int centerGroundY = api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)centerX, 0, (int)centerZ));

            // Cloud entities aren't spawned here - EnsureCloudsSpawned (called from OnGameTick)
            // picks up every column with CloudEntityId == 0 within one game tick of this storm
            // existing, new or old. One spawn path instead of two.
            BroadcastStormUnleashed(playerName, centerX, centerGroundY, centerZ);

            return TextCommandResult.Success(
                $"The Unknowing descends: {claims.Count} claim(s) for '{playerName}' erased, " +
                $"{storm.ChunkColumns.Count} chunk column(s) now open to loot and monsters. " +
                $"Active for {storm.DurationHours:0} in-game hour(s).");
        }

        // Self-healing: ensures every column of every non-Done storm has a live landmark entity
        // (theunknowing:stormcloud) - a static, tall translucent column meant to solve what
        // particles alone couldn't, something visible from well outside the storm regardless of
        // what's built nearby. Unlike the removed particle-based smoke beacon (see ROADMAP
        // 0.3/reversal), this is a real world object, so it isn't subject to the client's shared
        // particle render budget (confirmed live that budget gets contended by weather - see
        // GOTCHAS.md). Called from OnGameTick rather than only at storm creation, so a storm that
        // existed before this feature shipped (or a cloud that's despawned/failed to load for any
        // reason) gets one within one tick instead of needing a brand new storm.
        private void EnsureCloudsSpawned(UnknowingStorm storm)
        {
            bool changed = false;

            foreach (ChunkColumn column in storm.ChunkColumns)
            {
                if (column.CloudEntityId != 0 && api.World.GetEntityById(column.CloudEntityId) != null) continue;

                EntityProperties? cloudType = api.World.GetEntityType(new AssetLocation("theunknowing", "stormcloud"));
                if (cloudType == null)
                {
                    api.World.Logger.Warning("[TheUnknowing] stormcloud entity type not found, skipping cloud spawn.");
                    return;
                }

                int groundY = GetColumnGroundY(column);

                // Confirmed live: on the very first OnGameTick after a fresh server boot, the
                // relevant chunk can still be mid-load, and GetTerrainMapheightAt returns 0 before
                // real terrain exists - anchoring a cloud there forever, since this method only
                // checks whether the tracked entity still *exists*, never whether its position
                // still makes sense. A cloud spawned at world Y=0 then sits there uncorrected
                // across every subsequent restart. Skip and retry next tick instead of spawning
                // against an obviously-not-ready height; no real claim in this mod is ever
                // legitimately at Y=0.
                if (groundY <= 0) continue;

                double x = column.ChunkX * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;
                double z = column.ChunkZ * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;

                Entity cloudEntity = api.ClassRegistry.CreateEntity(cloudType);
                cloudEntity.Pos.SetPos(x, groundY, z);
                api.World.SpawnEntity(cloudEntity);

                column.CloudEntityId = cloudEntity.EntityId;
                changed = true;
            }

            if (changed) Persist();
        }

        // Tells every online player, not just whoever ran the command, the moment a storm goes
        // up - the whole server should know a base just became lootable and dangerous. The
        // location is a clickable command:// (VTML) link that runs /waypoint addati directly
        // rather than a bare coordinate string - VTML has no protocol for opening the map at a
        // location, but dropping a pinned waypoint there gets players to the same place in one
        // click, which is the actual goal ("so people can mark it").
        private void BroadcastStormUnleashed(string playerName, double x, int groundY, double z)
        {
            string waypointLink =
                $"command:///waypoint addati spiral {(int)x} {groundY} {(int)z} true #B4145A The Unknowing - {playerName}";

            api.BroadcastMessageToAllGroups(
                $"<strong>The Unknowing</strong> descends upon '{playerName}''s abandoned ground. " +
                $"<a href=\"{waypointLink}\">Mark the location</a> before it's lost.",
                EnumChatType.Notification, null);
        }

        // Registered on a real-time tick by TheUnknowingModSystem. Handles the Active ->
        // Collapsing transition (Collapsing itself is currently a dead end - no regen wired up
        // yet; see ROADMAP 0.4) and containment - keeping every storm-owned entity inside the
        // chunk columns it was summoned over.
        public void OnGameTick()
        {
            double nowHours = api.World.Calendar.TotalHours;
            bool changed = false;

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.Status == UnknowingStormStatus.Active && nowHours - storm.StartTotalHours >= storm.DurationHours)
                {
                    storm.Status = UnknowingStormStatus.Collapsing;
                    api.World.Logger.Notification($"[TheUnknowing] Storm over '{storm.TargetPlayerName}' is collapsing (duration elapsed).");
                    changed = true;
                }

                if (storm.Status != UnknowingStormStatus.Done)
                {
                    EnsureCloudsSpawned(storm);
                }

                if (EnforceContainment(storm))
                {
                    changed = true;
                }
            }

            UpdatePlayerStormMembership();

            if (changed) Persist();
        }

        // Tells each online player's client whether they're currently inside any storm's chunk
        // bounds, but only sends InStormPacket on an actual transition (entering/leaving), not
        // every tick - the client uses this to push/pop a real AmbientModifier fog effect (see
        // TheUnknowingModSystem.StartClientSide), which needs real client-side rendering state the
        // server has no other way to drive.
        private void UpdatePlayerStormMembership()
        {
            foreach (IServerPlayer player in api.World.AllOnlinePlayers.Cast<IServerPlayer>())
            {
                Entity? entity = player.Entity;
                if (entity == null) continue;

                (int chunkX, int chunkZ) = ClaimChunkMath.ToChunkColumn(entity.Pos.X, entity.Pos.Z);
                bool inAnyStorm = storms.Any(storm =>
                    storm.Status != UnknowingStormStatus.Done &&
                    storm.ChunkColumns.Any(c => c.ChunkX == chunkX && c.ChunkZ == chunkZ));

                bool wasInStorm = playersInStorm.Contains(player.PlayerUID);
                if (inAnyStorm == wasInStorm) continue;

                if (inAnyStorm) playersInStorm.Add(player.PlayerUID);
                else playersInStorm.Remove(player.PlayerUID);

                channel.SendPacket(new InStormPacket { InStorm = inAnyStorm, FogFadeSeconds = config.FogFadeSeconds }, player);
            }
        }

        // Registered on its own real-time tick by TheUnknowingModSystem, at
        // config.FogParticleIntervalSeconds. Spawns ember particles across every covered chunk
        // column of every non-Done storm. Not persisted - transient visual effect, not storm
        // state.
        //
        // Used to also spawn the void-black "containment wall" fog particles (SpawnFogParticles) -
        // removed once the stormcloud landmark entity existed to do the "visible from far away"
        // job instead. The particle wall was a real performance hazard: its density scaled with
        // chunk-column count, and (confirmed live) it competed with the client's shared particle
        // render budget under rain badly enough that raising the budget cap 5x didn't fix visible
        // dropout - only removing rain did. The stormcloud entity has none of that risk (real
        // world object, not particles - see GOTCHAS.md), so there's no reason to keep paying that
        // cost just for atmosphere. Interior fog went with it too, on the same reasoning.
        public void OnFogTick()
        {
            if (storms.Count == 0) return;

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.Status == UnknowingStormStatus.Done) continue;

                foreach (ChunkColumn column in storm.ChunkColumns)
                {
                    SpawnEmberParticles(column, GetColumnGroundY(column));
                }
            }
        }

        // Shared by every per-column fog-tick particle spawn below - column-center terrain
        // height, looked up once per column per tick.
        private int GetColumnGroundY(ChunkColumn column)
        {
            double minX = column.ChunkX * ClaimChunkMath.ChunkSize;
            double minZ = column.ChunkZ * ClaimChunkMath.ChunkSize;
            return api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)(minX + ClaimChunkMath.ChunkSize / 2), 0, (int)(minZ + ClaimChunkMath.ChunkSize / 2)));
        }

        // Small, fast "ember" particles scattered through the storm - reads as purple live
        // (RGB 180,20,90, closer to crimson/raspberry on paper, but that's not how it actually
        // renders against the dark background). Color contrast against the stormcloud entity's
        // void-black plus quick, erratic motion is meant to sell "dangerous energy," not just
        // "hazy." Liked live - specifically called out as reading well against the cloud, the
        // purple-against-void contrast selling a "cosmic destruction" feel.
        private void SpawnEmberParticles(ChunkColumn column, int groundY)
        {
            double minX = column.ChunkX * ClaimChunkMath.ChunkSize;
            double minZ = column.ChunkZ * ClaimChunkMath.ChunkSize;
            double maxX = minX + ClaimChunkMath.ChunkSize;
            double maxZ = minZ + ClaimChunkMath.ChunkSize;

            Vec3d minPos = new Vec3d(minX, groundY, minZ);
            Vec3d maxPos = new Vec3d(maxX, groundY + 4, maxZ);
            Vec3f minVelocity = new Vec3f(-0.06f, 0.02f, -0.06f);
            Vec3f maxVelocity = new Vec3f(0.06f, 0.1f, 0.06f);
            int color = ColorUtil.ColorFromRgba(180, 20, 90, 200);

            api.World.SpawnParticles(config.EmberParticlesPerColumn, color, minPos, maxPos, minVelocity, maxVelocity,
                lifeLength: 3f, gravityEffect: 0f, scale: 0.4f, EnumParticleModel.Quad, dualCallByPlayer: null);
        }

        // Registered on its own real-time tick by TheUnknowingModSystem, at
        // config.AmbientAudioIntervalSeconds. Plays the game's existing Rift sound (the closest
        // thematic match already in the game for "localized temporal wrongness") from roughly the
        // center of each non-Done storm.
        public void OnAmbientAudioTick()
        {
            if (storms.Count == 0) return;

            var riftSound = new AssetLocation("game", "sounds/effect/rift.ogg");

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.Status == UnknowingStormStatus.Done) continue;
                if (storm.ChunkColumns.Count == 0) continue;

                ChunkColumn center = storm.ChunkColumns[storm.ChunkColumns.Count / 2];
                double x = center.ChunkX * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;
                double z = center.ChunkZ * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;
                int groundY = api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)x, 0, (int)z));

                api.World.PlaySoundAt(riftSound, x, groundY, z, null, true, config.AmbientAudioRange);
            }
        }

        // Despawns any tracked entity that's wandered outside the storm's chunk columns (e.g.
        // fled a fight, wandered off) - the storm spawned it, it doesn't get to leave. Also prunes
        // already-dead/despawned entities from tracking, same as OnSpawnTick does - the two run on
        // different intervals, so an entity could die between spawn-tick checks.
        private bool EnforceContainment(UnknowingStorm storm)
        {
            bool changed = false;

            for (int i = storm.SpawnedEntityIds.Count - 1; i >= 0; i--)
            {
                long entityId = storm.SpawnedEntityIds[i];
                Entity? entity = api.World.GetEntityById(entityId);
                if (entity == null)
                {
                    storm.SpawnedEntityIds.RemoveAt(i);
                    changed = true;
                    continue;
                }

                (int chunkX, int chunkZ) = ClaimChunkMath.ToChunkColumn(entity.Pos.X, entity.Pos.Z);
                bool inBounds = storm.ChunkColumns.Any(c => c.ChunkX == chunkX && c.ChunkZ == chunkZ);
                if (!inBounds)
                {
                    api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    storm.SpawnedEntityIds.RemoveAt(i);
                    changed = true;
                }
            }

            return changed;
        }

        // Registered on its own real-time tick by TheUnknowingModSystem, at
        // config.EnemySpawnIntervalSeconds. One spawn attempt per Active storm per tick.
        public void OnSpawnTick()
        {
            bool changed = false;

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.Status != UnknowingStormStatus.Active) continue;

                // Prune dead/despawned entities first so the cap reflects what's actually alive,
                // not a lifetime spawn count.
                int before = storm.SpawnedEntityIds.Count;
                storm.SpawnedEntityIds.RemoveAll(id => api.World.GetEntityById(id) == null);
                if (storm.SpawnedEntityIds.Count != before) changed = true;

                if (storm.SpawnedEntityIds.Count >= config.MaxConcurrentEnemies) continue;
                if (storm.ChunkColumns.Count == 0 || config.EnemyEntityCodes.Count == 0) continue;

                if (TrySpawnOne(storm))
                {
                    changed = true;
                }
            }

            if (changed) Persist();
        }

        private bool TrySpawnOne(UnknowingStorm storm)
        {
            ChunkColumn column = storm.ChunkColumns[api.World.Rand.Next(storm.ChunkColumns.Count)];
            int blockX = column.ChunkX * ClaimChunkMath.ChunkSize + api.World.Rand.Next(ClaimChunkMath.ChunkSize);
            int blockZ = column.ChunkZ * ClaimChunkMath.ChunkSize + api.World.Rand.Next(ClaimChunkMath.ChunkSize);
            int groundY = api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos(blockX, 0, blockZ));

            string entityCode = config.EnemyEntityCodes[api.World.Rand.Next(config.EnemyEntityCodes.Count)];
            EntityProperties? entityType = api.World.GetEntityType(new AssetLocation(entityCode));
            if (entityType == null)
            {
                api.World.Logger.Warning($"[TheUnknowing] EnemyEntityCodes has unknown entity code '{entityCode}', skipping spawn.");
                return false;
            }

            Entity entity = api.ClassRegistry.CreateEntity(entityType);
            entity.Pos.SetPos(blockX + 0.5, groundY + 1, blockZ + 0.5);
            api.World.SpawnEntity(entity);

            storm.SpawnedEntityIds.Add(entity.EntityId);
            return true;
        }

        // Debug/testing-only utility - despawns every entity every tracked storm owns and clears
        // all storm state, regardless of status. Nothing in the real lifecycle calls this; it
        // exists because storms currently never end on their own (Collapsing is still a dead
        // end), so without this, every storm ever created in a test world keeps spawning forever.
        public TextCommandResult ClearAllStorms()
        {
            int stormCount = storms.Count;
            int entityCount = 0;

            foreach (UnknowingStorm storm in storms)
            {
                foreach (long entityId in storm.SpawnedEntityIds)
                {
                    Entity? entity = api.World.GetEntityById(entityId);
                    if (entity == null) continue;

                    api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    entityCount++;
                }

                foreach (ChunkColumn column in storm.ChunkColumns)
                {
                    if (column.CloudEntityId == 0) continue;

                    Entity? cloudEntity = api.World.GetEntityById(column.CloudEntityId);
                    if (cloudEntity == null) continue;

                    api.World.DespawnEntity(cloudEntity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    entityCount++;
                }
            }

            storms.Clear();
            Persist();

            return TextCommandResult.Success($"Cleared {stormCount} storm(s), despawned {entityCount} tracked entity/entities.");
        }

        // Debug/testing-only utility - despawns every column's current cloud entity and resets
        // CloudEntityId to 0, without touching the rest of the storm. EnsureCloudsSpawned (next
        // OnGameTick, within 10s) then spawns fresh ones against whatever the stormcloud entity
        // type currently looks like. Exists for iterating on the shape/texture without needing a
        // new land claim each time - a shape/geometry change might not be reflected on an
        // already-spawned entity instance, only a freshly spawned one.
        public TextCommandResult RespawnClouds()
        {
            int despawned = 0;

            foreach (UnknowingStorm storm in storms)
            {
                foreach (ChunkColumn column in storm.ChunkColumns)
                {
                    if (column.CloudEntityId == 0) continue;

                    Entity? cloudEntity = api.World.GetEntityById(column.CloudEntityId);
                    if (cloudEntity != null)
                    {
                        api.World.DespawnEntity(cloudEntity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                        despawned++;
                    }

                    column.CloudEntityId = 0;
                }
            }

            Persist();

            return TextCommandResult.Success($"Despawned {despawned} cloud(s); fresh ones will spawn within the next game tick.");
        }

        // Debug/testing-only utility - dumps every tracked storm's raw state so overlapping or
        // stale storms (e.g. leftover from an earlier test session, never cleared via
        // /unknowing-storm-clear) can be seen directly rather than inferred from symptoms.
        public TextCommandResult DumpStormStatus()
        {
            if (storms.Count == 0)
            {
                return TextCommandResult.Success("No tracked storms.");
            }

            var lines = storms.Select((storm, i) =>
                $"[{i}] target='{storm.TargetPlayerName}' status={storm.Status} " +
                $"columns={storm.ChunkColumns.Count} spawnedEntities={storm.SpawnedEntityIds.Count} " +
                $"cloudsSpawned={storm.ChunkColumns.Count(c => c.CloudEntityId != 0)}/{storm.ChunkColumns.Count} " +
                $"startHour={storm.StartTotalHours:0.0} durationHours={storm.DurationHours:0.0}");

            return TextCommandResult.Success($"{storms.Count} tracked storm(s):\n" + string.Join("\n", lines));
        }

        private void Persist()
        {
            api.WorldManager.SaveGame.StoreData(SaveDataKey, storms);
        }
    }
}
