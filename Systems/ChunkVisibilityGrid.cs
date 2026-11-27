using System;

namespace OptiTime
{
    /// <summary>
    /// Flat-grid conservative breadth-first chunk visibility flood for ChunkCuller.
    ///
    /// Snapshots per-chunk traversability bits into a pre-sized 3D array centered on the
    /// camera chunk, then floods monotonically outward using a 6-face entry mask so every
    /// cell is enqueued at most once and steady-state passes allocate zero bytes.
    ///
    /// Cell encoding: bit 15 marks a loaded chunk; bits 0-14 hold the 15 vanilla
    /// ClientChunk.traversability face-pair bits. Unloaded cells and chunks whose
    /// traversability data is not fresh are stored as AllPass and never block the flood.
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

        // Precomputed lookup [exitFace * 64 + entryFaceMask]: OR of pair bits connecting any entered face to exitFace.
        private static readonly ushort[] EntryMask = new ushort[6 * 64];

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

            for (int f = 0; f < 6; f++)
            {
                for (int entries = 0; entries < 64; entries++)
                {
                    ushort mask = 0;
                    for (int e = 0; e < 6; e++)
                    {
                        if ((entries & (1 << e)) != 0 && e != f)
                        {
                            mask |= (ushort)(1 << PairBit[e * 6 + f]);
                        }
                    }

                    EntryMask[f * 64 + entries] = mask;
                }
            }

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
        /// Floods outward monotonically from the camera cell and records every reached grid
        /// index in Queue. Passing dimension-local camY ensures floor and ceiling bounds match
        /// world height inside extra dimensions as well as the main dimension.
        /// </summary>
        public void Run(int camX, int camY, int camZ, int mapSizeX, int mapSizeY, int mapSizeZ, bool useOcclusion)
        {
            if (CellCount == 0 || Cells.Length < CellCount || Queue.Length < CellCount)
            {
                QueueLength = 0;
                return;
            }

            int w = Width;
            int h = Height;
            int r = RadiusXZ;
            int ry = RadiusY;
            ushort[] cells = Cells;
            byte[] st = state;
            int[] queue = Queue;
            bool aboveWorld = camY >= mapSizeY;

            Array.Clear(st, 0, CellCount);

            int cx = r;
            int cy = ry;
            int cz = r;
            int head = 0;
            int tail = 0;
            int start = (cy * w + cz) * w + cx;
            st[start] = ReachedBit;
            queue[tail++] = start;

            while (head < tail)
            {
                int idx = queue[head++];
                int lx = idx % w;
                int rest = idx / w;
                int lz = rest % w;
                int ly = rest / w;

                int ox = lx - cx;
                int oy = ly - cy;
                int oz = lz - cz;
                int dist = (ox < 0 ? -ox : ox) + (oy < 0 ? -oy : oy) + (oz < 0 ? -oz : oz);

                int cell = cells[idx];
                int trav = cell & AllPass;
                int entries = st[idx] & 63;

                // Matches vanilla: traversability only blocks loaded chunks beyond the first ring.
                bool check = useOcclusion && dist > 1 && (cell & LoadedBit) != 0;

                for (int f = 0; f < 6; f++)
                {
                    int fdx = FaceDx[f];
                    int fdy = FaceDy[f];
                    int fdz = FaceDz[f];

                    if (fdx != 0 && ox != 0 && ((ox > 0) != (fdx > 0)))
                    {
                        continue;
                    }

                    if (fdy != 0 && oy != 0 && ((oy > 0) != (fdy > 0)))
                    {
                        continue;
                    }

                    if (fdz != 0 && oz != 0 && ((oz > 0) != (fdz > 0)))
                    {
                        continue;
                    }

                    int nlx = lx + fdx;
                    int nly = ly + fdy;
                    int nlz = lz + fdz;
                    if ((uint)nlx >= (uint)w || (uint)nlz >= (uint)w || (uint)nly >= (uint)h)
                    {
                        continue;
                    }

                    int wx = camX + (nlx - cx);
                    int wy = camY + (nly - cy);
                    int wz = camZ + (nlz - cz);
                    if (!(wx >= 0 && wy >= 0 && wz >= 0 && wx < mapSizeX && wy < mapSizeY && wz < mapSizeZ)
                        && !(aboveWorld && wy > 0))
                    {
                        continue;
                    }

                    if (check && (trav & EntryMask[f * 64 + entries]) == 0)
                    {
                        continue;
                    }

                    int nidx = (nly * w + nlz) * w + nlx;
                    byte mask = st[nidx];
                    st[nidx] = (byte)(mask | ReachedBit | (1 << FaceOpposite[f]));
                    if ((mask & ReachedBit) == 0)
                    {
                        queue[tail++] = nidx;
                    }
                }
            }

            QueueLength = tail;
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
