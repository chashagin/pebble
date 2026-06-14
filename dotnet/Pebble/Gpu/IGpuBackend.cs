// The GPU backend abstraction. Pebble's macOS original is a hand-written Metal
// renderer; on Windows we target BOTH Vulkan and Direct3D 12 behind this one
// interface, selected at startup. The WorldRenderer port issues all GPU work
// through IGpuBackend so the pass structure stays backend-agnostic.
//
// This starts as the minimal swapchain/clear/present surface needed for a
// runnable vertical slice; it grows (buffers, textures, pipelines, draws) as
// the WorldRenderer pipelines are ported on top.

using System;
using Silk.NET.Windowing;

namespace Pebble.Gpu;

public interface IGpuBackend : IDisposable
{
    /// "Vulkan" or "Direct3D 12" — shown in the title bar / logs.
    string Name { get; }

    /// Create device, surface/swapchain, command infrastructure for this window.
    void Initialize(IWindow window);

    /// Recreate the swapchain for a new framebuffer size.
    void Resize(int width, int height);

    /// Acquire the next swapchain image and begin recording this frame.
    void BeginFrame();

    /// Clear the current backbuffer to an RGBA color (0..1).
    void ClearColor(float r, float g, float b, float a);

    /// Render the atmosphere for this frame: a full-screen sky gradient
    /// (zenith→horizon, dawn/dusk sun-glow band) drawn FIRST, behind everything,
    /// with depth-write OFF, plus the sun/moon celestial billboards at the far
    /// plane. Replaces the flat sky clear. The SkyState carries the gradient
    /// colors, sun direction, day fraction and fog (computed from the camera +
    /// engine time-of-day by SkyState.FromWorld). Also caches the sky so the
    /// subsequent RenderWorld uses the matching day-night lighting + fog. Called
    /// AFTER BeginFrame and BEFORE RenderWorld each frame.
    void RenderSky(in SkyState sky, PebbleCore.CamState cam, double timeSeconds);

    /// Draw all live streamed section meshes for this frame: opaque + cutout
    /// first, then the alpha-blended translucent (water/glass) layer with depth
    /// test ON / depth write OFF. Called between RenderSky/RenderEntities and
    /// EndFrame. Lighting + fog use the SkyState cached by the preceding
    /// RenderSky (falling back to full daylight if RenderSky was skipped).
    void RenderWorld(HostBridge host, PebbleCore.CamState cam, double timeSeconds);

    /// Draw the 3D entities (mobs + box-model dropped items) between the world
    /// and the HUD: depth test+write ON (so entities occlude / are occluded by
    /// terrain), back-face cull, each entity sampling its per-model skin texture.
    /// The EntityDrawList holds the per-entity model + part matrices, light/fog
    /// params and skin id (built by Pebble.EntityRenderer from the live world).
    /// Called AFTER RenderWorld and BEFORE RenderUI each frame.
    void RenderEntities(Pebble.EntityRenderer renderer, Pebble.EntityDrawList list,
                        PebbleCore.CamState cam, double timeSeconds);

    /// Draw the block-break crack overlay (destroy_N atlas tiles) over the faces of
    /// the block the player is mining. The BreakOverlay holds a small mesh in the
    /// chunk vertex format + the (block − cam) origin; the backend draws it with its
    /// world (translucent) pipeline, depth-test lessEqual, depth-write OFF, blended.
    /// Called AFTER RenderWorld / RenderEntities, BEFORE RenderParticles. A no-op
    /// when overlay.active is false (player not mining / creative).
    void RenderBreakOverlay(Pebble.BreakOverlay overlay, PebbleCore.CamState cam);

    /// Draw the camera-facing item billboards for the SPRITE_TYPES entities and
    /// dropped items (arrows / thrown items / dropped ItemEntity / fireballs / xp
    /// orbs). Each sprite samples its icon from the backend-owned icon Texture2DArray
    /// (layer resolved via IconResolver), alpha-blended, depth-test ON / write ON so
    /// terrain occludes them. Called AFTER RenderEntities, BEFORE RenderParticles.
    void RenderSprites(Pebble.SpriteList sprites, PebbleCore.CamState cam);

    /// Draw the CPU-simulated particles as instanced camera-facing billboards
    /// sampling the block-atlas Texture2DArray (alpha-blended, depth-test ON /
    /// depth-write OFF). Drawn AFTER entities/sprites so they composite over solid
    /// geometry. The Particles system owns the live list + per-frame instance pack.
    /// Called AFTER RenderSprites, BEFORE RenderUI.
    void RenderParticles(Pebble.Particles particles, PebbleCore.CamState cam);

    /// Draw the 2D UI / HUD on top of the already-rendered world, in a
    /// screen-space orthographic, alpha-blended pass with NO depth. The supplied
    /// UICanvas holds the batched quad vertex stream (filled by the caller's HUD
    /// draw) plus the pixel framebuffer size. Called AFTER RenderWorld and before
    /// EndFrame each frame.
    void RenderUI(Pebble.UI.UICanvas canvas);

    /// Resolves an item-icon to a layer in the backend-owned icon Texture2DArray,
    /// uploading its 16×16 pixels on first use. Assign to UICanvas.IconResolver.
    Pebble.UI.IconLayerResolver IconResolver { get; }

    /// Request that the just-presented frame's color image be read back to CPU
    /// and written to <paramref name="path"/> as a PNG. The capture is performed
    /// during the NEXT EndFrame (after that frame is rendered) so the image
    /// reflects fully-streamed terrain. Returns true if the backend supports it.
    bool RequestScreenshot(string path);

    /// True once a requested screenshot has been written to disk.
    bool ScreenshotComplete { get; }

    /// Finish recording, submit, and present.
    void EndFrame();
}
