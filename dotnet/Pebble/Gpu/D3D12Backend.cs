// Direct3D 12 rendering backend for Pebble (Windows).
//
// A clear-screen-and-present pipeline plus a full streamed-terrain world renderer,
// built on the Silk.NET 2.23.0 Direct3D 12 / DXGI bindings. Double-buffered
// (FlipDiscard) with explicit CPU/GPU fence sync per frame. This is the DX12 peer
// of VulkanBackend.cs and mirrors its structure: atlas texture array + depth +
// per-section vertex/index buffers + camera-relative draws + screenshot readback.
//
// Clear-order contract (matches Program's BeginFrame -> ClearColor/RenderWorld ->
// EndFrame):
//   - BeginFrame() opens the command list, transitions the current backbuffer
//     PRESENT -> RENDER_TARGET, binds RTV + DSV, and clears depth.
//   - ClearColor() records ClearRenderTargetView onto the bound RTV.
//   - RenderWorld() lazily inits the world pipeline (the atlas only exists after
//     the engine registers blocks), clears color to sky, then draws every live
//     section's opaque + cutout layer with a camera-relative viewProj.
//   - EndFrame() optionally copies the backbuffer to a readback buffer for a
//     screenshot, transitions RENDER_TARGET -> PRESENT, closes/executes the list,
//     presents, then signals + waits the fence and advances the frame index.
//
// HLSL (WorldShadersHlsl.cs) is compiled at runtime with D3DCompiler (vs_5_0 /
// ps_5_0). Root signature layout:
//   [0] CBV b0           — the shared ChunkShared constant buffer (root descriptor)
//   [1] root constants b1 — float4 origin (4 x 32-bit), per section
//   [2] SRV table t0     — the Texture2DArray atlas
//   + static sampler s0  — point/clamp
//
// The HWND is obtained from window.Native!.Win32!.Value.Hwnd (the window is
// created with GraphicsAPI.None for DX12, so there is no GL/VK context).

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using Silk.NET.Maths;
using Silk.NET.Windowing;

namespace Pebble.Gpu;

public sealed unsafe class D3D12Backend : IGpuBackend
{
    private const uint FrameCount = 2;

    // Sun shadows are ON BY DEFAULT (enableShadows defaults true from Program.cs;
    // pass --noshadows to disable). The shadow depth pass is fully wired: a
    // double-buffered D32 shadow map (per in-flight frame), a depth-only shadow PSO,
    // a comparison sampler + shadow SRV in the world root signature, and the HLSL
    // chunk_fs PCF sampling. --ultra is the SSAO/bloom post chain (still off here).
    private readonly bool _shadowsEnabled;
    private readonly bool _ultraEnabled;
    public D3D12Backend(bool enableShadows = false, bool enableUltra = false)
    {
        _shadowsEnabled = enableShadows;
        _ultraEnabled = enableUltra;
    }

    private D3D12? _d3d12;
    private DXGI? _dxgi;

    private ComPtr<IDXGIFactory2> _factory;
    private ComPtr<ID3D12Device> _device;
    private ComPtr<ID3D12CommandQueue> _queue;
    private ComPtr<IDXGISwapChain3> _swapChain;
    private ComPtr<ID3D12DescriptorHeap> _rtvHeap;
    private readonly ComPtr<ID3D12Resource>[] _renderTargets = new ComPtr<ID3D12Resource>[FrameCount];
    // ONE command allocator PER frame-in-flight. A single shared allocator is illegal
    // here: BeginFrame resets it every frame, but MoveToNextFrame only waits the fence
    // for the back-buffer index being recycled (FrameCount frames ago), NOT the frame
    // immediately prior — so a shared allocator could be Reset() while the previous
    // frame's command list is still executing on the GPU. The D3D12 debug layer flags
    // that as "command allocator was reset after the command list was recorded" and
    // removes the device (DXGI_ERROR_INVALID_CALL). It only bites under sustained full
    // streaming, when GPU frames run long enough for the reset to race ahead of
    // completion. Per-frame allocators reset allocator[_frameIndex], which
    // MoveToNextFrame has already fenced — so the reset is always safe.
    private readonly ComPtr<ID3D12CommandAllocator>[] _commandAllocators = new ComPtr<ID3D12CommandAllocator>[FrameCount];
    private ComPtr<ID3D12GraphicsCommandList> _commandList;

    // Dedicated upload list (atlas copy): kept separate from the per-frame list so
    // uploads never disturb the frame's render-target state / barriers.
    private ComPtr<ID3D12CommandAllocator> _uploadAllocator;
    private ComPtr<ID3D12GraphicsCommandList> _uploadList;

    private ComPtr<ID3D12Fence> _fence;
    private readonly ulong[] _fenceValues = new ulong[FrameCount];
    private ulong _fenceValue;
    private nint _fenceEvent;

    private uint _rtvDescriptorSize;
    private uint _frameIndex;

    private int _width = 1;
    private int _height = 1;
    private bool _initialized;

    public string Name => "Direct3D 12";

    // ───────────────────────────────────────────────── World renderer state

    // cbuffer b0 layout: float4x4 viewProj; float4x4 shadowMat; vec4 light; vec4
    // fog; vec4 fogColor; vec4 misc. = 2*64 + 4*16 = 192 bytes. CBVs must be a
    // multiple of 256 bytes, so the backing resource is rounded up.
    [StructLayout(LayoutKind.Sequential)]
    private struct ChunkShared
    {
        public Matrix4x4 viewProj;
        public Matrix4x4 shadowMat;
        public Vector4 light;
        public Vector4 fog;
        public Vector4 fogColor;
        public Vector4 misc;
    }

    // SkyU: float4x4 invViewProj; float4 zenith; float4 horizon; float4 horizonSun;
    // float4 sunDir. = 128 B, rounded up to a 256 B CBV.
    [StructLayout(LayoutKind.Sequential)]
    private struct SkyUniform
    {
        public Matrix4x4 invViewProj;
        public Vector4 zenith;
        public Vector4 horizon;
        public Vector4 horizonSun;
        public Vector4 sunDir;
    }

    // CelestialU: float4x4 viewProj; float4 center; float4 right; float4 up. = 128 B.
    [StructLayout(LayoutKind.Sequential)]
    private struct CelestialUniform
    {
        public Matrix4x4 viewProj;
        public Vector4 center;
        public Vector4 right;
        public Vector4 up;
    }

    private const uint VertexStride = 28; // 7 uint32 words
    private const uint CbvSize = 256;     // ChunkShared (192) rounded up to 256
    private const uint SkyCbvSize = 256;  // SkyU / CelestialU (128) rounded up to 256

    private const Format RtvFormat = Format.FormatR8G8B8A8Unorm;
    // The swapchain BUFFER is UNORM (flip-model), but the present RTV reinterprets it
    // as _SRGB so the composite/UI writes get the linear->sRGB gamma encode for free —
    // exactly what the Vulkan backend gets from its B8G8R8A8_SRGB swapchain image. The
    // offscreen-HDR composite outputs LINEAR color; without this encode the swapchain
    // would store raw linear values and the scene renders ~2x too dark (DX12-only).
    private const Format RtvFormatSrgb = Format.FormatR8G8B8A8UnormSrgb;
    private const Format DepthFormat = Format.FormatD32Float;

    private bool _worldReady;

    private ComPtr<ID3D12RootSignature> _rootSignature;
    private ComPtr<ID3D12PipelineState> _opaquePso;   // depth write ON, no blend; alpha test (cbuffer) covers cutout
    private ComPtr<ID3D12PipelineState> _translucentPso; // depth test ON / write OFF, alpha blend

    // ── Atmosphere (sky gradient + sun/moon) ──
    // The sky pass is a 3-vertex full-screen triangle (no VB) drawn FIRST with
    // depth disabled; the celestials are billboards at the far plane with depth
    // read-only. The root signature is a single root CBV b0. One sky CBV + two
    // celestial CBVs are ring-allocated per in-flight frame so a frame never
    // patches a CBV another frame may still be reading.
    private bool _skyReady;
    private const int SkyCbvsPerFrame = 3;       // 1 sky + 2 celestial
    private ComPtr<ID3D12RootSignature> _skyRootSig;
    private ComPtr<ID3D12PipelineState> _skyPso;       // gradient
    private ComPtr<ID3D12PipelineState> _celestialPso; // billboards
    private readonly ComPtr<ID3D12Resource>[] _skyCbuf = new ComPtr<ID3D12Resource>[FrameCount * SkyCbvsPerFrame];
    private readonly void*[] _skyCbufMapped = new void*[FrameCount * SkyCbvsPerFrame];

    // Captured per frame from RenderSky; consumed by RecordSky + RecordWorld.
    private bool _haveSky;
    private SkyState _frameSky;

    // ── Stars (night) + clouds (day) ──
    // Stars: a triangle list (each star CPU-expanded to a quad — D3D has no point
    // size) sampling the unit-sphere star buffer, drawn after the gradient at night.
    // Clouds: a flat alpha-blended quad sampling the procedural cloud texture, drawn
    // after the world (depth read-only). Stars reuse the sky root sig (root CBV b0);
    // clouds need their own root sig (CBV b0 + SRV table t0 + static sampler s0).
    private bool _skyExtraReady;
    private ComPtr<ID3D12PipelineState> _starsPso;
    private ComPtr<ID3D12PipelineState> _cloudPso;
    private ComPtr<ID3D12RootSignature> _starsRootSig;   // CBV b0, allows IA input layout
    private ComPtr<ID3D12RootSignature> _cloudRootSig;
    private readonly ComPtr<ID3D12Resource>[] _starsCbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly void*[] _starsCbufMapped = new void*[FrameCount];
    private readonly ComPtr<ID3D12Resource>[] _cloudCbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly void*[] _cloudCbufMapped = new void*[FrameCount];
    private ComPtr<ID3D12Resource> _starsVbuf;              // 6 verts/star × (pos3 mag1 corner2)
    private uint _starsVertexCount;
    private ComPtr<ID3D12Resource> _cloudTexture;
    private ComPtr<ID3D12DescriptorHeap> _cloudSrvHeap;

    [StructLayout(LayoutKind.Sequential)]
    private struct StarsUniform { public Matrix4x4 viewProj; public Vector4 paramsv; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CloudUniform { public Matrix4x4 viewProj; public Vector4 offset; public Vector4 scroll; }
    private const uint StarsCbvSize = 256;   // 64 + 16 -> 256
    private const uint CloudCbvSize = 256;   // 64 + 16 + 16 -> 256

    // Shared constant buffer (one, updated each frame). Persistently mapped upload
    // heap — fine for the single in-flight world draw recorded inside the frame.
    private ComPtr<ID3D12Resource> _cbuffer;
    private void* _cbufferMapped;

    // Second constant buffer for the translucent pass: opaque/cutout and
    // translucent draws use different fog.z/fog.w, and the GPU reads the CBV at
    // execution time — so they must be DISTINCT buffers (one root CBV can't hold
    // both values for draws sharing the open command list).
    private ComPtr<ID3D12Resource> _cbufferTrans;
    private void* _cbufferTransMapped;

    // Atlas texture array (Texture2DArray) + its SRV in a shader-visible heap.
    private ComPtr<ID3D12Resource> _atlasTexture;
    private ComPtr<ID3D12DescriptorHeap> _srvHeap;
    private uint _atlasLayers;
    private int _atlasTile;

    // Animated-tile driver (water/lava/fire/portal). Built from the pack's .mcmeta
    // frames; advanced each frame, with changed slices copied into the atlas at the
    // START of the per-frame command list (before any sampling), from a persistent
    // upload buffer ring so an in-flight frame never reads a half-written slot.
    private TileAnimator? _animator;
    private ComPtr<ID3D12Resource> _animUpload;
    private void* _animUploadMapped;
    private uint _animUploadSlotBytes;          // 256-aligned per-slice footprint
    private uint _animUploadSlots;
    private uint _animUploadCursor;
    private uint _animRowPitch;                 // dst row pitch (256-aligned)
    private double _lastAnimTime = -1;

    // Depth buffer + DSV heap.
    private ComPtr<ID3D12Resource> _depthBuffer;
    private ComPtr<ID3D12DescriptorHeap> _dsvHeap;

    // ── sun shadow map (DOUBLE-BUFFERED like Vulkan) ──
    // A depth-only D32 texture rendered from the sun direction (shadow_vs) in a
    // separate pass before the world pass, then sampled by chunk_fs (3×3 PCF via a
    // comparison sampler). One image + DSV + SRV per in-flight frame, so frame N's
    // shadow write never races frame N-1's still-in-flight sample (the cross-frame
    // hazard that forced shadows off). The world root signature gains a 2-descriptor
    // SRV table (atlas t0 + shadow t1) and a comparison static sampler (s1).
    private const int ShadowSize = 2048;
    private bool _shadowReady;
    private readonly ComPtr<ID3D12Resource>[] _shadowTex = new ComPtr<ID3D12Resource>[FrameCount];
    private ComPtr<ID3D12DescriptorHeap> _shadowDsvHeap;   // FrameCount DSVs
    private ComPtr<ID3D12DescriptorHeap> _shadowSrvHeap;   // FrameCount × (atlas SRV + shadow SRV) — shader-visible
    private uint _shadowSrvIncrement;
    private uint _shadowDsvIncrement;
    private ComPtr<ID3D12RootSignature> _shadowRootSig;    // CBV b0 + 32-bit consts b1 (origin)
    private ComPtr<ID3D12PipelineState> _shadowPso;        // depth-only, front-cull, depth bias
    // Per-frame shadow constant buffer (shadowMat in viewProj slot).
    private readonly ComPtr<ID3D12Resource>[] _shadowCbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly void*[] _shadowCbufMapped = new void*[FrameCount];
    private Matrix4x4 _frameShadowMat = Matrix4x4.Identity;
    private bool _frameShadowsOn;

    // ── offscreen-HDR post-processing chain ──
    // The whole 3D scene (sky/world/entities/sprites/particles/break/clouds) renders
    // into an R16G16B16A16_FLOAT HDR target (double-buffered per back-buffer index)
    // instead of the swapchain; the post chain then bright-passes + blurs (bloom),
    // computes half-res SSAO + volumetric god-rays (ultra), and composites with an
    // ACES filmic tonemap into the swapchain. The HUD draws on the swapchain after.
    //
    // All 3D PSOs target SceneRtvFormat; the composite/UI PSOs target RtvFormat.
    private const Format SceneRtvFormat = Format.FormatR16G16B16A16Float;
    private bool _postReady;
    private int _postW, _postH;
    // HDR scene color (per frame index) + its RTV/SRV. Depth reuses _depthBuffer.
    private readonly ComPtr<ID3D12Resource>[] _hdrColor = new ComPtr<ID3D12Resource>[FrameCount];
    private ComPtr<ID3D12DescriptorHeap> _hdrRtvHeap;   // FrameCount HDR RTVs
    private uint _hdrRtvIncrement;
    // Bloom mips (quarter-res ping-pong) + ultra targets (half-res ping-pong), per
    // frame index. RTV + SRV for each. Plus a depth SRV (for the ultra pass).
    private readonly ComPtr<ID3D12Resource>[] _bloomA = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly ComPtr<ID3D12Resource>[] _bloomB = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly ComPtr<ID3D12Resource>[] _ultraA = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly ComPtr<ID3D12Resource>[] _ultraB = new ComPtr<ID3D12Resource>[FrameCount];
    // Shader-visible SRV heap holding, per frame: hdr, bloomA, bloomB, ultraA, ultraB,
    // sceneDepth, shadow[f]. The composite/blur/extract/ultra passes index into it.
    private ComPtr<ID3D12DescriptorHeap> _postSrvHeap;
    private uint _postSrvIncrement;
    private const int PostSrvPerFrame = 7; // hdr, bloomA, bloomB, ultraA, ultraB, depth, shadow
    // Root signatures + PSOs for the post passes.
    private ComPtr<ID3D12RootSignature> _postRootSig;   // CBV b0 + SRV table (up to 3) + sampler s0
    private ComPtr<ID3D12RootSignature> _ultraRootSig;  // CBV b0 + SRV table (depth + shadow) + sampler s0
    private ComPtr<ID3D12PipelineState> _bloomExtractPso;
    private ComPtr<ID3D12PipelineState> _blurPso;
    private ComPtr<ID3D12PipelineState> _ultraPso;
    private ComPtr<ID3D12PipelineState> _ultraBlurPso;
    private ComPtr<ID3D12PipelineState> _compositePso;
    // Per-frame post constant buffers (CompositeU / UltraU). The blur/ultra-blur use
    // distinct CBVs per direction so two draws in one list don't share one buffer.
    private const uint CompositeCbvSize = 256;  // 3*vec4 = 48 -> 256
    private const uint UltraCbvSize = 256;       // 3 mat4 + 4 vec4 = 256
    private readonly ComPtr<ID3D12Resource>[] _compositeCbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly void*[] _compositeCbufMapped = new void*[FrameCount];
    private readonly ComPtr<ID3D12Resource>[] _ultraCbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly void*[] _ultraCbufMapped = new void*[FrameCount];
    // Blur direction CBVs: bloom H/V + ultra H/V = 4 per frame.
    private readonly ComPtr<ID3D12Resource>[] _blurCbuf = new ComPtr<ID3D12Resource>[FrameCount * 4];
    private readonly void*[] _blurCbufMapped = new void*[FrameCount * 4];

    [StructLayout(LayoutKind.Sequential)]
    private struct CompositeUniform { public Vector4 paramsv; public Vector4 tint; public Vector4 params2; }
    [StructLayout(LayoutKind.Sequential)]
    private struct UltraUniform
    {
        public Matrix4x4 invViewProj;
        public Matrix4x4 viewProj;
        public Matrix4x4 shadowMat;
        public Vector4 sunDir;
        public Vector4 paramsv;
        public Vector4 fogColor;
        public Vector4 texel;
    }

    // Per-section GPU mesh cache. Re-uploaded only when the source mesh changes.
    private sealed class SectionGpu
    {
        public ComPtr<ID3D12Resource> vbuf;
        public ComPtr<ID3D12Resource> ibuf;
        public uint indexCount;
        public uint vbufSize;
        public uint ibufSize;
    }

    private sealed class SectionLayers
    {
        public SectionGpu? opaque;
        public SectionGpu? cutout;
        public SectionGpu? translucent;
        public object? meshRef; // identity of the last-uploaded SectionMesh
    }

    private readonly Dictionary<(int, int, int), SectionLayers> _sectionCache = new();

    // ── Deferred section-buffer destruction ───────────────────────────────────
    //
    // SyncSections releases a section's upload-heap vbuf/ibuf whenever its mesh
    // changes or the section unloads. But those committed resources may still be
    // referenced by an in-flight command list (a prior frame's submission whose
    // fence hasn't signaled yet). Releasing them immediately → the GPU reads freed
    // memory → DXGI_ERROR_DEVICE_HUNG. Instead we DEFER releases through a ring of
    // (FrameCount + 1) buckets; a bucket is only released at the start of a later
    // frame, after MoveToNextFrame has waited the fence guaranteeing the GPU
    // finished the work submitted FrameCount frames ago. (Same root cause and fix
    // as the Vulkan backend; mirrors the macOS original's 3-frame-deferred arena.)
    private const int DeferFreeSlots = (int)FrameCount + 1; // = 3
    private readonly List<SectionGpu>[] _pendingFree = new List<SectionGpu>[DeferFreeSlots];
    private int _deferSlot;

    // Pending world draw captured during RenderWorld, replayed in EndFrame.
    private bool _haveWorldFrame;
    private PebbleCore.CamState _frameCam;
    private double _frameTime;
    private HostBridge? _frameHost;

    // Screenshot capture.
    private string? _screenshotPath;
    private bool _screenshotComplete;
    public bool ScreenshotComplete => _screenshotComplete;
    private ComPtr<ID3D12Resource> _readbackBuffer;
    private string? _pendingReadbackPath;
    private uint _readbackRowPitch;
    private uint _readbackW, _readbackH;

    // ─────────────────────────────────────────────────────────────── UI / HUD
    //
    // Screen-space orthographic, alpha-blended, NO depth pass drawn after the
    // world. Vertex layout (36 bytes): pos2 uv2 layer1 color4. The icon
    // Texture2DArray holds N 16×16 RGBA layers uploaded lazily. Root signature:
    //   [0] 32-bit constants b0 (float4 screen)
    //   [1] SRV table t0 (icon array) + static sampler s0
    private const uint UiVertexStride = 36; // 9 float32
    private const int UiIconLayers = 512;

    private bool _uiReady;
    private bool _iconTextureReady;
    private ComPtr<ID3D12RootSignature> _uiRootSignature;
    private ComPtr<ID3D12PipelineState> _uiPso;

    // Icon texture array + its SRV (in a dedicated shader-visible heap).
    private ComPtr<ID3D12Resource> _iconTexture;
    private ComPtr<ID3D12DescriptorHeap> _iconSrvHeap;
    private int _iconNext = 1; // layer 0 reserved blank
    private readonly Dictionary<string, int> _iconSlots = new();

    // Dedicated allocator/list/fence for icon uploads. Icons upload MID-FRAME
    // (during the HUD draw, while the per-frame command list is open), so they
    // must NOT touch the frame's command allocator or the frame sync fence —
    // doing so corrupts the in-flight frame. This is fully independent.
    private ComPtr<ID3D12CommandAllocator> _iconAllocator;
    private ComPtr<ID3D12GraphicsCommandList> _iconList;
    private ComPtr<ID3D12Fence> _iconFence;
    private ulong _iconFenceValue;
    private nint _iconFenceEvent;

    // Per-frame dynamic vertex buffer (one per in-flight frame, grown as needed).
    private readonly ComPtr<ID3D12Resource>[] _uiVbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly uint[] _uiVbufSize = new uint[FrameCount];

    // Captured per frame from RenderUI, replayed in EndFrame's draw.
    private float[]? _uiVerts;
    private int _uiVertCount;
    private float _uiScreenW, _uiScreenH;

    public Pebble.UI.IconLayerResolver IconResolver => ResolveIconLayer;

    // ─────────────────────────────────────────────────────────── Entities (3D)
    //
    // Depth-tested, back-face-culled pass drawn after the world, before the HUD,
    // on the same open command list. Vertex layout (stride 40): pos f3 @0, normal
    // f3 @12, uv f2 @24, part f1 @32. EntityU (1728 B) is rounded up to a 256-byte
    // CBV (1792 B); one big upload buffer holds every entity's block this frame and
    // each draw binds it via a root CBV at that entity's GPU virtual address. Each
    // model's skin SRV lives in a shared shader-visible heap; the per-draw SRV table
    // points at that model's slot.
    private const uint EntityVertexStride = 40;
    private const int EntityUSize = 1728;
    private const uint EntityCbvStride = 1792; // 1728 rounded up to 256
    private const int MaxEntitiesPerFrame = 512;
    private const int MaxEntityModels = 128;

    private bool _entityReady;
    private ComPtr<ID3D12RootSignature> _entityRootSig;
    private ComPtr<ID3D12PipelineState> _entityPso;

    // Per-frame EntityU upload buffer (persistently mapped; grown as needed).
    private readonly ComPtr<ID3D12Resource>[] _entityCbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly void*[] _entityCbufMapped = new void*[FrameCount];
    private readonly uint[] _entityCbufSize = new uint[FrameCount];

    // Shared shader-visible SRV heap for all model skins (one descriptor per model).
    private ComPtr<ID3D12DescriptorHeap> _entitySrvHeap;
    private uint _entitySrvIncrement;
    private int _entitySrvNext;

    private sealed class EntityModelGpu
    {
        public ComPtr<ID3D12Resource> vbuf;
        public uint vbufSize;
        public uint vertexCount;
        public ComPtr<ID3D12Resource> skin;
        public int srvSlot;     // index in _entitySrvHeap
    }
    private readonly Dictionary<string, EntityModelGpu> _entityModels = new();

    // Dedicated upload allocator/list/fence for entity VB+skin uploads (mid-frame,
    // like the icon uploads — must NOT touch the per-frame list / fence).
    private ComPtr<ID3D12CommandAllocator> _entityUploadAllocator;
    private ComPtr<ID3D12GraphicsCommandList> _entityUploadList;
    private ComPtr<ID3D12Fence> _entityUploadFence;
    private ulong _entityUploadFenceValue;
    private nint _entityUploadFenceEvent;

    // Captured per frame from RenderEntities.
    private Pebble.EntityRenderer? _entityRenderer;
    private Pebble.EntityDrawList? _entityList;
    private PebbleCore.CamState _entityCam;
    private byte[] _entityScratch = new byte[EntityUSize];

