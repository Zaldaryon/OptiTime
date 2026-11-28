using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Xunit;

namespace OptiTime.Tests
{
    [Collection("OptiTimeModLifecycle")]
    public sealed class BloomFramebufferOptimizationTests : IDisposable
    {
        public void Dispose()
        {
            BloomFramebufferOptimization.Cleanup();
        }

        [Fact]
        public void TryComputeHalfSizeValidatesBuffersAndComputesHalfDimensions()
        {
            Assert.False(BloomFramebufferOptimization.TryComputeHalfSize(null, out _, out _, out _));
            Assert.False(BloomFramebufferOptimization.TryComputeHalfSize(new List<FrameBufferRef>(), out _, out _, out _));

            var buffers = new List<FrameBufferRef>(8);
            for (int i = 0; i < 8; i++)
            {
                buffers.Add(null);
            }

            buffers[(int)EnumFrameBuffer.Primary] = MakeFb(1, 10, 3840, 2160);
            buffers[(int)EnumFrameBuffer.FindBright] = MakeFb(2, 11, 3840, 2160);

            Assert.True(BloomFramebufferOptimization.TryComputeHalfSize(buffers, out FrameBufferRef fb, out int halfW, out int halfH));
            Assert.Same(buffers[(int)EnumFrameBuffer.FindBright], fb);
            Assert.Equal(1920, halfW);
            Assert.Equal(1080, halfH);

            buffers[(int)EnumFrameBuffer.FindBright].Disposed = true;
            Assert.False(BloomFramebufferOptimization.TryComputeHalfSize(buffers, out _, out _, out _));
        }

        [Fact]
        public void ApplyToFrameBuffersIsIdempotentWhenAlreadyHalfResAndFailsOpenWhenDisabled()
        {
            var buffers = new List<FrameBufferRef>(8);
            for (int i = 0; i < 8; i++)
            {
                buffers.Add(null);
            }

            buffers[(int)EnumFrameBuffer.Primary] = MakeFb(1, 10, 3840, 2160);
            buffers[(int)EnumFrameBuffer.FindBright] = MakeFb(2, 11, 1920, 1080);

            BloomFramebufferOptimization.Configure(false);
            Assert.False(BloomFramebufferOptimization.ApplyToFrameBuffers(buffers));
            Assert.False(BloomFramebufferOptimization.IsActive);

            BloomFramebufferOptimization.Configure(true);
            Assert.True(BloomFramebufferOptimization.ApplyToFrameBuffers(buffers));
            Assert.True(BloomFramebufferOptimization.IsActive);
            Assert.Equal(1920, BloomFramebufferOptimization.ActiveWidth);
            Assert.Equal(1080, BloomFramebufferOptimization.ActiveHeight);

            BloomFramebufferOptimization.Cleanup();
            Assert.False(BloomFramebufferOptimization.IsActive);
            Assert.Equal(0, BloomFramebufferOptimization.ActiveWidth);
            Assert.Equal(0, BloomFramebufferOptimization.ActiveHeight);
        }

        private static FrameBufferRef MakeFb(int fboId, int texId, int width, int height)
        {
            return new FrameBufferRef
            {
                FboId = fboId,
                Width = width,
                Height = height,
                ColorTextureIds = new[] { texId }
            };
        }
    }
}
