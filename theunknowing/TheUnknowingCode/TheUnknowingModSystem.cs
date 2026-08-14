using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace TheUnknowing
{
    public class TheUnknowingModSystem : ModSystem
    {
        public override void StartServerSide(ICoreServerAPI api)
        {
            base.StartServerSide(api);

            api.ChatCommands.Create("unknowing-storm")
                .WithAlias(new[] { "unknowingstorm" })
                .WithDescription("Summons an Unknowing Storm over every land claim owned by the given player (offline is fine).")
                .RequiresPrivilege(Privilege.controlserver)
                .WithArgs(api.ChatCommands.Parsers.Word("playerName"))
                .HandleWith(args => OnUnknowingStorm(api, (string)args[0]));
        }

        // Targets by LastKnownOwnerName rather than resolving a live player UID - the whole
        // point is summoning this on someone who has already quit, so there's no online player
        // (or PlayerUidMapping lookup) to resolve against. Matches the engine's own convention
        // for offline-safe admin claim commands (/land adminfree <playerName>).
        //
        // This is a targeting dry run only for now - reports what would be hit, but doesn't
        // touch the claim or the world yet. Storm lifecycle (suppression, containment, VFX,
        // regen) is still to come.
        private static TextCommandResult OnUnknowingStorm(ICoreServerAPI api, string playerName)
        {
            List<LandClaim> claims = api.World.Claims.All
                .Where(claim => string.Equals(claim.LastKnownOwnerName, playerName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (claims.Count == 0)
            {
                return TextCommandResult.Error($"No land claims found for a player last known as '{playerName}'.");
            }

            HashSet<(int ChunkX, int ChunkZ)> chunks = ClaimChunkMath.GetCoveredChunkColumns(claims);

            return TextCommandResult.Success(
                $"The Unknowing stirs: {claims.Count} claim(s) for '{playerName}' spanning {chunks.Count} chunk column(s). " +
                "(Storm not implemented yet - targeting dry run only.)");
        }
    }
}
