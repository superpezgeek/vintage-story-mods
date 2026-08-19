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

            BroadcastStormUnleashed(playerName, centerX, centerGroundY, centerZ);

            return TextCommandResult.Success(
                $"The Unknowing descends: {claims.Count} claim(s) for '{playerName}' erased, " +
                $"{storm.ChunkColumns.Count} chunk column(s) now open to loot and monsters. " +
                $"Active for {storm.DurationHours:0} in-game hour(s).");
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
        // config.FogParticleIntervalSeconds. Spawns void-black particles descending from above
        // across every covered chunk column of every non-Done storm - fully our own particles, not
        // reusing the game's Temporal Stability system (an earlier approach did; see
        // UnknowingConfig for why that was dropped). Not persisted - transient visual effect, not
        // storm state.
        public void OnFogTick()
        {
            if (storms.Count == 0) return;

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.Status == UnknowingStormStatus.Done) continue;

                HashSet<(int ChunkX, int ChunkZ)> columnSet = storm.ChunkColumns.Select(c => (c.ChunkX, c.ChunkZ)).ToHashSet();

                foreach (ChunkColumn column in storm.ChunkColumns)
                {
                    bool isBoundary = !columnSet.Contains((column.ChunkX - 1, column.ChunkZ))
                        || !columnSet.Contains((column.ChunkX + 1, column.ChunkZ))
                        || !columnSet.Contains((column.ChunkX, column.ChunkZ - 1))
                        || !columnSet.Contains((column.ChunkX, column.ChunkZ + 1));

                    int groundY = GetColumnGroundY(column);

                    // TEMP DIAGNOSTIC (remove once the per-chunk fade/reset issue is root-caused):
                    // logs every burst issued so the actual server-side spawn cadence per column
                    // can be read directly from server-main.log instead of inferred from what's
                    // visible client-side.
                    api.World.Logger.Notification($"[TheUnknowing] fogburst chunk=({column.ChunkX},{column.ChunkZ}) boundary={isBoundary} groundY={groundY}");

                    SpawnFogParticles(column, isBoundary, groundY);
                    SpawnEmberParticles(column, groundY);
                }
            }
        }

        // Shared by every per-column fog-tick particle spawn below (SpawnFogParticles,
        // SpawnEmberParticles) - both want the same column-center terrain height, so it's looked
        // up once per column per tick here instead of each independently querying the block
        // accessor for the identical position.
        private int GetColumnGroundY(ChunkColumn column)
        {
            double minX = column.ChunkX * ClaimChunkMath.ChunkSize;
            double minZ = column.ChunkZ * ClaimChunkMath.ChunkSize;
            return api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)(minX + ClaimChunkMath.ChunkSize / 2), 0, (int)(minZ + ClaimChunkMath.ChunkSize / 2)));
        }

        // Void-black, semi-transparent quads falling from well above ground down onto a chunk
        // column's footprint - "inky blackness descending from the heavens" per the original
        // pitch, not ambient ground-level haze. Boundary columns (touching a chunk outside the
        // storm) get config.FogWallBoundaryMultiplier the particle count, higher opacity, and
        // spawn from much higher up (config.FogWallHeight vs the interior's fixed +20), so the
        // edge reads as a proper towering containment wall rather than knee-high mist, distinct
        // from the (still dark, just thinner and lower) interior - the "stormwall" quality.
        // Color/velocity/lifeLength are a first-pass guess, expect these to need retuning once
        // seen live (same as every other numeric value in this mod so far).
        private void SpawnFogParticles(ChunkColumn column, bool isBoundary, int groundY)
        {
            double minX = column.ChunkX * ClaimChunkMath.ChunkSize;
            double minZ = column.ChunkZ * ClaimChunkMath.ChunkSize;
            double maxX = minX + ClaimChunkMath.ChunkSize;
            double maxZ = minZ + ClaimChunkMath.ChunkSize;

            // Spawns high above the ground, falls down onto it rather than drifting at ground level.
            float spawnTopHeight = isBoundary ? config.FogWallHeight : 20f;
            Vec3d minPos = new Vec3d(minX, groundY + 10, minZ);
            Vec3d maxPos = new Vec3d(maxX, groundY + spawnTopHeight, maxZ);
            Vec3f minVelocity = new Vec3f(-0.02f, -0.15f, -0.02f);
            Vec3f maxVelocity = new Vec3f(0.02f, -0.05f, 0.02f);

            float quantity = isBoundary ? config.FogParticlesPerColumn * config.FogWallBoundaryMultiplier : config.FogParticlesPerColumn;
            int alpha = isBoundary ? 220 : 140;
            int color = ColorUtil.ColorFromRgba(15, 12, 20, alpha);

            api.World.SpawnParticles(quantity, color, minPos, maxPos, minVelocity, maxVelocity,
                lifeLength: 10f, gravityEffect: 0.05f, scale: 2f, EnumParticleModel.Quad, dualCallByPlayer: null);
        }

        // Small, fast, crimson "ember" particles scattered through the storm - the black fog/wall
        // alone read as smoke, not a threat. Color contrast against the void-black plus quick,
        // erratic motion is meant to sell "dangerous energy," not just "hazy."
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
            }

            storms.Clear();
            Persist();

            return TextCommandResult.Success($"Cleared {stormCount} storm(s), despawned {entityCount} tracked entity/entities.");
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
                $"startHour={storm.StartTotalHours:0.0} durationHours={storm.DurationHours:0.0}");

            return TextCommandResult.Success($"{storms.Count} tracked storm(s):\n" + string.Join("\n", lines));
        }

        private void Persist()
        {
            api.WorldManager.SaveGame.StoreData(SaveDataKey, storms);
        }
    }
}
