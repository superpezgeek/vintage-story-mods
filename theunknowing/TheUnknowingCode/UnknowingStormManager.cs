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
    // TheUnknowingModSystem creates one and drives it (tick listener, command handler), same
    // single-entry-point shape Caveshrooms uses.
    public class UnknowingStormManager
    {
        private const string SaveDataKey = "theunknowing:storms";

        // Multiplied against the cloud's texture in-shader (texColor *= color, see
        // entityanimated.fsh) - the near-white star pixels take on this hue directly, while the
        // near-black background stays near-black regardless of tint, so this alone turns a white
        // starfield into a purple one with no texture swap needed. Applied once, on the
        // GatheringStrength -> EnteringReality transition (see
        // TriggerEnteringRealityCloudEffects) - never reverted, since the cloud entity is despawned
        // outright once the storm reaches Collapsing (see FinishCollapse).
        private static readonly int EnteringRealityTintArgb = ColorUtil.ToRgba(255, 190, 60, 230);

        private readonly ICoreServerAPI api;
        private readonly UnknowingConfig config;
        private readonly IServerNetworkChannel channel;
        private readonly List<UnknowingStorm> storms;

        // Cached rather than rebuilt per SpawnEmberParticles call (same pattern as the base
        // game's firefly/rust-mote ambient particles) - basePos and Quantity are the only
        // fields that vary per column per tick, everything else (color, velocity range, the
        // trail SecondaryParticles) is fixed for the mod's lifetime. AdvancedParticleProperties
        // rather than SimpleParticleProperties specifically because trails need SecondaryParticles,
        // which only the Advanced variant supports.
        private readonly AdvancedParticleProperties emberParticles;

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

            // Went through a "make it vibrate" pass first (RandomVelocityChange re-rolling
            // in-flight) - confirmed live that just made particles wander aimlessly rather than
            // read as erratic. What actually landed: cosmic-ray streaks. Each particle draws ONE
            // random velocity at spawn from a wide range (so different particles shoot off at
            // wildly different speeds/angles) and then flies straight - no in-flight re-roll, so
            // each streak reads as a single fast arrival rather than a wobble. All three axes use
            // the same range so direction is unbiased (sideways/up/down all equally likely, not
            // just horizontal) - an uneven Y range here was the earlier bug.
            //
            // PosOffset X/Z spans the full chunk-column footprint (ChunkSize wide, centered at
            // ChunkSize/2 so it covers 0..ChunkSize) the same way MinPos/AddPos used to for
            // SimpleParticleProperties - basePos below is set to each column's near corner per
            // call, and this offset spreads spawns across the rest of the column.
            // AdvancedParticleProperties.Color is never actually sent to the client - ToBytes
            // only serializes HsvaColor, unconditionally indexing all 4 elements, so leaving
            // HsvaColor null crashed ToBytes with an NPE the moment this reached
            // ServerMain.SpawnParticles. Has to be expressed as HSV instead (var 0 = exact color,
            // no per-particle variation).
            //
            // (63,17,90) is the brightest opaque pixel actually sampled from
            // textures/entity/theunknowing.png - scaled up to full saturation (max channel = 255)
            // that's (179,48,255), a true violet. The original (180,20,90) read as
            // magenta/pink live because its hue (~334 degrees) sits in red-violet, not purple
            // (~270-290 degrees) - R was too high relative to B for the color the storm cloud
            // texture actually uses.
            //
            // Pool of colors, sampled: scanned every opaque pixel in theunknowing.png and converted
            // to HSV (game's 0-255 scale) - hue sits in a narrow, consistent band (170-198, most
            // of it ~185-198) since it's all the same purple identity, but Value spans almost the
            // whole range (3 to 90 - the texture is mostly near-black void with a few brighter
            // veins). Reproducing that: tight hue/saturation variance around the sampled peak
            // color, wide Value variance (0-255, so it can land on near-black same as the darkest
            // texture pixels) - this is what gives "some particles are black" for free, it's the
            // same channel that's already doing most of the work in the source texture. Shared
            // NatFloat instances (stateless generators, safe to reuse) so the trail dots below
            // draw from the identical pool rather than a separately-tuned one.
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
                // Each axis is an independent uniform draw in [-var, var] (avg 0, so direction
                // stays unbiased). var is the only speed knob available without a custom
                // direction/magnitude sampler - it sets BOTH the ceiling and the floor, so
                // narrowing the gap between "fast" and "really fast" means narrowing this rather
                // than raising a separate minimum (there isn't one; a lower minimum would mean
                // biasing away from zero on some axes, which biases direction instead of speed).
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
                // stationary (zero velocity) and fading out fast - marks where the streak has
                // been without following it, the same SecondaryParticles pattern the base game
                // uses for ExplosionFireTrailCubicles.
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

            Dictionary<(int ChunkX, int ChunkZ), (int MinY, int MaxY)> columns = ClaimChunkMath.GetCoveredChunkColumns(claims);

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
                StartUnixMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                GatheringStrengthDurationMinutes = config.GatheringStrengthDurationMinutes,
                EnteringRealityDurationMinutes = config.EnteringRealityDurationMinutes,
                Status = UnknowingStormStatus.GatheringStrength,
                ChunkColumns = columns.Select(c => new ChunkColumn(c.Key.ChunkX, c.Key.ChunkZ, c.Value.MinY, c.Value.MaxY)).ToList()
            };

            storms.Add(storm);
            Persist();

            var (centerX, centerGroundY, centerZ) = GetStormCenterPos(storm);

            // Cloud entities aren't spawned here - EnsureCloudsSpawned (called from OnGameTick)
            // picks up every column with CloudEntityId == 0 within one game tick of this storm
            // existing, new or old. One spawn path instead of two.
            BroadcastStormUnleashed(playerName, centerX, centerGroundY, centerZ);

            return TextCommandResult.Success(
                $"The Unknowing descends: {claims.Count} claim(s) for '{playerName}' erased, " +
                $"{storm.ChunkColumns.Count} chunk column(s) now open to loot and monsters. " +
                $"Gathering strength for {storm.GatheringStrengthDurationMinutes:0}m, then entering reality for " +
                $"{storm.EnteringRealityDurationMinutes:0}m before it collapses.");
        }

        // Self-healing: ensures every column of every non-Done storm has a live landmark entity
        // (theunknowing:theunknowing) - a static, tall translucent column meant to solve what
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

                EntityProperties? cloudType = api.World.GetEntityType(new AssetLocation("theunknowing", "theunknowing"));
                if (cloudType == null)
                {
                    api.World.Logger.Warning("[TheUnknowing] theunknowing:theunknowing entity type not found, skipping cloud spawn.");
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

                // "spawn" (see theunknowing's shape/entity JSON) keyframes the column element's
                // StretchY from near-0 to 1, pivoted at the element's rotationOrigin (the top of
                // the column, not the bottom) - so it reads as descending from the sky rather than
                // growing up from the ground.
                //
                // Started before SpawnEntity so the entity's first spawn packet already carries
                // "spawn is running" as part of its baseline synced state, rather than that arriving
                // via a later update. Uses the AnimationMetaData overload, not the
                // entity.StartAnimation(string) convenience one (the pattern AI tasks use, e.g.
                // AiTaskBase.StartAnimation) - that overload resolves the code via
                // entity.Properties.Client.AnimationsByMetaCode (vsapi AnimationManager), and
                // Properties isn't populated until Initialize(), which SpawnEntity is what triggers -
                // calling it pre-spawn would NullReferenceException. AnimManager.StartAnimation
                // (AnimationMetaData) skips that lookup entirely (confirmed via vsapi source), so
                // it's safe to call before SpawnEntity.
                //
                // EaseInSpeed pinned high (default is 10) - every animation's contribution actually
                // fades in via an internal EasingFactor/BlendedWeight that starts at 0 and ramps
                // toward 1 over several frames (see RunningAnimation.Progress/CalcBlendedWeight in
                // vsapi), independent of our own stretchY keyframes and independent of spawn/anim
                // call order above. Confirmed live (twice) that this, not call order, was the real
                // cause of the "full height -> snap to 0 -> grow" flash: the ease-in was blending
                // between the unanimated bind pose (full height) and our animation's own frame 0
                // (near-zero), on top of the keyframe animation we already wrote to do exactly that
                // growth ourselves. A high EaseInSpeed collapses that blend to effectively one frame,
                // leaving our own keyframes as the only thing driving the visible growth.
                var spawnAnim = new AnimationMetaData { Code = "spawn", Animation = "spawn", AnimationSpeed = 1f, EaseInSpeed = 1000f }.Init();
                cloudEntity.AnimManager.StartAnimation(spawnAnim);
                api.World.SpawnEntity(cloudEntity);

                column.CloudEntityId = cloudEntity.EntityId;
                changed = true;
            }

            if (changed) Persist();
        }

        // Escalates every cloud entity this storm owns when EnteringReality begins - "the
        // unknowing's strength in the realm" made visible, the same idea the since-removed
        // particle-based beacon growth had in 0.3, just applied to the cloud entity itself
        // instead. Two effects, both one-time (never reverted - the cloud entity is despawned
        // outright once the storm reaches Collapsing, see FinishCollapse):
        //  - "widen": 12 -> 32 blocks wide (target/current = 2.6667, see the shape's "widen"
        //    animation) while height stays untouched.
        //  - EnteringRealityTintArgb - shifts the white starfield purple (see that field's comment).
        //
        // Stops "spawn" first rather than letting both animations run concurrently on the same
        // element - "widen"'s own frame 0 matches spawn's held final pose (1,1,1) exactly, so
        // there's no visual jump, and it sidesteps any assumption about how multiple simultaneously
        // active animations blend together (untested, and this mod's animation work so far has a
        // strong track record of "the obvious assumption is wrong" - see NOTES.local.md).
        // EaseInSpeed pinned high for the same reason as "spawn" - avoids the same
        // blend-in-from-the-bind-pose flash bug already fixed once on that animation.
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

        // The average of every column's own center point, in block coordinates - used directly
        // when it lands inside a chunk the storm actually covers (the common case for a simple,
        // convex footprint), which can legitimately be a seam between two chunks rather than the
        // dead center of one specific chunk (e.g. a claim that's an even number of chunks wide).
        // Bugs fixed in sequence to get here:
        //
        // 1. The original code picked ChunkColumns[ChunkColumns.Count / 2] as "the center" - the
        //    middle *list index* of whatever order the set happened to enumerate in (ChunkColumns
        //    is built from a HashSet, see ClaimChunkMath.GetCoveredChunkColumns - not spatial, and
        //    not a guaranteed order either way). Confirmed live: the waypoint landed "way south" of
        //    the actual storm.
        // 2. Fixed to a true centroid, used unconditionally - correct for a simple filled
        //    rectangle, but for an L-shaped/multi-area claim the average can fall in a gap that was
        //    never actually part of the storm, or outside it entirely. Confirmed live again: the
        //    next waypoint was ~250 blocks off with GetTerrainMapheightAt returning 1 (no real
        //    terrain there).
        // 3. Fixed by *always* snapping to whichever actual chunk column is nearest the average -
        //    safe (always real, loaded ground), but threw away precision for the common convex
        //    case: confirmed live the waypoint was landing dead center of some chunk, just not the
        //    true center of the whole storm (e.g. a claim an even number of chunks wide has its
        //    real center sitting exactly on the seam between two chunks - snapping always picks one
        //    of them arbitrarily instead).
        //
        // Current fix: use the raw average whenever its own containing chunk is actually covered -
        // giving the true center, seam or not - and only fall back to snapping to the nearest
        // covered column when the average itself lands somewhere the storm doesn't cover (the
        // concave/multi-area case bug 2 introduced).
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

        // Builds the clickable command:// (VTML) link used by every storm broadcast below. VTML
        // has no protocol for opening the map at a location, but dropping a pinned waypoint there
        // gets players to the same place in one click, which is the actual goal - and every
        // broadcast gets its own link (not just the first), so missing/deleting an earlier one
        // isn't a dead end.
        //
        // The real bug behind every "wrong waypoint" investigation so far, found only after the
        // chunk-centroid math itself was confirmed correct via GetStormCenterPos's diagnostic log:
        // /waypoint addati's x/y/z args go through CmdArgs.PopFlexiblePos (see vsapi), which adds
        // the map's center offset to any bare number - `=` is required to mark a coordinate as a
        // literal absolute position (same convention as /tp; documented in the command's own
        // syntax help: "100 150 -180" is map-relative, "=512100 =150 =511880" is absolute). x/z
        // here are already absolute engine coordinates (derived from chunk index * ChunkSize), so
        // without `=` the game was adding the map-center offset a second time on top of them -
        // silently wrong under every version of the center-finding fix, since the bug was never in
        // the coordinates being computed, only in how they were handed to the command.
        private string BuildLocationLink(string playerName, double x, int groundY, double z, string linkText)
        {
            string waypointLink =
                $"command:///waypoint addati spiral ={(int)x} ={groundY} ={(int)z} true #B4145A The Unknowing - {playerName}";
            return $"<a href=\"{waypointLink}\">{linkText}</a>";
        }

        // Tells every online player, not just whoever ran the command, the moment a storm goes
        // up - the whole server should know a base just became lootable and dangerous. No player
        // name in the message itself (deliberate - the target is expected to be announced
        // separately, e.g. in Discord, ahead of the storm), but their name still seeds the
        // waypoint's own label.
        private void BroadcastStormUnleashed(string playerName, double x, int groundY, double z)
        {
            string link = BuildLocationLink(playerName, x, groundY, z, "forgotten lands");
            BroadcastMessage($"<strong>The Unknowing</strong> gathers strength over {link}.");
        }

        private void BroadcastMessage(string html)
        {
            api.BroadcastMessageToAllGroups(html, EnumChatType.Notification, null);
        }

        // Registered on a real-time tick by TheUnknowingModSystem. Handles the GatheringStrength ->
        // EnteringReality -> Collapsing progression, the actual wgen regen + full cleanup once a
        // storm reaches Collapsing (see FinishCollapse), and containment - keeping every
        // storm-owned entity inside the chunk columns it was summoned over. Player storm
        // membership (fog fade) is NOT handled here - see OnMembershipTick, its own faster tick.
        public void OnGameTick()
        {
            long nowMillis = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            bool changed = false;
            List<UnknowingStorm> finishedStorms = new();

            foreach (UnknowingStorm storm in storms)
            {
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

                // Checked as its own condition (not just on the transition edge above) so any
                // storm already sitting in Collapsing - including one that reached that status
                // before this method existed, like a storm already mid-test on a live server -
                // gets picked up and finished on the very next tick, not just storms that
                // transition into it fresh. Nothing calls for a lingering Collapsing phase; only
                // GatheringStrength/EnteringReality have real durations, so this runs immediately.
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

        // The actual ROADMAP 0.4 payoff - despawns everything the storm owns and regenerates the
        // claimed land, then the storm is dropped from tracking entirely (see OnGameTick) since
        // its whole lifecycle is complete. Doesn't bother with the "only drop tracking once a
        // despawn is confirmed" caution ClearAllStorms/RespawnClouds now use (see GOTCHAS.md) -
        // that mattered there because nothing else would ever clean up an unresolved entity once
        // its tracking was gone. Here, RegenClaimedChunks deletes and regenerates every chunk
        // column the storm covers regardless of what's currently loaded, which structurally wipes
        // any entity data left in those chunks (loaded or not) - the despawns below are just for
        // an immediate visual removal if a player happens to be nearby, not the real cleanup
        // mechanism.
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

            // Centroid computed after the regen (not before) so the waypoint's Y reflects the
            // freshly regenerated terrain height, not the pre-collapse claim's.
            var (x, groundY, z) = GetStormCenterPos(storm);
            string link = BuildLocationLink(storm.TargetPlayerName, x, groundY, z, "what was forgotten");
            BroadcastMessage($"<strong>The Unknowing</strong> reclaims {link}.");
        }

        // Runs /wgen regenrange over the rectangular bounding box of every chunk column the storm
        // covers, via a synthetic admin-privileged console Caller (same pattern vanilla itself
        // uses for block/BE-triggered commands - see BEConditional.getCaller in vs-source).
        //
        // Deliberately NOT /wgen regen (the radius-around-a-point subcommand the original 0.4 plan
        // called for) - confirmed by reading WgenCommands.cs that its chunk-range math
        // (GetCoordsFromRange) keys off Caller.Player.Entity.Pos, not Caller.Pos, and silently
        // falls back to the world map center if Player is null. A console-style Caller for a
        // target player who's most likely offline (the mod's entire premise) would hit exactly
        // that fallback - regenerating terrain around world spawn instead of the claim, with no
        // error to catch it. /wgen regenrange takes explicit chunk coordinates instead, with no
        // such fallback, so it's the only safe choice here.
        //
        // Uses the min/max bounding rectangle of each connected *cluster* of chunk columns
        // (see ClusterChunkColumns), rather than one box over storm.ChunkColumns as a whole - a
        // storm bundles every claim a player owns (see StartStorm's claims list), and two
        // separate claims can be anywhere on the map relative to each other, unlike multiple
        // Areas within one claim (the /land command requires those to be adjacent). A single
        // combined bounding box across two far-apart claims would regenrange every chunk of
        // unrelated, unclaimed terrain in between them too - confirmed live via a test with two
        // claims ~2000 blocks apart, where the combined box came out to roughly 500 chunk
        // columns. Clustering first keeps each regenrange call scoped to one real blob of
        // claimed ground - for the common case (one claim, or several adjacent claims/areas)
        // that's still exactly one call with the same box as before; only genuinely separate
        // claims now get their own, tighter calls instead of being merged.
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

        // Standard flood-fill connected-components over the chunk grid, 8-directional (a shared
        // corner counts as adjacent, not just a shared edge) - lenient enough that a claim shaped
        // like two blocks touching only at a corner still regens as one cluster/one call, while
        // anything with an actual gap (the far-apart-claims case this exists for) reliably ends
        // up in separate clusters.
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

        // Registered on its own real-time tick by TheUnknowingModSystem, at
        // config.StormMembershipIntervalSeconds - deliberately NOT the shared 10s OnGameTick
        // (ROADMAP 0.5). A player crossing a storm boundary right after the shared tick fired used
        // to wait up to 10s for the fog to catch up in either direction (blinded 10s after
        // actually clearing the storm, or unprotected 10s after actually entering it). This tick
        // only does a cheap per-online-player chunk-column membership check (no storm state
        // mutation, no Persist), so it's safe to run far more often than the 10s tick.
        //
        // Tells each online player's client whether they're currently inside any storm's chunk
        // bounds, but only sends InStormPacket on an actual transition (entering/leaving), not
        // every tick - the client uses this to push/pop a real AmbientModifier fog effect (see
        // TheUnknowingModSystem.StartClientSide), which needs real client-side rendering state the
        // server has no other way to drive.
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

        // Registered on its own real-time tick by TheUnknowingModSystem, at
        // config.FogParticleIntervalSeconds. Spawns ember particles across every covered chunk
        // column of every non-Done storm. Not persisted - transient visual effect, not storm
        // state.
        //
        // Used to also spawn the void-black "containment wall" fog particles (SpawnFogParticles) -
        // removed once the storm cloud landmark entity (theunknowing:theunknowing) existed to do
        // the "visible from far away" job instead. The particle wall was a real performance
        // hazard: its density scaled with chunk-column count, and (confirmed live) it competed
        // with the client's shared particle render budget under rain badly enough that raising
        // the budget cap 5x didn't fix visible dropout - only removing rain did. The storm cloud
        // entity has none of that risk (real
        // world object, not particles - see GOTCHAS.md), so there's no reason to keep paying that
        // cost just for atmosphere. Interior fog went with it too, on the same reasoning.
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

        // Shared by every per-column fog-tick particle spawn below - column-center terrain
        // height, looked up once per column per tick.
        private int GetColumnGroundY(ChunkColumn column)
        {
            double minX = column.ChunkX * ClaimChunkMath.ChunkSize;
            double minZ = column.ChunkZ * ClaimChunkMath.ChunkSize;
            return api.World.BlockAccessor.GetTerrainMapheightAt(new BlockPos((int)(minX + ClaimChunkMath.ChunkSize / 2), 0, (int)(minZ + ClaimChunkMath.ChunkSize / 2)));
        }

        // Small, fast "ember" particles scattered through the storm - reads as purple live.
        // Color contrast against the storm cloud entity's (theunknowing:theunknowing)
        // void-black plus fast cosmic-ray streaks (see emberParticles setup in the constructor)
        // is meant to sell "dangerous energy," not just "hazy." Liked live - specifically called
        // out as reading well against the cloud, the purple-against-void contrast selling a
        // "cosmic destruction" feel.
        private void SpawnEmberParticles(ChunkColumn column, int groundY, float particlesPerColumn)
        {
            double minX = column.ChunkX * ClaimChunkMath.ChunkSize;
            double minZ = column.ChunkZ * ClaimChunkMath.ChunkSize;

            emberParticles.basePos.Set(minX, groundY, minZ);
            emberParticles.Quantity = NatFloat.createUniform(particlesPerColumn, 0f);

            api.World.SpawnParticles(emberParticles, dualCallByPlayer: null);
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

        // Registered on its own real-time tick by TheUnknowingModSystem, at
        // config.EnemySpawnIntervalSeconds. One spawn attempt per GatheringStrength/EnteringReality
        // storm per tick - Collapsing is wind-down, no new spawns.
        public void OnSpawnTick()
        {
            bool changed = false;

            foreach (UnknowingStorm storm in storms)
            {
                if (storm.Status != UnknowingStormStatus.GatheringStrength &&
                    storm.Status != UnknowingStormStatus.EnteringReality) continue;

                // Prune dead/despawned entities first so the cap reflects what's actually alive,
                // not a lifetime spawn count.
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
        // 32-block column (the old behavior) could land a mob one step from a column it doesn't
        // own, and EnforceContainment despawns on that single step regardless of how it happened
        // to get there. Confirmed live: this was producing near-immediate OutOfBounds despawns
        // unrelated to actually fleeing or being chased off - see UnknowingStormManager audit,
        // 2026-08-23. Margin is in blocks, not a fraction of ChunkSize, so it stays meaningful if
        // ChunkSize ever changed.
        private const int SpawnEdgeMarginBlocks = 6;

        private bool TrySpawnOne(UnknowingStorm storm, List<string> pool, int cap)
        {
            ChunkColumn column = storm.ChunkColumns[api.World.Rand.Next(storm.ChunkColumns.Count)];
            int interiorSpan = ClaimChunkMath.ChunkSize - 2 * SpawnEdgeMarginBlocks;
            int blockX = column.ChunkX * ClaimChunkMath.ChunkSize + SpawnEdgeMarginBlocks + api.World.Rand.Next(interiorSpan);
            int blockZ = column.ChunkZ * ClaimChunkMath.ChunkSize + SpawnEdgeMarginBlocks + api.World.Rand.Next(interiorSpan);

            // The claim's own recorded Y span (see ClaimChunkMath.GetCoveredChunkColumns), not the
            // surface heightmap - an underground base's claim area is well below
            // GetTerrainMapheightAt, and spawning there via the surface would put every enemy far
            // from whatever it's actually supposed to be threatening. The cloud landmark and ember
            // particles stay surface-only (deliberate - see 2026-08-23 audit), but mobs are the
            // actual threat and need to land at the claim's own depth.
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

        // Finds actual solid ground within the claim's own recorded Y span, rather than trusting
        // startY (a blind random draw across the whole span) directly - a claim area is a
        // selection box, and players routinely "grow" it upward for headroom well past the real
        // floor (e.g. a claim saved at floor height then grown up 20 blocks), so most of that
        // span is open air above the one real floor, not standable ground. Confirmed live: mobs
        // spawning mid-air and falling to their death before this existed. Scans downward from
        // startY first (the common case - most of the span is headroom above a single floor
        // below the draw), then upward as a fallback for a genuinely multi-level claim where the
        // draw landed below every floor. Falls back to minY + 1 (matches the old single-floor
        // assumption) if no solid ground is found in either direction at all, e.g. the drawn X/Z
        // happens to sit over a shaft or stairwell hole - same "never let a missing case crash
        // the spawn" posture as the entity-type lookup below.
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

        // Debug/testing-only utility - despawns every entity every tracked storm owns and clears
        // all storm state. Nothing in the real lifecycle calls this; it exists because storms
        // currently never end on their own (Collapsing is still a dead end), so without this,
        // every storm ever created in a test world keeps spawning forever.
        //
        // Confirmed live: IWorldAccessor.GetEntityById only finds entities in currently-loaded
        // chunks - running this command from outside a storm's chunk footprint (e.g. "standing
        // near, not within the chunks") gets a null back for any entity in an unloaded chunk, not
        // proof it's already gone. The old version discarded storms.Clear()'d every storm's
        // tracking regardless, silently orphaning any such entity - un-despawned and now
        // un-trackable, since the record pointing at it was just wiped. Fix: force every storm
        // Done immediately (stops spawning/fog/cloud self-healing even for entities we can't
        // resolve yet), but only actually forget a storm once every entity it owned is confirmed
        // despawned - anything unresolved stays tracked so a rerun closer to the site finishes
        // the job instead of losing it.
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

        // Debug/testing-only utility - despawns every column's current cloud entity and resets
        // CloudEntityId to 0, without touching the rest of the storm. EnsureCloudsSpawned (next
        // OnGameTick, within 10s) then spawns fresh ones against whatever the theunknowing:theunknowing
        // entity type currently looks like. Exists for iterating on the shape/texture without needing a
        // new land claim each time - a shape/geometry change might not be reflected on an
        // already-spawned entity instance, only a freshly spawned one.
        //
        // Only resets CloudEntityId when the despawn is actually confirmed - GetEntityById
        // returning null means "not currently loaded," not "definitely gone" (see ClearAllStorms).
        // Resetting unconditionally on a null result orphans the still-standing entity (its old ID
        // forgotten) while EnsureCloudsSpawned spawns a fresh one right next to it - almost
        // certainly the real explanation for the earlier "stacked clouds" bug, not pure z-fighting.
        public TextCommandResult RespawnClouds()
        {
            int despawned = 0;
            int unresolved = 0;

            foreach (UnknowingStorm storm in storms)
            {
                foreach (ChunkColumn column in storm.ChunkColumns)
                {
                    if (column.CloudEntityId == 0) continue;

                    Entity? cloudEntity = api.World.GetEntityById(column.CloudEntityId);
                    if (cloudEntity == null)
                    {
                        unresolved++;
                        continue;
                    }

                    api.World.DespawnEntity(cloudEntity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                    despawned++;
                    column.CloudEntityId = 0;
                }
            }

            Persist();

            string note = unresolved > 0
                ? $" {unresolved} cloud(s) couldn't be found (likely in an unloaded chunk) - move closer and rerun to catch them."
                : "";
            return TextCommandResult.Success($"Despawned {despawned} cloud(s); fresh ones will spawn within the next game tick.{note}");
        }

        // Debug/testing-only utility - despawns any theunknowing:theunknowing entity within range of
        // the given position that isn't currently owned by a tracked storm. Exists specifically to
        // clean up clouds orphaned by the old ClearAllStorms/RespawnClouds bug (entities sitting in
        // a chunk that wasn't loaded when those commands ran, whose tracking got discarded anyway)
        // - both are fixed now to never discard tracking without a confirmed despawn, but this is
        // still useful as a general "find and remove a stray by eye" tool. Skips anything still
        // referenced by a storm's CloudEntityId so it can't accidentally kill an active storm's
        // landmark out from under it.
        public TextCommandResult KillNearbyClouds(Vec3d position, double range)
        {
            HashSet<long> trackedIds = storms
                .SelectMany(s => s.ChunkColumns)
                .Where(c => c.CloudEntityId != 0)
                .Select(c => c.CloudEntityId)
                .ToHashSet();

            Entity[] nearby = api.World.GetEntitiesAround(position, (float)range, (float)range,
                e => e.Code.Domain == "theunknowing" && e.Code.Path == "theunknowing");

            int killed = 0;
            foreach (Entity entity in nearby)
            {
                if (trackedIds.Contains(entity.EntityId)) continue;

                api.World.DespawnEntity(entity, new EntityDespawnData { Reason = EnumDespawnReason.Removed });
                killed++;
            }

            int stillTracked = nearby.Length - killed;
            return TextCommandResult.Success(
                $"Despawned {killed} untracked storm cloud entity/entities within {range:0} blocks" +
                (stillTracked > 0 ? $" ({stillTracked} nearby left alone - still owned by a tracked storm)." : "."));
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
                $"startUnixMillis={storm.StartUnixMillis} gatheringMinutes={storm.GatheringStrengthDurationMinutes:0.0} " +
                $"enteringMinutes={storm.EnteringRealityDurationMinutes:0.0}");

            return TextCommandResult.Success(
                $"{storms.Count} tracked storm(s), {TotalTrackedEntities()} mob(s) tracked total:\n" + string.Join("\n", lines));
        }

        private void Persist()
        {
            api.WorldManager.SaveGame.StoreData(SaveDataKey, storms);
        }

        // Sum of SpawnedEntityIds across every tracked storm - the mod-wide "how many mobs does
        // TheUnknowing currently believe are alive" figure, logged alongside every individual
        // spawn/despawn/prune event below so a per-storm count can be cross-checked against the
        // total (e.g. spotting whether one storm's cap is being bypassed by entities actually
        // tracked under another).
        private int TotalTrackedEntities() => storms.Sum(s => s.SpawnedEntityIds.Count);
    }
}
