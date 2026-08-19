using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace TheUnknowing
{
    public static class ClaimChunkMath
    {
        public const int ChunkSize = 32;

        // Union of chunk columns touched by every area of every given claim. A single claim can
        // have multiple disjoint Areas (e.g. an L-shaped base), and a player can own multiple
        // claims, so this always unions rather than assuming one contiguous cuboid.
        //
        // Uses arithmetic right-shift rather than division by ChunkSize - integer division
        // truncates toward zero, which gives the wrong chunk index for negative coordinates
        // (-33 / 32 == -1, but chunk -33 is actually in column -2). Shifting rounds toward
        // negative infinity instead, matching how the engine assigns block columns to chunks.
        public static HashSet<(int ChunkX, int ChunkZ)> GetCoveredChunkColumns(IEnumerable<LandClaim> claims)
        {
            var columns = new HashSet<(int, int)>();

            foreach (LandClaim claim in claims)
            {
                foreach (Cuboidi area in claim.Areas)
                {
                    // MinX/MaxX (not Start.X/End.X) - Cuboidi doesn't guarantee X1 <= X2 or
                    // Z1 <= Z2 (that's exactly why it has separate Min/Max properties). A claim
                    // area dragged from its "high" corner to its "low" corner has Start.X > End.X,
                    // which silently produced zero chunk columns for that area (the loop below
                    // never runs when minChunkX > maxChunkX) despite the area being real and
                    // non-empty - confirmed live via a claim whose storm came back with 0 chunk
                    // columns.
                    int minChunkX = area.MinX >> 5;
                    int maxChunkX = area.MaxX >> 5;
                    int minChunkZ = area.MinZ >> 5;
                    int maxChunkZ = area.MaxZ >> 5;

                    for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
                    {
                        for (int chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
                        {
                            columns.Add((chunkX, chunkZ));
                        }
                    }
                }
            }

            return columns;
        }

        // Same block->chunk math as above, for a live (fractional) entity position rather than a
        // claim's integer block bounds. Math.Floor before the shift matters here specifically
        // because entity coordinates are doubles - (int)(-0.5) truncates to 0, not -1, which
        // would put a position just west/north of the origin in the wrong chunk.
        public static (int ChunkX, int ChunkZ) ToChunkColumn(double blockX, double blockZ)
        {
            return ((int)Math.Floor(blockX) >> 5, (int)Math.Floor(blockZ) >> 5);
        }
    }
}
