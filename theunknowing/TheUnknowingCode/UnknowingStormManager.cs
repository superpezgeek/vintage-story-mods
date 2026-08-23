using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;

namespace TheUnknowing
{
    // Owns every active/collapsing storm for the mod's lifetime. Not a ModSystem itself -
    // TheUnknowingModSystem creates one and drives it (tick listeners, command handlers).
    public class UnknowingStormManager
    {
        private const string SaveDataKey = "theunknowing:storms";

        // Multiplied against the cloud's texture in-shader - the near-white star pixels take on
        // this hue directly, turning the white starfield purple with no texture swap. Applied
        // once on the GatheringStrength -> EnteringReality transition, never reverted (the cloud
        // is despawned outright once the storm reaches Collapsing).
        private static readonly int EnteringRealityTintArgb = ColorUtil.ToRgba(255, 190, 60, 230);

        private readonly ICoreServerAPI api;
        private readonly UnknowingConfig config;
        private readonly IServerNetworkChannel channel;
        private readonly List<UnknowingStorm> storms;

        // Cached rather than rebuilt per SpawnEmberParticles call - only basePos/Quantity vary
        // per column per tick. AdvancedParticleProperties (not Simple) specifically because the
        // trail needs SecondaryParticles, which only the Advanced variant supports.
        private readonly AdvancedParticleProperties emberParticles;

        // Player UIDs currently believed (server-side) to be inside some storm's chunk bounds -
        // not persisted, rebuilt as empty on every server start. Just a "did this change since
        // last check" cache driving InStormPacket.
        private readonly HashSet<string> playersInStorm = new();

        public UnknowingStormManager(ICoreServerAPI api, UnknowingConfig config, IServerNetworkChannel channel)
        {
            this.api = api;
            this.config = config;
            this.channel = channel;
            storms = api.WorldManager.SaveGame.GetData(SaveDataKey, new List<UnknowingStorm>());

            // Cosmic-ray streaks: each particle draws one random velocity at spawn from a wide
            // range and flies straight, no in-flight re-roll (an earlier "vibrate" version just
            // read as aimless wander). All three axes share the same range so direction stays
            // unbiased.
            //
            // PosOffset X/Z spans the full chunk-column footprint, centered at ChunkSize/2 so it
            // covers 0..ChunkSize - basePos is set to each column's near corner per call.
            //
            // HsvaColor (not a plain Color) because AdvancedParticleProperties.ToBytes only ever
            // serializes HSV to the client, unconditionally indexing all 4 elements - leaving it
            // null throws inside SpawnParticles.
            //
            // (179,48,255) is the brightest opaque pixel sampled from entity/theunknowing.png,
            // scaled to full saturation - a true violet, unlike an earlier (180,20,90) that read
            // as magenta/pink live (wrong hue band). Wide Value variance (0-255) reproduces the
            // texture's own near-black-to-bright range, including "some particles are black" for
            // free.
            int[] emberHsv = ColorUtil.RgbToHsvInts(179, 48, 255);
            NatFloat emberHueNat = NatFloat.createUniform(emberHsv[0], 12);
            NatFloat emberSatNat = NatFloat.createUniform(emberHsv[1], 25);
            NatFloat emberValNat = NatFloat.createUniform(128, 128);
            emberParticles = new AdvancedParticleProperties
            {
                HsvaColor = new NatFloat[]
                {
                    emberHueNat,
                    emberSatNat,
                    emberValNat,
                    NatFloat.createUniform(200, 0),
                },
                Velocity = new NatFloat[]
                {
                    NatFloat.createUniform(0f, 3.2f),
                    NatFloat.createUniform(0f, 3.2f),
                    NatFloat.createUniform(0f, 3.2f),
                },
                PosOffset = new NatFloat[]
                {
                    NatFloat.createUniform(ClaimChunkMath.ChunkSize / 2f, ClaimChunkMath.ChunkSize / 2f),
                    NatFloat.createUniform(2f, 2f),
                    NatFloat.createUniform(ClaimChunkMath.ChunkSize / 2f, ClaimChunkMath.ChunkSize / 2f),
                },
                LifeLength = NatFloat.createUniform(3f, 0f),
                GravityEffect = NatFloat.createUniform(0f, 0f),
                Size = NatFloat.createUniform(0.4f, 0f),
                ParticleModel = EnumParticleModel.Quad,
                RandomVelocityChange = false,

                // The trail: a small dim dot dropped every 0.03s of the parent's flight,
                // stationary and fading fast - marks where the streak has been (same pattern the
                // base game uses for ExplosionFireTrailCubicles).
                SecondaryParticles = new AdvancedParticleProperties[]
                {
                    new AdvancedParticleProperties
                    {
                        HsvaColor = new NatFloat[]
                        {
                            emberHueNat,
                            emberSatNat,
                            emberValNat,
                            NatFloat.createUniform(160, 0),
                        },
                        Velocity = new NatFloat[] { NatFloat.Zero, NatFloat.Zero, NatFloat.Zero },
                        Quantity = NatFloat.createUniform(1f, 0f),
                        Size = NatFloat.createUniform(0.18f, 0.03f),
                        LifeLength = NatFloat.createUniform(0.3f, 0f),
                        GravityEffect = NatFloat.createUniform(0f, 0f),
                        OpacityEvolve = EvolvingNatFloat.create(EnumTransformFunction.QUADRATIC, -16f),
                        SecondarySpawnInterval = NatFloat.createUniform(0.03f, 0f),
                        ParticleModel = EnumParticleModel.Quad,
                        TerrainCollision = false,
                    }
                },
            };
        }

