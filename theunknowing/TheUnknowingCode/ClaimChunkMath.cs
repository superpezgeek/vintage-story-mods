using System;
using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace TheUnknowing
{
    public static class ClaimChunkMath
    {
        public const int ChunkSize = 32;

        // Union of chunk columns touched by every area of every given claim, plus the vertical
        // (Y) span of whichever area(s) touch each column - mob spawning needs that span to land
        // at the claim's own depth rather than the surface. A claim can have multiple disjoint
        // Areas and a player can own multiple claims, so both the column set and each column's Y
        // span are unions, not assuming one contiguous cuboid.
        public static Dictionary<(int ChunkX, int ChunkZ), (int MinY, int MaxY)> GetCoveredChunkColumns(IEnumerable<LandClaim> claims)
        {
            var columns = new Dictionary<(int, int), (int MinY, int MaxY)>();

            foreach (LandClaim claim in claims)
            {
                foreach (Cuboidi area in claim.Areas)
                {
                    // MinX/MaxX, not Start.X/End.X - Cuboidi doesn't guarantee X1 <= X2, and an
                    // area dragged from its "high" corner to its "low" corner silently produces
                    // zero chunk columns from Start/End directly.
                    int minChunkX = area.MinX >> 5;
                    int maxChunkX = area.MaxX >> 5;
                    int minChunkZ = area.MinZ >> 5;
                    int maxChunkZ = area.MaxZ >> 5;

                    for (int chunkX = minChunkX; chunkX <= maxChunkX; chunkX++)
                    {
                        for (int chunkZ = minChunkZ; chunkZ <= maxChunkZ; chunkZ++)
                        {
                            var key = (chunkX, chunkZ);
                            columns[key] = columns.TryGetValue(key, out var existing)
                                ? (Math.Min(existing.MinY, area.MinY), Math.Max(existing.MaxY, area.MaxY))
                                : (area.MinY, area.MaxY);
                        }
                    }
                }
            }

            return columns;
        }

        // Math.Floor before the shift matters for fractional entity coordinates - (int)(-0.5)
        // truncates to 0, not -1, which would put a position just west/north of the origin in
        // the wrong chunk.
        public static (int ChunkX, int ChunkZ) ToChunkColumn(double blockX, double blockZ)
        {
            return ((int)Math.Floor(blockX) >> 5, (int)Math.Floor(blockZ) >> 5);
        }
    }
}
