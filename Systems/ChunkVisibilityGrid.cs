using System;
using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace OptiTime
{
    /// <summary>
    /// Flat-grid shell raycaster for ChunkCuller.
    ///
    /// Snapshots per-chunk traversability bits into a pre-sized 3D array centered on the
    /// camera chunk, then traces vanilla-equivalent straight shell rays outside chunksLock
    /// with O(1) per-step axis-slab stepping so occlusion stays as tight as vanilla without
    /// lock contention, dictionary lookups, or steady-state allocations.
    ///
    /// Cell encoding: bit 15 marks a loaded chunk; bits 0-14 hold the 15 vanilla
    /// ClientChunk.traversability face-pair bits. Unloaded cells and chunks whose
    /// traversability data is not fresh are stored as AllPass and never block rays.
    /// Faces follow BlockFacing.Index order: N=0, E=1, S=2, W=3, U=4, D=5.
    /// </summary>
    public sealed class ChunkVisibilityGrid
    {
        /// <summary>Chunk Y stride per dimension (32,768 blocks / 32 blocks per chunk).</summary>
        public const int ChunksPerDimension = 1024;

        /// <summary>Bit 15: a loaded chunk exists at this cell.</summary>
        public const ushort LoadedBit = 0x8000;

        /// <summary>All 15 face-pair traversability bits set.</summary>
        public const ushort AllPass = 0x7FFF;

        private const byte ReachedBit = 64;

        /// <summary>Minimum interval between recalculations while the camera stays in the same chunk.</summary>
        public const long RecalcCooldownMs = 150;

        /// <summary>Extended cooldown while the traversability regeneration backlog is very large.</summary>
        public const long BigBacklogCooldownMs = 1000;

        /// <summary>Regeneration queue threshold above which BigBacklogCooldownMs applies.</summary>
        public const int BigBacklogThreshold = 2000;

        /// <summary>Minimum regeneration queue delta that triggers a same-chunk recalculation.</summary>
        public const int SignificantQueueDelta = 10;

        internal static readonly int[] PairBit = new int[36];

        private static readonly int[] FaceDx = { 0, 1, 0, -1, 0, 0 };
        private static readonly int[] FaceDy = { 0, 0, 0, 0, 1, -1 };
        private static readonly int[] FaceDz = { -1, 0, 1, 0, 0, 0 };
        private static readonly int[] FaceOpposite = { 2, 3, 0, 1, 5, 4 };

        static ChunkVisibilityGrid()
        {
            for (int i = 0; i < 36; i++)
            {
                PairBit[i] = -1;
            }

            // Matches ClientChunk.traversabilityMapping face-pair bit layout.
            SetPair(0, 1, 0);  // N-E
            SetPair(0, 3, 1);  // N-W
            SetPair(0, 2, 2);  // N-S
            SetPair(0, 4, 3);  // N-U
            SetPair(0, 5, 4);  // N-D
            SetPair(1, 2, 5);  // E-S
            SetPair(1, 3, 6);  // E-W
            SetPair(1, 4, 7);  // E-U
            SetPair(1, 5, 8);  // E-D
            SetPair(2, 3, 9);  // S-W
            SetPair(2, 4, 10); // S-U
            SetPair(2, 5, 11); // S-D
            SetPair(3, 4, 12); // W-U
            SetPair(3, 5, 13); // W-D
            SetPair(4, 5, 14); // U-D

            static void SetPair(int a, int b, int bit)
            {
                PairBit[a * 6 + b] = bit;
                PairBit[b * 6 + a] = bit;
            }
        }

        public int RadiusXZ { get; private set; }
        public int RadiusY { get; private set; }
        public int Width { get; private set; }
        public int Height { get; private set; }
        public int CellCount { get; private set; }
        public int QueueLength { get; private set; }

        internal ushort[] Cells = Array.Empty<ushort>();
        internal int[] Queue = Array.Empty<int>();
        private byte[] state = Array.Empty<byte>();
        private int[] shellOffsets = Array.Empty<int>();
        private int shellPointCount;
        private int cachedShellRadius = -1;
        private int cachedShellMapY = -1;

        /// <summary>
        /// Sizes backing buffers for the current pass and clears the snapshot array.
        /// Arrays only grow, so steady view distance allocates nothing after the first pass.
        /// </summary>
        public void Prepare(int radiusXZ, int radiusY)
        {
            if (radiusXZ < 1)
            {
                radiusXZ = 1;
            }

            if (radiusY < 1)
            {
                radiusY = 1;
            }

            RadiusXZ = radiusXZ;
            RadiusY = radiusY;
            Width = 2 * radiusXZ + 1;
            Height = 2 * radiusY + 1;
            CellCount = Width * Width * Height;

            if (Cells.Length < CellCount)
            {
                Cells = new ushort[CellCount];
                state = new byte[CellCount];
                Queue = new int[CellCount];
            }
            else
            {
                Array.Clear(Cells, 0, CellCount);
            }

            QueueLength = 0;
        }

        public int IndexOf(int lx, int ly, int lz) => (ly * Width + lz) * Width + lx;

        public void SetCell(int index, ushort encoded) => Cells[index] = encoded;

        public int ReachedCell(int index) => Queue[index];

        public static ushort EncodeCell(ushort traversability, bool fresh)
            => (ushort)(LoadedBit | (fresh ? (traversability & AllPass) : AllPass));

        /// <summary>
        /// Traces straight shell rays over the flat snapshot grid centered on the camera cell
        /// and records every reached grid index once in Queue. Passing dimension-local camY
        /// ensures floor and ceiling bounds match world height inside extra dimensions as well
        /// as the main dimension.
        /// </summary>
        public void Run(int camX, int camY, int camZ, int mapSizeX, int mapSizeY, int mapSizeZ, bool useOcclusion)
        {
            if (CellCount == 0 || Cells.Length < CellCount || Queue.Length < CellCount)
            {
                QueueLength = 0;
                return;
            }

            int shellRadius = Math.Max(1, RadiusXZ - 3);
            EnsureShellPoints(shellRadius, mapSizeY);

            int w = Width;
            int h = Height;
            int r = RadiusXZ;
            int ry = RadiusY;
            ushort[] cells = Cells;
            byte[] st = state;
            int[] queue = Queue;
            bool aboveWorld = camY >= mapSizeY;

            Array.Clear(st, 0, CellCount);

            int tail = 0;
            int startIdx = (ry * w + r) * w + r;
            st[startIdx] = ReachedBit;
            queue[tail++] = startIdx;

            int[] offsets = shellOffsets;
            int count = shellPointCount;
            for (int i = 0; i < count; i++)
            {
                int baseOff = i * 3;
                int relX = offsets[baseOff];
                int relY = offsets[baseOff + 1];
                int relZ = offsets[baseOff + 2];

                TraceRay(cells, st, queue, ref tail, w, h, r, ry, camX, camY, camZ, mapSizeX, mapSizeY, mapSizeZ, aboveWorld, useOcclusion, relX, relY, relZ, 0.25, 0.5);
                TraceRay(cells, st, queue, ref tail, w, h, r, ry, camX, camY, camZ, mapSizeX, mapSizeY, mapSizeZ, aboveWorld, useOcclusion, relX, relY, relZ, 0.75, 0.5);
                TraceRay(cells, st, queue, ref tail, w, h, r, ry, camX, camY, camZ, mapSizeX, mapSizeY, mapSizeZ, aboveWorld, useOcclusion, relX, relY, relZ, 0.75, 0.0);
            }

            QueueLength = tail;
        }

        private void EnsureShellPoints(int shellRadius, int mapSizeY)
        {
            if (cachedShellRadius == shellRadius && cachedShellMapY == mapSizeY)
            {
                return;
            }

            var unique = new HashSet<(int X, int Y, int Z)>();
            Vec2i[] outer = ShapeUtil.GetOctagonPoints(0, 0, shellRadius);
            for (int i = 0; i < outer.Length; i++)
            {
                Vec2i p = outer[i];
                for (int j = -mapSizeY; j <= mapSizeY; j++)
                {
                    unique.Add((p.X, j, p.Y));
                }
            }

            for (int k = 0; k < shellRadius; k++)
            {
                Vec2i[] ring = ShapeUtil.GetOctagonPoints(0, 0, k);
                for (int i = 0; i < ring.Length; i++)
                {
                    Vec2i p = ring[i];
                    unique.Add((p.X, -mapSizeY, p.Y));
                    unique.Add((p.X, mapSizeY, p.Y));
                }
            }

            int[] packed = new int[unique.Count * 3];
            int idx = 0;
            foreach (var pt in unique)
            {
                packed[idx++] = pt.X;
                packed[idx++] = pt.Y;
                packed[idx++] = pt.Z;
            }

            shellOffsets = packed;
            shellPointCount = unique.Count;
            cachedShellRadius = shellRadius;
            cachedShellMapY = mapSizeY;
        }

        private static void TraceRay(
            ushort[] cells,
            byte[] st,
            int[] queue,
            ref int tail,
            int w,
            int h,
            int r,
            int ry,
            int camX,
            int camY,
            int camZ,
            int mapSizeX,
            int mapSizeY,
            int mapSizeZ,
            bool aboveWorld,
            bool useOcclusion,
            int relX,
            int relY,
            int relZ,
            double yoff,
            double xoff)
        {
            const double DirEps = 1e-5;
            const double TieEps = 1e-12;

            double dx = relX + xoff;
            double dy = relY + yoff;
            double dz = relZ + 0.5;

            int faceX = dx > DirEps ? 1 : (dx < -DirEps ? 3 : -1);
            int faceY = dy > DirEps ? 4 : (dy < -DirEps ? 5 : -1);
            int faceZ = dz > DirEps ? 2 : (dz < -DirEps ? 0 : -1);

            double invDx = faceX >= 0 ? 1.0 / dx : 0.0;
            double invDy = faceY >= 0 ? 1.0 / dy : 0.0;
            double invDz = faceZ >= 0 ? 1.0 / dz : 0.0;

            double planeOffX = faceX == 1 ? 1.0 : 0.0;
            double planeOffY = faceY == 4 ? 1.0 : 0.0;
            double planeOffZ = faceZ == 2 ? 1.0 : 0.0;

            int maxDist = (relX < 0 ? -relX : relX) + (relY < 0 ? -relY : relY) + (relZ < 0 ? -relZ : relZ) + 2;

            int ox = 0;
            int oy = 0;
            int oz = 0;
            int entry = -1;

            while (true)
            {
                int dist = (ox < 0 ? -ox : ox) + (oy < 0 ? -oy : oy) + (oz < 0 ? -oz : oz);
                if (dist > maxDist)
                {
                    break;
                }

                // Pick the earliest exiting slab face, breaking ties in vanilla BlockFacing order (N,E,S,W,U,D).
                int exit = -1;
                double bestT = double.PositiveInfinity;

                if (faceZ >= 0)
                {
                    double tz = (oz + planeOffZ - 0.5) * invDz;
                    if (tz >= -TieEps)
                    {
                        exit = faceZ;
                        bestT = tz;
                    }
                }

                if (faceX >= 0)
                {
                    double tx = (ox + planeOffX - xoff) * invDx;
                    if (tx >= -TieEps && (exit < 0 || tx < bestT - TieEps || (tx <= bestT + TieEps && faceX < exit)))
                    {
                        exit = faceX;
                        bestT = tx;
                    }
                }

                if (faceY >= 0)
                {
                    double ty = (oy + planeOffY - yoff) * invDy;
                    if (ty >= -TieEps && (exit < 0 || ty < bestT - TieEps || (ty <= bestT + TieEps && faceY < exit)))
                    {
                        exit = faceY;
                    }
                }

                if (exit < 0)
                {
                    break;
                }

                int lx = ox + r;
                int ly = oy + ry;
                int lz = oz + r;
                if ((uint)lx < (uint)w && (uint)lz < (uint)w && (uint)ly < (uint)h)
                {
                    int idx = (ly * w + lz) * w + lx;
                    int cell = cells[idx];
                    if ((cell & LoadedBit) != 0)
                    {
                        if ((st[idx] & ReachedBit) == 0)
                        {
                            st[idx] = ReachedBit;
                            queue[tail++] = idx;
                        }

                        if (useOcclusion && dist > 1 && entry >= 0 && entry != exit)
                        {
                            int pairBit = PairBit[entry * 6 + exit];
                            if (((cell >> pairBit) & 1) == 0)
                            {
                                break;
                            }
                        }
                    }
                }

                ox += FaceDx[exit];
                oy += FaceDy[exit];
                oz += FaceDz[exit];
                entry = FaceOpposite[exit];

                int wx = camX + ox;
                int wy = camY + oy;
                int wz = camZ + oz;
                if (!(wx >= 0 && wy >= 0 && wz >= 0 && wx < mapSizeX && wy < mapSizeY && wz < mapSizeZ)
                    && !(aboveWorld && wy > 0))
                {
                    break;
                }
            }
        }

        public static bool ShouldRecalculate(
            bool sameChunk,
            int regenCount,
            int lastRegenCount,
            ref bool pending,
            long nowMs,
            long lastRecalcMs)
            => ShouldRecalculate(
                sameChunk,
                regenCount,
                lastRegenCount,
                ref pending,
                nowMs,
                lastRecalcMs,
                RecalcCooldownMs,
                BigBacklogCooldownMs,
                BigBacklogThreshold,
                SignificantQueueDelta);

        public static bool ShouldRecalculate(
            bool sameChunk,
            int regenCount,
            int lastRegenCount,
            ref bool pending,
            long nowMs,
            long lastRecalcMs,
            long cooldownMs,
            long bigBacklogCooldownMs,
            int bigBacklogThreshold,
            int significantDelta)
        {
            if (!sameChunk)
            {
                pending = false;
                return true;
            }

            int delta = regenCount - lastRegenCount;
            if (delta < 0)
            {
                delta = -delta;
            }

            if (delta >= significantDelta || (regenCount == 0 && lastRegenCount != 0))
            {
                pending = true;
            }

            long minInterval = regenCount > bigBacklogThreshold ? bigBacklogCooldownMs : cooldownMs;
            if (!pending || nowMs - lastRecalcMs < minInterval)
            {
                return false;
            }

            pending = false;
            return true;
        }

        /// <summary>
        /// Converts EntityPlayer.CameraPos.Y into the internal chunk Y coordinate for currentDimension,
        /// reconciling torn cross-thread updates between local Y and DimensionYAdjustment.
        /// </summary>
        public static int ResolveCameraChunkY(double cameraY, int currentDimension)
        {
            if (currentDimension < 0)
            {
                currentDimension = 0;
            }

            int dimOffset = currentDimension * ChunksPerDimension;
            int camChunkY = (int)(cameraY / 32.0);
            if (currentDimension > 0 && camChunkY < dimOffset - (ChunksPerDimension / 2))
            {
                int localY = camChunkY >= 0 ? camChunkY % ChunksPerDimension : camChunkY;
                camChunkY = localY + dimOffset;
            }
            else if (camChunkY >= dimOffset + ChunksPerDimension)
            {
                camChunkY = (camChunkY % ChunksPerDimension) + dimOffset;
            }

            return camChunkY;
        }
    }
}