        // Resolves the name through PlayerData (populated at first join, kept regardless of
        // online status) rather than LandClaim.LastKnownOwnerName - that field isn't populated at
        // claim-creation time. OwnedByPlayerUid is what LandClaim actually keys ownership on.
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

            Dictionary<(int ChunkX, int ChunkZ), (int MinY, int MaxY)> columns = ClaimChunkMath.GetCoveredChunkColumns(claims);

            // A claim can exist with zero Areas (every area removed via /land removearea, claim
            // record left behind) - must bail before the destructive side effect below, since a
            // zero-column storm crashes every later ChunkColumns-indexing tick.
            if (columns.Count == 0)
            {
                return TextCommandResult.Error($"'{playerName}' has land claim(s) but they cover zero chunk columns (no Areas) - nothing to storm.");
            }

            foreach (LandClaim claim in claims)
            {
                api.World.Claims.Remove(claim);
            }

            var storm = new UnknowingStorm
            {
                TargetPlayerName = playerName,
                StartUnixMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                GatheringStrengthDurationMinutes = config.GatheringStrengthDurationMinutes,
                EnteringRealityDurationMinutes = config.EnteringRealityDurationMinutes,
                Status = UnknowingStormStatus.GatheringStrength,
                ChunkColumns = columns.Select(c => new ChunkColumn(c.Key.ChunkX, c.Key.ChunkZ, c.Value.MinY, c.Value.MaxY)).ToList()
            };

            storms.Add(storm);
            Persist();

            var (centerX, centerGroundY, centerZ) = GetStormCenterPos(storm);

            // Cloud entities are spawned by EnsureCloudsSpawned (from OnGameTick), not here - one
            // spawn path for both new and self-healed columns.
            BroadcastStormUnleashed(playerName, centerX, centerGroundY, centerZ);

            return TextCommandResult.Success(
                $"The Unknowing descends: {claims.Count} claim(s) for '{playerName}' erased, " +
                $"{storm.ChunkColumns.Count} chunk column(s) now open to loot and monsters. " +
                $"Gathering strength for {storm.GatheringStrengthDurationMinutes:0}m, then entering reality for " +
                $"{storm.EnteringRealityDurationMinutes:0}m before it collapses.");
        }

        // Not tied to a claim, and never progresses/spawns enemies - see UnknowingStorm.IsDemo.
        // Requires the target online since there's no claim to derive a position from.
        public TextCommandResult StartDemo(string playerName)
        {
            if (storms.Any(s => s.IsDemo && s.Status != UnknowingStormStatus.Done))
            {
                return TextCommandResult.Error("A demo storm is already running - use '/unknowing-demo kill' first.");
            }

            IServerPlayer? target = api.World.AllOnlinePlayers
                .Cast<IServerPlayer>()
                .FirstOrDefault(p => string.Equals(p.PlayerName, playerName, StringComparison.OrdinalIgnoreCase));

            if (target?.Entity == null)
            {
                return TextCommandResult.Error($"'{playerName}' isn't online.");
            }

            EntityPos pos = target.Entity.Pos;
            (int chunkX, int chunkZ) = ClaimChunkMath.ToChunkColumn(pos.X, pos.Z);
            int claimMinY = (int)pos.Y - 16;
            int claimMaxY = (int)pos.Y + 16;

            var storm = new UnknowingStorm
            {
                TargetPlayerName = playerName,
                StartUnixMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Status = UnknowingStormStatus.GatheringStrength,
                IsDemo = true,
                ChunkColumns = new List<ChunkColumn> { new ChunkColumn(chunkX, chunkZ, claimMinY, claimMaxY) }
            };

            storms.Add(storm);
            Persist();

            return TextCommandResult.Success(
                $"Demo storm summoned on {playerName}'s chunk - no claim removed, no enemies will spawn. " +
                "Run '/unknowing-demo kill' to end it.");
        }

