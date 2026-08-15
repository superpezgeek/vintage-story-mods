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
        private readonly List<UnknowingStorm> storms;

        public UnknowingStormManager(ICoreServerAPI api, UnknowingConfig config)
        {
            this.api = api;
            this.config = config;
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

            return TextCommandResult.Success(
                $"The Unknowing descends: {claims.Count} claim(s) for '{playerName}' erased, " +
                $"{storm.ChunkColumns.Count} chunk column(s) now open to loot and monsters. " +
                $"Active for {storm.DurationHours:0} in-game hour(s).");
        }

        // Registered on a real-time tick by TheUnknowingModSystem. Only handles the
        // Active -> Collapsing transition so far - Collapsing itself is currently a dead end
        // (no spawning to stop yet, no regen wired up yet; see ROADMAP 0.2/0.4).
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
            }

            if (changed) Persist();
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

            return TextCommandResult.Success($"Cleared {stormCount} storm(s), despawned {entityCount} tracked enemy/enemies.");
        }

        private void Persist()
        {
            api.WorldManager.SaveGame.StoreData(SaveDataKey, storms);
        }
    }
}
