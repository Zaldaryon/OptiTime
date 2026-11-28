using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace OptiTime
{
    /// <summary>
    /// Downsizes the FindBright bloom extraction framebuffer to half resolution to match
    /// BlurHorizontalMedRes, which immediately consumes it. Cuts FindBright pixel shading
    /// and RGBA16F VRAM traffic by 4x while preserving full HDR dynamic range and linear
    /// filtering. Fails open to full resolution if a driver reports FBO incompleteness.
    /// </summary>
    public static class BloomFramebufferOptimization
    {
        private static volatile bool enabled;
        private static volatile bool failedOnDriver;
        private static volatile bool active;
        private static volatile int activeWidth;
        private static volatile int activeHeight;
        private static ILogger logger;

        public static bool IsActive => active && enabled && !failedOnDriver;
        internal static int ActiveWidth => activeWidth;
        internal static int ActiveHeight => activeHeight;

        public static void Configure(bool isEnabled, ILogger modLogger = null)
        {
            enabled = isEnabled;
            if (modLogger != null)
            {
                logger = modLogger;
            }
        }

        public static void Cleanup()
        {
            enabled = false;
            failedOnDriver = false;
            active = false;
            activeWidth = 0;
            activeHeight = 0;
            logger = null;
        }

        internal static bool TryComputeHalfSize(List<FrameBufferRef> buffers, out FrameBufferRef findBright, out int halfWidth, out int halfHeight)
        {
            findBright = null;
            halfWidth = 0;
            halfHeight = 0;

            if (buffers == null || buffers.Count <= (int)EnumFrameBuffer.FindBright)
            {
                return false;
            }

            FrameBufferRef primary = buffers[(int)EnumFrameBuffer.Primary];
            FrameBufferRef fb = buffers[(int)EnumFrameBuffer.FindBright];
            if (primary == null || fb == null || primary.Disposed || fb.Disposed ||
                fb.FboId <= 0 || fb.Width < 2 || fb.Height < 2 ||
                fb.ColorTextureIds == null || fb.ColorTextureIds.Length == 0 || fb.ColorTextureIds[0] <= 0)
            {
                return false;
            }

            int targetW = primary.Width / 2;
            int targetH = primary.Height / 2;
            if (targetW < 1 || targetH < 1)
            {
                return false;
            }

            findBright = fb;
            halfWidth = targetW;
            halfHeight = targetH;
            return true;
        }

        public static void SetupDefaultFrameBuffers_Postfix(List<FrameBufferRef> __result)
        {
            ApplyToFrameBuffers(__result, logger);
        }

        public static void LoadFrameBuffer_Postfix(EnumFrameBuffer framebuffer)
        {
            if (active && enabled && !failedOnDriver && framebuffer == EnumFrameBuffer.FindBright)
            {
                int w = activeWidth;
                int h = activeHeight;
                if (w > 0 && h > 0)
                {
                    GL.Viewport(0, 0, w, h);
                }
            }
        }

        public static bool ApplyToFrameBuffers(List<FrameBufferRef> buffers, ILogger modLogger = null)
        {
            if (modLogger != null)
            {
                logger = modLogger;
            }

            if (!enabled || failedOnDriver)
            {
                active = false;
                activeWidth = 0;
                activeHeight = 0;
                return false;
            }

            if (!TryComputeHalfSize(buffers, out FrameBufferRef fb, out int targetW, out int targetH))
            {
                active = false;
                activeWidth = 0;
                activeHeight = 0;
                return false;
            }

            // Already resized to half resolution (idempotent guard).
            if (fb.Width == targetW && fb.Height == targetH)
            {
                activeWidth = targetW;
                activeHeight = targetH;
                active = true;
                return true;
            }

            int origW = fb.Width;
            int origH = fb.Height;
            int prevDrawFbo = 0;
            int prevReadFbo = 0;
            int prevTex = 0;
            bool stateSaved = false;

            try
            {
                GL.GetInteger(GetPName.DrawFramebufferBinding, out prevDrawFbo);
                GL.GetInteger(GetPName.ReadFramebufferBinding, out prevReadFbo);
                GL.GetInteger(GetPName.TextureBinding2D, out prevTex);
                stateSaved = true;

                if (!ReallocateRgba16f(fb, targetW, targetH))
                {
                    ReallocateRgba16f(fb, origW, origH);
                    failedOnDriver = true;
                    active = false;
                    activeWidth = 0;
                    activeHeight = 0;
                    logger?.Warning("[OptiTime] Half-resolution FindBright FBO unsupported on this GPU driver; keeping full resolution");
                    return false;
                }

                fb.Width = targetW;
                fb.Height = targetH;
                activeWidth = targetW;
                activeHeight = targetH;
                active = true;
                return true;
            }
            catch (Exception ex)
            {
                try
                {
                    ReallocateRgba16f(fb, origW, origH);
                    fb.Width = origW;
                    fb.Height = origH;
                }
                catch
                {
                }

                failedOnDriver = true;
                active = false;
                activeWidth = 0;
                activeHeight = 0;
                logger?.Warning("[OptiTime] Half-resolution FindBright FBO failed; keeping full resolution: " + ex.Message);
                return false;
            }
            finally
            {
                if (stateSaved)
                {
                    try
                    {
                        GL.BindTexture(TextureTarget.Texture2D, prevTex);
                        if (prevDrawFbo == prevReadFbo)
                        {
                            GL.BindFramebuffer(FramebufferTarget.Framebuffer, prevDrawFbo);
                        }
                        else
                        {
                            GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, prevDrawFbo);
                            GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, prevReadFbo);
                        }
                    }
                    catch
                    {
                    }
                }
            }
        }

        private static bool ReallocateRgba16f(FrameBufferRef fb, int width, int height)
        {
            int texId = fb.ColorTextureIds[0];

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb.FboId);
            GL.BindTexture(TextureTarget.Texture2D, texId);
            GL.TexImage2D(
                TextureTarget.Texture2D,
                0,
                PixelInternalFormat.Rgba16f,
                width,
                height,
                0,
                PixelFormat.Rgba,
                PixelType.UnsignedShort,
                IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            GL.FramebufferTexture2D(
                FramebufferTarget.Framebuffer,
                FramebufferAttachment.ColorAttachment0,
                TextureTarget.Texture2D,
                texId,
                0);

            return GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) == FramebufferErrorCode.FramebufferComplete;
        }
    }
}