        public TextCommandResult StopDemo()
        {
            List<UnknowingStorm> demoStorms = storms.Where(s => s.IsDemo).ToList();
            if (demoStorms.Count == 0)
            {
                return TextCommandResult.Error("No demo storm is currently running.");
            }

            foreach (UnknowingStorm storm in demoStorms)
            {
                foreach (ChunkColumn column in storm.ChunkColumns)
                {
                    if (column.CloudEntityId == 0) continue;
                    Entity? cloudEntity = api.World.GetEntityById(column.CloudEntityId);
                    if (cloudEntity == null) continue;
                    api.World.DespawnEntity(cloudEntity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                }
            }

            storms.RemoveAll(s => s.IsDemo);
            Persist();

            return TextCommandResult.Success("Demo storm ended.");
        }

        // Self-healing: ensures every column of every non-Done storm has a live landmark entity
        // (theunknowing:theunknowing), a static translucent column visible well outside the
        // storm regardless of what's built nearby. Runs from OnGameTick (not just at storm
        // creation) so a storm that predates this feature, or a cloud that's despawned/failed to
        // load, gets one within one tick.
        private void EnsureCloudsSpawned(UnknowingStorm storm)
        {
            bool changed = false;

            foreach (ChunkColumn column in storm.ChunkColumns)
            {
                if (column.CloudEntityId != 0 && api.World.GetEntityById(column.CloudEntityId) != null) continue;

                EntityProperties? cloudType = api.World.GetEntityType(new AssetLocation("theunknowing", "theunknowing"));
                if (cloudType == null)
                {
                    api.World.Logger.Warning("[TheUnknowing] theunknowing:theunknowing entity type not found, skipping cloud spawn.");
                    return;
                }

                int groundY = GetColumnGroundY(column);

                // On a fresh server boot the chunk can still be mid-load and
                // GetTerrainMapheightAt returns 0 before real terrain exists - skip and retry
                // next tick rather than anchoring a cloud at Y=0 forever (no real claim is ever
                // legitimately there).
                if (groundY <= 0) continue;

                double x = column.ChunkX * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;
                double z = column.ChunkZ * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;

                Entity cloudEntity = api.ClassRegistry.CreateEntity(cloudType);
                cloudEntity.Pos.SetPos(x, groundY, z);

                // Started before SpawnEntity, via the AnimationMetaData overload rather than
                // entity.StartAnimation(string) - that convenience overload resolves through
                // entity.Properties, which Initialize() (triggered by SpawnEntity) hasn't
                // populated yet. AnimManager.StartAnimation(AnimationMetaData) skips that lookup,
                // so it's safe pre-spawn.
                //
                // EaseInSpeed pinned high - every animation's contribution actually fades in via
                // an internal blend from the bind pose, independent of our own keyframes; left at
                // its default this produced a full-height flash before the intended grow-in.
                var spawnAnim = new AnimationMetaData { Code = "spawn", Animation = "spawn", AnimationSpeed = 1f, EaseInSpeed = 1000f }.Init();
                cloudEntity.AnimManager.StartAnimation(spawnAnim);
                api.World.SpawnEntity(cloudEntity);

                column.CloudEntityId = cloudEntity.EntityId;
                changed = true;
            }

            if (changed) Persist();
        }

        // Escalates every cloud entity this storm owns on the GatheringStrength -> EnteringReality
        // transition: widens 12 -> 32 blocks (chunk width) and tints the starfield purple (see
        // EnteringRealityTintArgb). Both one-time, never reverted (the cloud is despawned outright
        // at Collapsing). Stops "spawn" before starting "widen" rather than trusting the two to
        // blend correctly while concurrently active.
        private void TriggerEnteringRealityCloudEffects(UnknowingStorm storm)
        {
            var widenAnim = new AnimationMetaData { Code = "widen", Animation = "widen", AnimationSpeed = 1f, EaseInSpeed = 1000f }.Init();

            foreach (ChunkColumn column in storm.ChunkColumns)
            {
                if (column.CloudEntityId == 0) continue;
                Entity? cloudEntity = api.World.GetEntityById(column.CloudEntityId);
                if (cloudEntity == null) continue;

                cloudEntity.StopAnimation("spawn");
                cloudEntity.AnimManager.StartAnimation(widenAnim);
                cloudEntity.RenderColor = EnteringRealityTintArgb;
            }
        }

        // The average of every column's own center point - used directly when it lands inside a
        // chunk the storm actually covers (the common case, including a legitimate seam between
        // two chunks for an even-width claim), falling back to the nearest actually-covered
        // column only when the raw average lands somewhere the storm doesn't cover at all (a
        // concave/multi-area claim).
        private (double X, int GroundY, double Z) GetStormCenterPos(UnknowingStorm storm)
        {
            double avgX = storm.ChunkColumns.Average(c => c.ChunkX * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0);
            double avgZ = storm.ChunkColumns.Average(c => c.ChunkZ * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0);

            var avgColumn = ClaimChunkMath.ToChunkColumn(avgX, avgZ);
            bool avgIsCovered = storm.ChunkColumns.Any(c => c.ChunkX == avgColumn.ChunkX && c.ChunkZ == avgColumn.ChunkZ);

            double x, z;
            if (avgIsCovered)
            {
                x = avgX;
                z = avgZ;
            }
            else
            {
                ChunkColumn nearest = storm.ChunkColumns
                    .OrderBy(c => (c.ChunkX - avgColumn.ChunkX) * (c.ChunkX - avgColumn.ChunkX) + (c.ChunkZ - avgColumn.ChunkZ) * (c.ChunkZ - avgColumn.ChunkZ))
                    .First();
                x = nearest.ChunkX * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;
                z = nearest.ChunkZ * ClaimChunkMath.ChunkSize + ClaimChunkMath.ChunkSize / 2.0;
            }

            int groundY = api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)x, 0, (int)z));
            return (x, groundY, z);
        }

        // VTML has no protocol for opening the map at a location, so this drops a clickable
        // pinned waypoint instead - the closest one-click equivalent. `=` is required on each
        // coordinate to mark it as absolute (/waypoint addati otherwise treats a bare number as
        // map-relative and adds the map-center offset on top of it - see /tp's own syntax help).
        private string BuildLocationLink(string playerName, double x, int groundY, double z, string linkText)
        {
            string waypointLink =
                $"command:///waypoint addati spiral ={(int)x} ={groundY} ={(int)z} true #B4145A The Unknowing - {playerName}";
            return $"<a href=\"{waypointLink}\">{linkText}</a>";
        }

        private void BroadcastStormUnleashed(string playerName, double x, int groundY, double z)
        {
            string link = BuildLocationLink(playerName, x, groundY, z, "forgotten lands");
            BroadcastMessage($"<strong>The Unknowing</strong> gathers strength over {link}.");
        }

        private void BroadcastMessage(string html)
        {
            api.BroadcastMessageToAllGroups(html, EnumChatType.Notification, null);
        }

        // Handles the GatheringStrength -> EnteringReality -> Collapsing progression, the regen +
        // cleanup once a storm reaches Collapsing, and containment. Player storm membership (fog
        // fade) is handled separately, on its own faster tick - see OnMembershipTick.
        public void OnGameTick()
        {
            long nowMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool changed = false;
            List<UnknowingStorm> finishedStorms = new();

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.IsDemo)
                {
                    EnsureCloudsSpawned(storm);
                    continue;
                }

                double elapsedMinutes = (nowMillis - storm.StartUnixMillis) / 60000.0;

                if (storm.Status == UnknowingStormStatus.GatheringStrength &&
                    elapsedMinutes >= storm.GatheringStrengthDurationMinutes)
                {
                    storm.Status = UnknowingStormStatus.EnteringReality;
                    api.World.Logger.Notification($"[TheUnknowing] Storm over '{storm.TargetPlayerName}' is entering reality.");
                    {
                        var (x, groundY, z) = GetStormCenterPos(storm);
                        string link = BuildLocationLink(storm.TargetPlayerName, x, groundY, z, "forgotten lands");
                        BroadcastMessage($"<strong>The Unknowing</strong> begins to devour {link} - the horrors within grow stronger.");
                    }
                    TriggerEnteringRealityCloudEffects(storm);
                    changed = true;
                }
                else if (storm.Status == UnknowingStormStatus.EnteringReality &&
                    elapsedMinutes >= storm.GatheringStrengthDurationMinutes + storm.EnteringRealityDurationMinutes)
                {
                    storm.Status = UnknowingStormStatus.Collapsing;
                    api.World.Logger.Notification($"[TheUnknowing] Storm over '{storm.TargetPlayerName}' is collapsing (duration elapsed).");
                    {
                        var (x, groundY, z) = GetStormCenterPos(storm);
                        string link = BuildLocationLink(storm.TargetPlayerName, x, groundY, z, "forgotten lands");
                        BroadcastMessage($"Reality surrounding {link} begins to collapse.");
                    }
                    changed = true;
                }

                // Checked as its own condition (not just the transition edge above) so a storm
                // already sitting in Collapsing - e.g. from before this shipped - is picked up on
                // the very next tick too.
                if (storm.Status == UnknowingStormStatus.Collapsing)
                {
                    FinishCollapse(storm);
                    storm.Status = UnknowingStormStatus.Done;
                    finishedStorms.Add(storm);
                    changed = true;
                    continue;
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

            if (finishedStorms.Count > 0)
            {
                storms.RemoveAll(finishedStorms.Contains);
            }

            if (changed) Persist();
        }

        // Despawns everything the storm owns and regenerates the claimed land, then the storm is
        // dropped from tracking entirely - its lifecycle is complete.
        private void FinishCollapse(UnknowingStorm storm)
        {
            int toDespawn = storm.SpawnedEntityIds.Count;
            int despawned = 0;
            int unresolved = 0;

            foreach (long entityId in storm.SpawnedEntityIds)
            {
                Entity? entity = api.World.GetEntityById(entityId);
                if (entity == null)
                {
                    unresolved++;
                    continue;
                }
                api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                despawned++;
            }

            api.World.Logger.Notification(
                $"[TheUnknowing] [MobDespawn] reason=StormCollapse storm='{storm.TargetPlayerName}' tracked={toDespawn} " +
                $"despawned={despawned} unresolved={unresolved} totalTracked={TotalTrackedEntities() - toDespawn}");

            foreach (ChunkColumn column in storm.ChunkColumns)
            {
                if (column.CloudEntityId == 0) continue;
                Entity? cloudEntity = api.World.GetEntityById(column.CloudEntityId);
                if (cloudEntity == null) continue;
                api.World.DespawnEntity(cloudEntity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
            }

            RegenClaimedChunks(storm);

            // Computed after the regen so the waypoint's Y reflects the freshly regenerated
            // terrain, not the pre-collapse claim's.
            var (x, groundY, z) = GetStormCenterPos(storm);
            string link = BuildLocationLink(storm.TargetPlayerName, x, groundY, z, "what was forgotten");
            BroadcastMessage($"<strong>The Unknowing</strong> reclaims {link}.");
        }

        // Runs /wgen regenrange over the bounding box of each connected cluster of chunk columns
        // (not one box over the storm as a whole - two separate claims can be anywhere on the map
        // relative to each other, and a single combined box would regenrange every unclaimed
        // chunk between them too). Uses a synthetic console Caller, same pattern vanilla uses for
        // block/BE-triggered commands.
        //
        // Deliberately /wgen regenrange, not /wgen regen - regen's chunk-range math keys off
        // Caller.Player.Entity.Pos, not Caller.Pos, and silently falls back to the world map
        // center if Player is null (the console-Caller case here, for a target who's most likely
        // offline). regenrange takes explicit chunk coordinates with no such fallback.
        private void RegenClaimedChunks(UnknowingStorm storm)
        {
            if (storm.ChunkColumns.Count == 0) return;

            var caller = new Caller
            {
                Type = EnumCallerType.Console,
                CallerRole = "admin",
                CallerPrivileges = new[] { "*" },
                FromChatGroupId = GlobalConstants.ConsoleGroup
            };

            string playerName = storm.TargetPlayerName;

            foreach (List<ChunkColumn> cluster in ClusterChunkColumns(storm.ChunkColumns))
            {
                int minX = cluster.Min(c => c.ChunkX);
                int maxX = cluster.Max(c => c.ChunkX);
                int minZ = cluster.Min(c => c.ChunkZ);
                int maxZ = cluster.Max(c => c.ChunkZ);

                api.ChatCommands.ExecuteUnparsed(
                    $"/wgen regenrange {minX} {minZ} {maxX} {maxZ}",
                    new TextCommandCallingArgs { Caller = caller },
                    result => api.World.Logger.Notification(
                        $"[TheUnknowing] wgen regenrange for '{playerName}' ({minX},{minZ})-({maxX},{maxZ}): {result.StatusMessage}"));
            }
        }

        // Standard 8-directional flood-fill connected-components over the chunk grid - lenient
        // enough that a claim touching only at a corner still regens as one cluster, while a
        // genuine gap (the far-apart-claims case this exists for) reliably splits.
        private static List<List<ChunkColumn>> ClusterChunkColumns(List<ChunkColumn> columns)
        {
            var byPos = columns.ToDictionary(c => (c.ChunkX, c.ChunkZ));
            var visited = new HashSet<(int, int)>();
            var clusters = new List<List<ChunkColumn>>();

            foreach (ChunkColumn start in columns)
            {
                var startKey = (start.ChunkX, start.ChunkZ);
                if (!visited.Add(startKey)) continue;

                var cluster = new List<ChunkColumn> { start };
                var queue = new Queue<(int ChunkX, int ChunkZ)>();
                queue.Enqueue(startKey);

                while (queue.Count > 0)
                {
                    (int x, int z) = queue.Dequeue();
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            if (dx == 0 && dz == 0) continue;
                            var neighborKey = (x + dx, z + dz);
                            if (!byPos.TryGetValue(neighborKey, out ChunkColumn? neighbor)) continue;
                            if (!visited.Add(neighborKey)) continue;
                            cluster.Add(neighbor);
                            queue.Enqueue(neighborKey);
                        }
                    }
                }

                clusters.Add(cluster);
            }

            return clusters;
        }

        // Its own tick (StormMembershipIntervalSeconds), separate from the shared 10s OnGameTick,
        // so a player crossing a storm boundary doesn't wait up to 10s for the client-side fog to
        // catch up. Cheap: a chunk-column lookup per online player, no storm state mutation.
        // Tells the client only on an actual transition, since that's what drives the client's
        // push/pop of its own AmbientModifier fade.
        public void OnMembershipTick()
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

        // Spawns ember particles across every covered chunk column of every non-Done storm
        // (including demo storms - they stay non-Done indefinitely). Not persisted - transient
        // visual effect, not storm state.
        public void OnFogTick()
        {
            if (storms.Count == 0) return;

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.Status == UnknowingStormStatus.Done) continue;

                float particlesPerColumn = storm.Status == UnknowingStormStatus.EnteringReality
                    ? config.EmberParticlesPerColumn * config.EnteringIntensityMultiplier
                    : config.EmberParticlesPerColumn;

                foreach (ChunkColumn column in storm.ChunkColumns)
                {
                    SpawnEmberParticles(column, GetColumnGroundY(column), particlesPerColumn);
                }
            }
        }

        private int GetColumnGroundY(ChunkColumn column)
        {
            double minX = column.ChunkX * ClaimChunkMath.ChunkSize;
            double minZ = column.ChunkZ * ClaimChunkMath.ChunkSize;
            return api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)(minX + ClaimChunkMath.ChunkSize / 2), 0, (int)(minZ + ClaimChunkMath.ChunkSize / 2)));
        }

        private void SpawnEmberParticles(ChunkColumn column, int groundY, float particlesPerColumn)
        {
            double minX = column.ChunkX * ClaimChunkMath.ChunkSize;
            double minZ = column.ChunkZ * ClaimChunkMath.ChunkSize;

            emberParticles.basePos.Set(minX, groundY, minZ);
            emberParticles.Quantity = NatFloat.createUniform(particlesPerColumn, 0f);

            api.World.SpawnParticles(emberParticles, dualCallByPlayer: null);
        }

        // Despawns any tracked entity that's wandered outside the storm's chunk columns (fled a
        // fight, wandered off) - the storm spawned it, it doesn't get to leave. Also prunes
        // already-dead entities, same as OnSpawnTick (the two run on different intervals, so an
        // entity can die between one and the next).
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
                    api.World.Logger.Notification(
                        $"[TheUnknowing] [MobPrune] storm='{storm.TargetPlayerName}' id={entityId} " +
                        $"(dead/unloaded, untracked without an explicit despawn call) stormCount={storm.SpawnedEntityIds.Count} totalTracked={TotalTrackedEntities()}");
                    continue;
                }

                (int chunkX, int chunkZ) = ClaimChunkMath.ToChunkColumn(entity.Pos.X, entity.Pos.Z);
                bool inBounds = storm.ChunkColumns.Any(c => c.ChunkX == chunkX && c.ChunkZ == chunkZ);
                if (!inBounds)
                {
                    api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    storm.SpawnedEntityIds.RemoveAt(i);
                    changed = true;
                    api.World.Logger.Notification(
                        $"[TheUnknowing] [MobDespawn] reason=OutOfBounds entity='{entity.Code}' id={entityId} storm='{storm.TargetPlayerName}' " +
                        $"chunk=({chunkX},{chunkZ}) stormCount={storm.SpawnedEntityIds.Count} totalTracked={TotalTrackedEntities()}");
                }
            }

            return changed;
        }

        // One spawn attempt per GatheringStrength/EnteringReality storm per tick - Collapsing is
        // wind-down (no new spawns), and demo storms never spawn enemies at all.
        public void OnSpawnTick()
        {
            bool changed = false;

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.IsDemo) continue;
                if (storm.Status != UnknowingStormStatus.GatheringStrength &&
                    storm.Status != UnknowingStormStatus.EnteringReality) continue;

                int before = storm.SpawnedEntityIds.Count;
                storm.SpawnedEntityIds.RemoveAll(id => api.World.GetEntityById(id) == null);
                int prunedCount = before - storm.SpawnedEntityIds.Count;
                if (prunedCount > 0)
                {
                    changed = true;
                    api.World.Logger.Notification(
                        $"[TheUnknowing] [MobPrune] storm='{storm.TargetPlayerName}' pruned={prunedCount} " +
                        $"(dead/unloaded, untracked without an explicit despawn call) stormCount={storm.SpawnedEntityIds.Count} totalTracked={TotalTrackedEntities()}");
                }

                bool entering = storm.Status == UnknowingStormStatus.EnteringReality;
                int cap = entering ? (int)(config.MaxConcurrentEnemies * config.EnteringIntensityMultiplier) : config.MaxConcurrentEnemies;
                List<string> pool = entering ? config.EnteringEnemyEntityCodes : config.EnemyEntityCodes;

                if (storm.SpawnedEntityIds.Count >= cap) continue;
                if (storm.ChunkColumns.Count == 0 || pool.Count == 0) continue;

                if (TrySpawnOne(storm, pool, cap))
                {
                    changed = true;
                }
            }

            if (changed) Persist();
        }

        // Kept clear of a chosen column's own outer blocks - picking uniformly across the full
        // column could land a mob one step from a column it doesn't own, and EnforceContainment
        // despawns on that single step regardless of how it got there (confirmed live: producing
        // near-immediate OutOfBounds despawns unrelated to actually fleeing).
        private const int SpawnEdgeMarginBlocks = 6;

        private bool TrySpawnOne(UnknowingStorm storm, List<string> pool, int cap)
        {
            ChunkColumn column = storm.ChunkColumns[api.World.Rand.Next(storm.ChunkColumns.Count)];
            int interiorSpan = ClaimChunkMath.ChunkSize - 2 * SpawnEdgeMarginBlocks;
            int blockX = column.ChunkX * ClaimChunkMath.ChunkSize + SpawnEdgeMarginBlocks + api.World.Rand.Next(interiorSpan);
            int blockZ = column.ChunkZ * ClaimChunkMath.ChunkSize + SpawnEdgeMarginBlocks + api.World.Rand.Next(interiorSpan);

            // The claim's own recorded Y span, not the surface heightmap - an underground base's
            // claim area sits well below GetTerrainMapheightAt, and mobs need to threaten it at
            // its own depth.
            int startY = column.ClaimMinY + api.World.Rand.Next(Math.Max(1, column.ClaimMaxY - column.ClaimMinY + 1));
            int spawnY = FindGroundY(blockX, blockZ, column.ClaimMinY, column.ClaimMaxY, startY);

            string entityCode = pool[api.World.Rand.Next(pool.Count)];
            EntityProperties? entityType = api.World.GetEntityType(new AssetLocation(entityCode));
            if (entityType == null)
            {
                api.World.Logger.Warning($"[TheUnknowing] enemy pool has unknown entity code '{entityCode}', skipping spawn.");
                return false;
            }

            Entity entity = api.ClassRegistry.CreateEntity(entityType);
            entity.Pos.SetPos(blockX + 0.5, spawnY, blockZ + 0.5);
            api.World.SpawnEntity(entity);

            storm.SpawnedEntityIds.Add(entity.EntityId);

            api.World.Logger.Notification(
                $"[TheUnknowing] [MobSpawn] entity='{entityCode}' id={entity.EntityId} storm='{storm.TargetPlayerName}' " +
                $"phase={storm.Status} pos=({blockX},{spawnY},{blockZ}) stormCount={storm.SpawnedEntityIds.Count}/{cap} totalTracked={TotalTrackedEntities()}");

            return true;
        }

        // A claim area is a selection box, and players routinely grow it upward for headroom well
        // past the real floor - most of the span is open air above one real floor, not standable
        // ground. Scans downward from startY first (the common case), then upward as a fallback
        // for a genuinely multi-level claim, falling back to minY + 1 if neither direction finds
        // solid ground at all (e.g. the drawn X/Z sits over a shaft).
        private int FindGroundY(int blockX, int blockZ, int minY, int maxY, int startY)
        {
            for (int y = startY; y >= minY; y--)
            {
                if (api.World.BlockAccessor.GetBlock(new BlockPos(blockX, y, blockZ)).SideSolid[BlockFacing.UP.Index])
                {
                    return y + 1;
                }
            }
            for (int y = startY + 1; y <= maxY; y++)
            {
                if (api.World.BlockAccessor.GetBlock(new BlockPos(blockX, y, blockZ)).SideSolid[BlockFacing.UP.Index])
                {
                    return y + 1;
                }
            }
            return minY + 1;
        }

        // Debug/testing-only - despawns every entity every tracked storm owns and clears all
        // storm state. Nothing in the real lifecycle calls this (storms currently never end on
        // their own via any other path than reaching Collapsing).
        //
        // GetEntityById only finds entities in currently-loaded chunks, so a storm is only ever
        // forgotten once every entity it owned is confirmed despawned - anything unresolved stays
        // tracked so a rerun closer to the site finishes the job instead of orphaning it.
        public TextCommandResult ClearAllStorms()
        {
            int entityCount = 0;
            int unresolvedCount = 0;
            int mobsBefore = TotalTrackedEntities();

            foreach (UnknowingStorm storm in storms)
            {
                storm.Status = UnknowingStormStatus.Done;

                int stormMobCount = storm.SpawnedEntityIds.Count;
                int stormDespawned = 0;

                for (int i = storm.SpawnedEntityIds.Count - 1; i >= 0; i--)
                {
                    Entity? entity = api.World.GetEntityById(storm.SpawnedEntityIds[i]);
                    if (entity == null)
                    {
                        unresolvedCount++;
                        continue;
                    }

                    api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    storm.SpawnedEntityIds.RemoveAt(i);
                    entityCount++;
                    stormDespawned++;
                }

                if (stormMobCount > 0)
                {
                    api.World.Logger.Notification(
                        $"[TheUnknowing] [MobDespawn] reason=ClearAllStorms storm='{storm.TargetPlayerName}' tracked={stormMobCount} " +
                        $"despawned={stormDespawned} unresolved={stormMobCount - stormDespawned}");
                }

                foreach (ChunkColumn column in storm.ChunkColumns)
                {
                    if (column.CloudEntityId == 0) continue;

                    Entity? cloudEntity = api.World.GetEntityById(column.CloudEntityId);
                    if (cloudEntity == null)
                    {
                        unresolvedCount++;
                        continue;
                    }

                    api.World.DespawnEntity(cloudEntity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    column.CloudEntityId = 0;
                    entityCount++;
                }
            }

            if (unresolvedCount > 0)
            {
                api.World.Logger.Notification($"[TheUnknowing] ClearAllStorms: {unresolvedCount} entity/entities not found (likely unloaded chunks), left tracked for a rerun.");
            }

            api.World.Logger.Notification(
                $"[TheUnknowing] [MobDespawn] reason=ClearAllStorms totalMobsTrackedBefore={mobsBefore} totalMobsTrackedAfter={TotalTrackedEntities()}");

            int before = storms.Count;
            storms.RemoveAll(storm => storm.SpawnedEntityIds.Count == 0 && storm.ChunkColumns.All(c => c.CloudEntityId == 0));
            int stormCount = before - storms.Count;

            Persist();

            string note = unresolvedCount > 0
                ? $" {unresolvedCount} entit{(unresolvedCount == 1 ? "y" : "ies")} couldn't be found (likely in an unloaded chunk) - move closer to the storm site and rerun to finish clearing."
                : "";
            return TextCommandResult.Success($"Cleared {stormCount} storm(s), despawned {entityCount} tracked entity/entities.{note}");
        }

        // Debug/testing-only - dumps every tracked storm's raw state so overlapping or stale
        // storms can be seen directly rather than inferred from symptoms.
        public TextCommandResult DumpStormStatus()
        {
            if (storms.Count == 0)
            {
                return TextCommandResult.Success("No tracked storms.");
            }

            var lines = storms.Select((storm, i) =>
                $"[{i}] target='{storm.TargetPlayerName}' status={storm.Status}{(storm.IsDemo ? " (demo)" : "")} " +
                $"columns={storm.ChunkColumns.Count} spawnedEntities={storm.SpawnedEntityIds.Count} " +
                $"cloudsSpawned={storm.ChunkColumns.Count(c => c.CloudEntityId != 0)}/{storm.ChunkColumns.Count} " +
                $"startUnixMillis={storm.StartUnixMillis} gatheringMinutes={storm.GatheringStrengthDurationMinutes:0.0} " +
                $"enteringMinutes={storm.EnteringRealityDurationMinutes:0.0}");

            return TextCommandResult.Success(
                $"{storms.Count} tracked storm(s), {TotalTrackedEntities()} mob(s) tracked total:\n" + string.Join("\n", lines));
        }

        private void Persist()
        {
            api.WorldManager.SaveGame.StoreData(SaveDataKey, storms);
        }

        private int TotalTrackedEntities() => storms.Sum(s => s.SpawnedEntityIds.Count);
    }
}