    // ─────────────────────────────────────── Particles / sprites / break overlay
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleUniform
    {
        public Matrix4x4 viewProj;
        public Vector4 right;
        public Vector4 up;       // xyz + dayLight
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SpriteUniform
    {
        public Matrix4x4 viewProj;
        public Vector4 center;   // xyz + size
        public Vector4 right;    // xyz + layer
        public Vector4 light;    // light, fogStart, fogEnd, _
        public Vector4 fogColor;
    }
    private const uint ParticleInstanceStride = 48; // 12 f32
    private const uint ParticleCbvSize = 256;       // ParticleU (96) → 256
    private const uint SpriteCbvSize = 256;         // SpriteU (128) → 256
    private const int MaxSpritesPerFrame = 256;

    // Particles
    private bool _particleReady;
    private ComPtr<ID3D12RootSignature> _particleRootSig;
    private ComPtr<ID3D12PipelineState> _particlePso;
    private ComPtr<ID3D12Resource> _particleQuad;          // static 6-corner quad VB
    private readonly ComPtr<ID3D12Resource>[] _particleInst = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly uint[] _particleInstSize = new uint[FrameCount];
    private readonly ComPtr<ID3D12Resource>[] _particleCbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly void*[] _particleCbufMapped = new void*[FrameCount];
    private Pebble.Particles? _frameParticles;

    // Sprites — reuse the icon SRV heap; per-(frame×sprite) cbuffer ring.
    private bool _spriteReady;
    private ComPtr<ID3D12RootSignature> _spriteRootSig;
    private ComPtr<ID3D12PipelineState> _spritePso;
    private readonly ComPtr<ID3D12Resource>[] _spriteCbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly void*[] _spriteCbufMapped = new void*[FrameCount];
    private Pebble.SpriteList? _frameSprites;

    // Break overlay — reuse the world root sig + translucent PSO + atlas SRV; a
    // per-frame upload VB/IB + a dedicated cbuffer (alpha test off, alpha 0.9).
    private readonly ComPtr<ID3D12Resource>[] _overlayVbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly uint[] _overlayVbufSize = new uint[FrameCount];
    private readonly ComPtr<ID3D12Resource>[] _overlayIbuf = new ComPtr<ID3D12Resource>[FrameCount];
    private readonly uint[] _overlayIbufSize = new uint[FrameCount];
    private ComPtr<ID3D12Resource> _overlayCbuf;
    private void* _overlayCbufMapped;
    private Pebble.BreakOverlay? _frameOverlay;

    public void Initialize(IWindow window)
    {
        var size = window.FramebufferSize;
        _width = Math.Max(1, size.X);
        _height = Math.Max(1, size.Y);

        nint hwnd = window.Native!.Win32!.Value.Hwnd;

        _d3d12 = D3D12.GetApi();
        _dxgi = DXGI.GetApi(window);

        uint factoryFlags = 0;

        bool wantDebug = Array.Exists(Environment.GetCommandLineArgs(), a => a == "--d3d-debug");
        if (wantDebug && TryEnableDebugLayer())
            factoryFlags |= DXGI.CreateFactoryDebug;

        fixed (IDXGIFactory2** ppFactory = &_factory.Handle)
            ThrowIfFailed(
                _dxgi.CreateDXGIFactory2(factoryFlags, SilkMarshal.GuidPtrOf<IDXGIFactory2>(), (void**)ppFactory),
                "CreateDXGIFactory2");

        fixed (ID3D12Device** ppDevice = &_device.Handle)
            ThrowIfFailed(
                _d3d12.CreateDevice(
                    (IUnknown*)null,
                    D3DFeatureLevel.Level110,
                    SilkMarshal.GuidPtrOf<ID3D12Device>(),
                    (void**)ppDevice),
                "D3D12CreateDevice");

        var queueDesc = new CommandQueueDesc
        {
            Type = CommandListType.Direct,
            Flags = CommandQueueFlags.None,
        };
        fixed (ID3D12CommandQueue** ppQueue = &_queue.Handle)
            ThrowIfFailed(
                _device.CreateCommandQueue(&queueDesc, SilkMarshal.GuidPtrOf<ID3D12CommandQueue>(), (void**)ppQueue),
                "CreateCommandQueue");

        CreateSwapChain(hwnd);
        CreateRtvHeapAndTargets();
        CreateDepthResources();
        CreatePostResources();

        for (uint i = 0; i < FrameCount; i++)
        {
            fixed (ID3D12CommandAllocator** ppAlloc = &_commandAllocators[i].Handle)
                ThrowIfFailed(
                    _device.CreateCommandAllocator(CommandListType.Direct, SilkMarshal.GuidPtrOf<ID3D12CommandAllocator>(), (void**)ppAlloc),
                    "CreateCommandAllocator");
        }

        fixed (ID3D12GraphicsCommandList** ppList = &_commandList.Handle)
            ThrowIfFailed(
                _device.CreateCommandList(
                    0,
                    CommandListType.Direct,
                    _commandAllocators[_frameIndex],
                    (ID3D12PipelineState*)null,
                    SilkMarshal.GuidPtrOf<ID3D12GraphicsCommandList>(),
                    (void**)ppList),
                "CreateCommandList");
        ThrowIfFailed(_commandList.Close(), "CommandList.Close (initial)");

        // Dedicated upload allocator + list (used only for the atlas copy).
        fixed (ID3D12CommandAllocator** ppAlloc = &_uploadAllocator.Handle)
            ThrowIfFailed(
                _device.CreateCommandAllocator(CommandListType.Direct, SilkMarshal.GuidPtrOf<ID3D12CommandAllocator>(), (void**)ppAlloc),
                "CreateCommandAllocator (upload)");
        fixed (ID3D12GraphicsCommandList** ppList = &_uploadList.Handle)
            ThrowIfFailed(
                _device.CreateCommandList(
                    0, CommandListType.Direct, _uploadAllocator,
                    (ID3D12PipelineState*)null,
                    SilkMarshal.GuidPtrOf<ID3D12GraphicsCommandList>(), (void**)ppList),
                "CreateCommandList (upload)");
        ThrowIfFailed(_uploadList.Close(), "CommandList.Close (upload initial)");

        fixed (ID3D12Fence** ppFence = &_fence.Handle)
            ThrowIfFailed(
                _device.CreateFence(0, FenceFlags.None, SilkMarshal.GuidPtrOf<ID3D12Fence>(), (void**)ppFence),
                "CreateFence");
        _fenceValue = 1;
        _fenceEvent = SilkMarshal.CreateWindowsEvent(null, false, false, null);
        if (_fenceEvent == nint.Zero)
            throw new InvalidOperationException("Failed to create fence event handle.");

        for (int i = 0; i < DeferFreeSlots; i++)
            _pendingFree[i] = new List<SectionGpu>();

        _initialized = true;

        WaitForGpu();
    }

    private void CreateSwapChain(nint hwnd)
    {
        var swapDesc = new SwapChainDesc1
        {
            Width = (uint)_width,
            Height = (uint)_height,
            Format = RtvFormat,
            Stereo = false,
            SampleDesc = new SampleDesc(count: 1, quality: 0),
            BufferUsage = DXGI.UsageRenderTargetOutput,
            BufferCount = FrameCount,
            Scaling = Scaling.Stretch,
            SwapEffect = SwapEffect.FlipDiscard,
            AlphaMode = AlphaMode.Unspecified,
            Flags = 0,
        };

        var swapChain1 = default(ComPtr<IDXGISwapChain1>);
        ThrowIfFailed(
            _factory.CreateSwapChainForHwnd(
                (IUnknown*)_queue.Handle,
                hwnd,
                &swapDesc,
                (SwapChainFullscreenDesc*)null,
                (IDXGIOutput*)null,
                swapChain1.GetAddressOf()),
            "CreateSwapChainForHwnd");

        _swapChain = swapChain1.QueryInterface<IDXGISwapChain3>();
        swapChain1.Dispose();
        _frameIndex = _swapChain.GetCurrentBackBufferIndex();
    }

    private void CreateRtvHeapAndTargets()
    {
        var heapDesc = new DescriptorHeapDesc
        {
            NumDescriptors = FrameCount,
            Type = DescriptorHeapType.Rtv,
            Flags = DescriptorHeapFlags.None,
        };
        fixed (ID3D12DescriptorHeap** ppHeap = &_rtvHeap.Handle)
            ThrowIfFailed(
                _device.CreateDescriptorHeap(&heapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)ppHeap),
                "CreateDescriptorHeap (RTV)");

        _rtvDescriptorSize = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);

        CpuDescriptorHandle rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (uint i = 0; i < FrameCount; i++)
        {
            fixed (ID3D12Resource** ppTarget = &_renderTargets[i].Handle)
                ThrowIfFailed(
                    _swapChain.GetBuffer(i, SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)ppTarget),
                    $"SwapChain.GetBuffer({i})");

            // sRGB RTV over the UNORM buffer: writes are gamma-encoded on store (matches
            // Vulkan's sRGB swapchain). See RtvFormatSrgb docs.
            var srgbRtv = new RenderTargetViewDesc { Format = RtvFormatSrgb, ViewDimension = RtvDimension.Texture2D };
            _device.CreateRenderTargetView(_renderTargets[i], &srgbRtv, rtvHandle);
            rtvHandle.Ptr += _rtvDescriptorSize;
        }
    }

    private void CreateDepthResources()
    {
        // DSV heap (single descriptor).
        if (_dsvHeap.Handle == null)
        {
            var dsvHeapDesc = new DescriptorHeapDesc
            {
                NumDescriptors = 1,
                Type = DescriptorHeapType.Dsv,
                Flags = DescriptorHeapFlags.None,
            };
            fixed (ID3D12DescriptorHeap** ppHeap = &_dsvHeap.Handle)
                ThrowIfFailed(
                    _device.CreateDescriptorHeap(&dsvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)ppHeap),
                    "CreateDescriptorHeap (DSV)");
        }

        var heapProps = new HeapProperties
        {
            Type = HeapType.Default,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 1,
            VisibleNodeMask = 1,
        };
        var resDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)_width,
            Height = (uint)_height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = DepthFormat,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowDepthStencil,
        };
        var clear = new ClearValue { Format = DepthFormat };
        clear.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1.0f, Stencil = 0 };

        fixed (ID3D12Resource** pp = &_depthBuffer.Handle)
            ThrowIfFailed(
                _device.CreateCommittedResource(
                    &heapProps, HeapFlags.None, &resDesc,
                    ResourceStates.DepthWrite, &clear,
                    SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)pp),
                "CreateCommittedResource (depth)");

        var dsvDesc = new DepthStencilViewDesc
        {
            Format = DepthFormat,
            ViewDimension = DsvDimension.Texture2D,
            Flags = DsvFlags.None,
        };
        _device.CreateDepthStencilView(_depthBuffer, &dsvDesc, _dsvHeap.GetCPUDescriptorHandleForHeapStart());
    }

    // ── offscreen-HDR / post resources ──────────────────────────────────────────
    private ComPtr<ID3D12Resource> CreateRenderTexture(int w, int h, Format fmt, ResourceStates initial)
    {
        var heapProps = new HeapProperties
        {
            Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1,
        };
        var resDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D, Alignment = 0,
            Width = (ulong)w, Height = (uint)h, DepthOrArraySize = 1, MipLevels = 1,
            Format = fmt, SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown, Flags = ResourceFlags.AllowRenderTarget,
        };
        var clear = new ClearValue { Format = fmt };
        clear.Anonymous.Color[0] = 0; clear.Anonymous.Color[1] = 0; clear.Anonymous.Color[2] = 0; clear.Anonymous.Color[3] = 1;
        var res = default(ComPtr<ID3D12Resource>);
        ThrowIfFailed(
            _device.CreateCommittedResource(&heapProps, HeapFlags.None, &resDesc, initial, &clear,
                SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)res.GetAddressOf()),
            "CreateCommittedResource (post RT)");
        return res;
    }

    // Build the HDR scene color + bloom/ultra ping-pong targets (per back-buffer
    // index, mirroring the shadow-map double-buffer discipline), their RTV heap, and
    // a shader-visible SRV heap with: hdr, bloomA, bloomB, ultraA, ultraB, depth,
    // shadow[f] — seven SRVs per frame. Depth + shadow SRVs are (re)written here so
    // the ultra pass can sample the scene depth and the shadow map.
    private void CreatePostResources()
    {
        _postW = Math.Max(1, _width);
        _postH = Math.Max(1, _height);
        int bw = Math.Max(1, _postW >> 2), bh = Math.Max(1, _postH >> 2);
        int uw = Math.Max(1, _postW >> 1), uh = Math.Max(1, _postH >> 1);

        for (uint f = 0; f < FrameCount; f++)
        {
            _hdrColor[f] = CreateRenderTexture(_postW, _postH, SceneRtvFormat, ResourceStates.PixelShaderResource);
            _bloomA[f] = CreateRenderTexture(bw, bh, SceneRtvFormat, ResourceStates.PixelShaderResource);
            _bloomB[f] = CreateRenderTexture(bw, bh, SceneRtvFormat, ResourceStates.PixelShaderResource);
            _ultraA[f] = CreateRenderTexture(uw, uh, SceneRtvFormat, ResourceStates.PixelShaderResource);
            _ultraB[f] = CreateRenderTexture(uw, uh, SceneRtvFormat, ResourceStates.PixelShaderResource);
        }

        // RTV heap: hdr,bloomA,bloomB,ultraA,ultraB per frame = 5*FrameCount.
        if (_hdrRtvHeap.Handle == null)
        {
            var rtvHeapDesc = new DescriptorHeapDesc
            {
                NumDescriptors = 5 * FrameCount, Type = DescriptorHeapType.Rtv, Flags = DescriptorHeapFlags.None,
            };
            fixed (ID3D12DescriptorHeap** pp = &_hdrRtvHeap.Handle)
                ThrowIfFailed(_device.CreateDescriptorHeap(&rtvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)pp),
                    "CreateDescriptorHeap (post RTV)");
            _hdrRtvIncrement = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
        }
        // SRV heap (shader-visible): PostSrvPerFrame * FrameCount.
        if (_postSrvHeap.Handle == null)
        {
            var srvHeapDesc = new DescriptorHeapDesc
            {
                NumDescriptors = (uint)(PostSrvPerFrame * FrameCount), Type = DescriptorHeapType.CbvSrvUav,
                Flags = DescriptorHeapFlags.ShaderVisible,
            };
            fixed (ID3D12DescriptorHeap** pp = &_postSrvHeap.Handle)
                ThrowIfFailed(_device.CreateDescriptorHeap(&srvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)pp),
                    "CreateDescriptorHeap (post SRV)");
            _postSrvIncrement = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);
        }

        var rtvBase = _hdrRtvHeap.GetCPUDescriptorHandleForHeapStart();
        var srvCpu = _postSrvHeap.GetCPUDescriptorHandleForHeapStart();
        // Hoisted out of the loop (CA2014): one scratch buffer, refilled each frame
        // slot, so the stack doesn't grow per iteration.
        var rts = stackalloc ComPtr<ID3D12Resource>[5];
        for (uint f = 0; f < FrameCount; f++)
        {
            rts[0] = _hdrColor[f]; rts[1] = _bloomA[f]; rts[2] = _bloomB[f]; rts[3] = _ultraA[f]; rts[4] = _ultraB[f];
            for (int i = 0; i < 5; i++)
            {
                var rtvDesc = new RenderTargetViewDesc { Format = SceneRtvFormat, ViewDimension = RtvDimension.Texture2D };
                var h = rtvBase; h.Ptr += (nuint)((f * 5 + (uint)i) * _hdrRtvIncrement);
                _device.CreateRenderTargetView(rts[i], &rtvDesc, h);
            }

            // SRVs: hdr, bloomA, bloomB, ultraA, ultraB (color), then depth, shadow.
            void ColorSrv(ComPtr<ID3D12Resource> tex, int slot)
            {
                var d = new ShaderResourceViewDesc { Format = SceneRtvFormat, ViewDimension = SrvDimension.Texture2D, Shader4ComponentMapping = 0x1688 };
                d.Anonymous.Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f };
                var h = srvCpu; h.Ptr += (nuint)((f * PostSrvPerFrame + slot) * _postSrvIncrement);
                _device.CreateShaderResourceView(tex, &d, h);
            }
            ColorSrv(_hdrColor[f], 0);
            ColorSrv(_bloomA[f], 1);
            ColorSrv(_bloomB[f], 2);
            ColorSrv(_ultraA[f], 3);
            ColorSrv(_ultraB[f], 4);
            // depth SRV (D32 read as R32_FLOAT)
            {
                var d = new ShaderResourceViewDesc { Format = Format.FormatR32Float, ViewDimension = SrvDimension.Texture2D, Shader4ComponentMapping = 0x1688 };
                d.Anonymous.Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f };
                var h = srvCpu; h.Ptr += (nuint)((f * PostSrvPerFrame + 5) * _postSrvIncrement);
                _device.CreateShaderResourceView(_depthBuffer, &d, h);
            }
            // shadow SRV (filled in when shadows are ready; placeholder uses depth so
            // the descriptor is never null — UpdateShadowPostSrv rewrites it).
            {
                var d = new ShaderResourceViewDesc { Format = Format.FormatR32Float, ViewDimension = SrvDimension.Texture2D, Shader4ComponentMapping = 0x1688 };
                d.Anonymous.Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f };
                var h = srvCpu; h.Ptr += (nuint)((f * PostSrvPerFrame + 6) * _postSrvIncrement);
                var shadowSrc = _shadowTex[f].Handle != null ? _shadowTex[f] : _depthBuffer;
                _device.CreateShaderResourceView(shadowSrc, &d, h);
            }
        }
    }

    // Re-point the per-frame shadow SRV in the post heap at the real shadow texture
    // once shadow resources exist (CreatePostResources may run before InitWorld).
    private void UpdateShadowPostSrv()
    {
        if (_postSrvHeap.Handle == null) return;
        var srvCpu = _postSrvHeap.GetCPUDescriptorHandleForHeapStart();
        for (uint f = 0; f < FrameCount; f++)
        {
            if (_shadowTex[f].Handle == null) continue;
            var d = new ShaderResourceViewDesc { Format = Format.FormatR32Float, ViewDimension = SrvDimension.Texture2D, Shader4ComponentMapping = 0x1688 };
            d.Anonymous.Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f };
            var h = srvCpu; h.Ptr += (nuint)((f * PostSrvPerFrame + 6) * _postSrvIncrement);
            _device.CreateShaderResourceView(_shadowTex[f], &d, h);
        }
    }

    private CpuDescriptorHandle PostRtv(uint f, int idx)
    {
        var h = _hdrRtvHeap.GetCPUDescriptorHandleForHeapStart();
        h.Ptr += (nuint)((f * 5 + (uint)idx) * _hdrRtvIncrement);
        return h;
    }
    private GpuDescriptorHandle PostSrvGpu(uint f, int slot)
    {
        var h = _postSrvHeap.GetGPUDescriptorHandleForHeapStart();
        h.Ptr += (ulong)((f * PostSrvPerFrame + slot) * _postSrvIncrement);
        return h;
    }

    // ── post pipelines: bloom extract / blur / ultra / composite ────────────────
    private void InitPostRenderer()
    {
        CreatePostRootSignatures();
        CreatePostPipelines();
        for (uint f = 0; f < FrameCount; f++)
        {
            _compositeCbuf[f] = CreateUploadBuffer(CompositeCbvSize);
            _compositeCbufMapped[f] = MapBuffer(_compositeCbuf[f]);
            _ultraCbuf[f] = CreateUploadBuffer(UltraCbvSize);
            _ultraCbufMapped[f] = MapBuffer(_ultraCbuf[f]);
            for (int d = 0; d < 4; d++)
            {
                _blurCbuf[f * 4 + d] = CreateUploadBuffer(CompositeCbvSize);
                _blurCbufMapped[f * 4 + d] = MapBuffer(_blurCbuf[f * 4 + d]);
            }
        }
        _postReady = true;
    }

    private void CreatePostRootSignatures()
    {
        var linSampler = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipLinear, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp, MipLODBias = 0, MaxAnisotropy = 1, ComparisonFunc = ComparisonFunc.Always,
            BorderColor = StaticBorderColor.OpaqueBlack, MinLOD = 0, MaxLOD = 0, ShaderRegister = 0, RegisterSpace = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };

        // _postRootSig: CBV b0 + three single-descriptor SRV tables (t0,t1,t2).
        {
            var ranges = stackalloc DescriptorRange[3];
            for (int i = 0; i < 3; i++)
                ranges[i] = new DescriptorRange { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = (uint)i, RegisterSpace = 0, OffsetInDescriptorsFromTableStart = 0 };
            var rp = stackalloc RootParameter[4];
            rp[0] = new RootParameter { ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.Pixel };
            rp[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };
            for (int i = 0; i < 3; i++)
            {
                rp[i + 1] = new RootParameter { ParameterType = RootParameterType.TypeDescriptorTable, ShaderVisibility = ShaderVisibility.Pixel };
                rp[i + 1].Anonymous.DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &ranges[i] };
            }
            var s = linSampler;
            var rsDesc = new RootSignatureDesc { NumParameters = 4, PParameters = rp, NumStaticSamplers = 1, PStaticSamplers = &s, Flags = RootSignatureFlags.None };
            var blob = default(ComPtr<ID3D10Blob>); var errBlob = default(ComPtr<ID3D10Blob>);
            int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
            if (hr < 0) throw new InvalidOperationException($"[D3D12] SerializeRootSignature (post) failed (0x{hr:X8}): {BlobToString(errBlob)}");
            fixed (ID3D12RootSignature** pp = &_postRootSig.Handle)
                ThrowIfFailed(_device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(), SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp), "CreateRootSignature (post)");
            blob.Dispose(); if (errBlob.Handle != null) errBlob.Dispose();
        }
        // _ultraRootSig: CBV b0 + two single-descriptor SRV tables (t0 depth, t1 shadow).
        {
            var ranges = stackalloc DescriptorRange[2];
            for (int i = 0; i < 2; i++)
                ranges[i] = new DescriptorRange { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = (uint)i, RegisterSpace = 0, OffsetInDescriptorsFromTableStart = 0 };
            var rp = stackalloc RootParameter[3];
            rp[0] = new RootParameter { ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.Pixel };
            rp[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };
            for (int i = 0; i < 2; i++)
            {
                rp[i + 1] = new RootParameter { ParameterType = RootParameterType.TypeDescriptorTable, ShaderVisibility = ShaderVisibility.Pixel };
                rp[i + 1].Anonymous.DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = &ranges[i] };
            }
            var s = linSampler;
            var rsDesc = new RootSignatureDesc { NumParameters = 3, PParameters = rp, NumStaticSamplers = 1, PStaticSamplers = &s, Flags = RootSignatureFlags.None };
            var blob = default(ComPtr<ID3D10Blob>); var errBlob = default(ComPtr<ID3D10Blob>);
            int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
            if (hr < 0) throw new InvalidOperationException($"[D3D12] SerializeRootSignature (ultra) failed (0x{hr:X8}): {BlobToString(errBlob)}");
            fixed (ID3D12RootSignature** pp = &_ultraRootSig.Handle)
                ThrowIfFailed(_device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(), SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp), "CreateRootSignature (ultra)");
            blob.Dispose(); if (errBlob.Handle != null) errBlob.Dispose();
        }
    }

    private ComPtr<ID3D12PipelineState> CreateFullscreenPso(ComPtr<ID3D12RootSignature> rootSig, string vsSrc, string psSrc, Format rtv, string tag)
    {
        var vs = CompileHlsl(vsSrc, "VSMain", "vs_5_0");
        var ps = CompileHlsl(psSrc, "PSMain", "ps_5_0");
        try
        {
            var rasterizer = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false, DepthClipEnable = false };
            var ds = new DepthStencilDesc { DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always, StencilEnable = false };
            var blend = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blend.RenderTarget[0] = new RenderTargetBlendDesc { BlendEnable = false, LogicOpEnable = false, RenderTargetWriteMask = (byte)ColorWriteEnable.All };
            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = rootSig,
                VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = ps.GetBufferPointer(), BytecodeLength = ps.GetBufferSize() },
                RasterizerState = rasterizer, DepthStencilState = ds, BlendState = blend, SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, NumRenderTargets = 1,
                InputLayout = new InputLayoutDesc { PInputElementDescs = null, NumElements = 0 }, SampleDesc = new SampleDesc(1, 0),
            };
            psoDesc.RTVFormats[0] = rtv;
            var pso = default(ComPtr<ID3D12PipelineState>);
            ThrowIfFailed(_device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pso.GetAddressOf()), $"CreateGraphicsPipelineState ({tag})");
            return pso;
        }
        finally { vs.Dispose(); ps.Dispose(); }
    }

    private void CreatePostPipelines()
    {
        _bloomExtractPso = CreateFullscreenPso(_postRootSig, PostShadersHlsl.FullscreenVertex, PostShadersHlsl.BloomExtract, SceneRtvFormat, "bloom_extract");
        _blurPso = CreateFullscreenPso(_postRootSig, PostShadersHlsl.FullscreenVertex, PostShadersHlsl.Blur, SceneRtvFormat, "blur");
        _ultraPso = CreateFullscreenPso(_ultraRootSig, PostShadersHlsl.FullscreenVertex, PostShadersHlsl.Ultra, SceneRtvFormat, "ultra");
        _ultraBlurPso = CreateFullscreenPso(_postRootSig, PostShadersHlsl.FullscreenVertex, PostShadersHlsl.UltraBlur, SceneRtvFormat, "ultra_blur");
        // Composite targets the sRGB present RTV (gamma-encode on store, matches Vulkan).
        _compositePso = CreateFullscreenPso(_postRootSig, PostShadersHlsl.FullscreenVertex, PostShadersHlsl.Composite, RtvFormatSrgb, "composite");
    }

    private CpuDescriptorHandle CurrentRtvHandle()
    {
        CpuDescriptorHandle handle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        handle.Ptr += (nuint)(_frameIndex * _rtvDescriptorSize);
        return handle;
    }

    // True once the scene has been recorded into the HDR target this frame, so
    // EndFrame knows to run the post chain (composite) before present.
    private bool _scenePostPending;

    public void BeginFrame()
    {
        if (!_initialized)
            return;

        // The previous frame's MoveToNextFrame already signaled + waited the fence
        // for this back-buffer index, so the GPU is provably done with the work
        // submitted FrameCount frames ago. Advance the deferred-free ring and release
        // the bucket we're about to reuse: those sections were queued (DeferFree)
        // DeferFreeSlots frames back, so — submissions execute in order — the GPU no
        // longer references them. Doing this BEFORE any new section frees this frame
        // is what keeps full streaming (SyncSections releasing thousands of sections)
        // from device-removing.
        _deferSlot = (_deferSlot + 1) % DeferFreeSlots;
        DrainPendingFrees(_deferSlot);

        // Reset THIS frame's allocator (MoveToNextFrame already fenced it) — never a
        // shared one that the previous, still-in-flight frame may be executing from.
        var allocator = _commandAllocators[_frameIndex];
        ThrowIfFailed(allocator.Reset(), "CommandAllocator.Reset");
        ThrowIfFailed(_commandList.Reset(allocator, (ID3D12PipelineState*)null), "CommandList.Reset");

        // Swapchain backbuffer: PRESENT -> RENDER_TARGET (composite writes it later).
        ResourceBarrierTransition(
            _renderTargets[_frameIndex],
            ResourceStates.Present,
            ResourceStates.RenderTarget);

        // Reroute the 3D scene into the offscreen HDR target: transition this frame's
        // HDR color PIXEL_SHADER_RESOURCE -> RENDER_TARGET and bind it + the main DSV.
        _scenePostPending = false;
        if (_postReady && _hdrColor[_frameIndex].Handle != null)
        {
            ResourceBarrierTransition(_hdrColor[_frameIndex], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            CpuDescriptorHandle hdrRtv = PostRtv(_frameIndex, 0);
            CpuDescriptorHandle dsv0 = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
            _commandList.OMSetRenderTargets(1, &hdrRtv, false, &dsv0);
            _commandList.ClearDepthStencilView(dsv0, ClearFlags.Depth, 1.0f, 0, 0, (Box2D<int>*)null);
            _scenePostPending = true;
            return;
        }

        // Fallback (post not yet ready): render straight to the swapchain like before.
        CpuDescriptorHandle rtv = CurrentRtvHandle();
        CpuDescriptorHandle dsv = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        _commandList.OMSetRenderTargets(1, &rtv, false, &dsv);
        _commandList.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0, 0, (Box2D<int>*)null);
    }

    public void ClearColor(float r, float g, float b, float a)
    {
        if (!_initialized)
            return;
        // Clear whichever color target is currently bound (HDR when post is active).
        CpuDescriptorHandle rtv = _scenePostPending ? PostRtv(_frameIndex, 0) : CurrentRtvHandle();
        var color = stackalloc float[4] { r, g, b, a };
        _commandList.ClearRenderTargetView(rtv, color, 0, (Box2D<int>*)null);
    }

    public void RenderSky(in SkyState sky, PebbleCore.CamState cam, double timeSeconds)
    {
        if (!_initialized) return;
        _frameSky = sky;
        _frameCam = cam;
        _frameTime = timeSeconds;
        _haveSky = true;

        // Build the sun-direction shadow matrix for this frame (D3D does NOT flip Y).
        _frameShadowsOn = _shadowsEnabled && sky.ShadowsOn;
        if (_frameShadowsOn)
            _frameShadowMat = SkyState.BuildShadowMatrix(sky.SunDir, cam, ShadowSize, flipY: false);

        // The sky pass repaints every pixel, but clear the RTV to the fog/horizon
        // color first so fog-distance fragments blend toward the right color.
        ClearColor(sky.Fog.X, sky.Fog.Y, sky.Fog.Z, 1f);

        // Sky owns no world resources (no VB / no atlas) — init it independently.
        if (!_skyReady)
            InitSkyRenderer();
        if (!_skyReady) return;

        RecordSky();
    }

    public void RenderWorld(HostBridge host, PebbleCore.CamState cam, double timeSeconds)
    {
        _frameHost = host;
        _frameCam = cam;
        _frameTime = timeSeconds;
        _haveWorldFrame = true;

        // If RenderSky wasn't called this frame, fall back to the flat sky clear.
        if (!_haveSky)
            ClearColor(0.62f, 0.74f, 1.0f, 1f);

        // Lazy first-use init: the atlas tile list is only complete once the
        // engine has registered all blocks (after backend.Initialize).
        if (!_worldReady)
            InitWorldRenderer();
        if (!_worldReady) return;
        if (!_postReady)
            InitPostRenderer();

        // Reconcile the GPU section cache ONCE, BEFORE the shadow pass. SyncSections
        // frees/destroys GPU buffers for changed/removed sections; if it ran after
        // the shadow pass recorded draws against those buffers, the command list would
        // reference freed buffers → device-removed. (Same root cause fixed on Vulkan.)
        SyncSections(_frameHost);

        // Advance + copy animated tiles (water/lava/fire/portal) onto the open
        // command list before the world draws sample the atlas.
        FlushAtlasAnimations();

        // Sun shadow depth pass BEFORE the world pass, so chunk_fs can sample it.
        RecordShadowPass();

        RecordWorld();
    }

    // ═══════════════════════════════════════════════════════════════ UI / HUD

    private void InitUiRenderer()
    {
        EnsureIconTexture();
        CreateUiRootSignature();
        CreateUiPipeline();
        _uiReady = true;
    }

    private void EnsureIconTexture()
    {
        if (_iconTextureReady) return;
        _iconTextureReady = true;
        int tile = 16;
        uint layers = UiIconLayers;

        var heapProps = new HeapProperties
        {
            Type = HeapType.Default,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 1, VisibleNodeMask = 1,
        };
        var texDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)tile, Height = (uint)tile,
            DepthOrArraySize = (ushort)layers,
            MipLevels = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.None,
        };
        fixed (ID3D12Resource** pp = &_iconTexture.Handle)
            ThrowIfFailed(
                _device.CreateCommittedResource(
                    &heapProps, HeapFlags.None, &texDesc,
                    ResourceStates.PixelShaderResource, (ClearValue*)null,
                    SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)pp),
                "CreateCommittedResource (icons)");

        // Shader-visible SRV heap with the icon Texture2DArray view.
        var srvHeapDesc = new DescriptorHeapDesc
        {
            NumDescriptors = 1,
            Type = DescriptorHeapType.CbvSrvUav,
            Flags = DescriptorHeapFlags.ShaderVisible,
        };
        fixed (ID3D12DescriptorHeap** ppHeap = &_iconSrvHeap.Handle)
            ThrowIfFailed(
                _device.CreateDescriptorHeap(&srvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)ppHeap),
                "CreateDescriptorHeap (icon SRV)");

        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Format.FormatR8G8B8A8Unorm,
            ViewDimension = SrvDimension.Texture2Darray,
            Shader4ComponentMapping = 0x1688,
        };
        srvDesc.Anonymous.Texture2DArray = new Tex2DArraySrv
        {
            MostDetailedMip = 0, MipLevels = 1,
            FirstArraySlice = 0, ArraySize = layers,
            PlaneSlice = 0, ResourceMinLODClamp = 0f,
        };
        _device.CreateShaderResourceView(_iconTexture, &srvDesc, _iconSrvHeap.GetCPUDescriptorHandleForHeapStart());

        // Dedicated upload allocator/list/fence (see field docs).
        fixed (ID3D12CommandAllocator** pp = &_iconAllocator.Handle)
            ThrowIfFailed(
                _device.CreateCommandAllocator(CommandListType.Direct, SilkMarshal.GuidPtrOf<ID3D12CommandAllocator>(), (void**)pp),
                "CreateCommandAllocator (icon)");
        fixed (ID3D12GraphicsCommandList** pp = &_iconList.Handle)
            ThrowIfFailed(
                _device.CreateCommandList(0, CommandListType.Direct, _iconAllocator,
                    (ID3D12PipelineState*)null, SilkMarshal.GuidPtrOf<ID3D12GraphicsCommandList>(), (void**)pp),
                "CreateCommandList (icon)");
        ThrowIfFailed(_iconList.Close(), "iconList.Close (initial)");
        fixed (ID3D12Fence** pp = &_iconFence.Handle)
            ThrowIfFailed(_device.CreateFence(0, FenceFlags.None, SilkMarshal.GuidPtrOf<ID3D12Fence>(), (void**)pp),
                "CreateFence (icon)");
        _iconFenceValue = 1;
        _iconFenceEvent = SilkMarshal.CreateWindowsEvent(null, false, false, null);
    }

    /// Upload a 16×16 RGBA icon into one array layer via the dedicated icon list.
    private void UploadIconLayer(int layer, byte[] pixels)
    {
        int tile = 16;
        var texDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)tile, Height = (uint)tile,
            DepthOrArraySize = (ushort)UiIconLayers,
            MipLevels = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.None,
        };
        PlacedSubresourceFootprint footprint;
        uint numRows;
        ulong rowSize, totalBytes;
        _device.GetCopyableFootprints(&texDesc, (uint)layer, 1, 0, &footprint, &numRows, &rowSize, &totalBytes);
        // GetCopyableFootprints returns an absolute offset for subresource `layer`;
        // the upload buffer starts at 0, so rebase the placed footprint.
        footprint.Offset = 0;

        var upload = CreateUploadBuffer(totalBytes);
        byte* mapped = (byte*)MapBuffer(upload);
        int srcRowBytes = tile * 4;
        uint dstPitch = footprint.Footprint.RowPitch;
        for (int y = 0; y < tile; y++)
        {
            byte* dstRow = mapped + (long)y * dstPitch;
            pixels.AsSpan(y * srcRowBytes, srcRowBytes).CopyTo(new Span<byte>(dstRow, srcRowBytes));
        }
        upload.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        ThrowIfFailed(_iconAllocator.Reset(), "iconAllocator.Reset");
        ThrowIfFailed(_iconList.Reset(_iconAllocator, (ID3D12PipelineState*)null), "iconList.Reset");

        // PIXEL_SHADER_RESOURCE -> COPY_DEST on the single array slice.
        IconBarrier((uint)layer, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);

        var dstLoc = new TextureCopyLocation { PResource = _iconTexture, Type = TextureCopyType.SubresourceIndex };
        dstLoc.Anonymous.SubresourceIndex = (uint)layer;
        var srcLoc = new TextureCopyLocation { PResource = upload, Type = TextureCopyType.PlacedFootprint };
        srcLoc.Anonymous.PlacedFootprint = footprint;
        _iconList.CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, (Box*)null);

        IconBarrier((uint)layer, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);

        ThrowIfFailed(_iconList.Close(), "iconList.Close");
        ID3D12CommandList* cmd = (ID3D12CommandList*)_iconList.Handle;
        _queue.ExecuteCommandLists(1, &cmd);

        // Wait on the DEDICATED icon fence (never the frame fence) so the copy is
        // done before we sample it, without disturbing per-frame sync.
        ulong wait = _iconFenceValue++;
        ThrowIfFailed(_queue.Signal(_iconFence, wait), "Queue.Signal (icon)");
        if (_iconFence.GetCompletedValue() < wait)
        {
            ThrowIfFailed(_iconFence.SetEventOnCompletion(wait, (void*)_iconFenceEvent), "Fence.SetEventOnCompletion (icon)");
            SilkMarshal.WaitWindowsObjects(_iconFenceEvent, 0xFFFFFFFF, false);
        }

        upload.Dispose();
    }

    private void IconBarrier(uint subresource, ResourceStates before, ResourceStates after)
    {
        var barrier = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = _iconTexture, Subresource = subresource, StateBefore = before, StateAfter = after,
        };
        _iconList.ResourceBarrier(1, &barrier);
    }

    private int ResolveIconLayer(int itemId, PebbleCore.StackData? data)
    {
        EnsureIconTexture();
        string key = "i" + itemId + "|" + (data?.potion ?? "");
        if (_iconSlots.TryGetValue(key, out int existing)) return existing;
        if (_iconNext >= UiIconLayers) return -1;
        int layer = _iconNext++;
        var pixels = PebbleCore.IconGlobals.itemIconPixels(itemId, data);
        UploadIconLayer(layer, pixels);
        _iconSlots[key] = layer;
        return layer;
    }

    private void CreateUiRootSignature()
    {
        // [0] 32-bit constants b0 (float4 screen, 4 values)
        // [1] descriptor table -> SRV t0 (icon array)
        // static sampler s0 (point/clamp)
        var ranges = stackalloc DescriptorRange[1];
        ranges[0] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1, BaseShaderRegister = 0, RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };

        var rootParams = stackalloc RootParameter[2];
        rootParams[0] = new RootParameter
        {
            ParameterType = RootParameterType.Type32BitConstants,
            ShaderVisibility = ShaderVisibility.Vertex,
        };
        rootParams[0].Anonymous.Constants = new RootConstants { ShaderRegister = 0, RegisterSpace = 0, Num32BitValues = 4 };

        rootParams[1] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = ShaderVisibility.Pixel,
        };
        rootParams[1].Anonymous.DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = ranges };

        var sampler = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0, MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunc.Always, BorderColor = StaticBorderColor.OpaqueBlack,
            MinLOD = 0, MaxLOD = 0, ShaderRegister = 0, RegisterSpace = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };

        var rsDesc = new RootSignatureDesc
        {
            NumParameters = 2, PParameters = rootParams,
            NumStaticSamplers = 1, PStaticSamplers = &sampler,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };

        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0)
            throw new InvalidOperationException($"[D3D12] SerializeRootSignature (ui) failed (0x{hr:X8}): {BlobToString(errBlob)}");

        fixed (ID3D12RootSignature** pp = &_uiRootSignature.Handle)
            ThrowIfFailed(
                _device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(),
                    SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp),
                "CreateRootSignature (ui)");
        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    private void CreateUiPipeline()
    {
        var vs = CompileHlsl(UiShaders.HlslVertex, "VSMain", "vs_5_0");
        var ps = CompileHlsl(UiShaders.HlslFragment, "PSMain", "ps_5_0");

        var semPos = (byte*)SilkMarshal.StringToPtr("POSITION");
        var semUv = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        var semLayer = (byte*)SilkMarshal.StringToPtr("LAYER");
        var semColor = (byte*)SilkMarshal.StringToPtr("COLOR");
        try
        {
            var elems = stackalloc InputElementDesc[4];
            elems[0] = new InputElementDesc { SemanticName = semPos, SemanticIndex = 0, Format = Format.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[1] = new InputElementDesc { SemanticName = semUv, SemanticIndex = 0, Format = Format.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 8, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[2] = new InputElementDesc { SemanticName = semLayer, SemanticIndex = 0, Format = Format.FormatR32Float, InputSlot = 0, AlignedByteOffset = 16, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[3] = new InputElementDesc { SemanticName = semColor, SemanticIndex = 0, Format = Format.FormatR32G32B32A32Float, InputSlot = 0, AlignedByteOffset = 20, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };

            var rasterizer = new RasterizerDesc
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false,
                DepthBias = 0, DepthBiasClamp = 0, SlopeScaledDepthBias = 0,
                DepthClipEnable = false, MultisampleEnable = false, AntialiasedLineEnable = false,
                ForcedSampleCount = 0, ConservativeRaster = ConservativeRasterizationMode.Off,
            };
            // No depth — UI always over the world.
            var depthStencil = new DepthStencilDesc
            {
                DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always,
                StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff,
            };
            var blendOn = new RenderTargetBlendDesc
            {
                BlendEnable = true, LogicOpEnable = false,
                SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            var blendDesc = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendDesc.RenderTarget[0] = blendOn;

            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _uiRootSignature,
                VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = ps.GetBufferPointer(), BytecodeLength = ps.GetBufferSize() },
                RasterizerState = rasterizer,
                DepthStencilState = depthStencil,
                BlendState = blendDesc,
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 1,
                DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = elems, NumElements = 4 },
                SampleDesc = new SampleDesc(1, 0),
            };
            // UI draws into the same sRGB present RTV as the composite.
            psoDesc.RTVFormats[0] = RtvFormatSrgb;

            fixed (ID3D12PipelineState** pp = &_uiPso.Handle)
                ThrowIfFailed(
                    _device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp),
                    "CreateGraphicsPipelineState (ui)");
        }
        finally
        {
            SilkMarshal.Free((nint)semPos);
            SilkMarshal.Free((nint)semUv);
            SilkMarshal.Free((nint)semLayer);
            SilkMarshal.Free((nint)semColor);
            vs.Dispose();
            ps.Dispose();
        }
    }

    public void RenderUI(Pebble.UI.UICanvas canvas)
    {
        // Cloud layer over the world (depth read-only), recorded just before the UI
        // so it sits under the HUD. RenderUI is called every frame after the world,
        // so this is the reliable hook even when the HUD has no verts.
        if (_initialized && _worldReady && _skyExtraReady)
            RecordClouds();

        // Offscreen-HDR post chain: bloom + ACES composite (+ ultra) from the HDR
        // scene into the swapchain. Runs even when the HUD is empty, and rebinds the
        // swapchain RTV so the UI below draws on top of the composited image.
        if (_scenePostPending)
            RecordPost();

        _uiVertCount = canvas.VertexCount;
        if (_uiVertCount == 0) { _uiVerts = null; return; }
        int floats = _uiVertCount * Pebble.UI.UICanvas.FloatsPerVertex;
        if (_uiVerts == null || _uiVerts.Length < floats) _uiVerts = new float[floats];
        for (int i = 0; i < floats; i++) _uiVerts[i] = canvas.Verts[i];
        _uiScreenW = (float)canvas.width;
        _uiScreenH = (float)canvas.height;

        if (!_uiReady) InitUiRenderer();
        if (!_uiReady) return;

        RecordUi();
    }

    /// Record the UI draw onto the open command list (after RecordWorld). The
    /// backbuffer is still bound as RTV from BeginFrame.
    private void RecordUi()
    {
        if (_uiVerts == null || _uiVertCount == 0) return;

        // Upload this frame's vertices into the per-frame dynamic vertex buffer.
        uint bytes = (uint)(_uiVertCount * Pebble.UI.UICanvas.FloatsPerVertex * sizeof(float));
        uint f = _frameIndex;
        if (_uiVbufSize[f] < bytes)
        {
            if (_uiVbuf[f].Handle != null) { _uiVbuf[f].Dispose(); _uiVbuf[f] = default; }
            uint cap = Math.Max(bytes * 2, 65536);
            _uiVbuf[f] = CreateUploadBuffer(cap);
            _uiVbufSize[f] = cap;
        }
        void* mapped = MapBuffer(_uiVbuf[f]);
        _uiVerts.AsSpan(0, _uiVertCount * Pebble.UI.UICanvas.FloatsPerVertex)
            .CopyTo(new Span<float>(mapped, _uiVertCount * Pebble.UI.UICanvas.FloatsPerVertex));
        _uiVbuf[f].Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &scissor);

        _commandList.SetPipelineState(_uiPso);
        _commandList.SetGraphicsRootSignature(_uiRootSignature);
        var heap = (ID3D12DescriptorHeap*)_iconSrvHeap.Handle;
        _commandList.SetDescriptorHeaps(1, &heap);
        var screen = stackalloc float[4] { _uiScreenW, _uiScreenH, 0, 0 };
        _commandList.SetGraphicsRoot32BitConstants(0, 4, screen, 0);
        _commandList.SetGraphicsRootDescriptorTable(1, _iconSrvHeap.GetGPUDescriptorHandleForHeapStart());
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        var vbv = new VertexBufferView
        {
            BufferLocation = _uiVbuf[f].GetGPUVirtualAddress(),
            SizeInBytes = bytes,
            StrideInBytes = UiVertexStride,
        };
        _commandList.IASetVertexBuffers(0, 1, &vbv);
        _commandList.DrawInstanced((uint)_uiVertCount, 1, 0, 0);
    }

    // ── post chain: ultra (SSAO + volumetrics) -> bloom -> ACES composite ───────
    // Records after the 3D scene (HDR target is RENDER_TARGET); leaves the swapchain
    // backbuffer bound as RTV with the tonemapped image so RecordUi draws on top.
    private void RecordPost()
    {
        if (!_postReady) return;
        uint f = _frameIndex;
        int bw = Math.Max(1, _postW >> 2), bh = Math.Max(1, _postH >> 2);
        int uw = Math.Max(1, _postW >> 1), uh = Math.Max(1, _postH >> 1);
        bool ultraOn = _ultraEnabled;

        // HDR scene RENDER_TARGET -> PIXEL_SHADER_RESOURCE so post passes sample it.
        ResourceBarrierTransition(_hdrColor[f], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);

        var heap = (ID3D12DescriptorHeap*)_postSrvHeap.Handle;
        _commandList.SetDescriptorHeaps(1, &heap);

        void Fullscreen(ComPtr<ID3D12PipelineState> pso, CpuDescriptorHandle rtv, int vw, int vh)
        {
            _commandList.OMSetRenderTargets(1, &rtv, false, (CpuDescriptorHandle*)null);
            var vp = new Viewport(0, 0, vw, vh, 0f, 1f);
            _commandList.RSSetViewports(1, &vp);
            var sc = new Box2D<int>(0, 0, vw, vh);
            _commandList.RSSetScissorRects(1, &sc);
            _commandList.SetPipelineState(pso);
            _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
            _commandList.DrawInstanced(3, 1, 0, 0);
        }

        var cam = _frameCam;
        var viewProj = BuildViewProj(cam);
        Matrix4x4.Invert(viewProj, out var invViewProj);

        // ── ultra: half-res SSAO (alpha) + volumetric light (rgb), then 1 blur ──
        if (ultraOn)
        {
            // depth DEPTH_WRITE -> PIXEL_SHADER_RESOURCE (ultra reconstructs world pos).
            ResourceBarrierTransition(_depthBuffer, ResourceStates.DepthWrite, ResourceStates.PixelShaderResource);

            float far = Math.Max(256f, (float)_frameSky.FogEnd / 0.95f);
            var uu = new UltraUniform
            {
                invViewProj = invViewProj,
                viewProj = viewProj,
                shadowMat = _frameShadowsOn ? _frameShadowMat : Matrix4x4.Identity,
                sunDir = new Vector4(_frameSky.SunDir, _frameSky.DayLight),
                paramsv = new Vector4((float)_frameTime, far, (_frameShadowsOn && !cam.underwater) ? 1f : 0f, cam.underwater ? 1f : 0f),
                fogColor = new Vector4(_frameSky.Fog, (float)_frameSky.FogEnd),
                texel = new Vector4(1f / uw, 1f / uh, 0, 0),
            };
            System.Buffer.MemoryCopy(&uu, _ultraCbufMapped[f], UltraCbvSize, (long)Marshal.SizeOf<UltraUniform>());

            _commandList.SetGraphicsRootSignature(_ultraRootSig);
            _commandList.SetGraphicsRootConstantBufferView(0, _ultraCbuf[f].GetGPUVirtualAddress());
            _commandList.SetGraphicsRootDescriptorTable(1, PostSrvGpu(f, 5)); // depth -> t0
            _commandList.SetGraphicsRootDescriptorTable(2, PostSrvGpu(f, 6)); // shadow -> t1
            ResourceBarrierTransition(_ultraA[f], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            Fullscreen(_ultraPso, PostRtv(f, 3), uw, uh);
            ResourceBarrierTransition(_ultraA[f], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);

            // separable blur ultraA -> ultraB (H) -> ultraA (V), preserving alpha.
            UploadBlurDir(f, 0, 1f / uw, 0f);
            UploadBlurDir(f, 1, 0f, 1f / uh);
            _commandList.SetGraphicsRootSignature(_postRootSig);
            // H: source ultraA (t0), dest ultraB
            _commandList.SetGraphicsRootConstantBufferView(0, _blurCbuf[f * 4 + 0].GetGPUVirtualAddress());
            _commandList.SetGraphicsRootDescriptorTable(1, PostSrvGpu(f, 3));
            ResourceBarrierTransition(_ultraB[f], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            Fullscreen(_ultraBlurPso, PostRtv(f, 4), uw, uh);
            ResourceBarrierTransition(_ultraB[f], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            // V: source ultraB (t0), dest ultraA
            _commandList.SetGraphicsRootConstantBufferView(0, _blurCbuf[f * 4 + 1].GetGPUVirtualAddress());
            _commandList.SetGraphicsRootDescriptorTable(1, PostSrvGpu(f, 4));
            ResourceBarrierTransition(_ultraA[f], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            Fullscreen(_ultraBlurPso, PostRtv(f, 3), uw, uh);
            ResourceBarrierTransition(_ultraA[f], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);

            // depth back to DEPTH_WRITE for the next frame's scene pass.
            ResourceBarrierTransition(_depthBuffer, ResourceStates.PixelShaderResource, ResourceStates.DepthWrite);
        }

        // ── bloom: bright-pass HDR -> bloomA, then 2x separable blur (A<->B) ──
        _commandList.SetGraphicsRootSignature(_postRootSig);
        // extract: HDR (t0) -> bloomA
        _commandList.SetGraphicsRootConstantBufferView(0, _compositeCbuf[f].GetGPUVirtualAddress()); // unused by extract
        _commandList.SetGraphicsRootDescriptorTable(1, PostSrvGpu(f, 0)); // hdr -> t0
        ResourceBarrierTransition(_bloomA[f], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
        Fullscreen(_bloomExtractPso, PostRtv(f, 1), bw, bh);
        ResourceBarrierTransition(_bloomA[f], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);

        UploadBlurDir(f, 2, 1f / bw, 0f);
        UploadBlurDir(f, 3, 0f, 1f / bh);
        for (int pass = 0; pass < 2; pass++)
        {
            // H: bloomA (t0) -> bloomB
            _commandList.SetGraphicsRootConstantBufferView(0, _blurCbuf[f * 4 + 2].GetGPUVirtualAddress());
            _commandList.SetGraphicsRootDescriptorTable(1, PostSrvGpu(f, 1));
            ResourceBarrierTransition(_bloomB[f], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            Fullscreen(_blurPso, PostRtv(f, 2), bw, bh);
            ResourceBarrierTransition(_bloomB[f], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
            // V: bloomB (t0) -> bloomA
            _commandList.SetGraphicsRootConstantBufferView(0, _blurCbuf[f * 4 + 3].GetGPUVirtualAddress());
            _commandList.SetGraphicsRootDescriptorTable(1, PostSrvGpu(f, 2));
            ResourceBarrierTransition(_bloomA[f], ResourceStates.PixelShaderResource, ResourceStates.RenderTarget);
            Fullscreen(_blurPso, PostRtv(f, 1), bw, bh);
            ResourceBarrierTransition(_bloomA[f], ResourceStates.RenderTarget, ResourceStates.PixelShaderResource);
        }

        // ── composite: HDR + bloomA + ultraA, ACES tonemap, into the swapchain ──
        var tint = new Vector4(0, 0, 0, 0);
        if (cam.underwater) tint = new Vector4(0.1f, 0.2f, 0.45f, 0.12f);
        else if (cam.underLava) tint = new Vector4(0.9f, 0.3f, 0.05f, 0.55f);
        else if (cam.powderSnow) tint = new Vector4(0.95f, 0.97f, 1.0f, 0.5f);
        var cu = new CompositeUniform
        {
            paramsv = new Vector4(0.55f, 0f, (float)_frameTime, (float)cam.darkness),
            tint = tint,
            params2 = new Vector4(ultraOn ? 1f : 0f, 0.85f, 1.0f, 0f),
        };
        System.Buffer.MemoryCopy(&cu, _compositeCbufMapped[f], CompositeCbvSize, (long)Marshal.SizeOf<CompositeUniform>());

        CpuDescriptorHandle backRtv = CurrentRtvHandle();
        _commandList.OMSetRenderTargets(1, &backRtv, false, (CpuDescriptorHandle*)null);
        var fullVp = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &fullVp);
        var fullSc = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &fullSc);
        _commandList.SetGraphicsRootSignature(_postRootSig);
        _commandList.SetGraphicsRootConstantBufferView(0, _compositeCbuf[f].GetGPUVirtualAddress());
        _commandList.SetGraphicsRootDescriptorTable(1, PostSrvGpu(f, 0)); // scene -> t0
        _commandList.SetGraphicsRootDescriptorTable(2, PostSrvGpu(f, 1)); // bloomA -> t1
        _commandList.SetGraphicsRootDescriptorTable(3, PostSrvGpu(f, 3)); // ultraA -> t2
        _commandList.SetPipelineState(_compositePso);
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _commandList.DrawInstanced(3, 1, 0, 0);

        // Leave the swapchain RTV bound (no DSV) for the UI pass that follows.
        _scenePostPending = false;
    }

    private void UploadBlurDir(uint f, int idx, float dx, float dy)
    {
        var c = new CompositeUniform { tint = new Vector4(dx, dy, 0, 0) };
        System.Buffer.MemoryCopy(&c, _blurCbufMapped[f * 4 + idx], CompositeCbvSize, (long)Marshal.SizeOf<CompositeUniform>());
    }

    // ═══════════════════════════════════════════════════════════ Entities (3D)

    public void RenderEntities(Pebble.EntityRenderer renderer, Pebble.EntityDrawList list,
                               PebbleCore.CamState cam, double timeSeconds)
    {
        _entityRenderer = renderer;
        _entityList = list;
        _entityCam = cam;
        _frameTime = timeSeconds;
        if (list.draws.Count == 0) return;

        // The world pipeline must be live (it owns the depth buffer + atlas the
        // world wrote this frame); skip entities until the world has initialized.
        if (!_worldReady) return;
        if (!_entityReady) InitEntityRenderer();
        if (!_entityReady) return;

        RecordEntities();
    }

    private void InitEntityRenderer()
    {
        CreateEntityRootSignature();
        CreateEntityPipeline();

        // Shared shader-visible SRV heap (one slot per model skin).
        var srvHeapDesc = new DescriptorHeapDesc
        {
            NumDescriptors = MaxEntityModels,
            Type = DescriptorHeapType.CbvSrvUav,
            Flags = DescriptorHeapFlags.ShaderVisible,
        };
        fixed (ID3D12DescriptorHeap** pp = &_entitySrvHeap.Handle)
            ThrowIfFailed(
                _device.CreateDescriptorHeap(&srvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)pp),
                "CreateDescriptorHeap (entity SRV)");
        _entitySrvIncrement = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);

        // Dedicated upload allocator/list/fence (see icon-upload precedent).
        fixed (ID3D12CommandAllocator** pp = &_entityUploadAllocator.Handle)
            ThrowIfFailed(
                _device.CreateCommandAllocator(CommandListType.Direct, SilkMarshal.GuidPtrOf<ID3D12CommandAllocator>(), (void**)pp),
                "CreateCommandAllocator (entity upload)");
        fixed (ID3D12GraphicsCommandList** pp = &_entityUploadList.Handle)
            ThrowIfFailed(
                _device.CreateCommandList(0, CommandListType.Direct, _entityUploadAllocator,
                    (ID3D12PipelineState*)null, SilkMarshal.GuidPtrOf<ID3D12GraphicsCommandList>(), (void**)pp),
                "CreateCommandList (entity upload)");
        ThrowIfFailed(_entityUploadList.Close(), "entityUploadList.Close (initial)");
        fixed (ID3D12Fence** pp = &_entityUploadFence.Handle)
            ThrowIfFailed(_device.CreateFence(0, FenceFlags.None, SilkMarshal.GuidPtrOf<ID3D12Fence>(), (void**)pp),
                "CreateFence (entity upload)");
        _entityUploadFenceValue = 1;
        _entityUploadFenceEvent = SilkMarshal.CreateWindowsEvent(null, false, false, null);

        _entityReady = true;
    }

    private void CreateEntityRootSignature()
    {
        // [0] CBV b0 (root descriptor, per-entity GPU VA)
        // [1] SRV table t0 (skin)  + static sampler s0 (point/clamp)
        var ranges = stackalloc DescriptorRange[1];
        ranges[0] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 1, BaseShaderRegister = 0, RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };
        var rootParams = stackalloc RootParameter[2];
        rootParams[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.All,
        };
        rootParams[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };
        rootParams[1] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable, ShaderVisibility = ShaderVisibility.Pixel,
        };
        rootParams[1].Anonymous.DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = ranges };

        var sampler = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0, MaxAnisotropy = 1, ComparisonFunc = ComparisonFunc.Always,
            BorderColor = StaticBorderColor.OpaqueBlack, MinLOD = 0, MaxLOD = 0,
            ShaderRegister = 0, RegisterSpace = 0, ShaderVisibility = ShaderVisibility.Pixel,
        };
        var rsDesc = new RootSignatureDesc
        {
            NumParameters = 2, PParameters = rootParams,
            NumStaticSamplers = 1, PStaticSamplers = &sampler,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };
        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0)
            throw new InvalidOperationException($"[D3D12] SerializeRootSignature (entity) failed (0x{hr:X8}): {BlobToString(errBlob)}");
        fixed (ID3D12RootSignature** pp = &_entityRootSig.Handle)
            ThrowIfFailed(
                _device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(),
                    SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp),
                "CreateRootSignature (entity)");
        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    private void CreateEntityPipeline()
    {
        var vs = CompileHlsl(EntityShaders.HlslVertex, "VSMain", "vs_5_0");
        var ps = CompileHlsl(EntityShaders.HlslFragment, "PSMain", "ps_5_0");

        var semPos = (byte*)SilkMarshal.StringToPtr("POSITION");
        var semNorm = (byte*)SilkMarshal.StringToPtr("NORMAL");
        var semUv = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        var semPart = (byte*)SilkMarshal.StringToPtr("PART");
        try
        {
            var elems = stackalloc InputElementDesc[4];
            elems[0] = new InputElementDesc { SemanticName = semPos, SemanticIndex = 0, Format = Format.FormatR32G32B32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[1] = new InputElementDesc { SemanticName = semNorm, SemanticIndex = 0, Format = Format.FormatR32G32B32Float, InputSlot = 0, AlignedByteOffset = 12, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[2] = new InputElementDesc { SemanticName = semUv, SemanticIndex = 0, Format = Format.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 24, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[3] = new InputElementDesc { SemanticName = semPart, SemanticIndex = 0, Format = Format.FormatR32Float, InputSlot = 0, AlignedByteOffset = 32, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };

            var rasterizer = new RasterizerDesc
            {
                FillMode = FillMode.Solid, CullMode = CullMode.Back,
                // Same winding convention as the world pipeline (CCW-front in the
                // right-handed world; D3D default front is CW, so treat CCW as front).
                FrontCounterClockwise = true,
                DepthBias = 0, DepthBiasClamp = 0, SlopeScaledDepthBias = 0,
                DepthClipEnable = true, MultisampleEnable = false, AntialiasedLineEnable = false,
                ForcedSampleCount = 0, ConservativeRaster = ConservativeRasterizationMode.Off,
            };
            // Depth test + write ON.
            var depthStencil = new DepthStencilDesc
            {
                DepthEnable = true, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.Less,
                StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff,
            };
            var blendOn = new RenderTargetBlendDesc
            {
                BlendEnable = true, LogicOpEnable = false,
                SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            var blendDesc = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendDesc.RenderTarget[0] = blendOn;

            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _entityRootSig,
                VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = ps.GetBufferPointer(), BytecodeLength = ps.GetBufferSize() },
                RasterizerState = rasterizer,
                DepthStencilState = depthStencil,
                BlendState = blendDesc,
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 1,
                DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = elems, NumElements = 4 },
                SampleDesc = new SampleDesc(1, 0),
            };
            psoDesc.RTVFormats[0] = SceneRtvFormat;

            fixed (ID3D12PipelineState** pp = &_entityPso.Handle)
                ThrowIfFailed(
                    _device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp),
                    "CreateGraphicsPipelineState (entity)");
        }
        finally
        {
            SilkMarshal.Free((nint)semPos);
            SilkMarshal.Free((nint)semNorm);
            SilkMarshal.Free((nint)semUv);
            SilkMarshal.Free((nint)semPart);
            vs.Dispose();
            ps.Dispose();
        }
    }

    private EntityModelGpu GetEntityModel(string name)
    {
        if (_entityModels.TryGetValue(name, out var g)) return g;
        var data = _entityRenderer!.Geom(name);
        g = new EntityModelGpu { vertexCount = (uint)data.vertexCount, srvSlot = _entitySrvNext++ };

        // --- vertex buffer: repack 9-float verts into the 40-byte (10-float) layout ---
        int vc = data.vertexCount;
        var packed = new float[vc * 10];
        for (int i = 0; i < vc; i++)
        {
            int s = i * 9, d = i * 10;
            packed[d + 0] = data.verts[s + 0]; packed[d + 1] = data.verts[s + 1]; packed[d + 2] = data.verts[s + 2];
            packed[d + 3] = data.verts[s + 3]; packed[d + 4] = data.verts[s + 4]; packed[d + 5] = data.verts[s + 5];
            packed[d + 6] = data.verts[s + 6]; packed[d + 7] = data.verts[s + 7];
            packed[d + 8] = data.verts[s + 8]; packed[d + 9] = 0f;
        }
        g.vbufSize = (uint)packed.Length * sizeof(float);
        g.vbuf = CreateUploadBuffer(g.vbufSize);
        void* vmapped = MapBuffer(g.vbuf);
        packed.AsSpan().CopyTo(new Span<float>(vmapped, packed.Length));
        g.vbuf.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        // --- skin (2D RGBA8) on the DEFAULT heap + upload via the dedicated list ---
        UploadEntitySkin(g, data.skinW, data.skinH, data.skinRgba);

        _entityModels[name] = g;
        return g;
    }

    private void UploadEntitySkin(EntityModelGpu g, int w, int h, byte[] rgba)
    {
        var heapProps = new HeapProperties
        {
            Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1,
        };
        var texDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D, Alignment = 0,
            Width = (ulong)w, Height = (uint)h, DepthOrArraySize = 1, MipLevels = 1,
            Format = Format.FormatR8G8B8A8Unorm, SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown, Flags = ResourceFlags.None,
        };
        fixed (ID3D12Resource** pp = &g.skin.Handle)
            ThrowIfFailed(
                _device.CreateCommittedResource(&heapProps, HeapFlags.None, &texDesc,
                    ResourceStates.CopyDest, (ClearValue*)null,
                    SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)pp),
                "CreateCommittedResource (skin)");

        PlacedSubresourceFootprint footprint;
        uint numRows; ulong rowSize, totalBytes;
        _device.GetCopyableFootprints(&texDesc, 0, 1, 0, &footprint, &numRows, &rowSize, &totalBytes);

        var upload = CreateUploadBuffer(totalBytes);
        byte* mapped = (byte*)MapBuffer(upload);
        int srcRow = w * 4;
        uint dstPitch = footprint.Footprint.RowPitch;
        for (int y = 0; y < h; y++)
        {
            byte* dstRow = mapped + (long)footprint.Offset + (long)y * dstPitch;
            rgba.AsSpan(y * srcRow, srcRow).CopyTo(new Span<byte>(dstRow, srcRow));
        }
        upload.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        ThrowIfFailed(_entityUploadAllocator.Reset(), "entityUploadAllocator.Reset");
        ThrowIfFailed(_entityUploadList.Reset(_entityUploadAllocator, (ID3D12PipelineState*)null), "entityUploadList.Reset");

        var dstLoc = new TextureCopyLocation { PResource = g.skin, Type = TextureCopyType.SubresourceIndex };
        dstLoc.Anonymous.SubresourceIndex = 0;
        var srcLoc = new TextureCopyLocation { PResource = upload, Type = TextureCopyType.PlacedFootprint };
        srcLoc.Anonymous.PlacedFootprint = footprint;
        _entityUploadList.CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, (Box*)null);

        var barrier = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = g.skin, Subresource = 0,
            StateBefore = ResourceStates.CopyDest, StateAfter = ResourceStates.PixelShaderResource,
        };
        _entityUploadList.ResourceBarrier(1, &barrier);
        ThrowIfFailed(_entityUploadList.Close(), "entityUploadList.Close");
        ID3D12CommandList* cmd = (ID3D12CommandList*)_entityUploadList.Handle;
        _queue.ExecuteCommandLists(1, &cmd);
        ulong wait = _entityUploadFenceValue++;
        ThrowIfFailed(_queue.Signal(_entityUploadFence, wait), "Queue.Signal (entity upload)");
        if (_entityUploadFence.GetCompletedValue() < wait)
        {
            ThrowIfFailed(_entityUploadFence.SetEventOnCompletion(wait, (void*)_entityUploadFenceEvent), "Fence.SetEventOnCompletion (entity)");
            SilkMarshal.WaitWindowsObjects(_entityUploadFenceEvent, 0xFFFFFFFF, false);
        }
        upload.Dispose();

        // SRV at this model's slot in the shared shader-visible heap.
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Format.FormatR8G8B8A8Unorm, ViewDimension = SrvDimension.Texture2D, Shader4ComponentMapping = 0x1688,
        };
        srvDesc.Anonymous.Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f };
        CpuDescriptorHandle handle = _entitySrvHeap.GetCPUDescriptorHandleForHeapStart();
        handle.Ptr += (nuint)(g.srvSlot * (int)_entitySrvIncrement);
        _device.CreateShaderResourceView(g.skin, &srvDesc, handle);
    }

    /// Record the entity draws onto the open command list (after RecordWorld; the
    /// backbuffer RTV + depth DSV are still bound from BeginFrame).
    private void RecordEntities()
    {
        if (_entityList == null || _entityRenderer == null) return;
        var draws = _entityList.draws;
        int n = Math.Min(draws.Count, MaxEntitiesPerFrame);
        if (n == 0) return;

        uint f = _frameIndex;
        uint needed = EntityCbvStride * (uint)n;
        if (_entityCbufSize[f] < needed || _entityCbuf[f].Handle == null)
        {
            if (_entityCbuf[f].Handle != null) { _entityCbuf[f].Dispose(); _entityCbuf[f] = default; }
            uint cap = Math.Max(needed, EntityCbvStride * 32);
            _entityCbuf[f] = CreateUploadBuffer(cap);
            _entityCbufMapped[f] = MapBuffer(_entityCbuf[f]);
            _entityCbufSize[f] = cap;
        }

        // Pack each entity's EntityU block at its 256-aligned offset.
        byte* basePtr = (byte*)_entityCbufMapped[f];
        var vp = BuildViewProj(_entityCam);
        for (int i = 0; i < n; i++)
        {
            PackEntityU(draws[i], vp, _entityScratch);
            System.Buffer.MemoryCopy(
                System.Runtime.CompilerServices.Unsafe.AsPointer(ref _entityScratch[0]),
                basePtr + (long)i * EntityCbvStride, EntityUSize, EntityUSize);
        }

        var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &scissor);

        _commandList.SetPipelineState(_entityPso);
        _commandList.SetGraphicsRootSignature(_entityRootSig);
        var heap = (ID3D12DescriptorHeap*)_entitySrvHeap.Handle;
        _commandList.SetDescriptorHeaps(1, &heap);
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        ulong cbufBase = _entityCbuf[f].GetGPUVirtualAddress();
        GpuDescriptorHandle srvBase = _entitySrvHeap.GetGPUDescriptorHandleForHeapStart();
        for (int i = 0; i < n; i++)
        {
            var d = draws[i];
            var g = GetEntityModel(d.model);
            if (g.vertexCount == 0) continue;

            _commandList.SetGraphicsRootConstantBufferView(0, cbufBase + (ulong)((long)i * EntityCbvStride));
            var srv = srvBase;
            srv.Ptr += (ulong)(g.srvSlot * (int)_entitySrvIncrement);
            _commandList.SetGraphicsRootDescriptorTable(1, srv);

            var vbv = new VertexBufferView
            {
                BufferLocation = g.vbuf.GetGPUVirtualAddress(),
                SizeInBytes = g.vbufSize, StrideInBytes = EntityVertexStride,
            };
            _commandList.IASetVertexBuffers(0, 1, &vbv);
            _commandList.DrawInstanced(g.vertexCount, 1, 0, 0);
        }
    }

    /// Serialize one EntityDraw into the EntityU layout (matches the HLSL cbuffer).
    private void PackEntityU(Pebble.EntityDraw d, Matrix4x4 vp, byte[] dst)
    {
        var fl = new float[EntityUSize / 4];
        int p = 0;
        // viewProj — row-major bytes of System.Numerics view*proj (HLSL reads a
        // cbuffer float4x4 column-major, same convention as the world pass).
        fl[p++] = vp.M11; fl[p++] = vp.M12; fl[p++] = vp.M13; fl[p++] = vp.M14;
        fl[p++] = vp.M21; fl[p++] = vp.M22; fl[p++] = vp.M23; fl[p++] = vp.M24;
        fl[p++] = vp.M31; fl[p++] = vp.M32; fl[p++] = vp.M33; fl[p++] = vp.M34;
        fl[p++] = vp.M41; fl[p++] = vp.M42; fl[p++] = vp.M43; fl[p++] = vp.M44;
        d.modelMat.WriteColumnMajor(fl, p); p += 16;
        for (int i = 0; i < 24; i++) { d.parts[i].WriteColumnMajor(fl, p); p += 16; }
        fl[p++] = d.lightSky; fl[p++] = d.lightBlock; fl[p++] = d.lightDay; fl[p++] = d.lightGamma;
        fl[p++] = d.ambient; fl[p++] = d.alpha; fl[p++] = d.fogStart; fl[p++] = d.fogEnd;
        fl[p++] = d.overlayR; fl[p++] = d.overlayG; fl[p++] = d.overlayB; fl[p++] = d.overlayA;
        fl[p++] = d.fogR; fl[p++] = d.fogG; fl[p++] = d.fogB; fl[p++] = 1f;
        System.Buffer.BlockCopy(fl, 0, dst, 0, EntityUSize);
    }

    // ═══════════════════════════════════════════════════════ Break-crack overlay

    public void RenderBreakOverlay(Pebble.BreakOverlay overlay, PebbleCore.CamState cam)
    {
        _frameOverlay = overlay;
        _frameCam = cam;
        if (!_initialized || !_worldReady) return;
        if (!overlay.active || overlay.indexCount == 0) return;
        RecordBreakOverlay();
    }

    private void RecordBreakOverlay()
    {
        var ov = _frameOverlay!;
        uint f = _frameIndex;
        uint vBytes = (uint)ov.verts.Length * sizeof(uint);
        uint iBytes = (uint)ov.idx.Length * sizeof(uint);
        if (_overlayVbufSize[f] < vBytes || _overlayVbuf[f].Handle == null)
        {
            if (_overlayVbuf[f].Handle != null) { _overlayVbuf[f].Dispose(); _overlayVbuf[f] = default; }
            uint cap = Math.Max(vBytes, 16384);
            _overlayVbuf[f] = CreateUploadBuffer(cap);
            _overlayVbufSize[f] = cap;
        }
        if (_overlayIbufSize[f] < iBytes || _overlayIbuf[f].Handle == null)
        {
            if (_overlayIbuf[f].Handle != null) { _overlayIbuf[f].Dispose(); _overlayIbuf[f] = default; }
            uint cap = Math.Max(iBytes, 8192);
            _overlayIbuf[f] = CreateUploadBuffer(cap);
            _overlayIbufSize[f] = cap;
        }
        if (_overlayCbuf.Handle == null)
        {
            _overlayCbuf = CreateUploadBuffer(CbvSize);
            _overlayCbufMapped = MapBuffer(_overlayCbuf);
        }
        void* vm = MapBuffer(_overlayVbuf[f]);
        ov.verts.AsSpan().CopyTo(new Span<uint>(vm, ov.verts.Length));
        _overlayVbuf[f].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
        void* im = MapBuffer(_overlayIbuf[f]);
        ov.idx.AsSpan().CopyTo(new Span<uint>(im, ov.idx.Length));
        _overlayIbuf[f].Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        // crack cbuffer: same as world but alpha test off, alpha 0.9 (blended).
        float dayLight = _haveSky ? _frameSky.DayLight : 1.0f;
        var fogCol = _haveSky ? _frameSky.Fog : new Vector3(0.62f, 0.74f, 1.0f);
        float fogStart = _haveSky ? _frameSky.FogStart : 350f;
        float fogEnd = _haveSky ? _frameSky.FogEnd : 512f;
        var ubo = new ChunkShared
        {
            viewProj = BuildViewProj(_frameCam),
            shadowMat = Matrix4x4.Identity,
            light = new Vector4(dayLight, 0.0f, 0.1f, 0.0f),
            fogColor = new Vector4(fogCol, 1f),
            misc = new Vector4((float)_frameTime, 0, 0, 0),
            fog = new Vector4(fogStart, fogEnd, 0f, 0.9f),
        };
        System.Buffer.MemoryCopy(&ubo, _overlayCbufMapped, CbvSize, (long)Marshal.SizeOf<ChunkShared>());

        var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &scissor);
        _commandList.SetPipelineState(_translucentPso);
        _commandList.SetGraphicsRootSignature(_rootSignature);
        // Bind the per-frame combined SRV table [atlas t0, shadow[f] t1]; the overlay
        // sets shadowsOn=0 so the shadow map isn't sampled, but the world root sig's
        // 2-descriptor table must still point at valid descriptors.
        var heap = (ID3D12DescriptorHeap*)_shadowSrvHeap.Handle;
        _commandList.SetDescriptorHeaps(1, &heap);
        _commandList.SetGraphicsRootConstantBufferView(0, _overlayCbuf.GetGPUVirtualAddress());
        GpuDescriptorHandle ovTable = _shadowSrvHeap.GetGPUDescriptorHandleForHeapStart();
        ovTable.Ptr += (ulong)(_frameIndex * 2 * (int)_shadowSrvIncrement);
        _commandList.SetGraphicsRootDescriptorTable(2, ovTable);
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        var origin = stackalloc float[4] { ov.originX, ov.originY, ov.originZ, 0 };
        _commandList.SetGraphicsRoot32BitConstants(1, 4, origin, 0);
        var vbv = new VertexBufferView { BufferLocation = _overlayVbuf[f].GetGPUVirtualAddress(), SizeInBytes = vBytes, StrideInBytes = VertexStride };
        _commandList.IASetVertexBuffers(0, 1, &vbv);
        var ibv = new IndexBufferView { BufferLocation = _overlayIbuf[f].GetGPUVirtualAddress(), SizeInBytes = iBytes, Format = Format.FormatR32Uint };
        _commandList.IASetIndexBuffer(&ibv);
        _commandList.DrawIndexedInstanced((uint)ov.indexCount, 1, 0, 0, 0);
    }

    // ═══════════════════════════════════════════════════════════════ Sprites

    public void RenderSprites(Pebble.SpriteList sprites, PebbleCore.CamState cam)
    {
        _frameSprites = sprites;
        _frameCam = cam;
        if (!_initialized || !_worldReady) return;
        if (sprites.sprites.Count == 0) return;
        if (!_spriteReady) InitSpriteRenderer();
        if (!_spriteReady) return;
        RecordSprites();
    }

    private void InitSpriteRenderer()
    {
        EnsureIconTexture();
        CreateSpriteRootSignature();
        CreateSpritePipeline();
        for (uint i = 0; i < FrameCount; i++)
        {
            _spriteCbuf[i] = CreateUploadBuffer(SpriteCbvSize * (uint)MaxSpritesPerFrame);
            _spriteCbufMapped[i] = MapBuffer(_spriteCbuf[i]);
        }
        _spriteReady = true;
    }

    private void CreateSpriteRootSignature()
    {
        // [0] CBV b0 (per-sprite), [1] SRV table t0 (icon array) + static sampler s0.
        var ranges = stackalloc DescriptorRange[1];
        ranges[0] = new DescriptorRange { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = 0, RegisterSpace = 0, OffsetInDescriptorsFromTableStart = 0 };
        var rootParams = stackalloc RootParameter[2];
        rootParams[0] = new RootParameter { ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.All };
        rootParams[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };
        rootParams[1] = new RootParameter { ParameterType = RootParameterType.TypeDescriptorTable, ShaderVisibility = ShaderVisibility.Pixel };
        rootParams[1].Anonymous.DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = ranges };
        var sampler = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Clamp, AddressV = TextureAddressMode.Clamp, AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0, MaxAnisotropy = 1, ComparisonFunc = ComparisonFunc.Always, BorderColor = StaticBorderColor.OpaqueBlack,
            MinLOD = 0, MaxLOD = 0, ShaderRegister = 0, RegisterSpace = 0, ShaderVisibility = ShaderVisibility.Pixel,
        };
        var rsDesc = new RootSignatureDesc { NumParameters = 2, PParameters = rootParams, NumStaticSamplers = 1, PStaticSamplers = &sampler, Flags = RootSignatureFlags.None };
        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0) throw new InvalidOperationException($"[D3D12] SerializeRootSignature (sprite) failed (0x{hr:X8}): {BlobToString(errBlob)}");
        fixed (ID3D12RootSignature** pp = &_spriteRootSig.Handle)
            ThrowIfFailed(_device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(), SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp), "CreateRootSignature (sprite)");
        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    private void CreateSpritePipeline()
    {
        var vs = CompileHlsl(SpriteShaders.HlslVertex, "VSMain", "vs_5_0");
        var ps = CompileHlsl(SpriteShaders.HlslFragment, "PSMain", "ps_5_0");
        try
        {
            var rasterizer = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false, DepthClipEnable = true };
            var ds = new DepthStencilDesc { DepthEnable = true, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.Less, StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff };
            var blendOn = new RenderTargetBlendDesc
            {
                BlendEnable = true, LogicOpEnable = false, SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add, LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            var blend = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blend.RenderTarget[0] = blendOn;
            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _spriteRootSig,
                VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = ps.GetBufferPointer(), BytecodeLength = ps.GetBufferSize() },
                RasterizerState = rasterizer, DepthStencilState = ds, BlendState = blend, SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, NumRenderTargets = 1, DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = null, NumElements = 0 }, SampleDesc = new SampleDesc(1, 0),
            };
            psoDesc.RTVFormats[0] = SceneRtvFormat;
            fixed (ID3D12PipelineState** pp = &_spritePso.Handle)
                ThrowIfFailed(_device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp), "CreateGraphicsPipelineState (sprite)");
        }
        finally { vs.Dispose(); ps.Dispose(); }
    }

    private void RecordSprites()
    {
        var list = _frameSprites!;
        int n = Math.Min(list.sprites.Count, MaxSpritesPerFrame);
        if (n == 0) return;
        uint f = _frameIndex;
        var viewProj = BuildViewProj(_frameCam);

        byte* basePtr = (byte*)_spriteCbufMapped[f];
        for (int i = 0; i < n; i++)
        {
            var s = list.sprites[i];
            // camera-relative center (BuildViewProj places the eye at the origin).
            var u = new SpriteUniform
            {
                viewProj = viewProj,
                center = new Vector4(
                    (float)(s.x - _frameCam.x), (float)(s.y + s.bob - _frameCam.y), (float)(s.z - _frameCam.z), s.size),
                right = new Vector4(list.rightX, 0, list.rightZ, s.iconLayer),
                light = new Vector4(s.light, list.fogStart, list.fogEnd, 0),
                fogColor = new Vector4(list.fogR, list.fogG, list.fogB, 1),
            };
            System.Buffer.MemoryCopy(&u, basePtr + (long)i * SpriteCbvSize, SpriteCbvSize, (long)Marshal.SizeOf<SpriteUniform>());
        }

        var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &scissor);
        _commandList.SetPipelineState(_spritePso);
        _commandList.SetGraphicsRootSignature(_spriteRootSig);
        var heap = (ID3D12DescriptorHeap*)_iconSrvHeap.Handle;
        _commandList.SetDescriptorHeaps(1, &heap);
        _commandList.SetGraphicsRootDescriptorTable(1, _iconSrvHeap.GetGPUDescriptorHandleForHeapStart());
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        ulong cbufBase = _spriteCbuf[f].GetGPUVirtualAddress();
        for (int i = 0; i < n; i++)
        {
            _commandList.SetGraphicsRootConstantBufferView(0, cbufBase + (ulong)((long)i * SpriteCbvSize));
            _commandList.DrawInstanced(6, 1, 0, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════ Particles

    public void RenderParticles(Pebble.Particles particles, PebbleCore.CamState cam)
    {
        _frameParticles = particles;
        _frameCam = cam;
        if (!_initialized || !_worldReady) return;
        if (particles.Count == 0) return;
        if (!_particleReady) InitParticleRenderer();
        if (!_particleReady) return;
        RecordParticles();
    }

    private void InitParticleRenderer()
    {
        CreateParticleRootSignature();
        CreateParticlePipeline();
        // static quad VB (6 corners).
        float[] quad = { -1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1 };
        _particleQuad = CreateUploadBuffer((ulong)quad.Length * sizeof(float));
        void* qm = MapBuffer(_particleQuad);
        quad.AsSpan().CopyTo(new Span<float>(qm, quad.Length));
        _particleQuad.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
        for (uint i = 0; i < FrameCount; i++)
        {
            _particleCbuf[i] = CreateUploadBuffer(ParticleCbvSize);
            _particleCbufMapped[i] = MapBuffer(_particleCbuf[i]);
        }
        _particleReady = true;
    }

    private void CreateParticleRootSignature()
    {
        // [0] CBV b0 (ParticleU), [1] SRV table t0 (atlas array) + static sampler s0.
        var ranges = stackalloc DescriptorRange[1];
        ranges[0] = new DescriptorRange { RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = 0, RegisterSpace = 0, OffsetInDescriptorsFromTableStart = 0 };
        var rootParams = stackalloc RootParameter[2];
        rootParams[0] = new RootParameter { ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.Vertex };
        rootParams[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };
        rootParams[1] = new RootParameter { ParameterType = RootParameterType.TypeDescriptorTable, ShaderVisibility = ShaderVisibility.Pixel };
        rootParams[1].Anonymous.DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = ranges };
        var sampler = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipPoint, AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0, MaxAnisotropy = 1, ComparisonFunc = ComparisonFunc.Always, BorderColor = StaticBorderColor.OpaqueBlack,
            MinLOD = 0, MaxLOD = 0, ShaderRegister = 0, RegisterSpace = 0, ShaderVisibility = ShaderVisibility.Pixel,
        };
        var rsDesc = new RootSignatureDesc { NumParameters = 2, PParameters = rootParams, NumStaticSamplers = 1, PStaticSamplers = &sampler, Flags = RootSignatureFlags.AllowInputAssemblerInputLayout };
        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0) throw new InvalidOperationException($"[D3D12] SerializeRootSignature (particle) failed (0x{hr:X8}): {BlobToString(errBlob)}");
        fixed (ID3D12RootSignature** pp = &_particleRootSig.Handle)
            ThrowIfFailed(_device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(), SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp), "CreateRootSignature (particle)");
        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    private void CreateParticlePipeline()
    {
        var vs = CompileHlsl(ParticleShaders.HlslVertex, "VSMain", "vs_5_0");
        var ps = CompileHlsl(ParticleShaders.HlslFragment, "PSMain", "ps_5_0");
        var semCorner = (byte*)SilkMarshal.StringToPtr("CORNER");
        var semPos = (byte*)SilkMarshal.StringToPtr("POSITION");
        var semUv = (byte*)SilkMarshal.StringToPtr("UVRECT");
        var semLs = (byte*)SilkMarshal.StringToPtr("LAYERSIZE");
        var semCl = (byte*)SilkMarshal.StringToPtr("COLORLIGHT");
        try
        {
            var elems = stackalloc InputElementDesc[5];
            elems[0] = new InputElementDesc { SemanticName = semCorner, SemanticIndex = 0, Format = Format.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[1] = new InputElementDesc { SemanticName = semPos, SemanticIndex = 0, Format = Format.FormatR32G32B32Float, InputSlot = 1, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1 };
            elems[2] = new InputElementDesc { SemanticName = semUv, SemanticIndex = 0, Format = Format.FormatR32G32B32A32Float, InputSlot = 1, AlignedByteOffset = 12, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1 };
            elems[3] = new InputElementDesc { SemanticName = semLs, SemanticIndex = 0, Format = Format.FormatR32Float, InputSlot = 1, AlignedByteOffset = 28, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1 };
            elems[4] = new InputElementDesc { SemanticName = semCl, SemanticIndex = 0, Format = Format.FormatR32G32B32A32Float, InputSlot = 1, AlignedByteOffset = 32, InputSlotClass = InputClassification.PerInstanceData, InstanceDataStepRate = 1 };

            var rasterizer = new RasterizerDesc { FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false, DepthClipEnable = true };
            // depth test ON, write OFF.
            var ds = new DepthStencilDesc { DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.LessEqual, StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff };
            var blendOn = new RenderTargetBlendDesc
            {
                BlendEnable = true, LogicOpEnable = false, SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add, LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            var blend = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blend.RenderTarget[0] = blendOn;
            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _particleRootSig,
                VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = ps.GetBufferPointer(), BytecodeLength = ps.GetBufferSize() },
                RasterizerState = rasterizer, DepthStencilState = ds, BlendState = blend, SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, NumRenderTargets = 1, DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = elems, NumElements = 5 }, SampleDesc = new SampleDesc(1, 0),
            };
            psoDesc.RTVFormats[0] = SceneRtvFormat;
            fixed (ID3D12PipelineState** pp = &_particlePso.Handle)
                ThrowIfFailed(_device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp), "CreateGraphicsPipelineState (particle)");
        }
        finally
        {
            SilkMarshal.Free((nint)semCorner); SilkMarshal.Free((nint)semPos); SilkMarshal.Free((nint)semUv);
            SilkMarshal.Free((nint)semLs); SilkMarshal.Free((nint)semCl);
            vs.Dispose(); ps.Dispose();
        }
    }

    private void RecordParticles()
    {
        var cam = _frameCam;
        int n = _frameParticles!.Pack(cam.x, cam.y, cam.z);
        if (n == 0) return;
        var data = _frameParticles.Instances;
        uint f = _frameIndex;
        uint bytes = (uint)n * ParticleInstanceStride;
        if (_particleInstSize[f] < bytes || _particleInst[f].Handle == null)
        {
            if (_particleInst[f].Handle != null) { _particleInst[f].Dispose(); _particleInst[f] = default; }
            uint cap = Math.Max(bytes, 256 * ParticleInstanceStride);
            _particleInst[f] = CreateUploadBuffer(cap);
            _particleInstSize[f] = cap;
        }
        void* m = MapBuffer(_particleInst[f]);
        data.AsSpan(0, n * 12).CopyTo(new Span<float>(m, n * 12));
        _particleInst[f].Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        var pr = new Vector3((float)PebbleCore.DetMath.detCos(cam.yaw), 0, (float)PebbleCore.DetMath.detSin(cam.yaw));
        var pu = new Vector3(
            (float)(PebbleCore.DetMath.detSin(cam.yaw) * PebbleCore.DetMath.detSin(cam.pitch)),
            (float)PebbleCore.DetMath.detCos(cam.pitch),
            (float)(-PebbleCore.DetMath.detCos(cam.yaw) * PebbleCore.DetMath.detSin(cam.pitch)));
        float dayLight = _haveSky ? _frameSky.DayLight : 1.0f;
        var u = new ParticleUniform { viewProj = BuildViewProj(cam), right = new Vector4(pr, 0), up = new Vector4(pu, dayLight) };
        System.Buffer.MemoryCopy(&u, _particleCbufMapped[f], ParticleCbvSize, (long)Marshal.SizeOf<ParticleUniform>());

        var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &scissor);
        _commandList.SetPipelineState(_particlePso);
        _commandList.SetGraphicsRootSignature(_particleRootSig);
        var heap = (ID3D12DescriptorHeap*)_srvHeap.Handle;
        _commandList.SetDescriptorHeaps(1, &heap);
        _commandList.SetGraphicsRootConstantBufferView(0, _particleCbuf[f].GetGPUVirtualAddress());
        _commandList.SetGraphicsRootDescriptorTable(1, _srvHeap.GetGPUDescriptorHandleForHeapStart());
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        var vbvs = stackalloc VertexBufferView[2];
        vbvs[0] = new VertexBufferView { BufferLocation = _particleQuad.GetGPUVirtualAddress(), SizeInBytes = 12 * sizeof(float), StrideInBytes = 8 };
        vbvs[1] = new VertexBufferView { BufferLocation = _particleInst[f].GetGPUVirtualAddress(), SizeInBytes = bytes, StrideInBytes = ParticleInstanceStride };
        _commandList.IASetVertexBuffers(0, 2, vbvs);
        _commandList.DrawInstanced(6, (uint)n, 0, 0);
    }

    public bool RequestScreenshot(string path)
    {
        _screenshotPath = path;
        return true;
    }

    public void EndFrame()
    {
        if (!_initialized)
            return;

        // Safety net: if the scene was rerouted to HDR but RecordPost never ran (e.g.
        // no UI this frame), composite now so the swapchain holds the final image.
        if (_scenePostPending)
            RecordPost();

        // Screenshot: copy the rendered backbuffer (still RENDER_TARGET) to a
        // readback buffer before transitioning to PRESENT.
        bool capturing = _screenshotPath != null;
        if (capturing)
            CaptureScreenshot();

        // RENDER_TARGET -> PRESENT
        ResourceBarrierTransition(
            _renderTargets[_frameIndex],
            ResourceStates.RenderTarget,
            ResourceStates.Present);

        ThrowIfFailed(_commandList.Close(), "CommandList.Close");

        ID3D12CommandList* list = (ID3D12CommandList*)_commandList.Handle;
        _queue.ExecuteCommandLists(1, &list);

        ThrowIfFailed(_swapChain.Present(1, 0), "SwapChain.Present");

        MoveToNextFrame();
        _haveWorldFrame = false;

        // Encode the readback buffer once the GPU has finished the copy. MoveToNextFrame
        // above stalled until the previous frame's fence; do a full GPU drain here to
        // be certain the copy is complete before mapping.
        if (capturing && _pendingReadbackPath != null)
        {
            WaitForGpu();
            EncodePendingScreenshot();
        }
    }

    public void Resize(int width, int height)
    {
        if (!_initialized || width == 0 || height == 0)
            return;
        if (width == _width && height == _height)
            return;

        WaitForGpu();

        for (uint i = 0; i < FrameCount; i++)
        {
            if (_renderTargets[i].Handle != null)
            {
                _renderTargets[i].Dispose();
                _renderTargets[i] = default;
            }
            _fenceValues[i] = _fenceValue;
        }

        if (_depthBuffer.Handle != null) { _depthBuffer.Dispose(); _depthBuffer = default; }

        _width = width;
        _height = height;

        ThrowIfFailed(
            _swapChain.ResizeBuffers(FrameCount, (uint)_width, (uint)_height, RtvFormat, 0),
            "SwapChain.ResizeBuffers");

        _frameIndex = _swapChain.GetCurrentBackBufferIndex();

        CpuDescriptorHandle rtvHandle = _rtvHeap.GetCPUDescriptorHandleForHeapStart();
        for (uint i = 0; i < FrameCount; i++)
        {
            fixed (ID3D12Resource** ppTarget = &_renderTargets[i].Handle)
                ThrowIfFailed(
                    _swapChain.GetBuffer(i, SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)ppTarget),
                    $"SwapChain.GetBuffer({i}) (resize)");

            // sRGB RTV over the UNORM buffer (see CreateRtvHeapAndTargets / RtvFormatSrgb).
            var srgbRtv = new RenderTargetViewDesc { Format = RtvFormatSrgb, ViewDimension = RtvDimension.Texture2D };
            _device.CreateRenderTargetView(_renderTargets[i], &srgbRtv, rtvHandle);
            rtvHandle.Ptr += _rtvDescriptorSize;
        }

        CreateDepthResources();
        // HDR scene + bloom/ultra targets are framebuffer-sized; rebuild on resize.
        for (uint i = 0; i < FrameCount; i++)
        {
            if (_hdrColor[i].Handle != null) { _hdrColor[i].Dispose(); _hdrColor[i] = default; }
            if (_bloomA[i].Handle != null) { _bloomA[i].Dispose(); _bloomA[i] = default; }
            if (_bloomB[i].Handle != null) { _bloomB[i].Dispose(); _bloomB[i] = default; }
            if (_ultraA[i].Handle != null) { _ultraA[i].Dispose(); _ultraA[i] = default; }
            if (_ultraB[i].Handle != null) { _ultraB[i].Dispose(); _ultraB[i] = default; }
        }
        CreatePostResources();
    }

    // ═══════════════════════════════════════════════════════ World renderer

    private void InitWorldRenderer()
    {
        CreateAtlasTexture();
        CreateConstantBuffer();
        CreateShadowResources();   // shadow textures + SRVs must exist before the world root sig binds them
        CreateRootSignature();
        CreatePipelines();
        CreateShadowPipeline();
        // Now that the shadow textures exist, point the post SRV heap's shadow slot at
        // them (ultra samples the shadow map for volumetrics).
        UpdateShadowPostSrv();
        _worldReady = true;
    }

    // ── upload-heap committed buffer helper ──────────────────────────────────
    private ComPtr<ID3D12Resource> CreateUploadBuffer(ulong size)
    {
        var heapProps = new HeapProperties
        {
            Type = HeapType.Upload,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 1,
            VisibleNodeMask = 1,
        };
        var resDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = size,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.None,
        };
        var res = default(ComPtr<ID3D12Resource>);
        ThrowIfFailed(
            _device.CreateCommittedResource(
                &heapProps, HeapFlags.None, &resDesc,
                ResourceStates.GenericRead, (ClearValue*)null,
                SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)res.GetAddressOf()),
            "CreateCommittedResource (upload buffer)");
        return res;
    }

    private static void* MapBuffer(ComPtr<ID3D12Resource> res)
    {
        void* mapped;
        var readRange = new Silk.NET.Direct3D12.Range { Begin = (nuint)0, End = (nuint)0 };
        if (res.Map(0, &readRange, &mapped) < 0)
            throw new InvalidOperationException("[D3D12] ID3D12Resource.Map failed.");
        return mapped;
    }

    // ── atlas: a Texture2DArray with N TILE×TILE RGBA8 layers ────────────────
    private void CreateAtlasTexture()
    {
        // Resource pack first: if the Faithful 32x zip is present, upload its
        // per-tile slices at the pack resolution (32×). One slice per tile id in
        // tileName order so the mesher's layer indices still map correctly; tiles
        // the pack lacks fall back to the procedural art inside LoadPack.
        var pack = Pebble.ResourcePacks.LoadDefault();
        byte[][] sliceData;
        int tile;
        uint layers;
        int missing;
        if (pack != null)
        {
            sliceData = pack.slices;
            tile = pack.res;
            layers = (uint)pack.slices.Length;
            missing = 0;
            _animator = new TileAnimator(pack.animations);
        }
        else
        {
            var atlas = PebbleCore.AtlasGlobals.buildAtlas();
            sliceData = atlas.pixels;
            tile = PebbleCore.AtlasGlobals.TILE;
            layers = (uint)atlas.count;
            missing = atlas.missing.Length;
            _animator = null;
        }
        _atlasLayers = layers;
        _atlasTile = tile;

        // Destination Texture2DArray (DEFAULT heap).
        var heapProps = new HeapProperties
        {
            Type = HeapType.Default,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 1,
            VisibleNodeMask = 1,
        };
        var texDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = (ulong)tile,
            Height = (uint)tile,
            DepthOrArraySize = (ushort)layers,
            MipLevels = 1,
            Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.None,
        };
        fixed (ID3D12Resource** pp = &_atlasTexture.Handle)
            ThrowIfFailed(
                _device.CreateCommittedResource(
                    &heapProps, HeapFlags.None, &texDesc,
                    ResourceStates.CopyDest, (ClearValue*)null,
                    SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)pp),
                "CreateCommittedResource (atlas)");

        // Query the copyable footprints for all array slices so the upload-buffer
        // row pitch matches D3D12's 256-byte alignment requirement.
        var layouts = new PlacedSubresourceFootprint[layers];
        var numRows = new uint[layers];
        var rowSizes = new ulong[layers];
        ulong totalBytes = 0;
        fixed (PlacedSubresourceFootprint* pLayouts = layouts)
        fixed (uint* pNumRows = numRows)
        fixed (ulong* pRowSizes = rowSizes)
        {
            _device.GetCopyableFootprints(&texDesc, 0, layers, 0, pLayouts, pNumRows, pRowSizes, &totalBytes);
        }

        var upload = CreateUploadBuffer(totalBytes);
        byte* mapped = (byte*)MapBuffer(upload);
        int srcRowBytes = tile * 4;
        for (int slice = 0; slice < layers; slice++)
        {
            var src = sliceData[slice];
            ulong dstOffset = layouts[slice].Offset;
            uint dstRowPitch = layouts[slice].Footprint.RowPitch;
            for (int y = 0; y < tile; y++)
            {
                byte* dstRow = mapped + (long)dstOffset + (long)y * dstRowPitch;
                src.AsSpan(y * srcRowBytes, srcRowBytes)
                    .CopyTo(new Span<byte>(dstRow, srcRowBytes));
            }
        }
        upload.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        // Record a one-shot copy of each slice + transition to PIXEL_SHADER_RESOURCE
        // onto the dedicated upload list (separate from the per-frame list).
        ThrowIfFailed(_uploadAllocator.Reset(), "uploadAllocator.Reset");
        ThrowIfFailed(_uploadList.Reset(_uploadAllocator, (ID3D12PipelineState*)null), "uploadList.Reset");
        for (uint slice = 0; slice < layers; slice++)
        {
            var dstLoc = new TextureCopyLocation
            {
                PResource = _atlasTexture,
                Type = TextureCopyType.SubresourceIndex,
            };
            dstLoc.Anonymous.SubresourceIndex = slice;

            var srcLoc = new TextureCopyLocation
            {
                PResource = upload,
                Type = TextureCopyType.PlacedFootprint,
            };
            srcLoc.Anonymous.PlacedFootprint = layouts[slice];

            _uploadList.CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, (Box*)null);
        }
        // CopyDest -> PixelShaderResource on the atlas (upload list).
        {
            var barrier = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
            barrier.Anonymous.Transition = new ResourceTransitionBarrier
            {
                PResource = _atlasTexture,
                Subresource = unchecked((uint)0xffffffff),
                StateBefore = ResourceStates.CopyDest,
                StateAfter = ResourceStates.PixelShaderResource,
            };
            _uploadList.ResourceBarrier(1, &barrier);
        }
        ThrowIfFailed(_uploadList.Close(), "uploadList.Close");
        ID3D12CommandList* uploadCmd = (ID3D12CommandList*)_uploadList.Handle;
        _queue.ExecuteCommandLists(1, &uploadCmd);
        WaitForGpu();

        upload.Dispose();

        // Shader-visible SRV heap with the atlas's Texture2DArray view.
        var srvHeapDesc = new DescriptorHeapDesc
        {
            NumDescriptors = 1,
            Type = DescriptorHeapType.CbvSrvUav,
            Flags = DescriptorHeapFlags.ShaderVisible,
        };
        fixed (ID3D12DescriptorHeap** ppHeap = &_srvHeap.Handle)
            ThrowIfFailed(
                _device.CreateDescriptorHeap(&srvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)ppHeap),
                "CreateDescriptorHeap (SRV)");

        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Format.FormatR8G8B8A8Unorm,
            ViewDimension = SrvDimension.Texture2Darray,
            // D3D12_DEFAULT_SHADER_4_COMPONENT_MAPPING
            Shader4ComponentMapping = 0x1688,
        };
        srvDesc.Anonymous.Texture2DArray = new Tex2DArraySrv
        {
            MostDetailedMip = 0,
            MipLevels = 1,
            FirstArraySlice = 0,
            ArraySize = layers,
            PlaneSlice = 0,
            ResourceMinLODClamp = 0f,
        };
        _device.CreateShaderResourceView(_atlasTexture, &srvDesc, _srvHeap.GetCPUDescriptorHandleForHeapStart());

        // Persistent upload-buffer ring for animated-slice re-uploads. One slot per
        // (FrameCount+1) so a slice queued this frame isn't overwritten while an
        // in-flight frame may still copy from it. Row pitch is 256-aligned per D3D12.
        if (_animator != null && _animator.HasAnimations)
        {
            // single-slice footprint (subresource 0) gives the 256-aligned row pitch
            PlacedSubresourceFootprint fp;
            uint aNumRows; ulong aRowSize, aTotalBytes;
            var oneDesc = texDesc; // texDesc still in scope from above
            _device.GetCopyableFootprints(&oneDesc, 0, 1, 0, &fp, &aNumRows, &aRowSize, &aTotalBytes);
            _animRowPitch = fp.Footprint.RowPitch;
            // align slot size to 512 so each slot's base satisfies placement alignment
            _animUploadSlotBytes = (uint)(((long)aTotalBytes + 511) & ~511L);
            _animUploadSlots = (uint)(FrameCount + 1);
            _animUpload = CreateUploadBuffer((ulong)_animUploadSlotBytes * _animUploadSlots);
            _animUploadMapped = MapBuffer(_animUpload);
        }

        Console.WriteLine($"[D3D12] atlas uploaded: {layers} tiles ({tile}x{tile}), missing={missing}, animated={(_animator?.HasAnimations == true ? "yes" : "no")}");
    }

    // Advance + flush animated tiles onto the open per-frame command list (called at
    // BeginFrame, before any sampling). Each changed slice: PIXEL_SHADER_RESOURCE ->
    // COPY_DEST, CopyTextureRegion from the upload ring, -> PIXEL_SHADER_RESOURCE.
    private void FlushAtlasAnimations()
    {
        if (_animator == null || !_animator.HasAnimations) return;

        double now = _frameTime;
        double dtMs = _lastAnimTime < 0 ? 50.0 : Math.Max(0, (now - _lastAnimTime) * 1000.0);
        _lastAnimTime = now;
        _animator.Tick(dtMs);
        var updates = _animator.DrainPending();
        if (updates == null) return;

        int tile = _atlasTile;
        int srcRowBytes = tile * 4;
        foreach (var up in updates)
        {
            uint slot = _animUploadCursor % _animUploadSlots;
            _animUploadCursor++;
            ulong baseOff = (ulong)_animUploadSlotBytes * slot;

            // copy CPU pixels into the ring slot honoring the 256-aligned dst pitch
            byte* mapped = (byte*)_animUploadMapped + (long)baseOff;
            for (int y = 0; y < tile; y++)
            {
                byte* dstRow = mapped + (long)y * _animRowPitch;
                up.Pixels.AsSpan(y * srcRowBytes, srcRowBytes).CopyTo(new Span<byte>(dstRow, srcRowBytes));
            }

            AtlasSubresourceBarrier((uint)up.Slice, ResourceStates.PixelShaderResource, ResourceStates.CopyDest);

            var dstLoc = new TextureCopyLocation { PResource = _atlasTexture, Type = TextureCopyType.SubresourceIndex };
            dstLoc.Anonymous.SubresourceIndex = (uint)up.Slice;
            var fp = new PlacedSubresourceFootprint
            {
                Offset = baseOff,
                Footprint = new SubresourceFootprint
                {
                    Format = Format.FormatR8G8B8A8Unorm,
                    Width = (uint)tile,
                    Height = (uint)tile,
                    Depth = 1,
                    RowPitch = _animRowPitch,
                },
            };
            var srcLoc = new TextureCopyLocation { PResource = _animUpload, Type = TextureCopyType.PlacedFootprint };
            srcLoc.Anonymous.PlacedFootprint = fp;
            _commandList.CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, (Box*)null);

            AtlasSubresourceBarrier((uint)up.Slice, ResourceStates.CopyDest, ResourceStates.PixelShaderResource);
        }
    }

    private void AtlasSubresourceBarrier(uint subresource, ResourceStates before, ResourceStates after)
    {
        var barrier = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = _atlasTexture, Subresource = subresource, StateBefore = before, StateAfter = after,
        };
        _commandList.ResourceBarrier(1, &barrier);
    }

    private void CreateConstantBuffer()
    {
        _cbuffer = CreateUploadBuffer(CbvSize);
        _cbufferMapped = MapBuffer(_cbuffer);
        _cbufferTrans = CreateUploadBuffer(CbvSize);
        _cbufferTransMapped = MapBuffer(_cbufferTrans);
    }

    private void CreateRootSignature()
    {
        // Root signature:
        //   [0] CBV b0           (root descriptor)
        //   [1] 32-bit constants b1 (4 values = float4 origin)
        //   [2] descriptor table  -> SRV t0 (atlas) + t1 (shadow map), contiguous
        //   static sampler s0     atlas: point/clamp
        //   static sampler s1     shadow: comparison LessEqual, linear, clamp
        var ranges = stackalloc DescriptorRange[1];
        ranges[0] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv,
            NumDescriptors = 2,        // t0 atlas + t1 shadow
            BaseShaderRegister = 0,    // t0..t1
            RegisterSpace = 0,
            OffsetInDescriptorsFromTableStart = 0,
        };

        var rootParams = stackalloc RootParameter[3];
        rootParams[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv,
            ShaderVisibility = ShaderVisibility.All,
        };
        rootParams[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };

        rootParams[1] = new RootParameter
        {
            ParameterType = RootParameterType.Type32BitConstants,
            ShaderVisibility = ShaderVisibility.Vertex,
        };
        rootParams[1].Anonymous.Constants = new RootConstants
        {
            ShaderRegister = 1, // b1
            RegisterSpace = 0,
            Num32BitValues = 4, // float4 origin
        };

        rootParams[2] = new RootParameter
        {
            ParameterType = RootParameterType.TypeDescriptorTable,
            ShaderVisibility = ShaderVisibility.Pixel,
        };
        rootParams[2].Anonymous.DescriptorTable = new RootDescriptorTable
        {
            NumDescriptorRanges = 1,
            PDescriptorRanges = ranges,
        };

        var samplers = stackalloc StaticSamplerDesc[2];
        samplers[0] = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunc.Always,
            BorderColor = StaticBorderColor.OpaqueBlack,
            MinLOD = 0,
            MaxLOD = 0,
            ShaderRegister = 0, // s0 (atlas)
            RegisterSpace = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };
        // s1: shadow comparison sampler (LessEqual → returns 1 when ref <= depth = lit),
        // linear filtering for hardware PCF, clamp to edge, white border (= lit outside).
        samplers[1] = new StaticSamplerDesc
        {
            Filter = Filter.ComparisonMinMagLinearMipPoint,
            AddressU = TextureAddressMode.Clamp,
            AddressV = TextureAddressMode.Clamp,
            AddressW = TextureAddressMode.Clamp,
            MipLODBias = 0,
            MaxAnisotropy = 1,
            ComparisonFunc = ComparisonFunc.LessEqual,
            BorderColor = StaticBorderColor.OpaqueWhite,
            MinLOD = 0,
            MaxLOD = 0,
            ShaderRegister = 1, // s1 (shadow)
            RegisterSpace = 0,
            ShaderVisibility = ShaderVisibility.Pixel,
        };

        var rsDesc = new RootSignatureDesc
        {
            NumParameters = 3,
            PParameters = rootParams,
            NumStaticSamplers = 2,
            PStaticSamplers = samplers,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };

        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(
            &rsDesc, D3DRootSignatureVersion.Version1,
            blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0)
        {
            string msg = BlobToString(errBlob);
            throw new InvalidOperationException($"[D3D12] SerializeRootSignature failed (0x{hr:X8}): {msg}");
        }

        fixed (ID3D12RootSignature** pp = &_rootSignature.Handle)
            ThrowIfFailed(
                _device.CreateRootSignature(
                    0, blob.GetBufferPointer(), blob.GetBufferSize(),
                    SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp),
                "CreateRootSignature");

        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    // ── D3DCompiler: HLSL -> DXBC at runtime ─────────────────────────────────
    // The D3DCompiler API wrapper is held for the lifetime of the backend: it owns
    // the loaded d3dcompiler_47.dll, and disposing it invalidates any blobs it has
    // produced (their vtable lives in that DLL). PSO creation below needs the blob
    // pointers to stay valid, so we keep the compiler alive until Dispose.
    private D3DCompiler? _compiler;

    private ComPtr<ID3D10Blob> CompileHlsl(string source, string entry, string target)
    {
        _compiler ??= D3DCompiler.GetApi();
        var compiler = _compiler;

        var srcBytes = System.Text.Encoding.ASCII.GetBytes(source);
        var entryBytes = System.Text.Encoding.ASCII.GetBytes(entry + "\0");
        var targetBytes = System.Text.Encoding.ASCII.GetBytes(target + "\0");

        var code = default(ComPtr<ID3D10Blob>);
        var errors = default(ComPtr<ID3D10Blob>);
        int hr;
        fixed (byte* pSrc = srcBytes)
        fixed (byte* pEntry = entryBytes)
        fixed (byte* pTarget = targetBytes)
        {
            hr = compiler.Compile(
                pSrc, (nuint)srcBytes.Length,
                (byte*)null,                      // source name
                (D3DShaderMacro*)null,            // defines
                (ID3DInclude*)null,               // includes
                pEntry, pTarget,
                0, 0,                             // flags
                code.GetAddressOf(), errors.GetAddressOf());
        }
        if (hr < 0)
        {
            string msg = BlobToString(errors);
            throw new InvalidOperationException($"[D3D12] D3DCompile {target} failed (0x{hr:X8}): {msg}");
        }
        if (errors.Handle != null) errors.Dispose();
        return code;
    }

    private static string BlobToString(ComPtr<ID3D10Blob> blob)
    {
        if (blob.Handle == null) return "(no message)";
        byte* p = (byte*)blob.GetBufferPointer();
        nuint n = blob.GetBufferSize();
        if (p == null || n == 0) return "(empty)";
        return System.Text.Encoding.ASCII.GetString(p, (int)n).TrimEnd('\0', '\n', '\r');
    }

    private void CreatePipelines()
    {
        var vs = CompileHlsl(WorldShadersHlsl.Vertex, "VSMain", "vs_5_0");
        var ps = CompileHlsl(WorldShadersHlsl.Fragment, "PSMain", "ps_5_0");

        var semPos = (byte*)SilkMarshal.StringToPtr("POSITION");
        var semUv = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        var semA = (byte*)SilkMarshal.StringToPtr("A");
        var semB = (byte*)SilkMarshal.StringToPtr("B");
        try
        {
            var elems = stackalloc InputElementDesc[4];
            elems[0] = new InputElementDesc
            {
                SemanticName = semPos, SemanticIndex = 0,
                Format = Format.FormatR32G32B32Float,
                InputSlot = 0, AlignedByteOffset = 0,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            elems[1] = new InputElementDesc
            {
                SemanticName = semUv, SemanticIndex = 0,
                Format = Format.FormatR32G32Float,
                InputSlot = 0, AlignedByteOffset = 12,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            elems[2] = new InputElementDesc
            {
                SemanticName = semA, SemanticIndex = 0,
                Format = Format.FormatR32Uint,
                InputSlot = 0, AlignedByteOffset = 20,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };
            elems[3] = new InputElementDesc
            {
                SemanticName = semB, SemanticIndex = 0,
                Format = Format.FormatR32Uint,
                InputSlot = 0, AlignedByteOffset = 24,
                InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0,
            };

            var rasterizer = new RasterizerDesc
            {
                FillMode = FillMode.Solid,
                CullMode = CullMode.Back,
                // The mesher emits CCW-front faces in a right-handed world. D3D's
                // default front-face winding is clockwise; treating the mesher's CCW
                // faces as front (FrontCounterClockwise = true) keeps outward faces
                // visible — verified against the screenshot.
                FrontCounterClockwise = true,
                DepthBias = 0,
                DepthBiasClamp = 0,
                SlopeScaledDepthBias = 0,
                DepthClipEnable = true,
                MultisampleEnable = false,
                AntialiasedLineEnable = false,
                ForcedSampleCount = 0,
                ConservativeRaster = ConservativeRasterizationMode.Off,
            };

            var depthStencil = new DepthStencilDesc
            {
                DepthEnable = true,
                DepthWriteMask = DepthWriteMask.All,
                DepthFunc = ComparisonFunc.Less,
                StencilEnable = false,
                StencilReadMask = 0xff,
                StencilWriteMask = 0xff,
            };

            var blendOff = new RenderTargetBlendDesc
            {
                BlendEnable = false,
                LogicOpEnable = false,
                SrcBlend = Blend.One,
                DestBlend = Blend.Zero,
                BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One,
                DestBlendAlpha = Blend.Zero,
                BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop,
                RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            var blendDesc = new BlendDesc
            {
                AlphaToCoverageEnable = false,
                IndependentBlendEnable = false,
            };
            blendDesc.RenderTarget[0] = blendOff;

            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _rootSignature,
                VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = ps.GetBufferPointer(), BytecodeLength = ps.GetBufferSize() },
                RasterizerState = rasterizer,
                DepthStencilState = depthStencil,
                BlendState = blendDesc,
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 1,
                DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = elems, NumElements = 4 },
                SampleDesc = new SampleDesc(1, 0),
            };
            psoDesc.RTVFormats[0] = SceneRtvFormat;

            // A single PSO serves both the opaque and cutout layers: the alpha test
            // (fog.z) is driven by the shared constant buffer, so the cutout layer
            // reuses this pipeline exactly as the Vulkan backend reuses its opaque one.
            fixed (ID3D12PipelineState** pp = &_opaquePso.Handle)
                ThrowIfFailed(
                    _device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp),
                    "CreateGraphicsPipelineState (world)");

            // Translucent PSO: same VS/PS + input layout, but depth-write OFF and
            // alpha blending ON (water/glass/ice). Depth TEST stays on so opaque
            // terrain in front still occludes the water.
            var depthTrans = depthStencil;
            depthTrans.DepthWriteMask = DepthWriteMask.Zero;
            var blendOn = new RenderTargetBlendDesc
            {
                BlendEnable = true, LogicOpEnable = false,
                SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            var blendTransDesc = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendTransDesc.RenderTarget[0] = blendOn;
            var psoTrans = psoDesc;
            psoTrans.DepthStencilState = depthTrans;
            psoTrans.BlendState = blendTransDesc;
            fixed (ID3D12PipelineState** pp = &_translucentPso.Handle)
                ThrowIfFailed(
                    _device.CreateGraphicsPipelineState(&psoTrans, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp),
                    "CreateGraphicsPipelineState (translucent)");
        }
        finally
        {
            SilkMarshal.Free((nint)semPos);
            SilkMarshal.Free((nint)semUv);
            SilkMarshal.Free((nint)semA);
            SilkMarshal.Free((nint)semB);
            vs.Dispose();
            ps.Dispose();
        }
    }

    // ── sun shadow map: per-frame depth textures, DSV/SRV heaps, cbuffers ──────
    private void CreateShadowResources()
    {
        // Per-frame D32 depth textures (render target + shader resource).
        var heapProps = new HeapProperties
        {
            Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1,
        };
        var resDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D, Alignment = 0,
            Width = ShadowSize, Height = ShadowSize, DepthOrArraySize = 1, MipLevels = 1,
            Format = Format.FormatR32Typeless,   // typeless so it can be both DSV (D32) and SRV (R32)
            SampleDesc = new SampleDesc(1, 0), Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.AllowDepthStencil,
        };
        var clear = new ClearValue { Format = DepthFormat };
        clear.Anonymous.DepthStencil = new DepthStencilValue { Depth = 1.0f, Stencil = 0 };

        for (int f = 0; f < FrameCount; f++)
        {
            fixed (ID3D12Resource** pp = &_shadowTex[f].Handle)
                ThrowIfFailed(
                    _device.CreateCommittedResource(&heapProps, HeapFlags.None, &resDesc,
                        ResourceStates.PixelShaderResource, &clear,
                        SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)pp),
                    $"CreateCommittedResource (shadow {f})");
        }

        // DSV heap (FrameCount D32 depth-stencil views).
        var dsvHeapDesc = new DescriptorHeapDesc
        {
            NumDescriptors = FrameCount, Type = DescriptorHeapType.Dsv, Flags = DescriptorHeapFlags.None,
        };
        fixed (ID3D12DescriptorHeap** pp = &_shadowDsvHeap.Handle)
            ThrowIfFailed(_device.CreateDescriptorHeap(&dsvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)pp),
                "CreateDescriptorHeap (shadow DSV)");
        _shadowDsvIncrement = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.Dsv);

        // Shader-visible SRV heap: per frame [atlas SRV (t0), shadow SRV (t1)] so the
        // world descriptor table for frame f binds atlas+shadow[f] contiguously.
        var srvHeapDesc = new DescriptorHeapDesc
        {
            NumDescriptors = FrameCount * 2, Type = DescriptorHeapType.CbvSrvUav, Flags = DescriptorHeapFlags.ShaderVisible,
        };
        fixed (ID3D12DescriptorHeap** pp = &_shadowSrvHeap.Handle)
            ThrowIfFailed(_device.CreateDescriptorHeap(&srvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)pp),
                "CreateDescriptorHeap (shadow SRV)");
        _shadowSrvIncrement = _device.GetDescriptorHandleIncrementSize(DescriptorHeapType.CbvSrvUav);

        // Atlas SRV descriptor (replicated into each frame's slot 0).
        var atlasSrv = new ShaderResourceViewDesc
        {
            Format = Format.FormatR8G8B8A8Unorm, ViewDimension = SrvDimension.Texture2Darray, Shader4ComponentMapping = 0x1688,
        };
        atlasSrv.Anonymous.Texture2DArray = new Tex2DArraySrv
        {
            MostDetailedMip = 0, MipLevels = 1, FirstArraySlice = 0, ArraySize = _atlasLayers, PlaneSlice = 0, ResourceMinLODClamp = 0f,
        };
        // Shadow SRV descriptor (R32_FLOAT view of the typeless depth texture).
        var shadowSrv = new ShaderResourceViewDesc
        {
            Format = Format.FormatR32Float, ViewDimension = SrvDimension.Texture2D, Shader4ComponentMapping = 0x1688,
        };
        shadowSrv.Anonymous.Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f };

        var dsvDesc = new DepthStencilViewDesc { Format = DepthFormat, ViewDimension = DsvDimension.Texture2D, Flags = DsvFlags.None };

        CpuDescriptorHandle srvBase = _shadowSrvHeap.GetCPUDescriptorHandleForHeapStart();
        CpuDescriptorHandle dsvBase = _shadowDsvHeap.GetCPUDescriptorHandleForHeapStart();
        for (int f = 0; f < FrameCount; f++)
        {
            var atlasHandle = srvBase; atlasHandle.Ptr += (nuint)((f * 2 + 0) * (int)_shadowSrvIncrement);
            _device.CreateShaderResourceView(_atlasTexture, &atlasSrv, atlasHandle);
            var shadowHandle = srvBase; shadowHandle.Ptr += (nuint)((f * 2 + 1) * (int)_shadowSrvIncrement);
            _device.CreateShaderResourceView(_shadowTex[f], &shadowSrv, shadowHandle);

            var dsvHandle = dsvBase; dsvHandle.Ptr += (nuint)(f * (int)_shadowDsvIncrement);
            _device.CreateDepthStencilView(_shadowTex[f], &dsvDesc, dsvHandle);

            _shadowCbuf[f] = CreateUploadBuffer(CbvSize);
            _shadowCbufMapped[f] = MapBuffer(_shadowCbuf[f]);
        }

        CreateShadowRootSignature();
        _shadowReady = true;
    }

    private void CreateShadowRootSignature()
    {
        // [0] CBV b0 (shadowMat in viewProj slot) + [1] 32-bit consts b1 (origin). No SRV.
        var rootParams = stackalloc RootParameter[2];
        rootParams[0] = new RootParameter { ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.Vertex };
        rootParams[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };
        rootParams[1] = new RootParameter { ParameterType = RootParameterType.Type32BitConstants, ShaderVisibility = ShaderVisibility.Vertex };
        rootParams[1].Anonymous.Constants = new RootConstants { ShaderRegister = 1, RegisterSpace = 0, Num32BitValues = 4 };

        var rsDesc = new RootSignatureDesc
        {
            NumParameters = 2, PParameters = rootParams, NumStaticSamplers = 0, PStaticSamplers = null,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };
        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0) throw new InvalidOperationException($"[D3D12] SerializeRootSignature (shadow) failed (0x{hr:X8}): {BlobToString(errBlob)}");
        fixed (ID3D12RootSignature** pp = &_shadowRootSig.Handle)
            ThrowIfFailed(_device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(),
                SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp), "CreateRootSignature (shadow)");
        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    private void CreateShadowPipeline()
    {
        var vs = CompileHlsl(WorldShadersHlsl.ShadowVertex, "VSMain", "vs_5_0");
        var semPos = (byte*)SilkMarshal.StringToPtr("POSITION");
        var semUv = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        var semA = (byte*)SilkMarshal.StringToPtr("A");
        var semB = (byte*)SilkMarshal.StringToPtr("B");
        try
        {
            var elems = stackalloc InputElementDesc[4];
            elems[0] = new InputElementDesc { SemanticName = semPos, SemanticIndex = 0, Format = Format.FormatR32G32B32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[1] = new InputElementDesc { SemanticName = semUv, SemanticIndex = 0, Format = Format.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 12, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[2] = new InputElementDesc { SemanticName = semA, SemanticIndex = 0, Format = Format.FormatR32Uint, InputSlot = 0, AlignedByteOffset = 20, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[3] = new InputElementDesc { SemanticName = semB, SemanticIndex = 0, Format = Format.FormatR32Uint, InputSlot = 0, AlignedByteOffset = 24, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };

            // Front-face cull + depth bias to fight acne / peter-panning (mirrors the
            // Vulkan shadow pipeline + the Swift setDepthBias(6, slopeScale 8, clamp .02)).
            // D3D depth bias is an integer for the constant term; 6/512 ≈ scaled units.
            var rasterizer = new RasterizerDesc
            {
                FillMode = FillMode.Solid, CullMode = CullMode.Front, FrontCounterClockwise = true,
                DepthBias = 6, DepthBiasClamp = 0.02f, SlopeScaledDepthBias = 8.0f,
                DepthClipEnable = true, MultisampleEnable = false, AntialiasedLineEnable = false,
                ForcedSampleCount = 0, ConservativeRaster = ConservativeRasterizationMode.Off,
            };
            var depthStencil = new DepthStencilDesc
            {
                DepthEnable = true, DepthWriteMask = DepthWriteMask.All, DepthFunc = ComparisonFunc.LessEqual,
                StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff,
            };
            var blendDesc = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };

            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _shadowRootSig,
                VS = new ShaderBytecode { PShaderBytecode = vs.GetBufferPointer(), BytecodeLength = vs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = null, BytecodeLength = 0 },   // depth-only, no PS
                RasterizerState = rasterizer,
                DepthStencilState = depthStencil,
                BlendState = blendDesc,
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 0,
                DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = elems, NumElements = 4 },
                SampleDesc = new SampleDesc(1, 0),
            };
            fixed (ID3D12PipelineState** pp = &_shadowPso.Handle)
                ThrowIfFailed(_device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp),
                    "CreateGraphicsPipelineState (shadow)");
        }
        finally
        {
            SilkMarshal.Free((nint)semPos); SilkMarshal.Free((nint)semUv);
            SilkMarshal.Free((nint)semA); SilkMarshal.Free((nint)semB);
            vs.Dispose();
        }
    }

    /// Render the scene depth from the sun direction into this frame's shadow map.
    /// Recorded BEFORE the world pass; the world PS then samples shadowTex[_frameIndex].
    private void RecordShadowPass()
    {
        if (!_shadowReady || !_frameShadowsOn || _frameHost == null) return;
        uint f = _frameIndex;

        // shadow cbuffer: shadowMat in the viewProj slot (shadow_vs reads viewProj).
        var su = new ChunkShared { viewProj = _frameShadowMat, shadowMat = _frameShadowMat };
        System.Buffer.MemoryCopy(&su, _shadowCbufMapped[f], CbvSize, (long)Marshal.SizeOf<ChunkShared>());

        // PIXEL_SHADER_RESOURCE -> DEPTH_WRITE for this frame's shadow texture.
        ResourceBarrierTransition(_shadowTex[f], ResourceStates.PixelShaderResource, ResourceStates.DepthWrite);

        CpuDescriptorHandle dsv = _shadowDsvHeap.GetCPUDescriptorHandleForHeapStart();
        dsv.Ptr += (nuint)(f * (int)_shadowDsvIncrement);
        _commandList.OMSetRenderTargets(0, (CpuDescriptorHandle*)null, false, &dsv);
        _commandList.ClearDepthStencilView(dsv, ClearFlags.Depth, 1.0f, 0, 0, (Box2D<int>*)null);

        var viewport = new Viewport(0, 0, ShadowSize, ShadowSize, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, ShadowSize, ShadowSize);
        _commandList.RSSetScissorRects(1, &scissor);

        _commandList.SetPipelineState(_shadowPso);
        _commandList.SetGraphicsRootSignature(_shadowRootSig);
        _commandList.SetGraphicsRootConstantBufferView(0, _shadowCbuf[f].GetGPUVirtualAddress());
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        // Sun-aligned radius bound (matches Swift) + light-space frustum cull so we
        // only rasterize sections that land inside the shadow map.
        var cam = _frameCam;
        const float r = 72f;
        var shadowVp = _frameShadowMat;
        // Hoisted out of the (per-section) loop (CA2014): one scratch buffer, refilled
        // each visible section, so the stack doesn't grow across thousands of sections.
        var origin = stackalloc float[4];
        foreach (var kv in _sectionCache)
        {
            if (kv.Value.opaque == null) continue;
            var (cx, sy, cz) = kv.Key;
            if (!_frameHost.sections.TryGetValue(kv.Key, out var sm)) continue;
            float ox = (float)(cx * 16 - cam.x);
            float oy = (float)(sm.minY + sy * 16 - cam.y);
            float oz = (float)(cz * 16 - cam.z);
            if (Math.Abs(ox + 8) > r + 24 || Math.Abs(oz + 8) > r + 24) continue;
            if (!FrustumCull.SectionVisible(new Vector3(ox, oy, oz), shadowVp)) continue;
            origin[0] = ox; origin[1] = oy; origin[2] = oz; origin[3] = 0;
            _commandList.SetGraphicsRoot32BitConstants(1, 4, origin, 0);
            DrawSectionGpu(kv.Value.opaque);
        }

        // DEPTH_WRITE -> PIXEL_SHADER_RESOURCE so the world PS can sample it.
        ResourceBarrierTransition(_shadowTex[f], ResourceStates.DepthWrite, ResourceStates.PixelShaderResource);

        // Re-bind the scene color RTV (HDR target when post is active) + main DSV.
        CpuDescriptorHandle rtv = _scenePostPending ? PostRtv(_frameIndex, 0) : CurrentRtvHandle();
        CpuDescriptorHandle mainDsv = _dsvHeap.GetCPUDescriptorHandleForHeapStart();
        _commandList.OMSetRenderTargets(1, &rtv, false, &mainDsv);
    }

    // ── camera / matrices (camera-relative; D3D clip: +Y up, z in [0,1], LH) ──
    private Matrix4x4 BuildViewProj(in PebbleCore.CamState cam)
    {
        float aspect = _height == 0 ? 1f : (float)_width / _height;
        float fovY = (float)(cam.fov * Math.PI / 180.0);
        const float near = 0.05f, far = 512f;

        // Right-handed perspective mapping z to [0,1] (D3D NDC), +Y up. This is the
        // Vulkan proj WITHOUT the row-1 Y negation, since D3D clip space is +Y-up.
        float f = 1.0f / MathF.Tan(fovY * 0.5f);
        var proj = new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, f, 0, 0,
            0, 0, far / (near - far), -1,
            0, 0, (near * far) / (near - far), 0);

        double yaw = cam.yaw, pitch = cam.pitch;
        var dir = new Vector3(
            (float)(Math.Cos(pitch) * -Math.Sin(yaw)),
            (float)Math.Sin(-pitch),
            (float)(Math.Cos(pitch) * Math.Cos(yaw)));
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, dir, new Vector3(0, 1, 0));

        // System.Numerics is row-vector (clip_row = rel_row * (view*proj)). HLSL reads
        // a cbuffer float4x4 COLUMN-major (same as GLSL std140), so uploading the
        // row-major bytes of (view*proj) makes mul(viewProj, float4(rel,1)) evaluate
        // transpose(view*proj) * rel_col == rel_row * (view*proj) — exactly the clip
        // position we want. So we upload it UN-transposed, identical to the Vulkan
        // backend (which differs only by negating proj row 1 for its +Y-down clip).
        return Matrix4x4.Multiply(view, proj);
    }

    // ── per-section GPU upload (cache; re-upload only on mesh change) ─────────
    private SectionGpu? UploadLayer(in PebbleCore.MeshLayer layer)
    {
        if (layer.idx == null || layer.idx.Length == 0 || layer.data == null || layer.data.Length == 0)
            return null;

        var gpu = new SectionGpu { indexCount = (uint)layer.idx.Length };
        gpu.vbufSize = (uint)layer.data.Length * sizeof(uint);
        gpu.ibufSize = (uint)layer.idx.Length * sizeof(uint);

        gpu.vbuf = CreateUploadBuffer(gpu.vbufSize);
        gpu.ibuf = CreateUploadBuffer(gpu.ibufSize);

        void* vmapped = MapBuffer(gpu.vbuf);
        layer.data.AsSpan().CopyTo(new Span<uint>(vmapped, layer.data.Length));
        gpu.vbuf.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        void* imapped = MapBuffer(gpu.ibuf);
        layer.idx.AsSpan().CopyTo(new Span<uint>(imapped, layer.idx.Length));
        gpu.ibuf.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        return gpu;
    }

    /// Immediately release a section's GPU buffers. ONLY safe when the GPU is known
    /// not to reference them (deferred-free drain after a fence wait, or after a
    /// full GPU drain in Dispose). Use DeferFreeSectionGpu from the frame loop.
    private void DestroySectionGpu(SectionGpu? gpu)
    {
        if (gpu == null) return;
        if (gpu.vbuf.Handle != null) { gpu.vbuf.Dispose(); gpu.vbuf = default; }
        if (gpu.ibuf.Handle != null) { gpu.ibuf.Dispose(); gpu.ibuf = default; }
    }

    /// Queue a section's GPU buffers for release FrameCount frames from now. They
    /// may still be referenced by an in-flight command list, so they are parked in
    /// the current ring slot and released by DrainPendingFrees only after a later
    /// frame's fence wait proves the GPU is done with them.
    private void DeferFreeSectionGpu(SectionGpu? gpu)
    {
        if (gpu == null) return;
        _pendingFree[_deferSlot].Add(gpu);
    }

    /// Release everything parked in the given ring slot and clear it. The caller
    /// must have already waited the fence that guarantees the GPU finished the work
    /// submitted when this slot was last filled.
    private void DrainPendingFrees(int slot)
    {
        var bucket = _pendingFree[slot];
        if (bucket == null || bucket.Count == 0) return;
        for (int i = 0; i < bucket.Count; i++)
            DestroySectionGpu(bucket[i]);
        bucket.Clear();
    }

    private void SyncSections(HostBridge host)
    {
        var live = new HashSet<(int, int, int)>();
        foreach (var kv in host.sections)
        {
            var key = kv.Key;
            var sm = kv.Value;
            live.Add(key);

            if (_sectionCache.TryGetValue(key, out var existing) && ReferenceEquals(existing.meshRef, sm))
                continue;

            if (existing != null)
            {
                DeferFreeSectionGpu(existing.opaque);
                DeferFreeSectionGpu(existing.cutout);
                DeferFreeSectionGpu(existing.translucent);
            }
            var layers = new SectionLayers
            {
                meshRef = sm,
                opaque = UploadLayer(sm.mesh.opaque),
                cutout = UploadLayer(sm.mesh.cutout),
                translucent = UploadLayer(sm.mesh.translucent),
            };
            _sectionCache[key] = layers;
        }

        if (_sectionCache.Count > live.Count)
        {
            var dead = new List<(int, int, int)>();
            foreach (var key in _sectionCache.Keys)
                if (!live.Contains(key)) dead.Add(key);
            foreach (var key in dead)
            {
                var l = _sectionCache[key];
                DeferFreeSectionGpu(l.opaque);
                DeferFreeSectionGpu(l.cutout);
                DeferFreeSectionGpu(l.translucent);
                _sectionCache.Remove(key);
            }
        }
    }

    /// Record the world draw onto the open command list (called from RenderWorld).
    /// SyncSections already ran in RenderWorld (before the shadow pass), so the
    /// section cache is current here — do NOT re-sync (it would free/destroy GPU
    /// buffers the shadow pass already recorded draws against this frame).
    private void RecordWorld()
    {
        if (!_haveWorldFrame || _frameHost == null) return;

        var cam = _frameCam;

        // Update the shared constant buffer once for the whole frame. A single alpha
        // test of 0.5 is correct for cutout AND harmless for opaque (alpha 1.0).
        // Day-night lighting + fog come from the frame's SkyState (cached by
        // RenderSky); falls back to full daylight if RenderSky was skipped.
        float dayLight = _haveSky ? _frameSky.DayLight : 1.0f;
        var fogCol = _haveSky ? _frameSky.Fog : new Vector3(0.62f, 0.74f, 1.0f);
        float fogStart = _haveSky ? _frameSky.FogStart : 350f;
        float fogEnd = _haveSky ? _frameSky.FogEnd : 512f;
        float shadowsOn = _frameShadowsOn ? 1f : 0f;
        float shadowTexel = 1f / ShadowSize;
        var ubo = new ChunkShared
        {
            viewProj = BuildViewProj(cam),
            shadowMat = _frameShadowsOn ? _frameShadowMat : Matrix4x4.Identity,
            light = new Vector4(dayLight, 0.0f, 0.1f, shadowsOn),   // dayLight, gamma, ambient, shadowsOn
            fogColor = new Vector4(fogCol, 1f),
            misc = new Vector4((float)_frameTime, 0, 0, shadowTexel), // time, _, _, shadowTexel
            fog = new Vector4(fogStart, fogEnd, 0.5f, 1f),
        };
        System.Buffer.MemoryCopy(&ubo, _cbufferMapped, CbvSize, (long)Marshal.SizeOf<ChunkShared>());

        // Viewport + scissor.
        var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &scissor);

        // Pipeline + root signature + descriptor heap + root CBV. Bind the per-frame
        // combined SRV table [atlas t0, shadow[_frameIndex] t1]; frame f samples its
        // own double-buffered shadow image, never a prior in-flight frame's.
        _commandList.SetPipelineState(_opaquePso);
        _commandList.SetGraphicsRootSignature(_rootSignature);
        var heap = (ID3D12DescriptorHeap*)_shadowSrvHeap.Handle;
        _commandList.SetDescriptorHeaps(1, &heap);
        _commandList.SetGraphicsRootConstantBufferView(0, _cbuffer.GetGPUVirtualAddress());
        GpuDescriptorHandle srvTable = _shadowSrvHeap.GetGPUDescriptorHandleForHeapStart();
        srvTable.Ptr += (ulong)(_frameIndex * 2 * (int)_shadowSrvIncrement);
        _commandList.SetGraphicsRootDescriptorTable(2, srvTable);
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        // Same viewProj uploaded to the GPU this frame, used to cull off-screen
        // sections. Drawing all ~5400 sections every frame hangs the DX12 device
        // (DXGI_ERROR_DEVICE_HUNG, near the GPU TDR limit); culling fixes that.
        var viewProj = ubo.viewProj;
        int totalSections = 0, drawnSections = 0;
        bool anyTranslucent = false;

        // Hoisted out of the (per-section) loops below (CA2014): one scratch buffer,
        // refilled each drawn section, so the stack doesn't grow across thousands of
        // sections. Reused by both the opaque/cutout and translucent passes.
        var origin = stackalloc float[4];

        foreach (var kv in _sectionCache)
        {
            var (cx, sy, cz) = kv.Key;
            if (!_frameHost.sections.TryGetValue(kv.Key, out var sm)) continue;
            totalSections++;

            float ox = (float)(cx * 16 - cam.x);
            float oy = (float)(sm.minY + sy * 16 - cam.y);
            float oz = (float)(cz * 16 - cam.z);

            // View-frustum cull: skip recording sections fully outside the frustum.
            // The GPU mesh cache is left intact (sections may be visible next frame).
            if (!FrustumCull.SectionVisible(new Vector3(ox, oy, oz), viewProj))
                continue;
            drawnSections++;

            origin[0] = ox; origin[1] = oy; origin[2] = oz; origin[3] = 0;
            _commandList.SetGraphicsRoot32BitConstants(1, 4, origin, 0);

            DrawSectionGpu(kv.Value.opaque);
            DrawSectionGpu(kv.Value.cutout);
            if (kv.Value.translucent != null) anyTranslucent = true;
        }

        // Translucent (water/glass/ice) pass: depth-test ON / depth-write OFF,
        // alpha-blended, after all opaque/cutout. Uses _cbufferTrans (distinct CBV
        // with globalAlpha 0.82, no alpha test) so the opaque draws keep their own.
        if (anyTranslucent)
        {
            var tubo = ubo;
            tubo.fog.Z = 0f;
            tubo.fog.W = 0.82f;
            System.Buffer.MemoryCopy(&tubo, _cbufferTransMapped, CbvSize, (long)Marshal.SizeOf<ChunkShared>());

            _commandList.SetPipelineState(_translucentPso);
            _commandList.SetGraphicsRootConstantBufferView(0, _cbufferTrans.GetGPUVirtualAddress());
            foreach (var kv in _sectionCache)
            {
                if (kv.Value.translucent == null) continue;
                var (cx, sy, cz) = kv.Key;
                if (!_frameHost.sections.TryGetValue(kv.Key, out var sm)) continue;
                float ox = (float)(cx * 16 - cam.x);
                float oy = (float)(sm.minY + sy * 16 - cam.y);
                float oz = (float)(cz * 16 - cam.z);
                if (!FrustumCull.SectionVisible(new Vector3(ox, oy, oz), viewProj)) continue;
                origin[0] = ox; origin[1] = oy; origin[2] = oz; origin[3] = 0;
                _commandList.SetGraphicsRoot32BitConstants(1, 4, origin, 0);
                DrawSectionGpu(kv.Value.translucent);
            }
        }

        LogCullStats(drawnSections, totalSections);
    }

    // Throttled (≈1 Hz) drawn-vs-total section logging.
    private double _lastCullLog = double.NegativeInfinity;
    private void LogCullStats(int drawn, int total)
    {
        if (_frameTime - _lastCullLog < 1.0) return;
        _lastCullLog = _frameTime;
        Console.WriteLine($"[D3D12] sections drawn {drawn}/{total} (frustum-culled {total - drawn})");
    }

    private void DrawSectionGpu(SectionGpu? gpu)
    {
        if (gpu == null || gpu.indexCount == 0) return;

        var vbv = new VertexBufferView
        {
            BufferLocation = gpu.vbuf.GetGPUVirtualAddress(),
            SizeInBytes = gpu.vbufSize,
            StrideInBytes = VertexStride,
        };
        _commandList.IASetVertexBuffers(0, 1, &vbv);

        var ibv = new IndexBufferView
        {
            BufferLocation = gpu.ibuf.GetGPUVirtualAddress(),
            SizeInBytes = gpu.ibufSize,
            Format = Format.FormatR32Uint,
        };
        _commandList.IASetIndexBuffer(&ibv);

        _commandList.DrawIndexedInstanced(gpu.indexCount, 1, 0, 0, 0);
    }

    // ═══════════════════════════════════════════════════════════════ Atmosphere

    private void InitSkyRenderer()
    {
        CreateSkyRootSignature();
        CreateSkyPipelines();
        for (int i = 0; i < FrameCount * SkyCbvsPerFrame; i++)
        {
            _skyCbuf[i] = CreateUploadBuffer(SkyCbvSize);
            _skyCbufMapped[i] = MapBuffer(_skyCbuf[i]);
        }
        InitSkyExtras();
        _skyReady = true;
    }

    private void CreateSkyRootSignature()
    {
        // Single root CBV b0 (the SkyU / CelestialU block). No SRV, no sampler.
        var rootParams = stackalloc RootParameter[1];
        rootParams[0] = new RootParameter
        {
            ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.All,
        };
        rootParams[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };

        var rsDesc = new RootSignatureDesc
        {
            NumParameters = 1, PParameters = rootParams,
            NumStaticSamplers = 0, PStaticSamplers = null,
            Flags = RootSignatureFlags.None,
        };
        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0)
            throw new InvalidOperationException($"[D3D12] SerializeRootSignature (sky) failed (0x{hr:X8}): {BlobToString(errBlob)}");
        fixed (ID3D12RootSignature** pp = &_skyRootSig.Handle)
            ThrowIfFailed(
                _device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(),
                    SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp),
                "CreateRootSignature (sky)");
        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    private void CreateSkyPipelines()
    {
        var skyVs = CompileHlsl(SkyShadersHlsl.SkyVertex, "VSMain", "vs_5_0");
        var skyPs = CompileHlsl(SkyShadersHlsl.SkyFragment, "PSMain", "ps_5_0");
        var celVs = CompileHlsl(SkyShadersHlsl.CelestialVertex, "VSMain", "vs_5_0");
        var celPs = CompileHlsl(SkyShadersHlsl.CelestialFragment, "PSMain", "ps_5_0");
        try
        {
            var rasterizer = new RasterizerDesc
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false,
                DepthBias = 0, DepthBiasClamp = 0, SlopeScaledDepthBias = 0,
                DepthClipEnable = false, MultisampleEnable = false, AntialiasedLineEnable = false,
                ForcedSampleCount = 0, ConservativeRaster = ConservativeRasterizationMode.Off,
            };
            // Sky gradient: depth disabled (paints behind world).
            var depthOff = new DepthStencilDesc
            {
                DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always,
                StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff,
            };
            // Celestials: depth TEST (LessEqual) read-only so terrain occludes them.
            var depthCel = new DepthStencilDesc
            {
                DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.LessEqual,
                StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff,
            };
            var blendNone = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendNone.RenderTarget[0] = new RenderTargetBlendDesc
            {
                BlendEnable = false, LogicOpEnable = false,
                SrcBlend = Blend.One, DestBlend = Blend.Zero, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.Zero, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            var blendAlpha = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendAlpha.RenderTarget[0] = new RenderTargetBlendDesc
            {
                BlendEnable = true, LogicOpEnable = false,
                SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };

            var psoDesc = new GraphicsPipelineStateDesc
            {
                PRootSignature = _skyRootSig,
                RasterizerState = rasterizer,
                SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 1,
                DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = null, NumElements = 0 },
                SampleDesc = new SampleDesc(1, 0),
            };
            psoDesc.RTVFormats[0] = SceneRtvFormat;

            psoDesc.VS = new ShaderBytecode { PShaderBytecode = skyVs.GetBufferPointer(), BytecodeLength = skyVs.GetBufferSize() };
            psoDesc.PS = new ShaderBytecode { PShaderBytecode = skyPs.GetBufferPointer(), BytecodeLength = skyPs.GetBufferSize() };
            psoDesc.DepthStencilState = depthOff;
            psoDesc.BlendState = blendNone;
            fixed (ID3D12PipelineState** pp = &_skyPso.Handle)
                ThrowIfFailed(
                    _device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp),
                    "CreateGraphicsPipelineState (sky)");

            psoDesc.VS = new ShaderBytecode { PShaderBytecode = celVs.GetBufferPointer(), BytecodeLength = celVs.GetBufferSize() };
            psoDesc.PS = new ShaderBytecode { PShaderBytecode = celPs.GetBufferPointer(), BytecodeLength = celPs.GetBufferSize() };
            psoDesc.DepthStencilState = depthCel;
            psoDesc.BlendState = blendAlpha;
            fixed (ID3D12PipelineState** pp = &_celestialPso.Handle)
                ThrowIfFailed(
                    _device.CreateGraphicsPipelineState(&psoDesc, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp),
                    "CreateGraphicsPipelineState (celestial)");
        }
        finally
        {
            skyVs.Dispose(); skyPs.Dispose(); celVs.Dispose(); celPs.Dispose();
        }
    }

    // ── stars + clouds: resources, root sig, PSOs ────────────────────────────
    private void InitSkyExtras()
    {
        CreateStarsRootSignature();
        CreateCloudRootSignature();
        CreateSkyExtraPipelines();
        BuildStarVertexBuffer();
        BuildCloudTexture();
        for (int i = 0; i < FrameCount; i++)
        {
            _starsCbuf[i] = CreateUploadBuffer(StarsCbvSize); _starsCbufMapped[i] = MapBuffer(_starsCbuf[i]);
            _cloudCbuf[i] = CreateUploadBuffer(CloudCbvSize); _cloudCbufMapped[i] = MapBuffer(_cloudCbuf[i]);
        }
        _skyExtraReady = true;
    }

    private void CreateStarsRootSignature()
    {
        // Single root CBV b0, but the stars pipeline uses an input layout, so the
        // root signature must allow the IA (the sky root sig forbids it).
        var rootParams = stackalloc RootParameter[1];
        rootParams[0] = new RootParameter { ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.All };
        rootParams[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };
        var rsDesc = new RootSignatureDesc
        {
            NumParameters = 1, PParameters = rootParams, NumStaticSamplers = 0, PStaticSamplers = null,
            Flags = RootSignatureFlags.AllowInputAssemblerInputLayout,
        };
        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0)
            throw new InvalidOperationException($"[D3D12] SerializeRootSignature (stars) failed (0x{hr:X8}): {BlobToString(errBlob)}");
        fixed (ID3D12RootSignature** pp = &_starsRootSig.Handle)
            ThrowIfFailed(_device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(),
                SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp), "CreateRootSignature (stars)");
        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    private void CreateCloudRootSignature()
    {
        // [0] root CBV b0 (CloudU)  [1] SRV table t0 (cloud tex) + static sampler s0
        var ranges = stackalloc DescriptorRange[1];
        ranges[0] = new DescriptorRange
        {
            RangeType = DescriptorRangeType.Srv, NumDescriptors = 1, BaseShaderRegister = 0,
            RegisterSpace = 0, OffsetInDescriptorsFromTableStart = 0,
        };
        var rootParams = stackalloc RootParameter[2];
        rootParams[0] = new RootParameter { ParameterType = RootParameterType.TypeCbv, ShaderVisibility = ShaderVisibility.All };
        rootParams[0].Anonymous.Descriptor = new RootDescriptor { ShaderRegister = 0, RegisterSpace = 0 };
        rootParams[1] = new RootParameter { ParameterType = RootParameterType.TypeDescriptorTable, ShaderVisibility = ShaderVisibility.Pixel };
        rootParams[1].Anonymous.DescriptorTable = new RootDescriptorTable { NumDescriptorRanges = 1, PDescriptorRanges = ranges };

        var sampler = new StaticSamplerDesc
        {
            Filter = Filter.MinMagMipLinear,
            AddressU = TextureAddressMode.Wrap, AddressV = TextureAddressMode.Wrap, AddressW = TextureAddressMode.Wrap,
            MipLODBias = 0, MaxAnisotropy = 1, ComparisonFunc = ComparisonFunc.Always,
            BorderColor = StaticBorderColor.OpaqueBlack, MinLOD = 0, MaxLOD = 0,
            ShaderRegister = 0, RegisterSpace = 0, ShaderVisibility = ShaderVisibility.Pixel,
        };
        var rsDesc = new RootSignatureDesc
        {
            NumParameters = 2, PParameters = rootParams, NumStaticSamplers = 1, PStaticSamplers = &sampler,
            Flags = RootSignatureFlags.None,
        };
        var blob = default(ComPtr<ID3D10Blob>);
        var errBlob = default(ComPtr<ID3D10Blob>);
        int hr = _d3d12!.SerializeRootSignature(&rsDesc, D3DRootSignatureVersion.Version1, blob.GetAddressOf(), errBlob.GetAddressOf());
        if (hr < 0)
            throw new InvalidOperationException($"[D3D12] SerializeRootSignature (cloud) failed (0x{hr:X8}): {BlobToString(errBlob)}");
        fixed (ID3D12RootSignature** pp = &_cloudRootSig.Handle)
            ThrowIfFailed(_device.CreateRootSignature(0, blob.GetBufferPointer(), blob.GetBufferSize(),
                SilkMarshal.GuidPtrOf<ID3D12RootSignature>(), (void**)pp), "CreateRootSignature (cloud)");
        blob.Dispose();
        if (errBlob.Handle != null) errBlob.Dispose();
    }

    private void CreateSkyExtraPipelines()
    {
        var starVs = CompileHlsl(SkyShadersHlsl.StarsVertex, "VSMain", "vs_5_0");
        var starPs = CompileHlsl(SkyShadersHlsl.StarsFragment, "PSMain", "ps_5_0");
        var cloudVs = CompileHlsl(SkyShadersHlsl.CloudVertex, "VSMain", "vs_5_0");
        var cloudPs = CompileHlsl(SkyShadersHlsl.CloudFragment, "PSMain", "ps_5_0");
        var semPos = (byte*)SilkMarshal.StringToPtr("POSITION");
        var semTex = (byte*)SilkMarshal.StringToPtr("TEXCOORD");
        try
        {
            var raster = new RasterizerDesc
            {
                FillMode = FillMode.Solid, CullMode = CullMode.None, FrontCounterClockwise = false,
                DepthBias = 0, DepthBiasClamp = 0, SlopeScaledDepthBias = 0, DepthClipEnable = false,
                MultisampleEnable = false, AntialiasedLineEnable = false, ForcedSampleCount = 0,
                ConservativeRaster = ConservativeRasterizationMode.Off,
            };
            // stars: depth disabled (far), additive blend
            var depthOff = new DepthStencilDesc
            {
                DepthEnable = false, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.Always,
                StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff,
            };
            // clouds: depth test LessEqual read-only (terrain occludes), alpha blend
            var depthRead = new DepthStencilDesc
            {
                DepthEnable = true, DepthWriteMask = DepthWriteMask.Zero, DepthFunc = ComparisonFunc.LessEqual,
                StencilEnable = false, StencilReadMask = 0xff, StencilWriteMask = 0xff,
            };
            var blendAdd = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendAdd.RenderTarget[0] = new RenderTargetBlendDesc
            {
                BlendEnable = true, LogicOpEnable = false,
                SrcBlend = Blend.SrcAlpha, DestBlend = Blend.One, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.One, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };
            var blendAlpha = new BlendDesc { AlphaToCoverageEnable = false, IndependentBlendEnable = false };
            blendAlpha.RenderTarget[0] = new RenderTargetBlendDesc
            {
                BlendEnable = true, LogicOpEnable = false,
                SrcBlend = Blend.SrcAlpha, DestBlend = Blend.InvSrcAlpha, BlendOp = BlendOp.Add,
                SrcBlendAlpha = Blend.One, DestBlendAlpha = Blend.InvSrcAlpha, BlendOpAlpha = BlendOp.Add,
                LogicOp = LogicOp.Noop, RenderTargetWriteMask = (byte)ColorWriteEnable.All,
            };

            // stars input layout: POSITION f3 @0, TEXCOORD0 f1 @12 (mag), TEXCOORD1 f2 @16 (corner)
            var elems = stackalloc InputElementDesc[3];
            elems[0] = new InputElementDesc { SemanticName = semPos, SemanticIndex = 0, Format = Format.FormatR32G32B32Float, InputSlot = 0, AlignedByteOffset = 0, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[1] = new InputElementDesc { SemanticName = semTex, SemanticIndex = 0, Format = Format.FormatR32Float, InputSlot = 0, AlignedByteOffset = 12, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };
            elems[2] = new InputElementDesc { SemanticName = semTex, SemanticIndex = 1, Format = Format.FormatR32G32Float, InputSlot = 0, AlignedByteOffset = 16, InputSlotClass = InputClassification.PerVertexData, InstanceDataStepRate = 0 };

            var psoStars = new GraphicsPipelineStateDesc
            {
                PRootSignature = _starsRootSig, RasterizerState = raster, SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, NumRenderTargets = 1, DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = elems, NumElements = 3 },
                SampleDesc = new SampleDesc(1, 0),
                DepthStencilState = depthOff, BlendState = blendAdd,
                VS = new ShaderBytecode { PShaderBytecode = starVs.GetBufferPointer(), BytecodeLength = starVs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = starPs.GetBufferPointer(), BytecodeLength = starPs.GetBufferSize() },
            };
            psoStars.RTVFormats[0] = SceneRtvFormat;
            fixed (ID3D12PipelineState** pp = &_starsPso.Handle)
                ThrowIfFailed(_device.CreateGraphicsPipelineState(&psoStars, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp), "CreateGraphicsPipelineState (stars)");

            var psoCloud = new GraphicsPipelineStateDesc
            {
                PRootSignature = _cloudRootSig, RasterizerState = raster, SampleMask = uint.MaxValue,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle, NumRenderTargets = 1, DSVFormat = DepthFormat,
                InputLayout = new InputLayoutDesc { PInputElementDescs = null, NumElements = 0 },
                SampleDesc = new SampleDesc(1, 0),
                DepthStencilState = depthRead, BlendState = blendAlpha,
                VS = new ShaderBytecode { PShaderBytecode = cloudVs.GetBufferPointer(), BytecodeLength = cloudVs.GetBufferSize() },
                PS = new ShaderBytecode { PShaderBytecode = cloudPs.GetBufferPointer(), BytecodeLength = cloudPs.GetBufferSize() },
            };
            psoCloud.RTVFormats[0] = SceneRtvFormat;
            fixed (ID3D12PipelineState** pp = &_cloudPso.Handle)
                ThrowIfFailed(_device.CreateGraphicsPipelineState(&psoCloud, SilkMarshal.GuidPtrOf<ID3D12PipelineState>(), (void**)pp), "CreateGraphicsPipelineState (cloud)");
        }
        finally
        {
            SilkMarshal.Free((nint)semPos); SilkMarshal.Free((nint)semTex);
            starVs.Dispose(); starPs.Dispose(); cloudVs.Dispose(); cloudPs.Dispose();
        }
    }

    private void BuildStarVertexBuffer()
    {
        var stars = SkyAssets.BuildStars();   // float4 per star (x,y,z,mag)
        int n = SkyAssets.StarCount;
        // expand each star to 6 verts (two triangles); each vert: pos3 mag1 corner2 (24 bytes)
        var corners = new (float x, float y)[] { (-1, -1), (1, -1), (1, 1), (-1, -1), (1, 1), (-1, 1) };
        var verts = new float[n * 6 * 6];
        int o = 0;
        for (int i = 0; i < n; i++)
        {
            float px = stars[i * 4], py = stars[i * 4 + 1], pz = stars[i * 4 + 2], mag = stars[i * 4 + 3];
            foreach (var (cx, cy) in corners)
            {
                verts[o++] = px; verts[o++] = py; verts[o++] = pz; verts[o++] = mag; verts[o++] = cx; verts[o++] = cy;
            }
        }
        _starsVertexCount = (uint)(n * 6);
        ulong size = (ulong)verts.Length * sizeof(float);
        _starsVbuf = CreateUploadBuffer(size);
        void* mapped = MapBuffer(_starsVbuf);
        verts.AsSpan().CopyTo(new Span<float>(mapped, verts.Length));
        _starsVbuf.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
    }

    private void BuildCloudTexture()
    {
        int size = SkyAssets.CloudSize;
        var px = SkyAssets.BuildCloudTexture();
        var heapProps = new HeapProperties
        {
            Type = HeapType.Default, CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown, CreationNodeMask = 1, VisibleNodeMask = 1,
        };
        var texDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D, Alignment = 0, Width = (ulong)size, Height = (uint)size,
            DepthOrArraySize = 1, MipLevels = 1, Format = Format.FormatR8G8B8A8Unorm,
            SampleDesc = new SampleDesc(1, 0), Layout = TextureLayout.LayoutUnknown, Flags = ResourceFlags.None,
        };
        fixed (ID3D12Resource** pp = &_cloudTexture.Handle)
            ThrowIfFailed(_device.CreateCommittedResource(&heapProps, HeapFlags.None, &texDesc,
                ResourceStates.CopyDest, (ClearValue*)null, SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)pp),
                "CreateCommittedResource (cloud)");

        PlacedSubresourceFootprint fp; uint numRows; ulong rowSize, totalBytes;
        _device.GetCopyableFootprints(&texDesc, 0, 1, 0, &fp, &numRows, &rowSize, &totalBytes);
        var upload = CreateUploadBuffer(totalBytes);
        byte* mapped = (byte*)MapBuffer(upload);
        int srcRowBytes = size * 4;
        for (int y = 0; y < size; y++)
            px.AsSpan(y * srcRowBytes, srcRowBytes).CopyTo(new Span<byte>(mapped + (long)y * fp.Footprint.RowPitch, srcRowBytes));
        upload.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        ThrowIfFailed(_uploadAllocator.Reset(), "uploadAllocator.Reset (cloud)");
        ThrowIfFailed(_uploadList.Reset(_uploadAllocator, (ID3D12PipelineState*)null), "uploadList.Reset (cloud)");
        var dstLoc = new TextureCopyLocation { PResource = _cloudTexture, Type = TextureCopyType.SubresourceIndex };
        dstLoc.Anonymous.SubresourceIndex = 0;
        var srcLoc = new TextureCopyLocation { PResource = upload, Type = TextureCopyType.PlacedFootprint };
        srcLoc.Anonymous.PlacedFootprint = fp;
        _uploadList.CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, (Box*)null);
        var barrier = new ResourceBarrier { Type = ResourceBarrierType.Transition, Flags = ResourceBarrierFlags.None };
        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = _cloudTexture, Subresource = unchecked((uint)0xffffffff),
            StateBefore = ResourceStates.CopyDest, StateAfter = ResourceStates.PixelShaderResource,
        };
        _uploadList.ResourceBarrier(1, &barrier);
        ThrowIfFailed(_uploadList.Close(), "uploadList.Close (cloud)");
        ID3D12CommandList* uploadCmd = (ID3D12CommandList*)_uploadList.Handle;
        _queue.ExecuteCommandLists(1, &uploadCmd);
        WaitForGpu();
        upload.Dispose();

        var srvHeapDesc = new DescriptorHeapDesc
        {
            NumDescriptors = 1, Type = DescriptorHeapType.CbvSrvUav, Flags = DescriptorHeapFlags.ShaderVisible,
        };
        fixed (ID3D12DescriptorHeap** ppHeap = &_cloudSrvHeap.Handle)
            ThrowIfFailed(_device.CreateDescriptorHeap(&srvHeapDesc, SilkMarshal.GuidPtrOf<ID3D12DescriptorHeap>(), (void**)ppHeap), "CreateDescriptorHeap (cloud SRV)");
        var srvDesc = new ShaderResourceViewDesc
        {
            Format = Format.FormatR8G8B8A8Unorm, ViewDimension = SrvDimension.Texture2D, Shader4ComponentMapping = 0x1688,
        };
        srvDesc.Anonymous.Texture2D = new Tex2DSrv { MostDetailedMip = 0, MipLevels = 1, PlaneSlice = 0, ResourceMinLODClamp = 0f };
        _device.CreateShaderResourceView(_cloudTexture, &srvDesc, _cloudSrvHeap.GetCPUDescriptorHandleForHeapStart());
    }

    /// Record the sky gradient + sun/moon billboards (called first, from RenderSky).
    private void RecordSky()
    {
        var cam = _frameCam;
        var viewProj = BuildViewProj(cam);
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
            invViewProj = Matrix4x4.Identity;

        var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &scissor);

        _commandList.SetGraphicsRootSignature(_skyRootSig);
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);

        int baseCbv = (int)_frameIndex * SkyCbvsPerFrame;

        // ── gradient (full-screen triangle, no VB) ──
        var skyU = new SkyUniform
        {
            invViewProj = invViewProj,
            zenith = new Vector4(_frameSky.Zenith, 0),
            horizon = new Vector4(_frameSky.Horizon, 0),
            horizonSun = _frameSky.HorizonSun,
            sunDir = new Vector4(_frameSky.SunDir, _frameSky.VoidSky ? 1f : 0f),
        };
        System.Buffer.MemoryCopy(&skyU, _skyCbufMapped[baseCbv], SkyCbvSize, (long)Marshal.SizeOf<SkyUniform>());
        _commandList.SetPipelineState(_skyPso);
        _commandList.SetGraphicsRootConstantBufferView(0, _skyCbuf[baseCbv].GetGPUVirtualAddress());
        _commandList.DrawInstanced(3, 1, 0, 0);

        // ── stars (night) — behind celestials, far plane, additive ──
        if (_skyExtraReady && !_frameSky.VoidSky && !cam.underwater && !cam.underLava && _frameSky.StarAlpha > 0.01f)
        {
            float aspect = _height > 0 ? (float)_width / _height : 1f;
            var su = new StarsUniform { viewProj = viewProj, paramsv = new Vector4((float)_frameTime, _frameSky.StarAlpha, aspect, 0) };
            System.Buffer.MemoryCopy(&su, _starsCbufMapped[_frameIndex], StarsCbvSize, (long)Marshal.SizeOf<StarsUniform>());
            _commandList.SetGraphicsRootSignature(_starsRootSig);
            _commandList.SetPipelineState(_starsPso);
            _commandList.SetGraphicsRootConstantBufferView(0, _starsCbuf[_frameIndex].GetGPUVirtualAddress());
            var svbv = new VertexBufferView
            {
                BufferLocation = _starsVbuf.GetGPUVirtualAddress(), SizeInBytes = _starsVertexCount * 24, StrideInBytes = 24,
            };
            _commandList.IASetVertexBuffers(0, 1, &svbv);
            _commandList.DrawInstanced(_starsVertexCount, 1, 0, 0);
        }

        // ── sun + moon billboards ──
        if (!_frameSky.VoidSky && !cam.underwater && !cam.underLava)
        {
            // stars may have switched to its own root sig; restore the sky root sig
            _commandList.SetGraphicsRootSignature(_skyRootSig);
            _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
            _commandList.SetPipelineState(_celestialPso);

            void DrawCelestial(int slot, Vector3 cdir, float size, float moonPhase)
            {
                int idx = baseCbv + slot;
                var up0 = new Vector3(0, 0, 1);
                var right = Vector3.Cross(cdir, up0);
                float rl = right.Length();
                right = rl < 1e-6f ? new Vector3(1, 0, 0) : right / rl;
                var up2 = Vector3.Cross(right, cdir);
                var cu = new CelestialUniform
                {
                    viewProj = viewProj,
                    center = new Vector4(cdir * 500f, size),
                    right = new Vector4(right, 0),
                    up = new Vector4(up2, moonPhase),
                };
                System.Buffer.MemoryCopy(&cu, _skyCbufMapped[idx], SkyCbvSize, (long)Marshal.SizeOf<CelestialUniform>());
                _commandList.SetGraphicsRootConstantBufferView(0, _skyCbuf[idx].GetGPUVirtualAddress());
                _commandList.DrawInstanced(6, 1, 0, 0);
            }

            DrawCelestial(1, _frameSky.SunDir, 55f, -1f);
            float phase = (float)(((_frameSky.MoonPhase / 8.0) + 0.5) % 1.0);
            DrawCelestial(2, -_frameSky.SunDir, 38f, phase);
        }

        // The world pass re-binds the world root signature + heaps afterwards, so
        // no cleanup is needed here; the depth the world writes occludes celestials.
    }

    /// Clouds: drawn after the world (depth read-only) in the overworld, daytime.
    private void RecordClouds()
    {
        if (!_skyExtraReady || !_haveSky || _frameSky.VoidSky) return;
        var cam = _frameCam;
        if (cam.underwater || cam.underLava) return;
        var viewProj = BuildViewProj(cam);

        float cy = (float)(192.33 - cam.y);
        double scroll = _frameTime * 0.0006;
        float fogEnd = _frameSky.FogEnd;
        var cu = new CloudUniform
        {
            viewProj = viewProj,
            offset = new Vector4(0, cy, 0, 2048f),
            scroll = new Vector4(
                (float)((cam.x / 4096 + scroll) % 1.0),
                (float)((cam.z / 4096) % 1.0),
                0.75f + _frameSky.DayLight * 0.25f,
                fogEnd * 2.5f),
        };
        System.Buffer.MemoryCopy(&cu, _cloudCbufMapped[_frameIndex], CloudCbvSize, (long)Marshal.SizeOf<CloudUniform>());

        var viewport = new Viewport(0, 0, _width, _height, 0f, 1f);
        _commandList.RSSetViewports(1, &viewport);
        var scissor = new Box2D<int>(0, 0, _width, _height);
        _commandList.RSSetScissorRects(1, &scissor);

        _commandList.SetGraphicsRootSignature(_cloudRootSig);
        _commandList.SetPipelineState(_cloudPso);
        var heap = _cloudSrvHeap.Handle;
        _commandList.SetDescriptorHeaps(1, &heap);
        _commandList.SetGraphicsRootConstantBufferView(0, _cloudCbuf[_frameIndex].GetGPUVirtualAddress());
        _commandList.SetGraphicsRootDescriptorTable(1, _cloudSrvHeap.GetGPUDescriptorHandleForHeapStart());
        _commandList.IASetPrimitiveTopology(D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist);
        _commandList.DrawInstanced(6, 1, 0, 0);
    }

    // ── screenshot: copy the rendered backbuffer to a readback buffer ────────
    private void CaptureScreenshot()
    {
        uint w = (uint)_width, h = (uint)_height;

        // Footprint of the backbuffer subresource so the readback row pitch is
        // 256-byte aligned.
        var rtDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Texture2D,
            Alignment = 0,
            Width = w,
            Height = h,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = RtvFormat,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutUnknown,
            Flags = ResourceFlags.None,
        };
        PlacedSubresourceFootprint footprint;
        uint numRows;
        ulong rowSize;
        ulong totalBytes;
        _device.GetCopyableFootprints(&rtDesc, 0, 1, 0, &footprint, &numRows, &rowSize, &totalBytes);

        // Readback heap buffer.
        var heapProps = new HeapProperties
        {
            Type = HeapType.Readback,
            CPUPageProperty = CpuPageProperty.Unknown,
            MemoryPoolPreference = MemoryPool.Unknown,
            CreationNodeMask = 1,
            VisibleNodeMask = 1,
        };
        var bufDesc = new ResourceDesc
        {
            Dimension = ResourceDimension.Buffer,
            Alignment = 0,
            Width = totalBytes,
            Height = 1,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = Format.FormatUnknown,
            SampleDesc = new SampleDesc(1, 0),
            Layout = TextureLayout.LayoutRowMajor,
            Flags = ResourceFlags.None,
        };
        if (_readbackBuffer.Handle != null) { _readbackBuffer.Dispose(); _readbackBuffer = default; }
        fixed (ID3D12Resource** pp = &_readbackBuffer.Handle)
            ThrowIfFailed(
                _device.CreateCommittedResource(
                    &heapProps, HeapFlags.None, &bufDesc,
                    ResourceStates.CopyDest, (ClearValue*)null,
                    SilkMarshal.GuidPtrOf<ID3D12Resource>(), (void**)pp),
                "CreateCommittedResource (readback)");

        // RENDER_TARGET -> COPY_SOURCE, copy, -> RENDER_TARGET (restored for the
        // PRESENT transition that follows in EndFrame).
        ResourceBarrierTransition(_renderTargets[_frameIndex], ResourceStates.RenderTarget, ResourceStates.CopySource);

        var dstLoc = new TextureCopyLocation
        {
            PResource = _readbackBuffer,
            Type = TextureCopyType.PlacedFootprint,
        };
        dstLoc.Anonymous.PlacedFootprint = footprint;
        var srcLoc = new TextureCopyLocation
        {
            PResource = _renderTargets[_frameIndex],
            Type = TextureCopyType.SubresourceIndex,
        };
        srcLoc.Anonymous.SubresourceIndex = 0;
        _commandList.CopyTextureRegion(&dstLoc, 0, 0, 0, &srcLoc, (Box*)null);

        ResourceBarrierTransition(_renderTargets[_frameIndex], ResourceStates.CopySource, ResourceStates.RenderTarget);

        _pendingReadbackPath = _screenshotPath;
        _screenshotPath = null;
        _readbackRowPitch = footprint.Footprint.RowPitch;
        _readbackW = w;
        _readbackH = h;
    }

    private void EncodePendingScreenshot()
    {
        if (_pendingReadbackPath == null || _readbackBuffer.Handle == null) return;
        string path = _pendingReadbackPath;
        _pendingReadbackPath = null;

        uint w = _readbackW, h = _readbackH, pitch = _readbackRowPitch;
        void* mapped;
        var readRange = new Silk.NET.Direct3D12.Range { Begin = (nuint)0, End = (nuint)(pitch * h) };
        if (_readbackBuffer.Map(0, &readRange, &mapped) < 0)
            throw new InvalidOperationException("[D3D12] readback Map failed.");

        var rgba = new byte[(int)(w * h * 4)];
        byte* src = (byte*)mapped;
        // RtvFormat is R8G8B8A8_UNORM — already RGBA, just de-pad rows.
        for (int y = 0; y < (int)h; y++)
        {
            byte* row = src + (long)y * pitch;
            for (int x = 0; x < (int)w; x++)
            {
                int o = (y * (int)w + x) * 4;
                rgba[o + 0] = row[x * 4 + 0];
                rgba[o + 1] = row[x * 4 + 1];
                rgba[o + 2] = row[x * 4 + 2];
                rgba[o + 3] = row[x * 4 + 3];
            }
        }
        _readbackBuffer.Unmap(0, (Silk.NET.Direct3D12.Range*)null);

        using (var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            rgba, (int)w, (int)h))
        using (var fs = System.IO.File.Create(path))
        {
            img.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        }

        _readbackBuffer.Dispose();
        _readbackBuffer = default;
        _screenshotComplete = true;
        Console.WriteLine($"[D3D12] screenshot written: {path} ({w}x{h})");
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try { WaitForGpu(); } catch { }
        }

        // World renderer resources. The GPU is drained above (WaitForGpu), so it's
        // safe to release immediately. Flush ALL deferred-free buckets first so
        // nothing parked in the ring leaks, then the live section cache (all three
        // layers — opaque, cutout, translucent).
        for (int s = 0; s < DeferFreeSlots; s++)
            DrainPendingFrees(s);
        foreach (var l in _sectionCache.Values)
        {
            DestroySectionGpu(l.opaque);
            DestroySectionGpu(l.cutout);
            DestroySectionGpu(l.translucent);
        }
        _sectionCache.Clear();

        if (_cbufferMapped != null && _cbuffer.Handle != null)
        {
            _cbuffer.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
            _cbufferMapped = null;
        }
        DisposeCom(ref _cbuffer);
        if (_animUploadMapped != null && _animUpload.Handle != null)
        {
            _animUpload.Unmap(0, (Silk.NET.Direct3D12.Range*)null);
            _animUploadMapped = null;
        }
        DisposeCom(ref _animUpload);
        _animator = null;
        DisposeCom(ref _starsPso);
        DisposeCom(ref _cloudPso);
        DisposeCom(ref _starsRootSig);
        DisposeCom(ref _cloudRootSig);
        DisposeCom(ref _starsVbuf);
        DisposeCom(ref _cloudTexture);
        DisposeCom(ref _cloudSrvHeap);
        for (int i = 0; i < FrameCount; i++)
        {
            DisposeCom(ref _starsCbuf[i]);
            DisposeCom(ref _cloudCbuf[i]);
        }
        DisposeCom(ref _atlasTexture);
        DisposeCom(ref _srvHeap);
        DisposeCom(ref _depthBuffer);
        DisposeCom(ref _dsvHeap);
        DisposeCom(ref _opaquePso);
        DisposeCom(ref _translucentPso);
        DisposeCom(ref _rootSignature);
        DisposeCom(ref _readbackBuffer);

        // Sun shadow map (double-buffered).
        for (int f = 0; f < FrameCount; f++)
        {
            if (_shadowCbufMapped[f] != null && _shadowCbuf[f].Handle != null)
            {
                _shadowCbuf[f].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
                _shadowCbufMapped[f] = null;
            }
            DisposeCom(ref _shadowCbuf[f]);
            DisposeCom(ref _shadowTex[f]);
        }
        DisposeCom(ref _shadowDsvHeap);
        DisposeCom(ref _shadowSrvHeap);
        DisposeCom(ref _shadowRootSig);
        DisposeCom(ref _shadowPso);

        // UI / HUD resources.
        for (uint i = 0; i < FrameCount; i++)
        {
            if (_uiVbuf[i].Handle != null) { _uiVbuf[i].Dispose(); _uiVbuf[i] = default; }
            _uiVbufSize[i] = 0;
        }
        DisposeCom(ref _uiPso);
        DisposeCom(ref _uiRootSignature);
        DisposeCom(ref _iconTexture);
        DisposeCom(ref _iconSrvHeap);
        DisposeCom(ref _iconList);
        DisposeCom(ref _iconAllocator);
        DisposeCom(ref _iconFence);
        if (_iconFenceEvent != nint.Zero) { SilkMarshal.CloseWindowsHandle(_iconFenceEvent); _iconFenceEvent = nint.Zero; }

        // Entity (3D) resources.
        foreach (var g in _entityModels.Values)
        {
            if (g.vbuf.Handle != null) { g.vbuf.Dispose(); g.vbuf = default; }
            if (g.skin.Handle != null) { g.skin.Dispose(); g.skin = default; }
        }
        _entityModels.Clear();
        for (uint i = 0; i < FrameCount; i++)
        {
            if (_entityCbuf[i].Handle != null)
            {
                _entityCbuf[i].Unmap(0, (Silk.NET.Direct3D12.Range*)null);
                _entityCbuf[i].Dispose();
                _entityCbuf[i] = default;
            }
            _entityCbufSize[i] = 0;
        }
        DisposeCom(ref _entityPso);
        DisposeCom(ref _entityRootSig);
        DisposeCom(ref _entitySrvHeap);
        DisposeCom(ref _entityUploadList);
        DisposeCom(ref _entityUploadAllocator);
        DisposeCom(ref _entityUploadFence);
        if (_entityUploadFenceEvent != nint.Zero) { SilkMarshal.CloseWindowsHandle(_entityUploadFenceEvent); _entityUploadFenceEvent = nint.Zero; }

        // particle / sprite / break-overlay resources
        for (uint i = 0; i < FrameCount; i++)
        {
            if (_particleInst[i].Handle != null) { _particleInst[i].Dispose(); _particleInst[i] = default; }
            if (_particleCbuf[i].Handle != null) { _particleCbuf[i].Dispose(); _particleCbuf[i] = default; }
            if (_spriteCbuf[i].Handle != null) { _spriteCbuf[i].Dispose(); _spriteCbuf[i] = default; }
            if (_overlayVbuf[i].Handle != null) { _overlayVbuf[i].Dispose(); _overlayVbuf[i] = default; }
            if (_overlayIbuf[i].Handle != null) { _overlayIbuf[i].Dispose(); _overlayIbuf[i] = default; }
        }
        DisposeCom(ref _particleQuad);
        DisposeCom(ref _particlePso);
        DisposeCom(ref _particleRootSig);
        DisposeCom(ref _spritePso);
        DisposeCom(ref _spriteRootSig);
        DisposeCom(ref _overlayCbuf);

        // Offscreen-HDR / post resources.
        for (uint i = 0; i < FrameCount; i++)
        {
            DisposeCom(ref _hdrColor[i]);
            DisposeCom(ref _bloomA[i]); DisposeCom(ref _bloomB[i]);
            DisposeCom(ref _ultraA[i]); DisposeCom(ref _ultraB[i]);
            DisposeCom(ref _compositeCbuf[i]); DisposeCom(ref _ultraCbuf[i]);
            for (int d = 0; d < 4; d++) DisposeCom(ref _blurCbuf[i * 4 + d]);
        }
        DisposeCom(ref _hdrRtvHeap); DisposeCom(ref _postSrvHeap);
        DisposeCom(ref _postRootSig); DisposeCom(ref _ultraRootSig);
        DisposeCom(ref _bloomExtractPso); DisposeCom(ref _blurPso);
        DisposeCom(ref _ultraPso); DisposeCom(ref _ultraBlurPso); DisposeCom(ref _compositePso);

        _commandList.Dispose();
        _commandList = default;
        for (uint i = 0; i < FrameCount; i++)
            DisposeCom(ref _commandAllocators[i]);
        DisposeCom(ref _uploadList);
        DisposeCom(ref _uploadAllocator);

        for (uint i = 0; i < FrameCount; i++)
        {
            _renderTargets[i].Dispose();
            _renderTargets[i] = default;
        }

        _rtvHeap.Dispose();
        _rtvHeap = default;
        _swapChain.Dispose();
        _swapChain = default;
        _queue.Dispose();
        _queue = default;
        _device.Dispose();
        _device = default;
        _fence.Dispose();
        _fence = default;
        _factory.Dispose();
        _factory = default;

        if (_fenceEvent != nint.Zero)
        {
            SilkMarshal.CloseWindowsHandle(_fenceEvent);
            _fenceEvent = nint.Zero;
        }

        _compiler?.Dispose();
        _compiler = null;
        _d3d12?.Dispose();
        _d3d12 = null;
        _dxgi?.Dispose();
        _dxgi = null;

        _initialized = false;
    }

    private static void DisposeCom<T>(ref ComPtr<T> com) where T : unmanaged, IComVtbl<T>
    {
        if (com.Handle != null) { com.Dispose(); com = default; }
    }

    // --- Helpers ---------------------------------------------------------

    private void ResourceBarrierTransition(ComPtr<ID3D12Resource> resource, ResourceStates before, ResourceStates after)
    {
        var barrier = new ResourceBarrier
        {
            Type = ResourceBarrierType.Transition,
            Flags = ResourceBarrierFlags.None,
        };
        barrier.Anonymous.Transition = new ResourceTransitionBarrier
        {
            PResource = resource,
            Subresource = unchecked((uint)0xffffffff),
            StateBefore = before,
            StateAfter = after,
        };
        _commandList.ResourceBarrier(1, &barrier);
    }

    private void MoveToNextFrame()
    {
        ulong currentFenceValue = _fenceValue;
        ThrowIfFailed(_queue.Signal(_fence, currentFenceValue), "Queue.Signal (MoveToNextFrame)");
        _fenceValues[_frameIndex] = currentFenceValue;
        _fenceValue++;

        _frameIndex = _swapChain.GetCurrentBackBufferIndex();

        if (_fence.GetCompletedValue() < _fenceValues[_frameIndex])
        {
            ThrowIfFailed(
                _fence.SetEventOnCompletion(_fenceValues[_frameIndex], (void*)_fenceEvent),
                "Fence.SetEventOnCompletion (MoveToNextFrame)");
            SilkMarshal.WaitWindowsObjects(_fenceEvent, 0xFFFFFFFF, false);
        }
    }

    private void WaitForGpu()
    {
        ulong waitValue = _fenceValue;
        ThrowIfFailed(_queue.Signal(_fence, waitValue), "Queue.Signal (WaitForGpu)");
        _fenceValue++;

        if (_fence.GetCompletedValue() < waitValue)
        {
            ThrowIfFailed(
                _fence.SetEventOnCompletion(waitValue, (void*)_fenceEvent),
                "Fence.SetEventOnCompletion (WaitForGpu)");
            SilkMarshal.WaitWindowsObjects(_fenceEvent, 0xFFFFFFFF, false);
        }

        _fenceValues[_frameIndex] = waitValue;
    }

    private bool TryEnableDebugLayer()
    {
        try
        {
            using var debug = default(ComPtr<ID3D12Debug>);
            int hr = _d3d12!.GetDebugInterface(SilkMarshal.GuidPtrOf<ID3D12Debug>(), (void**)debug.GetAddressOf());
            if (hr >= 0 && debug.Handle != null)
            {
                debug.EnableDebugLayer();
                return true;
            }
        }
        catch { }
        return false;
    }

    private void ThrowIfFailed(int hr, string what)
    {
        if (hr < 0)
        {
            string extra = "";
            if ((uint)hr == 0x887A0005u && _device.Handle != null)
            {
                extra = $"  device-removed reason: 0x{_device.GetDeviceRemovedReason():X8}";
                DrainDebugMessages();
            }
            throw new InvalidOperationException($"[D3D12] {what} failed (HRESULT 0x{hr:X8}).{extra}");
        }
    }

    private void DrainDebugMessages()
    {
        try
        {
            using var iq = default(ComPtr<ID3D12InfoQueue>);
            if (_device.QueryInterface(SilkMarshal.GuidPtrOf<ID3D12InfoQueue>(), (void**)iq.GetAddressOf()) < 0 || iq.Handle == null)
                return;
            ulong n = iq.GetNumStoredMessages();
            for (ulong i = 0; i < n; i++)
            {
                nuint len = 0;
                iq.GetMessageA(i, (Message*)null, ref len);
                if (len == 0) continue;
                var buf = new byte[(int)len];
                fixed (byte* p = buf)
                {
                    if (iq.GetMessageA(i, (Message*)p, ref len) < 0) continue;
                    var msg = (Message*)p;
                    string text = msg->PDescription != null
                        ? System.Text.Encoding.ASCII.GetString((byte*)msg->PDescription, (int)msg->DescriptionByteLength).TrimEnd('\0')
                        : "(no text)";
                    Console.WriteLine($"[D3D12-DBG] {msg->Severity} id={msg->ID}: {text}");
                }
            }
        }
        catch (Exception e) { Console.WriteLine($"[D3D12-DBG] drain failed: {e.Message}"); }
    }
}
