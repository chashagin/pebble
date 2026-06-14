// Vulkan rendering backend for the Pebble Windows app (Silk.NET 2.23.0).
//
// This is a minimal-but-correct "clear screen and present" pipeline: it stands
// up a VkInstance, surface, device, swapchain, render pass, framebuffers,
// command buffers and per-frame sync, then each frame acquires an image, records
// a render pass that clears the backbuffer, submits, and presents. It grows into
// the full WorldRenderer port later; for now it gives a runnable vertical slice
// behind IGpuBackend.
//
// ── BeginFrame / ClearColor / EndFrame ordering ──────────────────────────────
// Program.cs calls, every frame:  BeginFrame()  ->  ClearColor(r,g,b,a)  ->  EndFrame()
//
// To honor that order with a single clean code path we DEFER vkCmdBeginRenderPass
// until EndFrame:
//   * BeginFrame()  waits the in-flight fence, acquires the next swapchain image,
//                   resets + begins the command buffer. (No render pass yet.)
//   * ClearColor()  just stores the requested color in a field.
//   * EndFrame()    begins the render pass using the stored color as the single
//                   color attachment's CLEAR value (loadOp = CLEAR), immediately
//                   ends the render pass (nothing else to draw yet), ends the
//                   command buffer, submits with the imageAvailable/renderFinished
//                   semaphores + in-flight fence, then presents.
// Because the clear is the render pass's attachment clear value, the color set by
// ClearColor is captured at EndFrame regardless of when (or whether) it was
// called — making the Begin -> Clear -> End sequence correct by construction.

using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Silk.NET.Core;
using Silk.NET.Core.Native;
using Silk.NET.Maths;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.EXT;
using Silk.NET.Vulkan.Extensions.KHR;
using Silk.NET.Windowing;

// ImplicitUsings pulls in System.Threading, which also defines a Semaphore type;
// alias the Vulkan one so unqualified "Semaphore" is unambiguous here.
using Semaphore = Silk.NET.Vulkan.Semaphore;
// System.Buffer collides with Silk.NET.Vulkan.Buffer; alias the Vulkan one and
// call System.Buffer.MemoryCopy fully-qualified at the (few) use sites.
using Buffer = Silk.NET.Vulkan.Buffer;

namespace Pebble.Gpu;

public sealed unsafe class VulkanBackend : IGpuBackend
{
    public string Name => "Vulkan";

    // Number of frames recorded/in-flight concurrently.
    private const int MaxFramesInFlight = 2;

    // Toggle validation layers. Kept on for debug builds; guarded so it silently
    // disables itself if the layer isn't installed on the machine.
#if DEBUG
    private const bool WantValidation = true;
#else
    private const bool WantValidation = false;
#endif

    private readonly Vk _vk = Vk.GetApi();

    // Opt-in heavy passes (default off so the robust path always runs). The sun
    // shadow pass and the ultra/bloom post chain are gated behind --shadows/--ultra.
    private readonly bool _shadowsEnabled;
    private readonly bool _ultraEnabled;
    public VulkanBackend(bool enableShadows = false, bool enableUltra = false)
    {
        _shadowsEnabled = enableShadows;
        _ultraEnabled = enableUltra;
    }

    private IWindow _window = null!;

    // Core objects.
    private Instance _instance;
    private DebugUtilsMessengerEXT _debugMessenger;
    private ExtDebugUtils? _debugUtils;
    private KhrSurface _khrSurface = null!;
    private SurfaceKHR _surface;
    private PhysicalDevice _physicalDevice;
    private Device _device;
    private Queue _graphicsQueue;
    private Queue _presentQueue;
    private uint _graphicsQueueFamily;
    private uint _presentQueueFamily;

    private bool _validationEnabled;

    // Swapchain + attachments.
    private KhrSwapchain _khrSwapchain = null!;
    private SwapchainKHR _swapchain;
    private Image[] _swapchainImages = Array.Empty<Image>();
    private ImageView[] _swapchainImageViews = Array.Empty<ImageView>();
    private Format _swapchainFormat;
    private Extent2D _swapchainExtent;

    // Render pass + framebuffers (one per swapchain image).
    private RenderPass _renderPass;
    private Framebuffer[] _framebuffers = Array.Empty<Framebuffer>();

    // Depth attachment (one per swapchain image is unnecessary — we only ever
    // render one frame's image at a time, but the swapchain may own >1 image
    // and a framebuffer references a fixed depth view, so allocate per image).
    private const Format DepthFormat = Format.D32Sfloat;
    private Image[] _depthImages = Array.Empty<Image>();
    private DeviceMemory[] _depthMemories = Array.Empty<DeviceMemory>();
    private ImageView[] _depthViews = Array.Empty<ImageView>();

    // Commands.
    private CommandPool _commandPool;
    private CommandBuffer[] _commandBuffers = Array.Empty<CommandBuffer>();

    // Per-frame sync.
    private Semaphore[] _imageAvailable = Array.Empty<Semaphore>();
    private Semaphore[] _renderFinished = Array.Empty<Semaphore>();
    private Fence[] _inFlightFences = Array.Empty<Fence>();

    // Frame state.
    private int _currentFrame;
    private uint _imageIndex;
    private bool _frameActive;          // true between a successful BeginFrame and EndFrame
    private bool _framebufferResized;   // set when present/acquire reports OUT_OF_DATE/SUBOPTIMAL
    private ClearColorValue _clearValue = new(0.62f, 0.74f, 1.0f, 1f); // sky

    private bool _disposed;

    // ─────────────────────────────────────────────── World renderer state
    //
    // std140 layout of the ChunkShared uniform (mirrors the Metal original):
    //   mat4 viewProj; mat4 shadowMat; vec4 light; vec4 fog; vec4 fogColor; vec4 misc;
    // = 2*64 + 4*16 = 192 bytes.
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

    [StructLayout(LayoutKind.Sequential)]
    private struct PushConstants
    {
        public Vector4 origin;
    }

    // std140 SkyU: mat4 invViewProj; vec4 zenith; vec4 horizon; vec4 horizonSun;
    // vec4 sunDir. = 64 + 64 = 128 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct SkyUniform
    {
        public Matrix4x4 invViewProj;
        public Vector4 zenith;
        public Vector4 horizon;
        public Vector4 horizonSun;
        public Vector4 sunDir;
    }

    // std140 CelestialU: mat4 viewProj; vec4 center; vec4 right; vec4 up. = 128 bytes.
    [StructLayout(LayoutKind.Sequential)]
    private struct CelestialUniform
    {
        public Matrix4x4 viewProj;
        public Vector4 center;
        public Vector4 right;
        public Vector4 up;
    }

    private const uint VertexStride = 28; // 7 uint32 words

    private bool _worldReady;

    // ── Atmosphere (sky gradient + sun/moon) ──
    // The sky is a full-screen triangle drawn first with depth-write off; the two
    // celestials are billboards at the far plane. Both use a UBO-only descriptor
    // set (no vertex buffer). One sky UBO + two celestial UBOs (sun + moon) are
    // ring-allocated per in-flight frame so a frame never patches a UBO another
    // frame may still be reading.
    private bool _skyReady;
    private DescriptorSetLayout _skyDescLayout;     // single UBO at binding 0
    private PipelineLayout _skyPipelineLayout;
    private Pipeline _skyPipeline;                  // gradient, no depth, no blend
    private Pipeline _celestialPipeline;            // billboards, depth-test read, alpha blend
    private DescriptorPool _skyDescPool;
    // Per-frame: 1 sky UBO + 2 celestial UBOs = 3 sets/frame.
    private const int SkySetsPerFrame = 3;
    private readonly Buffer[] _skyUbo = new Buffer[MaxFramesInFlight * SkySetsPerFrame];
    private readonly DeviceMemory[] _skyUboMem = new DeviceMemory[MaxFramesInFlight * SkySetsPerFrame];
    private readonly void*[] _skyUboMapped = new void*[MaxFramesInFlight * SkySetsPerFrame];
    private readonly DescriptorSet[] _skyDescSet = new DescriptorSet[MaxFramesInFlight * SkySetsPerFrame];

    // Captured per frame from RenderSky; consumed by RecordSky + RecordWorld.
    private bool _haveSky;
    private SkyState _frameSky;

    // ── Stars (night) + clouds (day) ──
    // Stars: a point list sampling the unit-sphere star buffer, drawn after the sky
    // gradient (depth far) when StarAlpha > 0. Reuses the sky UBO-only layout.
    // Clouds: a flat alpha-blended quad at cloud height, drawn after the world
    // (depth-read), sampling the procedural cloud texture (UBO + sampler layout).
    private bool _skyExtraReady;
    private Pipeline _starsPipeline;                 // point list, additive
    private Pipeline _cloudPipeline;                 // quad, alpha, depth-read
    private DescriptorSetLayout _cloudDescLayout;    // UBO@0 + sampler@1
    private PipelineLayout _cloudPipelineLayout;
    private DescriptorPool _skyExtraDescPool;
    // Per frame: 1 stars set + 1 cloud set.
    private readonly Buffer[] _starsUbo = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _starsUboMem = new DeviceMemory[MaxFramesInFlight];
    private readonly void*[] _starsUboMapped = new void*[MaxFramesInFlight];
    private readonly DescriptorSet[] _starsDescSet = new DescriptorSet[MaxFramesInFlight];
    private readonly Buffer[] _cloudUbo = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _cloudUboMem = new DeviceMemory[MaxFramesInFlight];
    private readonly void*[] _cloudUboMapped = new void*[MaxFramesInFlight];
    private readonly DescriptorSet[] _cloudDescSet = new DescriptorSet[MaxFramesInFlight];
    // Star vertex buffer (StarCount × float4).
    private Buffer _starsVbuf;
    private DeviceMemory _starsVmem;
    private int _starsCount;
    // Cloud texture (sampler2D).
    private Image _cloudImage;
    private DeviceMemory _cloudMemory;
    private ImageView _cloudView;
    private Sampler _cloudSampler;

    [StructLayout(LayoutKind.Sequential)]
    private struct StarsUniform { public Matrix4x4 viewProj; public Vector4 paramsv; }
    [StructLayout(LayoutKind.Sequential)]
    private struct CloudUniform { public Matrix4x4 viewProj; public Vector4 offset; public Vector4 scroll; }

    // Pipeline objects.
    private DescriptorSetLayout _descSetLayout;
    private PipelineLayout _pipelineLayout;
    private Pipeline _opaquePipeline;     // depth write ON, no blend, alphaTest via uniform
    private Pipeline _translucentPipeline; // depth write OFF, alpha blend

    // Uniform buffer (one shared, updated per frame — fine for the single
    // in-flight world draw recorded inside EndFrame).
    private Buffer _uboBuffer;
    private DeviceMemory _uboMemory;
    private void* _uboMapped;

    // Second world UBO for the translucent pass: the opaque/cutout draws and the
    // translucent draws use different fog.z/fog.w, and the GPU reads the UBO at
    // execution time — so they MUST be distinct buffers (one mapped region can't
    // hold both values for draws sharing a command buffer).
    private Buffer _uboBufferTrans;
    private DeviceMemory _uboMemoryTrans;
    private void* _uboMappedTrans;
    private DescriptorSet _descSetTrans;

    // Atlas texture array.
    private Image _atlasImage;
    private DeviceMemory _atlasMemory;
    private ImageView _atlasView;
    private Sampler _atlasSampler;
    private uint _atlasLayers;
    private int _atlasMissing;
    private int _atlasTile;                  // pixel resolution of one atlas slice

    // Animated-tile driver (water/lava/fire/portal). Built from the pack's .mcmeta
    // frames; advanced each frame, with changed slices blitted at command-buffer
    // start (before the render pass) from a persistent host-visible staging buffer.
    private TileAnimator? _animator;
    private Buffer _animStaging;
    private DeviceMemory _animStagingMem;
    private void* _animStagingMapped;
    private ulong _animStagingSize;
    private double _lastAnimTime = -1;

    // Descriptors.
    private DescriptorPool _descPool;
    private DescriptorSet _descSet;

    // ── sun shadow map ──
    // A depth-only texture rendered from the sun direction (shadow_vs) in a separate
    // depth pass before the main pass, then sampled by chunk_fs (3×3 PCF). Bound at
    // world descriptor binding 2 with a clamp sampler. shadowMat is uploaded into the
    // chunk UBO's viewProj slot for the shadow pass and into shadowMat for the world.
    //
    // DOUBLE-BUFFERED: the shadow depth image + view + framebuffer are allocated once
    // PER IN-FLIGHT FRAME. Frame N writes (shadow pass) and samples (world pass) the
    // SAME image[N] within one submit, so frame N's write never races frame N-1's
    // still-in-flight read of image[N-1]. A single shared image caused a cross-frame
    // GPU hazard → device-loss, which is why shadows used to be gated behind --shadows.
    private const int ShadowSize = 2048;
    private bool _shadowReady;
    private readonly Image[] _shadowImage = new Image[MaxFramesInFlight];
    private readonly DeviceMemory[] _shadowMemory = new DeviceMemory[MaxFramesInFlight];
    private readonly ImageView[] _shadowView = new ImageView[MaxFramesInFlight];
    private Sampler _shadowSampler;
    private RenderPass _shadowRenderPass;
    private readonly Framebuffer[] _shadowFramebuffer = new Framebuffer[MaxFramesInFlight];
    private Pipeline _shadowPipeline;
    private DescriptorSetLayout _shadowDescLayout;   // UBO only (binding 0)
    private PipelineLayout _shadowPipelineLayout;    // shadow-only layout (UBO + push)
    // Per-frame ring so frame N's UBO write doesn't corrupt frame N-1's in-flight read.
    private readonly Buffer[] _shadowUbo = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _shadowUboMem = new DeviceMemory[MaxFramesInFlight];
    private readonly void*[] _shadowUboMapped = new void*[MaxFramesInFlight];
    private readonly DescriptorSet[] _shadowDescSet = new DescriptorSet[MaxFramesInFlight];
    // World descriptor sets are also per-frame now: set f samples shadow image[f].
    // (opaque + translucent variants × MaxFramesInFlight)
    private readonly DescriptorSet[] _descSetFrame = new DescriptorSet[MaxFramesInFlight];
    private readonly DescriptorSet[] _descSetTransFrame = new DescriptorSet[MaxFramesInFlight];
    private Matrix4x4 _frameShadowMat = Matrix4x4.Identity;
    private bool _frameShadowsOn;

    // ── offscreen-HDR post-processing chain ──
    // The whole 3D scene (sky/world/entities/sprites/particles/break/clouds) renders
    // into an R16G16B16A16_SFLOAT HDR target (a separate render pass, double-buffered
    // per in-flight frame) instead of the swapchain. The post chain then bright-passes
    // + blurs (bloom), computes half-res SSAO + volumetric god-rays (ultra), and
    // composites with an ACES filmic tonemap into the swapchain (_renderPass), after
    // which the HUD draws on the swapchain. All 3D pipelines target _hdrRenderPass;
    // the composite + UI pipelines target _renderPass.
    private const Format HdrFormat = Format.R16G16B16A16Sfloat;
    private bool _postReady;
    private RenderPass _hdrRenderPass;                         // color (HDR, store->read) + depth (store->read)
    private readonly Image[] _hdrColorImage = new Image[MaxFramesInFlight];
    private readonly DeviceMemory[] _hdrColorMem = new DeviceMemory[MaxFramesInFlight];
    private readonly ImageView[] _hdrColorView = new ImageView[MaxFramesInFlight];
    private readonly Image[] _hdrDepthImage = new Image[MaxFramesInFlight];   // sampleable scene depth (ultra)
    private readonly DeviceMemory[] _hdrDepthMem = new DeviceMemory[MaxFramesInFlight];
    private readonly ImageView[] _hdrDepthView = new ImageView[MaxFramesInFlight];
    private readonly Framebuffer[] _hdrFramebuffer = new Framebuffer[MaxFramesInFlight];
    // Bloom (quarter-res ping-pong) + ultra (half-res ping-pong) targets, per frame.
    private readonly Image[] _bloomImageA = new Image[MaxFramesInFlight];
    private readonly DeviceMemory[] _bloomMemA = new DeviceMemory[MaxFramesInFlight];
    private readonly ImageView[] _bloomViewA = new ImageView[MaxFramesInFlight];
    private readonly Framebuffer[] _bloomFbA = new Framebuffer[MaxFramesInFlight];
    private readonly Image[] _bloomImageB = new Image[MaxFramesInFlight];
    private readonly DeviceMemory[] _bloomMemB = new DeviceMemory[MaxFramesInFlight];
    private readonly ImageView[] _bloomViewB = new ImageView[MaxFramesInFlight];
    private readonly Framebuffer[] _bloomFbB = new Framebuffer[MaxFramesInFlight];
    private readonly Image[] _ultraImageA = new Image[MaxFramesInFlight];
    private readonly DeviceMemory[] _ultraMemA = new DeviceMemory[MaxFramesInFlight];
    private readonly ImageView[] _ultraViewA = new ImageView[MaxFramesInFlight];
    private readonly Framebuffer[] _ultraFbA = new Framebuffer[MaxFramesInFlight];
    private readonly Image[] _ultraImageB = new Image[MaxFramesInFlight];
    private readonly DeviceMemory[] _ultraMemB = new DeviceMemory[MaxFramesInFlight];
    private readonly ImageView[] _ultraViewB = new ImageView[MaxFramesInFlight];
    private readonly Framebuffer[] _ultraFbB = new Framebuffer[MaxFramesInFlight];
    private RenderPass _postColorPass;     // single HDR color attachment, load=dontCare, final=shaderRead
    private int _hdrW, _hdrH, _bloomW, _bloomH, _ultraW, _ultraH;
    private Sampler _postSampler;          // linear/clamp for the post passes
    // Pipelines.
    private DescriptorSetLayout _postOneTexLayout;   // UBO@0 + sampler@1 (extract/blur)
    private DescriptorSetLayout _ultraLayout;        // UBO@0 + depth@1 + shadow@2
    private DescriptorSetLayout _compositeLayout;    // UBO@0 + scene@1 + bloom@2 + ultra@3
    private PipelineLayout _postOneTexPipeLayout;
    private PipelineLayout _ultraPipeLayout;
    private PipelineLayout _compositePipeLayout;
    private Pipeline _bloomExtractPipeline;
    private Pipeline _blurPipeline;
    private Pipeline _ultraPipeline;
    private Pipeline _ultraBlurPipeline;
    private Pipeline _compositePipeline;
    private DescriptorPool _postDescPool;
    // Per-frame UBOs + descriptor sets. Composite + ultra each one set/frame. Blur
    // uses 4 sets/frame (bloom H/V, ultra H/V) each with its own dir UBO. Extract 1.
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
    private readonly Buffer[] _compositeUbo = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _compositeUboMem = new DeviceMemory[MaxFramesInFlight];
    private readonly void*[] _compositeUboMapped = new void*[MaxFramesInFlight];
    private readonly DescriptorSet[] _compositeSet = new DescriptorSet[MaxFramesInFlight];
    private readonly Buffer[] _ultraUbo = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _ultraUboMem = new DeviceMemory[MaxFramesInFlight];
    private readonly void*[] _ultraUboMapped = new void*[MaxFramesInFlight];
    private readonly DescriptorSet[] _ultraSet = new DescriptorSet[MaxFramesInFlight];
    // bloom: extract set (scene) + 2 blur sets (A,B). ultra: 2 blur sets (A,B).
    private readonly DescriptorSet[] _bloomExtractSet = new DescriptorSet[MaxFramesInFlight];
    private readonly DescriptorSet[] _bloomBlurSetA = new DescriptorSet[MaxFramesInFlight]; // samples bloomA
    private readonly DescriptorSet[] _bloomBlurSetB = new DescriptorSet[MaxFramesInFlight]; // samples bloomB
    private readonly DescriptorSet[] _ultraBlurSetA = new DescriptorSet[MaxFramesInFlight]; // samples ultraA
    private readonly DescriptorSet[] _ultraBlurSetB = new DescriptorSet[MaxFramesInFlight]; // samples ultraB
    // blur direction UBOs: bloomH, bloomV, ultraH, ultraV per frame (CompositeUniform).
    private readonly Buffer[] _blurUbo = new Buffer[MaxFramesInFlight * 4];
    private readonly DeviceMemory[] _blurUboMem = new DeviceMemory[MaxFramesInFlight * 4];
    private readonly void*[] _blurUboMapped = new void*[MaxFramesInFlight * 4];

    // Per-section GPU mesh cache. Re-uploaded only when the source mesh changes.
    private sealed class SectionGpu
    {
        public Buffer vbuf;
        public DeviceMemory vmem;
        public Buffer ibuf;
        public DeviceMemory imem;
        public uint indexCount;
        public ulong vbufSize;
        public ulong ibufSize;
    }

    private sealed class SectionLayers
    {
        public SectionGpu? opaque;
        public SectionGpu? cutout;
        public SectionGpu? translucent;
        public object? meshRef; // identity of the last-uploaded MeshOutput-bearing SectionMesh
    }

    private readonly Dictionary<(int, int, int), SectionLayers> _sectionCache = new();

    // Pending world draw captured during RenderWorld, replayed inside EndFrame's
    // render pass (kept tiny — just the data the recording loop needs).
    private bool _haveWorldFrame;
    private bool _sectionsSynced;   // SyncSections already ran this frame (before shadow pass)
    private PebbleCore.CamState _frameCam;
    private double _frameTime;
    private HostBridge? _frameHost;

    // Screenshot capture.
    private string? _screenshotPath;
    private bool _screenshotComplete;
    public bool ScreenshotComplete => _screenshotComplete;

    // ─────────────────────────────────────────────────────────────── UI / HUD
    //
    // A screen-space orthographic, alpha-blended, no-depth pass drawn after the
    // world inside the same render pass. Vertex layout (36 bytes): pos2 uv2 layer1
    // color4. The icon Texture2DArray holds N 16×16 RGBA layers, uploaded lazily.
    private const uint UiVertexStride = 36; // 9 float32
    private const int UiIconLayers = 512;

    [StructLayout(LayoutKind.Sequential)]
    private struct UiPush { public Vector4 screen; }

    private bool _uiReady;
    private DescriptorSetLayout _uiDescSetLayout;
    private PipelineLayout _uiPipelineLayout;
    private Pipeline _uiPipeline;
    private DescriptorPool _uiDescPool;
    private DescriptorSet _uiDescSet;

    // Icon texture array (512 × 16×16 RGBA).
    private Image _iconImage;
    private DeviceMemory _iconMemory;
    private ImageView _iconView;
    private Sampler _iconSampler;
    private int _iconNext = 1; // layer 0 reserved blank
    private readonly Dictionary<string, int> _iconSlots = new();

    // Per-frame dynamic vertex buffer (grown as needed; one per in-flight frame).
    private readonly Buffer[] _uiVbuf = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _uiVmem = new DeviceMemory[MaxFramesInFlight];
    private readonly ulong[] _uiVbufSize = new ulong[MaxFramesInFlight];

    // Captured per frame from RenderUI, replayed inside EndFrame's render pass.
    private float[]? _uiVerts;
    private int _uiVertCount;
    private float _uiScreenW, _uiScreenH;

    public Pebble.UI.IconLayerResolver IconResolver => ResolveIconLayer;

    // ─────────────────────────────────────────────────────────── Entities (3D)
    //
    // A depth-tested, back-face-culled pass drawn after the world, before the UI.
    // Vertex layout (stride 40): pos f3 @0, normal f3 @12, uv f2 @24, part f1 @32.
    // EntityU (1728 bytes) is a DYNAMIC uniform buffer: one big buffer holds every
    // entity's uniform block this frame and each draw binds it with a dynamic
    // offset, so a single descriptor set serves all entities without mid-command-
    // buffer UBO patching. binding 0 = EntityU (dynamic), binding 1 = skin 2D.
    private const uint EntityVertexStride = 40; // pos3 + normal3 + uv2 + part1 (10 f32)
    private const int EntityUSize = 1728;       // 64 + 64 + 24*64 + 4*16
    private const int MaxEntitiesPerFrame = 512;

    [StructLayout(LayoutKind.Sequential)]
    private struct EntityVertex
    {
        public float px, py, pz, nx, ny, nz, u, v, part;
    }

    private bool _entityReady;
    private DescriptorSetLayout _entityDescLayout;
    private PipelineLayout _entityPipelineLayout;
    private Pipeline _entityPipeline;
    private DescriptorPool _entityDescPool;
    private uint _entityUAligned;             // EntityUSize rounded up to minUboAlignment

    // Dynamic EntityU buffer (per in-flight frame; grown if needed).
    private readonly Buffer[] _entityUBuf = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _entityUMem = new DeviceMemory[MaxFramesInFlight];
    private readonly ulong[] _entityUSizeBytes = new ulong[MaxFramesInFlight];

    // Per-model GPU geometry (vertex buffer) + skin texture, cached by model name.
    private sealed class EntityModelGpu
    {
        public Buffer vbuf;
        public DeviceMemory vmem;
        public uint vertexCount;
        public Image skinImage;
        public DeviceMemory skinMem;
        public ImageView skinView;
        public Sampler skinSampler;
        // per-frame descriptor sets: set[f] binds frame f's dynamic UBO + this skin.
        public DescriptorSet[] descSet = new DescriptorSet[MaxFramesInFlight];
    }
    private readonly Dictionary<string, EntityModelGpu> _entityModels = new();

    // Captured per frame from RenderEntities, replayed inside EndFrame's render pass.
    private Pebble.EntityRenderer? _entityRenderer;
    private Pebble.EntityDrawList? _entityList;
    private PebbleCore.CamState _entityCam;
    private bool _haveEntityFrame;
    // Scratch buffer for packing all entities' EntityU blocks (one big upload).
    private byte[] _entityUScratch = new byte[EntityUSize * MaxEntitiesPerFrame];

    // ─────────────────────────────────────────────── Particles / sprites / crack
    //
    // Particles: an instanced billboard pass sampling the block atlas array. The
    // per-vertex quad (binding 0) is static; the per-instance stream (binding 1,
    // 48 bytes) is rebuilt + uploaded each frame. ParticleU (mat4 + 2 vec4 = 96 B)
    // is a per-frame UBO. The descriptor set binds the UBO + atlas array.
    [StructLayout(LayoutKind.Sequential)]
    private struct ParticleUniform
    {
        public Matrix4x4 viewProj;
        public Vector4 right;
        public Vector4 up;       // xyz + dayLight
    }
    private const int ParticleInstanceStride = 48; // 12 f32

    private bool _particleReady;
    private DescriptorSetLayout _particleDescLayout;
    private PipelineLayout _particlePipelineLayout;
    private Pipeline _particlePipeline;
    private DescriptorPool _particleDescPool;
    private Buffer _particleQuadBuf;
    private DeviceMemory _particleQuadMem;
    private readonly Buffer[] _particleInstBuf = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _particleInstMem = new DeviceMemory[MaxFramesInFlight];
    private readonly ulong[] _particleInstSize = new ulong[MaxFramesInFlight];
    private readonly Buffer[] _particleUbo = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _particleUboMem = new DeviceMemory[MaxFramesInFlight];
    private readonly void*[] _particleUboMapped = new void*[MaxFramesInFlight];
    private readonly DescriptorSet[] _particleDescSet = new DescriptorSet[MaxFramesInFlight];
    private Pebble.Particles? _frameParticles;
    private bool _haveParticleFrame;

    // Sprites: camera-facing item billboards sampling the icon array. SpriteU (mat4
    // + 4 vec4 = 128 B) is ring-allocated per sprite per frame; the descriptor set
    // binds the UBO + icon array. The icon texture is the SAME array the UI pass
    // uses (EnsureIconTexture) so layers resolved via ResolveIconLayer match.
    [StructLayout(LayoutKind.Sequential)]
    private struct SpriteUniform
    {
        public Matrix4x4 viewProj;
        public Vector4 center;   // xyz + size
        public Vector4 right;    // xyz + layer
        public Vector4 light;    // light, fogStart, fogEnd, _
        public Vector4 fogColor;
    }
    private const int MaxSpritesPerFrame = 256;

    private bool _spriteReady;
    private DescriptorSetLayout _spriteDescLayout;
    private PipelineLayout _spritePipelineLayout;
    private Pipeline _spritePipeline;
    private DescriptorPool _spriteDescPool;
    private readonly Buffer[] _spriteUbo = new Buffer[MaxFramesInFlight * MaxSpritesPerFrame];
    private readonly DeviceMemory[] _spriteUboMem = new DeviceMemory[MaxFramesInFlight * MaxSpritesPerFrame];
    private readonly void*[] _spriteUboMapped = new void*[MaxFramesInFlight * MaxSpritesPerFrame];
    private readonly DescriptorSet[] _spriteDescSet = new DescriptorSet[MaxFramesInFlight * MaxSpritesPerFrame];
    private Pebble.SpriteList? _frameSprites;
    private bool _haveSpriteFrame;

    // Break overlay: a small chunk-format mesh drawn with the translucent world
    // pipeline + a per-frame dynamic VB/IB.
    private readonly Buffer[] _overlayVbuf = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _overlayVmem = new DeviceMemory[MaxFramesInFlight];
    private readonly ulong[] _overlayVbufSize = new ulong[MaxFramesInFlight];
    private readonly Buffer[] _overlayIbuf = new Buffer[MaxFramesInFlight];
    private readonly DeviceMemory[] _overlayImem = new DeviceMemory[MaxFramesInFlight];
    private readonly ulong[] _overlayIbufSize = new ulong[MaxFramesInFlight];
    private Pebble.BreakOverlay? _frameOverlay;
    private bool _haveOverlayFrame;

    // ───────────────────────────────────────────────────────────────── Initialize

    public void Initialize(IWindow window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        if (window.VkSurface is null)
            throw new InvalidOperationException(
                "Window was not created with a Vulkan surface (GraphicsAPI.DefaultVulkan).");

        CreateInstance();
        SetupDebugMessenger();
        CreateSurface();
        PickPhysicalDevice();
        CreateLogicalDevice();
        CreateSwapchain();
        CreateImageViews();
        CreateDepthResources();
        CreateRenderPass();
        CreateFramebuffers();
        CreateHdrResources();
        CreateCommandPool();
        CreateCommandBuffers();
        CreateSyncObjects();

        // World renderer (atlas, UBO, descriptors, pipelines) is initialized
        // lazily on the first RenderWorld: the atlas tile list is only populated
        // after the engine boots and registers all blocks, which happens AFTER
        // backend.Initialize in Program.cs.
    }

    private void CreateInstance()
    {
        var appName = (byte*)SilkMarshal.StringToPtr("Pebble");
        var engineName = (byte*)SilkMarshal.StringToPtr("Pebble");

        var appInfo = new ApplicationInfo
        {
            SType = StructureType.ApplicationInfo,
            PApplicationName = appName,
            ApplicationVersion = new Version32(1, 0, 0),
            PEngineName = engineName,
            EngineVersion = new Version32(1, 0, 0),
            ApiVersion = Vk.Version12,
        };

        // Surface extensions required by the window (e.g. VK_KHR_surface +
        // VK_KHR_win32_surface), plus the debug-utils ext when validating.
        var windowExts = _window.VkSurface!.GetRequiredExtensions(out uint windowExtCount);

        _validationEnabled = WantValidation && CheckValidationLayerSupport();

        var extensions = new System.Collections.Generic.List<string>();
        for (uint i = 0; i < windowExtCount; i++)
            extensions.Add(SilkMarshal.PtrToString((nint)windowExts[i])!);
        if (_validationEnabled)
            extensions.Add(ExtDebugUtils.ExtensionName);

        var ppEnabledExtensions = (byte**)SilkMarshal.StringArrayToPtr(extensions.ToArray());

        string[] validationLayers = { "VK_LAYER_KHRONOS_validation" };
        var ppEnabledLayers = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

        var createInfo = new InstanceCreateInfo
        {
            SType = StructureType.InstanceCreateInfo,
            PApplicationInfo = &appInfo,
            EnabledExtensionCount = (uint)extensions.Count,
            PpEnabledExtensionNames = ppEnabledExtensions,
        };

        var debugCreateInfo = new DebugUtilsMessengerCreateInfoEXT();
        if (_validationEnabled)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = ppEnabledLayers;
            PopulateDebugMessengerCreateInfo(ref debugCreateInfo);
            createInfo.PNext = &debugCreateInfo;
        }

        try
        {
            if (_vk.CreateInstance(in createInfo, null, out _instance) != Result.Success)
                throw new InvalidOperationException("vkCreateInstance failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)appName);
            SilkMarshal.Free((nint)engineName);
            SilkMarshal.Free((nint)ppEnabledExtensions);
            SilkMarshal.Free((nint)ppEnabledLayers);
        }

        if (!_vk.TryGetInstanceExtension(_instance, out _khrSurface))
            throw new InvalidOperationException("VK_KHR_surface extension not available.");
    }

    private bool CheckValidationLayerSupport()
    {
        uint layerCount = 0;
        _vk.EnumerateInstanceLayerProperties(ref layerCount, null);
        if (layerCount == 0) return false;

        var available = new LayerProperties[layerCount];
        fixed (LayerProperties* pAvailable = available)
        {
            _vk.EnumerateInstanceLayerProperties(ref layerCount, pAvailable);
            for (uint i = 0; i < layerCount; i++)
            {
                var name = SilkMarshal.PtrToString((nint)pAvailable[i].LayerName);
                if (name == "VK_LAYER_KHRONOS_validation")
                    return true;
            }
        }
        return false;
    }

    private void PopulateDebugMessengerCreateInfo(ref DebugUtilsMessengerCreateInfoEXT info)
    {
        info.SType = StructureType.DebugUtilsMessengerCreateInfoExt;
        info.MessageSeverity =
            DebugUtilsMessageSeverityFlagsEXT.WarningBitExt |
            DebugUtilsMessageSeverityFlagsEXT.ErrorBitExt;
        info.MessageType =
            DebugUtilsMessageTypeFlagsEXT.GeneralBitExt |
            DebugUtilsMessageTypeFlagsEXT.ValidationBitExt |
            DebugUtilsMessageTypeFlagsEXT.PerformanceBitExt;
        info.PfnUserCallback = (DebugUtilsMessengerCallbackFunctionEXT)DebugCallback;
    }

    private static uint DebugCallback(
        DebugUtilsMessageSeverityFlagsEXT severity,
        DebugUtilsMessageTypeFlagsEXT type,
        DebugUtilsMessengerCallbackDataEXT* callbackData,
        void* userData)
    {
        var msg = SilkMarshal.PtrToString((nint)callbackData->PMessage);
        Console.WriteLine($"[Vulkan] {severity}: {msg}");
        return Vk.False;
    }

    private void SetupDebugMessenger()
    {
        if (!_validationEnabled) return;
        if (!_vk.TryGetInstanceExtension(_instance, out ExtDebugUtils debugUtils)) return;
        _debugUtils = debugUtils;

        var info = new DebugUtilsMessengerCreateInfoEXT();
        PopulateDebugMessengerCreateInfo(ref info);
        if (_debugUtils.CreateDebugUtilsMessenger(_instance, in info, null, out _debugMessenger)
            != Result.Success)
        {
            _debugUtils = null; // non-fatal
        }
    }

    private void CreateSurface()
    {
        _surface = _window.VkSurface!.Create<AllocationCallbacks>(_instance.ToHandle(), null)
            .ToSurface();
    }

    private void PickPhysicalDevice()
    {
        uint deviceCount = 0;
        _vk.EnumeratePhysicalDevices(_instance, ref deviceCount, null);
        if (deviceCount == 0)
            throw new InvalidOperationException("No Vulkan-capable physical devices found.");

        var devices = new PhysicalDevice[deviceCount];
        fixed (PhysicalDevice* pDevices = devices)
            _vk.EnumeratePhysicalDevices(_instance, ref deviceCount, pDevices);

        // Prefer a DISCRETE GPU when more than one device is present. On laptops /
        // desktops with both an integrated AMD/Intel GPU and a discrete NVIDIA/AMD
        // card, the integrated GPU is often enumerated first; the heavy shadow +
        // world passes can TDR the weaker iGPU, so pick the discrete card if it is
        // suitable. Falls back to the first suitable device otherwise.
        PhysicalDevice firstSuitable = default;
        bool haveFirst = false;
        uint firstGfx = 0, firstPresent = 0;
        foreach (var device in devices)
        {
            if (!IsDeviceSuitable(device, out uint gfx, out uint present))
                continue;
            _vk.GetPhysicalDeviceProperties(device, out var props);
            string name = SilkMarshal.PtrToString((nint)props.DeviceName) ?? "?";
            Console.WriteLine($"[Vulkan] candidate GPU: {name} ({props.DeviceType})");
            if (!haveFirst)
            {
                firstSuitable = device; firstGfx = gfx; firstPresent = present; haveFirst = true;
            }
            if (props.DeviceType == PhysicalDeviceType.DiscreteGpu)
            {
                _physicalDevice = device;
                _graphicsQueueFamily = gfx;
                _presentQueueFamily = present;
                Console.WriteLine($"[Vulkan] selected discrete GPU: {name}");
                return;
            }
        }
        if (haveFirst)
        {
            _physicalDevice = firstSuitable;
            _graphicsQueueFamily = firstGfx;
            _presentQueueFamily = firstPresent;
            _vk.GetPhysicalDeviceProperties(firstSuitable, out var props);
            Console.WriteLine($"[Vulkan] selected GPU: {SilkMarshal.PtrToString((nint)props.DeviceName)}");
            return;
        }
        throw new InvalidOperationException("No suitable GPU (graphics + present + swapchain) found.");
    }

    private bool IsDeviceSuitable(PhysicalDevice device, out uint graphicsFamily, out uint presentFamily)
    {
        graphicsFamily = 0;
        presentFamily = 0;

        if (!FindQueueFamilies(device, out bool hasGfx, out graphicsFamily, out bool hasPresent, out presentFamily))
            return false;
        if (!hasGfx || !hasPresent) return false;
        if (!CheckDeviceExtensionSupport(device)) return false;

        // Need at least one surface format and present mode.
        uint formatCount = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(device, _surface, ref formatCount, null);
        uint presentModeCount = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(device, _surface, ref presentModeCount, null);
        return formatCount > 0 && presentModeCount > 0;
    }

    private bool FindQueueFamilies(
        PhysicalDevice device,
        out bool hasGraphics, out uint graphicsFamily,
        out bool hasPresent, out uint presentFamily)
    {
        hasGraphics = false; graphicsFamily = 0;
        hasPresent = false; presentFamily = 0;

        uint count = 0;
        _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, null);
        if (count == 0) return false;

        var families = new QueueFamilyProperties[count];
        fixed (QueueFamilyProperties* pFamilies = families)
            _vk.GetPhysicalDeviceQueueFamilyProperties(device, ref count, pFamilies);

        for (uint i = 0; i < count; i++)
        {
            if (!hasGraphics && families[i].QueueFlags.HasFlag(QueueFlags.GraphicsBit))
            {
                hasGraphics = true;
                graphicsFamily = i;
            }

            _khrSurface.GetPhysicalDeviceSurfaceSupport(device, i, _surface, out Bool32 supported);
            if (!hasPresent && supported)
            {
                hasPresent = true;
                presentFamily = i;
            }

            if (hasGraphics && hasPresent) break;
        }
        return true;
    }

    private bool CheckDeviceExtensionSupport(PhysicalDevice device)
    {
        uint extCount = 0;
        _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extCount, null);
        if (extCount == 0) return false;

        var available = new ExtensionProperties[extCount];
        fixed (ExtensionProperties* pAvailable = available)
        {
            _vk.EnumerateDeviceExtensionProperties(device, (byte*)null, ref extCount, pAvailable);
            for (uint i = 0; i < extCount; i++)
            {
                if (SilkMarshal.PtrToString((nint)pAvailable[i].ExtensionName) == KhrSwapchain.ExtensionName)
                    return true;
            }
        }
        return false;
    }

    private void CreateLogicalDevice()
    {
        var uniqueFamilies = _graphicsQueueFamily == _presentQueueFamily
            ? new[] { _graphicsQueueFamily }
            : new[] { _graphicsQueueFamily, _presentQueueFamily };

        float queuePriority = 1f;
        var queueCreateInfos = stackalloc DeviceQueueCreateInfo[uniqueFamilies.Length];
        for (int i = 0; i < uniqueFamilies.Length; i++)
        {
            queueCreateInfos[i] = new DeviceQueueCreateInfo
            {
                SType = StructureType.DeviceQueueCreateInfo,
                QueueFamilyIndex = uniqueFamilies[i],
                QueueCount = 1,
                PQueuePriorities = &queuePriority,
            };
        }

        var features = new PhysicalDeviceFeatures();

        string[] deviceExts = { KhrSwapchain.ExtensionName };
        var ppDeviceExts = (byte**)SilkMarshal.StringArrayToPtr(deviceExts);

        string[] validationLayers = { "VK_LAYER_KHRONOS_validation" };
        var ppLayers = (byte**)SilkMarshal.StringArrayToPtr(validationLayers);

        var createInfo = new DeviceCreateInfo
        {
            SType = StructureType.DeviceCreateInfo,
            QueueCreateInfoCount = (uint)uniqueFamilies.Length,
            PQueueCreateInfos = queueCreateInfos,
            PEnabledFeatures = &features,
            EnabledExtensionCount = (uint)deviceExts.Length,
            PpEnabledExtensionNames = ppDeviceExts,
        };
        if (_validationEnabled)
        {
            createInfo.EnabledLayerCount = (uint)validationLayers.Length;
            createInfo.PpEnabledLayerNames = ppLayers;
        }

        try
        {
            if (_vk.CreateDevice(_physicalDevice, in createInfo, null, out _device) != Result.Success)
                throw new InvalidOperationException("vkCreateDevice failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)ppDeviceExts);
            SilkMarshal.Free((nint)ppLayers);
        }

        if (!_vk.TryGetDeviceExtension(_instance, _device, out _khrSwapchain))
            throw new InvalidOperationException("VK_KHR_swapchain extension not available on device.");

        _vk.GetDeviceQueue(_device, _graphicsQueueFamily, 0, out _graphicsQueue);
        _vk.GetDeviceQueue(_device, _presentQueueFamily, 0, out _presentQueue);
    }

    // ───────────────────────────────────────────────────────────────── Swapchain

    private void CreateSwapchain()
    {
        _khrSurface.GetPhysicalDeviceSurfaceCapabilities(_physicalDevice, _surface, out var caps);

        var format = ChooseSurfaceFormat();
        var presentMode = ChoosePresentMode();
        var extent = ChooseExtent(caps);

        uint imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
            imageCount = caps.MaxImageCount;

        var createInfo = new SwapchainCreateInfoKHR
        {
            SType = StructureType.SwapchainCreateInfoKhr,
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace,
            ImageExtent = extent,
            ImageArrayLayers = 1,
            // TransferSrc so the screenshot path can copy the presented color
            // image to a host-visible buffer.
            ImageUsage = ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.TransferSrcBit,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = CompositeAlphaFlagsKHR.OpaqueBitKhr,
            PresentMode = presentMode,
            Clipped = true,
            OldSwapchain = default,
        };

        if (_graphicsQueueFamily != _presentQueueFamily)
        {
            var indices = stackalloc uint[] { _graphicsQueueFamily, _presentQueueFamily };
            createInfo.ImageSharingMode = SharingMode.Concurrent;
            createInfo.QueueFamilyIndexCount = 2;
            createInfo.PQueueFamilyIndices = indices;
            if (_khrSwapchain.CreateSwapchain(_device, in createInfo, null, out _swapchain) != Result.Success)
                throw new InvalidOperationException("vkCreateSwapchainKHR failed.");
        }
        else
        {
            createInfo.ImageSharingMode = SharingMode.Exclusive;
            if (_khrSwapchain.CreateSwapchain(_device, in createInfo, null, out _swapchain) != Result.Success)
                throw new InvalidOperationException("vkCreateSwapchainKHR failed.");
        }

        uint count = 0;
        _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref count, null);
        _swapchainImages = new Image[count];
        fixed (Image* pImages = _swapchainImages)
            _khrSwapchain.GetSwapchainImages(_device, _swapchain, ref count, pImages);

        _swapchainFormat = format.Format;
        _swapchainExtent = extent;
    }

    private SurfaceFormatKHR ChooseSurfaceFormat()
    {
        uint count = 0;
        _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref count, null);
        var formats = new SurfaceFormatKHR[count];
        fixed (SurfaceFormatKHR* pFormats = formats)
            _khrSurface.GetPhysicalDeviceSurfaceFormats(_physicalDevice, _surface, ref count, pFormats);

        foreach (var f in formats)
        {
            if (f.Format == Format.B8G8R8A8Srgb && f.ColorSpace == ColorSpaceKHR.SpaceSrgbNonlinearKhr)
                return f;
        }
        return formats[0];
    }

    private PresentModeKHR ChoosePresentMode()
    {
        uint count = 0;
        _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref count, null);
        var modes = new PresentModeKHR[count];
        fixed (PresentModeKHR* pModes = modes)
            _khrSurface.GetPhysicalDeviceSurfacePresentModes(_physicalDevice, _surface, ref count, pModes);

        foreach (var m in modes)
        {
            if (m == PresentModeKHR.MailboxKhr)
                return m;
        }
        // FIFO is always available and is the VSync-on mode.
        return PresentModeKHR.FifoKhr;
    }

    private Extent2D ChooseExtent(SurfaceCapabilitiesKHR caps)
    {
        if (caps.CurrentExtent.Width != uint.MaxValue)
            return caps.CurrentExtent;

        var fb = _window.FramebufferSize;
        var extent = new Extent2D((uint)fb.X, (uint)fb.Y);
        extent.Width = Math.Clamp(extent.Width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width);
        extent.Height = Math.Clamp(extent.Height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height);
        return extent;
    }

    private void CreateImageViews()
    {
        _swapchainImageViews = new ImageView[_swapchainImages.Length];
        for (int i = 0; i < _swapchainImages.Length; i++)
        {
            var createInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _swapchainImages[i],
                ViewType = ImageViewType.Type2D,
                Format = _swapchainFormat,
                Components =
                {
                    R = ComponentSwizzle.Identity,
                    G = ComponentSwizzle.Identity,
                    B = ComponentSwizzle.Identity,
                    A = ComponentSwizzle.Identity,
                },
                SubresourceRange =
                {
                    AspectMask = ImageAspectFlags.ColorBit,
                    BaseMipLevel = 0,
                    LevelCount = 1,
                    BaseArrayLayer = 0,
                    LayerCount = 1,
                },
            };
            if (_vk.CreateImageView(_device, in createInfo, null, out _swapchainImageViews[i]) != Result.Success)
                throw new InvalidOperationException("vkCreateImageView failed.");
        }
    }

    private void CreateRenderPass()
    {
        var attachments = stackalloc AttachmentDescription[2];
        attachments[0] = new AttachmentDescription
        {
            Format = _swapchainFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.PresentSrcKhr,
        };
        attachments[1] = new AttachmentDescription
        {
            Format = DepthFormat,
            Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            StencilLoadOp = AttachmentLoadOp.DontCare,
            StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined,
            FinalLayout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var colorAttachmentRef = new AttachmentReference
        {
            Attachment = 0,
            Layout = ImageLayout.ColorAttachmentOptimal,
        };
        var depthAttachmentRef = new AttachmentReference
        {
            Attachment = 1,
            Layout = ImageLayout.DepthStencilAttachmentOptimal,
        };

        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachmentRef,
            PDepthStencilAttachment = &depthAttachmentRef,
        };

        var dependency = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal,
            DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = 0,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
            DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
        };

        var renderPassInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo,
            AttachmentCount = 2,
            PAttachments = attachments,
            SubpassCount = 1,
            PSubpasses = &subpass,
            DependencyCount = 1,
            PDependencies = &dependency,
        };

        if (_vk.CreateRenderPass(_device, in renderPassInfo, null, out _renderPass) != Result.Success)
            throw new InvalidOperationException("vkCreateRenderPass failed.");
    }

    private void CreateDepthResources()
    {
        int n = _swapchainImages.Length;
        _depthImages = new Image[n];
        _depthMemories = new DeviceMemory[n];
        _depthViews = new ImageView[n];

        for (int i = 0; i < n; i++)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo,
                ImageType = ImageType.Type2D,
                Extent = new Extent3D(_swapchainExtent.Width, _swapchainExtent.Height, 1),
                MipLevels = 1,
                ArrayLayers = 1,
                Format = DepthFormat,
                Tiling = ImageTiling.Optimal,
                InitialLayout = ImageLayout.Undefined,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit,
                Samples = SampleCountFlags.Count1Bit,
                SharingMode = SharingMode.Exclusive,
            };
            if (_vk.CreateImage(_device, in imageInfo, null, out _depthImages[i]) != Result.Success)
                throw new InvalidOperationException("vkCreateImage (depth) failed.");

            _vk.GetImageMemoryRequirements(_device, _depthImages[i], out var memReq);
            var allocInfo = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo,
                AllocationSize = memReq.Size,
                MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            if (_vk.AllocateMemory(_device, in allocInfo, null, out _depthMemories[i]) != Result.Success)
                throw new InvalidOperationException("vkAllocateMemory (depth) failed.");
            _vk.BindImageMemory(_device, _depthImages[i], _depthMemories[i], 0);

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo,
                Image = _depthImages[i],
                ViewType = ImageViewType.Type2D,
                Format = DepthFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            };
            if (_vk.CreateImageView(_device, in viewInfo, null, out _depthViews[i]) != Result.Success)
                throw new InvalidOperationException("vkCreateImageView (depth) failed.");
        }
    }

    private void CreateFramebuffers()
    {
        _framebuffers = new Framebuffer[_swapchainImageViews.Length];
        var attachments = stackalloc ImageView[2];
        for (int i = 0; i < _swapchainImageViews.Length; i++)
        {
            attachments[0] = _swapchainImageViews[i];
            attachments[1] = _depthViews[i];
            var framebufferInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo,
                RenderPass = _renderPass,
                AttachmentCount = 2,
                PAttachments = attachments,
                Width = _swapchainExtent.Width,
                Height = _swapchainExtent.Height,
                Layers = 1,
            };
            if (_vk.CreateFramebuffer(_device, in framebufferInfo, null, out _framebuffers[i]) != Result.Success)
                throw new InvalidOperationException("vkCreateFramebuffer failed.");
        }
    }

    // ── offscreen-HDR + post render targets ─────────────────────────────────────
    private void MakeImage(int w, int h, Format fmt, ImageUsageFlags usage, ImageAspectFlags aspect,
        out Image image, out DeviceMemory mem, out ImageView view)
    {
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)w, (uint)h, 1), MipLevels = 1, ArrayLayers = 1,
            Format = fmt, Tiling = ImageTiling.Optimal, InitialLayout = ImageLayout.Undefined,
            Usage = usage, Samples = SampleCountFlags.Count1Bit, SharingMode = SharingMode.Exclusive,
        };
        if (_vk.CreateImage(_device, in imageInfo, null, out image) != Result.Success)
            throw new InvalidOperationException("vkCreateImage (post) failed.");
        _vk.GetImageMemoryRequirements(_device, image, out var memReq);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        if (_vk.AllocateMemory(_device, in alloc, null, out mem) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory (post) failed.");
        _vk.BindImageMemory(_device, image, mem, 0);
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = image, ViewType = ImageViewType.Type2D,
            Format = fmt, SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
        };
        if (_vk.CreateImageView(_device, in viewInfo, null, out view) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView (post) failed.");
    }

    private Framebuffer MakeFramebuffer(RenderPass pass, int w, int h, ImageView a, ImageView b)
    {
        var attachments = stackalloc ImageView[2] { a, b };
        var fbInfo = new FramebufferCreateInfo
        {
            SType = StructureType.FramebufferCreateInfo, RenderPass = pass,
            AttachmentCount = b.Handle != 0 ? 2u : 1u, PAttachments = attachments,
            Width = (uint)w, Height = (uint)h, Layers = 1,
        };
        if (_vk.CreateFramebuffer(_device, in fbInfo, null, out var fb) != Result.Success)
            throw new InvalidOperationException("vkCreateFramebuffer (post) failed.");
        return fb;
    }

    // HDR render pass: HDR color (clear->store->shaderRead) + depth (clear->store->
    // shaderRead, so the ultra pass samples scene depth). Post color pass: a single
    // HDR color attachment (dontCare->store->shaderRead) for bloom/ultra targets.
    private void CreateHdrRenderPasses()
    {
        if (_hdrRenderPass.Handle == 0)
        {
            var attachments = stackalloc AttachmentDescription[2];
            attachments[0] = new AttachmentDescription
            {
                Format = HdrFormat, Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            attachments[1] = new AttachmentDescription
            {
                Format = DepthFormat, Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.DepthStencilReadOnlyOptimal,
            };
            var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
            var depthRef = new AttachmentReference { Attachment = 1, Layout = ImageLayout.DepthStencilAttachmentOptimal };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1,
                PColorAttachments = &colorRef, PDepthStencilAttachment = &depthRef,
            };
            var deps = stackalloc SubpassDependency[2];
            deps[0] = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.FragmentShaderBit, DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.EarlyFragmentTestsBit,
                SrcAccessMask = AccessFlags.ShaderReadBit, DstAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit,
            };
            deps[1] = new SubpassDependency
            {
                SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit | PipelineStageFlags.LateFragmentTestsBit, DstStageMask = PipelineStageFlags.FragmentShaderBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit | AccessFlags.DepthStencilAttachmentWriteBit, DstAccessMask = AccessFlags.ShaderReadBit,
            };
            var rpInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo, AttachmentCount = 2, PAttachments = attachments,
                SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 2, PDependencies = deps,
            };
            if (_vk.CreateRenderPass(_device, in rpInfo, null, out _hdrRenderPass) != Result.Success)
                throw new InvalidOperationException("vkCreateRenderPass (hdr) failed.");
        }
        if (_postColorPass.Handle == 0)
        {
            var attach = new AttachmentDescription
            {
                Format = HdrFormat, Samples = SampleCountFlags.Count1Bit,
                LoadOp = AttachmentLoadOp.DontCare, StoreOp = AttachmentStoreOp.Store,
                StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
                InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.ShaderReadOnlyOptimal,
            };
            var colorRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
            var subpass = new SubpassDescription
            {
                PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &colorRef,
            };
            var deps = stackalloc SubpassDependency[2];
            deps[0] = new SubpassDependency
            {
                SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
                SrcStageMask = PipelineStageFlags.FragmentShaderBit, DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
                SrcAccessMask = AccessFlags.ShaderReadBit, DstAccessMask = AccessFlags.ColorAttachmentWriteBit,
            };
            deps[1] = new SubpassDependency
            {
                SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
                SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit, DstStageMask = PipelineStageFlags.FragmentShaderBit,
                SrcAccessMask = AccessFlags.ColorAttachmentWriteBit, DstAccessMask = AccessFlags.ShaderReadBit,
            };
            var rpInfo = new RenderPassCreateInfo
            {
                SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &attach,
                SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 2, PDependencies = deps,
            };
            if (_vk.CreateRenderPass(_device, in rpInfo, null, out _postColorPass) != Result.Success)
                throw new InvalidOperationException("vkCreateRenderPass (post color) failed.");
        }
        if (_postSampler.Handle == 0)
        {
            var sInfo = new SamplerCreateInfo
            {
                SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear,
                MipmapMode = SamplerMipmapMode.Nearest, AddressModeU = SamplerAddressMode.ClampToEdge,
                AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge,
                BorderColor = BorderColor.FloatOpaqueBlack, CompareEnable = false, MinLod = 0, MaxLod = 0,
            };
            if (_vk.CreateSampler(_device, in sInfo, null, out _postSampler) != Result.Success)
                throw new InvalidOperationException("vkCreateSampler (post) failed.");
        }
    }

    private void DestroyHdrImages()
    {
        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            void Free(ref Framebuffer fb, ref ImageView v, ref Image img, ref DeviceMemory m)
            {
                if (fb.Handle != 0) { _vk.DestroyFramebuffer(_device, fb, null); fb = default; }
                if (v.Handle != 0) { _vk.DestroyImageView(_device, v, null); v = default; }
                if (img.Handle != 0) { _vk.DestroyImage(_device, img, null); img = default; }
                if (m.Handle != 0) { _vk.FreeMemory(_device, m, null); m = default; }
            }
            Free(ref _hdrFramebuffer[f], ref _hdrColorView[f], ref _hdrColorImage[f], ref _hdrColorMem[f]);
            if (_hdrDepthView[f].Handle != 0) { _vk.DestroyImageView(_device, _hdrDepthView[f], null); _hdrDepthView[f] = default; }
            if (_hdrDepthImage[f].Handle != 0) { _vk.DestroyImage(_device, _hdrDepthImage[f], null); _hdrDepthImage[f] = default; }
            if (_hdrDepthMem[f].Handle != 0) { _vk.FreeMemory(_device, _hdrDepthMem[f], null); _hdrDepthMem[f] = default; }
            Free(ref _bloomFbA[f], ref _bloomViewA[f], ref _bloomImageA[f], ref _bloomMemA[f]);
            Free(ref _bloomFbB[f], ref _bloomViewB[f], ref _bloomImageB[f], ref _bloomMemB[f]);
            Free(ref _ultraFbA[f], ref _ultraViewA[f], ref _ultraImageA[f], ref _ultraMemA[f]);
            Free(ref _ultraFbB[f], ref _ultraViewB[f], ref _ultraImageB[f], ref _ultraMemB[f]);
        }
    }

    private void CreateHdrResources()
    {
        CreateHdrRenderPasses();
        DestroyHdrImages();

        _hdrW = (int)_swapchainExtent.Width; _hdrH = (int)_swapchainExtent.Height;
        _bloomW = Math.Max(1, _hdrW >> 2); _bloomH = Math.Max(1, _hdrH >> 2);
        _ultraW = Math.Max(1, _hdrW >> 1); _ultraH = Math.Max(1, _hdrH >> 1);

        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            MakeImage(_hdrW, _hdrH, HdrFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit,
                out _hdrColorImage[f], out _hdrColorMem[f], out _hdrColorView[f]);
            MakeImage(_hdrW, _hdrH, DepthFormat,
                ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.DepthBit,
                out _hdrDepthImage[f], out _hdrDepthMem[f], out _hdrDepthView[f]);
            _hdrFramebuffer[f] = MakeFramebuffer(_hdrRenderPass, _hdrW, _hdrH, _hdrColorView[f], _hdrDepthView[f]);

            MakeImage(_bloomW, _bloomH, HdrFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit,
                out _bloomImageA[f], out _bloomMemA[f], out _bloomViewA[f]);
            _bloomFbA[f] = MakeFramebuffer(_postColorPass, _bloomW, _bloomH, _bloomViewA[f], default);
            MakeImage(_bloomW, _bloomH, HdrFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit,
                out _bloomImageB[f], out _bloomMemB[f], out _bloomViewB[f]);
            _bloomFbB[f] = MakeFramebuffer(_postColorPass, _bloomW, _bloomH, _bloomViewB[f], default);

            MakeImage(_ultraW, _ultraH, HdrFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit,
                out _ultraImageA[f], out _ultraMemA[f], out _ultraViewA[f]);
            _ultraFbA[f] = MakeFramebuffer(_postColorPass, _ultraW, _ultraH, _ultraViewA[f], default);
            MakeImage(_ultraW, _ultraH, HdrFormat,
                ImageUsageFlags.ColorAttachmentBit | ImageUsageFlags.SampledBit, ImageAspectFlags.ColorBit,
                out _ultraImageB[f], out _ultraMemB[f], out _ultraViewB[f]);
            _ultraFbB[f] = MakeFramebuffer(_postColorPass, _ultraW, _ultraH, _ultraViewB[f], default);
        }
    }

    // ── post pipelines: bloom extract / blur / ultra / composite ────────────────
    private DescriptorSetLayout MakeSetLayout(int sampledCount)
    {
        // binding 0 = UBO, bindings 1..sampledCount = combined image samplers.
        var bindings = stackalloc DescriptorSetLayoutBinding[1 + sampledCount];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        for (int i = 0; i < sampledCount; i++)
            bindings[1 + i] = new DescriptorSetLayoutBinding
            {
                Binding = (uint)(1 + i), DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1,
                StageFlags = ShaderStageFlags.FragmentBit,
            };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = (uint)(1 + sampledCount), PBindings = bindings,
        };
        if (_vk.CreateDescriptorSetLayout(_device, in info, null, out var layout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout (post) failed.");
        return layout;
    }

    private PipelineLayout MakePipeLayout(DescriptorSetLayout setLayout)
    {
        var s = setLayout;
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &s,
        };
        if (_vk.CreatePipelineLayout(_device, in info, null, out var layout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout (post) failed.");
        return layout;
    }

    private Pipeline MakeFullscreenPipeline(string fragSrc, string tag, PipelineLayout layout, RenderPass pass)
    {
        var vsMod = CreateShaderModule(CompileGlsl(PostShaders.FullscreenVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "post.vert"));
        var fsMod = CreateShaderModule(CompileGlsl(fragSrc, Silk.NET.Shaderc.ShaderKind.FragmentShader, tag + ".frag"));
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var emptyVI = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var ia = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates,
            };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill,
                LineWidth = 1.0f, CullMode = CullModeFlags.None, FrontFace = FrontFace.CounterClockwise,
            };
            var msaa = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            // The swapchain (_renderPass) and post (_postColorPass) subpasses differ:
            // the swapchain subpass HAS a depth attachment, so a pipeline targeting it
            // must supply a depth-stencil state (disabled). Always provide one.
            var depthState = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false, DepthWriteEnable = false, DepthCompareOp = CompareOp.Always,
                StencilTestEnable = false,
            };
            var noBlendAttach = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &noBlendAttach,
            };
            var stages = stackalloc PipelineShaderStageCreateInfo[2]
            {
                new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsMod, PName = entry },
                new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fsMod, PName = entry },
            };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &emptyVI, PInputAssemblyState = &ia, PViewportState = &viewportState,
                PRasterizationState = &raster, PMultisampleState = &msaa, PDepthStencilState = &depthState,
                PColorBlendState = &blend, PDynamicState = &dynamicState, Layout = layout, RenderPass = pass, Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out var pipeline) != Result.Success)
                throw new InvalidOperationException($"vkCreateGraphicsPipelines ({tag}) failed.");
            return pipeline;
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vsMod, null);
            _vk.DestroyShaderModule(_device, fsMod, null);
        }
    }

    private void InitPostRenderer()
    {
        CreateHdrRenderPasses();   // idempotent; ensures passes exist
        _postOneTexLayout = MakeSetLayout(1);
        _ultraLayout = MakeSetLayout(2);
        _compositeLayout = MakeSetLayout(3);
        _postOneTexPipeLayout = MakePipeLayout(_postOneTexLayout);
        _ultraPipeLayout = MakePipeLayout(_ultraLayout);
        _compositePipeLayout = MakePipeLayout(_compositeLayout);

        _bloomExtractPipeline = MakeFullscreenPipeline(PostShaders.BloomExtract, "bloom_extract", _postOneTexPipeLayout, _postColorPass);
        _blurPipeline = MakeFullscreenPipeline(PostShaders.Blur, "blur", _postOneTexPipeLayout, _postColorPass);
        _ultraBlurPipeline = MakeFullscreenPipeline(PostShaders.UltraBlur, "ultra_blur", _postOneTexPipeLayout, _postColorPass);
        _ultraPipeline = MakeFullscreenPipeline(PostShaders.Ultra, "ultra", _ultraPipeLayout, _postColorPass);
        _compositePipeline = MakeFullscreenPipeline(PostShaders.Composite, "composite", _compositePipeLayout, _renderPass);

        // Descriptor pool: per frame -> composite(1) + ultra(1) + extract(1) +
        // bloomBlurA/B(2) + ultraBlurA/B(2) = 7 sets. UBOs: composite,ultra,4 blur.
        const int setsPerFrame = 7;
        var poolSizes = stackalloc DescriptorPoolSize[2]
        {
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)(setsPerFrame * MaxFramesInFlight) },
            new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)(8 * MaxFramesInFlight) },
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 2, PPoolSizes = poolSizes,
            MaxSets = (uint)(setsPerFrame * MaxFramesInFlight),
        };
        if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out _postDescPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool (post) failed.");

        ulong compSize = (ulong)Marshal.SizeOf<CompositeUniform>();
        ulong ultraSize = (ulong)Marshal.SizeOf<UltraUniform>();
        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            CreateBuffer(compSize, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _compositeUbo[f], out _compositeUboMem[f]);
            void* cm; _vk.MapMemory(_device, _compositeUboMem[f], 0, compSize, 0, &cm); _compositeUboMapped[f] = cm;
            CreateBuffer(ultraSize, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _ultraUbo[f], out _ultraUboMem[f]);
            void* um; _vk.MapMemory(_device, _ultraUboMem[f], 0, ultraSize, 0, &um); _ultraUboMapped[f] = um;
            for (int d = 0; d < 4; d++)
            {
                CreateBuffer(compSize, BufferUsageFlags.UniformBufferBit,
                    MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit, out _blurUbo[f * 4 + d], out _blurUboMem[f * 4 + d]);
                void* bm; _vk.MapMemory(_device, _blurUboMem[f * 4 + d], 0, compSize, 0, &bm); _blurUboMapped[f * 4 + d] = bm;
            }

            _compositeSet[f] = AllocSet(_compositeLayout);
            _ultraSet[f] = AllocSet(_ultraLayout);
            _bloomExtractSet[f] = AllocSet(_postOneTexLayout);
            _bloomBlurSetA[f] = AllocSet(_postOneTexLayout);
            _bloomBlurSetB[f] = AllocSet(_postOneTexLayout);
            _ultraBlurSetA[f] = AllocSet(_postOneTexLayout);
            _ultraBlurSetB[f] = AllocSet(_postOneTexLayout);
        }
        _postReady = true;
        RefreshPostDescriptorSets();
    }

    private DescriptorSet AllocSet(DescriptorSetLayout layout)
    {
        var l = layout;
        var ai = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _postDescPool, DescriptorSetCount = 1, PSetLayouts = &l,
        };
        if (_vk.AllocateDescriptorSets(_device, in ai, out var set) != Result.Success)
            throw new InvalidOperationException("vkAllocateDescriptorSets (post) failed.");
        return set;
    }

    private void WriteUbo(DescriptorSet set, Buffer ubo, ulong size)
    {
        var bi = new DescriptorBufferInfo { Buffer = ubo, Offset = 0, Range = size };
        var w = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0,
            DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bi,
        };
        _vk.UpdateDescriptorSets(_device, 1, in w, 0, null);
    }

    private void WriteSampler(DescriptorSet set, uint binding, ImageView view, ImageLayout layout)
    {
        var ii = new DescriptorImageInfo { ImageLayout = layout, ImageView = view, Sampler = _postSampler };
        var w = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = binding,
            DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &ii,
        };
        _vk.UpdateDescriptorSets(_device, 1, in w, 0, null);
    }

    // (Re)point the post descriptor sets at the current per-frame HDR/bloom/ultra
    // views + their UBOs. Called on init and after a resize (views change).
    private void RefreshPostDescriptorSets()
    {
        if (!_postReady) return;
        ulong compSize = (ulong)Marshal.SizeOf<CompositeUniform>();
        ulong ultraSize = (ulong)Marshal.SizeOf<UltraUniform>();
        const ImageLayout RO = ImageLayout.ShaderReadOnlyOptimal;
        const ImageLayout DRO = ImageLayout.DepthStencilReadOnlyOptimal;
        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            // composite: UBO, scene(hdr), bloom(A final), ultra(A final)
            WriteUbo(_compositeSet[f], _compositeUbo[f], compSize);
            WriteSampler(_compositeSet[f], 1, _hdrColorView[f], RO);
            WriteSampler(_compositeSet[f], 2, _bloomViewA[f], RO);
            WriteSampler(_compositeSet[f], 3, _ultraViewA[f], RO);
            // ultra: UBO, depth, shadow[f]
            WriteUbo(_ultraSet[f], _ultraUbo[f], ultraSize);
            WriteSampler(_ultraSet[f], 1, _hdrDepthView[f], DRO);
            var shadowView = _shadowReady && _shadowView[f].Handle != 0 ? _shadowView[f] : _hdrDepthView[f];
            WriteSampler(_ultraSet[f], 2, shadowView, DRO);
            // bloom extract: UBO(unused), scene(hdr)
            WriteUbo(_bloomExtractSet[f], _blurUbo[f * 4 + 0], compSize);
            WriteSampler(_bloomExtractSet[f], 1, _hdrColorView[f], RO);
            // bloom blur A (samples bloomA), B (samples bloomB)
            WriteUbo(_bloomBlurSetA[f], _blurUbo[f * 4 + 0], compSize);
            WriteSampler(_bloomBlurSetA[f], 1, _bloomViewA[f], RO);
            WriteUbo(_bloomBlurSetB[f], _blurUbo[f * 4 + 1], compSize);
            WriteSampler(_bloomBlurSetB[f], 1, _bloomViewB[f], RO);
            // ultra blur A (samples ultraA), B (samples ultraB)
            WriteUbo(_ultraBlurSetA[f], _blurUbo[f * 4 + 2], compSize);
            WriteSampler(_ultraBlurSetA[f], 1, _ultraViewA[f], RO);
            WriteUbo(_ultraBlurSetB[f], _blurUbo[f * 4 + 3], compSize);
            WriteSampler(_ultraBlurSetB[f], 1, _ultraViewB[f], RO);
        }
    }

    private void CreateCommandPool()
    {
        var poolInfo = new CommandPoolCreateInfo
        {
            SType = StructureType.CommandPoolCreateInfo,
            Flags = CommandPoolCreateFlags.ResetCommandBufferBit,
            QueueFamilyIndex = _graphicsQueueFamily,
        };
        if (_vk.CreateCommandPool(_device, in poolInfo, null, out _commandPool) != Result.Success)
            throw new InvalidOperationException("vkCreateCommandPool failed.");
    }

    private void CreateCommandBuffers()
    {
        _commandBuffers = new CommandBuffer[MaxFramesInFlight];
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = (uint)_commandBuffers.Length,
        };
        fixed (CommandBuffer* pBuffers = _commandBuffers)
        {
            if (_vk.AllocateCommandBuffers(_device, in allocInfo, pBuffers) != Result.Success)
                throw new InvalidOperationException("vkAllocateCommandBuffers failed.");
        }
    }

    private void CreateSyncObjects()
    {
        _imageAvailable = new Semaphore[MaxFramesInFlight];
        _renderFinished = new Semaphore[MaxFramesInFlight];
        _inFlightFences = new Fence[MaxFramesInFlight];

        var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
        var fenceInfo = new FenceCreateInfo
        {
            SType = StructureType.FenceCreateInfo,
            Flags = FenceCreateFlags.SignaledBit, // start signaled so frame 0 doesn't deadlock
        };

        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_vk.CreateSemaphore(_device, in semaphoreInfo, null, out _imageAvailable[i]) != Result.Success ||
                _vk.CreateSemaphore(_device, in semaphoreInfo, null, out _renderFinished[i]) != Result.Success ||
                _vk.CreateFence(_device, in fenceInfo, null, out _inFlightFences[i]) != Result.Success)
            {
                throw new InvalidOperationException("Failed to create per-frame sync objects.");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════ World renderer

    private void InitWorldRenderer()
    {
        CreateAtlasTexture();
        CreateUniformBuffer();
        CreateShadowResources();      // shadow texture/sampler must exist before the set
        CreateDescriptorSetLayout();
        CreatePipelineLayout();
        CreatePipelines();
        CreateShadowPipeline();
        CreateDescriptorSet();
        _worldReady = true;
    }

    // ── memory helper ────────────────────────────────────────────────────────
    private uint FindMemoryType(uint typeFilter, MemoryPropertyFlags props)
    {
        _vk.GetPhysicalDeviceMemoryProperties(_physicalDevice, out var memProps);
        for (uint i = 0; i < memProps.MemoryTypeCount; i++)
        {
            if ((typeFilter & (1u << (int)i)) != 0 &&
                (memProps.MemoryTypes[(int)i].PropertyFlags & props) == props)
                return i;
        }
        throw new InvalidOperationException("No suitable Vulkan memory type found.");
    }

    private void CreateBuffer(ulong size, BufferUsageFlags usage, MemoryPropertyFlags props,
        out Buffer buffer, out DeviceMemory memory)
    {
        var bufferInfo = new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
        if (_vk.CreateBuffer(_device, in bufferInfo, null, out buffer) != Result.Success)
            throw new InvalidOperationException("vkCreateBuffer failed.");

        _vk.GetBufferMemoryRequirements(_device, buffer, out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, props),
        };
        if (_vk.AllocateMemory(_device, in allocInfo, null, out memory) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory (buffer) failed.");
        _vk.BindBufferMemory(_device, buffer, memory, 0);
    }

    // ── one-shot command submission (for uploads / layout transitions) ────────
    private CommandBuffer BeginOneShot()
    {
        var allocInfo = new CommandBufferAllocateInfo
        {
            SType = StructureType.CommandBufferAllocateInfo,
            CommandPool = _commandPool,
            Level = CommandBufferLevel.Primary,
            CommandBufferCount = 1,
        };
        _vk.AllocateCommandBuffers(_device, in allocInfo, out CommandBuffer cmd);
        var begin = new CommandBufferBeginInfo
        {
            SType = StructureType.CommandBufferBeginInfo,
            Flags = CommandBufferUsageFlags.OneTimeSubmitBit,
        };
        _vk.BeginCommandBuffer(cmd, in begin);
        return cmd;
    }

    private void EndOneShot(CommandBuffer cmd)
    {
        _vk.EndCommandBuffer(cmd);
        var submit = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
        };
        _vk.QueueSubmit(_graphicsQueue, 1, in submit, default);
        _vk.QueueWaitIdle(_graphicsQueue);
        _vk.FreeCommandBuffers(_device, _commandPool, 1, in cmd);
    }

    // ── animated-tile flush: copy the frame's changed atlas slices ────────────
    //
    // Records into the MAIN command buffer at frame start (before the render pass),
    // so the copies are GPU-ordered ahead of this frame's sampling and an in-flight
    // frame still finishes reading the old contents first. Each slice round-trips
    // SHADER_READ_ONLY -> TRANSFER_DST -> SHADER_READ_ONLY.
    private uint _animStagingCursor;
    private void FlushAtlasAnimations(CommandBuffer cmd)
    {
        if (_animator == null) return;
        var updates = _animator.DrainPending();
        if (updates == null) return;

        int layerBytes = _atlasTile * _atlasTile * 4;
        uint ringSlots = (uint)(MaxFramesInFlight + 1);
        foreach (var up in updates)
        {
            // pick a staging ring slot and copy the CPU pixels in
            uint slot = _animStagingCursor % ringSlots;
            _animStagingCursor++;
            ulong off = (ulong)layerBytes * slot;
            var dst = new Span<byte>((byte*)_animStagingMapped + (long)off, layerBytes);
            up.Pixels.AsSpan(0, layerBytes).CopyTo(dst);

            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, (uint)up.Slice, 1);
            var toDst = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.ShaderReadOnlyOptimal,
                NewLayout = ImageLayout.TransferDstOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _atlasImage,
                SubresourceRange = range,
                SrcAccessMask = AccessFlags.ShaderReadBit,
                DstAccessMask = AccessFlags.TransferWriteBit,
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
                0, 0, null, 0, null, 1, in toDst);

            var copy = new BufferImageCopy
            {
                BufferOffset = off,
                BufferRowLength = 0,
                BufferImageHeight = 0,
                ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, (uint)up.Slice, 1),
                ImageOffset = new Offset3D(0, 0, 0),
                ImageExtent = new Extent3D((uint)_atlasTile, (uint)_atlasTile, 1),
            };
            _vk.CmdCopyBufferToImage(cmd, _animStaging, _atlasImage, ImageLayout.TransferDstOptimal, 1, in copy);

            var toRead = new ImageMemoryBarrier
            {
                SType = StructureType.ImageMemoryBarrier,
                OldLayout = ImageLayout.TransferDstOptimal,
                NewLayout = ImageLayout.ShaderReadOnlyOptimal,
                SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
                Image = _atlasImage,
                SubresourceRange = range,
                SrcAccessMask = AccessFlags.TransferWriteBit,
                DstAccessMask = AccessFlags.ShaderReadBit,
            };
            _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
                0, 0, null, 0, null, 1, in toRead);
        }
    }

    // ── atlas: a sampler2DArray with N TILE×TILE RGBA8 layers ─────────────────
    private void CreateAtlasTexture()
    {
        // Resource pack first: if the Faithful 32x zip is present, upload its
        // per-tile slices at the pack resolution (32×). One slice per tile id in
        // tileName order, so the mesher's layer indices still map correctly.
        // Tiles the pack lacks fall back to the procedural art inside LoadPack.
        var pack = Pebble.ResourcePacks.LoadDefault();
        byte[][] sliceData;
        int tile;
        uint layers;
        if (pack != null)
        {
            sliceData = pack.slices;
            tile = pack.res;
            layers = (uint)pack.slices.Length;
            _atlasMissing = 0;
            _animator = new TileAnimator(pack.animations);
        }
        else
        {
            var atlas = PebbleCore.AtlasGlobals.buildAtlas();
            sliceData = atlas.pixels;
            tile = PebbleCore.AtlasGlobals.TILE;
            layers = (uint)atlas.count;
            _atlasMissing = atlas.missing.Length;
            _animator = null;
        }
        _atlasLayers = layers;
        _atlasTile = tile;
        int layerBytes = tile * tile * 4;

        // Pack all layers tightly into one staging buffer.
        ulong stagingSize = (ulong)layerBytes * layers;
        CreateBuffer(stagingSize, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer staging, out DeviceMemory stagingMem);

        void* mapped;
        _vk.MapMemory(_device, stagingMem, 0, stagingSize, 0, &mapped);
        var dst = new Span<byte>(mapped, (int)stagingSize);
        for (int i = 0; i < layers; i++)
        {
            var src = sliceData[i];
            src.AsSpan(0, layerBytes).CopyTo(dst.Slice(i * layerBytes, layerBytes));
        }
        _vk.UnmapMemory(_device, stagingMem);

        // The destination array image.
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)tile, (uint)tile, 1),
            MipLevels = 1,
            ArrayLayers = layers,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };
        if (_vk.CreateImage(_device, in imageInfo, null, out _atlasImage) != Result.Success)
            throw new InvalidOperationException("vkCreateImage (atlas) failed.");

        _vk.GetImageMemoryRequirements(_device, _atlasImage, out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        if (_vk.AllocateMemory(_device, in allocInfo, null, out _atlasMemory) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory (atlas) failed.");
        _vk.BindImageMemory(_device, _atlasImage, _atlasMemory, 0);

        // Transition UNDEFINED -> TRANSFER_DST, copy all layers, transition -> SHADER_READ.
        var cmd = BeginOneShot();

        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, layers);
        var toDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _atlasImage,
            SubresourceRange = range,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.TransferWriteBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, in toDst);

        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, layers),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D((uint)tile, (uint)tile, 1),
        };
        _vk.CmdCopyBufferToImage(cmd, staging, _atlasImage, ImageLayout.TransferDstOptimal, 1, in copy);

        var toRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _atlasImage,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in toRead);

        EndOneShot(cmd);

        _vk.DestroyBuffer(_device, staging, null);
        _vk.FreeMemory(_device, stagingMem, null);

        // Array view + nearest sampler.
        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _atlasImage,
            ViewType = ImageViewType.Type2DArray,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, layers),
        };
        if (_vk.CreateImageView(_device, in viewInfo, null, out _atlasView) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView (atlas) failed.");

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            AnisotropyEnable = false,
            MaxAnisotropy = 1,
            BorderColor = BorderColor.IntOpaqueBlack,
            UnnormalizedCoordinates = false,
            CompareEnable = false,
            CompareOp = CompareOp.Always,
            MinLod = 0,
            MaxLod = 0,
        };
        if (_vk.CreateSampler(_device, in samplerInfo, null, out _atlasSampler) != Result.Success)
            throw new InvalidOperationException("vkCreateSampler (atlas) failed.");

        // Persistent host-visible staging for animated-slice re-uploads: holds up to
        // MaxFramesInFlight+1 slices so a slice queued this frame isn't overwritten
        // while an in-flight frame may still copy from it.
        if (_animator != null && _animator.HasAnimations)
        {
            _animStagingSize = (ulong)(tile * tile * 4) * (MaxFramesInFlight + 1);
            CreateBuffer(_animStagingSize, BufferUsageFlags.TransferSrcBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _animStaging, out _animStagingMem);
            fixed (void** pp = &_animStagingMapped)
                _vk.MapMemory(_device, _animStagingMem, 0, _animStagingSize, 0, pp);
        }

        Console.WriteLine($"[Vulkan] atlas uploaded: {layers} tiles ({tile}x{tile}), missing={_atlasMissing}, animated={(_animator?.HasAnimations == true ? "yes" : "no")}");
    }

    private void CreateUniformBuffer()
    {
        ulong size = (ulong)Marshal.SizeOf<ChunkShared>();
        CreateBuffer(size, BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _uboBuffer, out _uboMemory);
        void* mapped;
        _vk.MapMemory(_device, _uboMemory, 0, size, 0, &mapped);
        _uboMapped = mapped;

        // Second UBO for the translucent pass (see field docs).
        CreateBuffer(size, BufferUsageFlags.UniformBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _uboBufferTrans, out _uboMemoryTrans);
        void* mappedT;
        _vk.MapMemory(_device, _uboMemoryTrans, 0, size, 0, &mappedT);
        _uboMappedTrans = mappedT;
    }

    private void CreateDescriptorSetLayout()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[3];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        bindings[2] = new DescriptorSetLayoutBinding
        {
            Binding = 2,                 // shadow map (sampler2D)
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 3,
            PBindings = bindings,
        };
        if (_vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out _descSetLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout failed.");
    }

    private void CreatePipelineLayout()
    {
        var setLayout = _descSetLayout;
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<PushConstants>(),
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _pipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout failed.");
    }

    // ── shaderc: GLSL -> SPIR-V at runtime ────────────────────────────────────
    private uint[] CompileGlsl(string source, Silk.NET.Shaderc.ShaderKind kind, string name)
    {
        var sc = Silk.NET.Shaderc.Shaderc.GetApi();
        var compiler = sc.CompilerInitialize();
        var options = sc.CompileOptionsInitialize();
        // Target Vulkan 1.2 so the generated SPIR-V matches the device.
        sc.CompileOptionsSetTargetEnv(options, Silk.NET.Shaderc.TargetEnv.Vulkan, (uint)Silk.NET.Shaderc.EnvVersion.Vulkan12);
        sc.CompileOptionsSetOptimizationLevel(options, Silk.NET.Shaderc.OptimizationLevel.Performance);

        var srcBytes = System.Text.Encoding.UTF8.GetBytes(source);
        Silk.NET.Shaderc.CompilationResult* result;
        fixed (byte* pSrc = srcBytes)
        {
            result = sc.CompileIntoSpv(compiler, pSrc, (nuint)srcBytes.Length, kind, name, "main", options);
        }

        var status = sc.ResultGetCompilationStatus(result);
        if (status != Silk.NET.Shaderc.CompilationStatus.Success)
        {
            string err = sc.ResultGetErrorMessageS(result);
            sc.ResultRelease(result);
            sc.CompileOptionsRelease(options);
            sc.CompilerRelease(compiler);
            throw new InvalidOperationException($"Shaderc failed to compile {name}: {err}");
        }

        nuint len = sc.ResultGetLength(result);
        byte* bytes = sc.ResultGetBytes(result);
        uint[] spirv = new uint[len / sizeof(uint)];
        fixed (uint* pSpirv = spirv)
            System.Buffer.MemoryCopy(bytes, pSpirv, (long)len, (long)len);

        sc.ResultRelease(result);
        sc.CompileOptionsRelease(options);
        sc.CompilerRelease(compiler);
        sc.Dispose();
        return spirv;
    }

    private ShaderModule CreateShaderModule(uint[] spirv)
    {
        fixed (uint* pCode = spirv)
        {
            var info = new ShaderModuleCreateInfo
            {
                SType = StructureType.ShaderModuleCreateInfo,
                CodeSize = (nuint)(spirv.Length * sizeof(uint)),
                PCode = pCode,
            };
            if (_vk.CreateShaderModule(_device, in info, null, out ShaderModule module) != Result.Success)
                throw new InvalidOperationException("vkCreateShaderModule failed.");
            return module;
        }
    }

    private void CreatePipelines()
    {
        var vertSpirv = CompileGlsl(WorldShaders.Vertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "world.vert");
        var fragSpirv = CompileGlsl(WorldShaders.Fragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "world.frag");
        var vertModule = CreateShaderModule(vertSpirv);
        var fragModule = CreateShaderModule(fragSpirv);

        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit,
                Module = vertModule,
                PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit,
                Module = fragModule,
                PName = entry,
            };

            // Vertex input: one binding, stride 28; 4 attributes.
            var binding = new VertexInputBindingDescription
            {
                Binding = 0,
                Stride = VertexStride,
                InputRate = VertexInputRate.Vertex,
            };
            var attrs = stackalloc VertexInputAttributeDescription[4];
            attrs[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };
            attrs[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 12 };
            attrs[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32Uint, Offset = 20 };
            attrs[3] = new VertexInputAttributeDescription { Location = 3, Binding = 0, Format = Format.R32Uint, Offset = 24 };

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 4,
                PVertexAttributeDescriptions = attrs,
            };

            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
                PrimitiveRestartEnable = false,
            };

            // Dynamic viewport/scissor so swapchain resizes don't need pipeline rebuilds.
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo,
                ViewportCount = 1,
                ScissorCount = 1,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo,
                DynamicStateCount = 2,
                PDynamicStates = dynStates,
            };

            // Back-face culling. The mesher emits CCW-front faces in a right-handed
            // world; our viewProj flips Y for Vulkan clip space, which inverts the
            // apparent winding — so FrontFace = Clockwise keeps outward faces visible.
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                DepthClampEnable = false,
                RasterizerDiscardEnable = false,
                PolygonMode = PolygonMode.Fill,
                LineWidth = 1.0f,
                CullMode = CullModeFlags.BackBit,
                FrontFace = FrontFace.Clockwise,
                DepthBiasEnable = false,
            };

            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                SampleShadingEnable = false,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            var depthStencilOpaque = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true,
                DepthWriteEnable = true,
                DepthCompareOp = CompareOp.Less,
                DepthBoundsTestEnable = false,
                StencilTestEnable = false,
            };

            // Opaque/cutout: no blend, write all channels.
            var noBlend = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var blendOpaque = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 1,
                PAttachments = &noBlend,
            };

            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2,
                PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencilOpaque,
                PColorBlendState = &blendOpaque,
                PDynamicState = &dynamicState,
                Layout = _pipelineLayout,
                RenderPass = _hdrRenderPass,
                Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _opaquePipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (opaque) failed.");

            // Translucent: alpha blend, depth test but no depth write.
            var depthStencilTrans = depthStencilOpaque;
            depthStencilTrans.DepthWriteEnable = false;
            var alphaBlend = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
            };
            var blendTrans = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                LogicOpEnable = false,
                AttachmentCount = 1,
                PAttachments = &alphaBlend,
            };
            var pipelineInfoTrans = pipelineInfo;
            pipelineInfoTrans.PDepthStencilState = &depthStencilTrans;
            pipelineInfoTrans.PColorBlendState = &blendTrans;
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfoTrans, null, out _translucentPipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (translucent) failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
        }
    }

    private void CreateDescriptorSet()
    {
        // (opaque + translucent) PER in-flight frame, each = 1 UBO + 2 samplers
        // (atlas + shadow). The shadow image is double-buffered, so frame f's world
        // sets sample shadow view[f] — the same image the shadow pass wrote this
        // frame, never the one a prior in-flight frame is still reading.
        const int sets = 2 * MaxFramesInFlight;
        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = sets };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = sets * 2 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
            MaxSets = sets,
        };
        if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out _descPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool failed.");

        var imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _atlasView,
            Sampler = _atlasSampler,
        };

        // The translucent set binds _uboBufferTrans; the opaque set binds _uboBuffer.
        // `frame` selects which double-buffered shadow view to sample.
        void AllocSet(Buffer ubo, int frame, out DescriptorSet set)
        {
            var setLayout = _descSetLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _descPool,
                DescriptorSetCount = 1,
                PSetLayouts = &setLayout,
            };
            if (_vk.AllocateDescriptorSets(_device, in allocInfo, out set) != Result.Success)
                throw new InvalidOperationException("vkAllocateDescriptorSets failed.");

            var bufferInfo = new DescriptorBufferInfo
            {
                Buffer = ubo, Offset = 0, Range = (ulong)Marshal.SizeOf<ChunkShared>(),
            };
            var localImage = imageInfo;
            var localShadow = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.DepthStencilReadOnlyOptimal,
                ImageView = _shadowView[frame],
                Sampler = _shadowSampler,
            };
            var localSet = set;
            var writes = stackalloc WriteDescriptorSet[3];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = localSet, DstBinding = 0,
                DstArrayElement = 0, DescriptorType = DescriptorType.UniformBuffer,
                DescriptorCount = 1, PBufferInfo = &bufferInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = localSet, DstBinding = 1,
                DstArrayElement = 0, DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1, PImageInfo = &localImage,
            };
            writes[2] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = localSet, DstBinding = 2,
                DstArrayElement = 0, DescriptorType = DescriptorType.CombinedImageSampler,
                DescriptorCount = 1, PImageInfo = &localShadow,
            };
            _vk.UpdateDescriptorSets(_device, 3, writes, 0, null);
        }

        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            AllocSet(_uboBuffer, f, out _descSetFrame[f]);
            AllocSet(_uboBufferTrans, f, out _descSetTransFrame[f]);
        }
        // Keep the legacy fields pointing at frame 0 for any non-per-frame callers.
        _descSet = _descSetFrame[0];
        _descSetTrans = _descSetTransFrame[0];
    }

    // ── sun shadow map: depth texture, render pass, framebuffer, pipeline ──────
    private void CreateShadowResources()
    {
        var sInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Nearest, AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge, AddressModeW = SamplerAddressMode.ClampToEdge,
            BorderColor = BorderColor.FloatOpaqueWhite, CompareEnable = false, MinLod = 0, MaxLod = 0,
        };
        if (_vk.CreateSampler(_device, in sInfo, null, out _shadowSampler) != Result.Success)
            throw new InvalidOperationException("vkCreateSampler (shadow) failed.");

        // depth-only render pass: clear → store, final layout = depth read-only.
        // Shared by all per-frame framebuffers (the format/layout is identical).
        var depthAttach = new AttachmentDescription
        {
            Format = DepthFormat, Samples = SampleCountFlags.Count1Bit,
            LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare,
            InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.DepthStencilReadOnlyOptimal,
        };
        var depthRef = new AttachmentReference { Attachment = 0, Layout = ImageLayout.DepthStencilAttachmentOptimal };
        var subpass = new SubpassDescription
        {
            PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 0,
            PDepthStencilAttachment = &depthRef,
        };
        var deps = stackalloc SubpassDependency[2];
        deps[0] = new SubpassDependency
        {
            SrcSubpass = Vk.SubpassExternal, DstSubpass = 0,
            SrcStageMask = PipelineStageFlags.FragmentShaderBit, DstStageMask = PipelineStageFlags.EarlyFragmentTestsBit,
            SrcAccessMask = AccessFlags.ShaderReadBit, DstAccessMask = AccessFlags.DepthStencilAttachmentWriteBit,
        };
        deps[1] = new SubpassDependency
        {
            SrcSubpass = 0, DstSubpass = Vk.SubpassExternal,
            SrcStageMask = PipelineStageFlags.LateFragmentTestsBit, DstStageMask = PipelineStageFlags.FragmentShaderBit,
            SrcAccessMask = AccessFlags.DepthStencilAttachmentWriteBit, DstAccessMask = AccessFlags.ShaderReadBit,
        };
        var rpInfo = new RenderPassCreateInfo
        {
            SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &depthAttach,
            SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 2, PDependencies = deps,
        };
        if (_vk.CreateRenderPass(_device, in rpInfo, null, out _shadowRenderPass) != Result.Success)
            throw new InvalidOperationException("vkCreateRenderPass (shadow) failed.");

        // Per-frame depth image + view + framebuffer (double-buffer the hazard away).
        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            var imageInfo = new ImageCreateInfo
            {
                SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D,
                Extent = new Extent3D(ShadowSize, ShadowSize, 1), MipLevels = 1, ArrayLayers = 1,
                Format = DepthFormat, Tiling = ImageTiling.Optimal, InitialLayout = ImageLayout.Undefined,
                Usage = ImageUsageFlags.DepthStencilAttachmentBit | ImageUsageFlags.SampledBit,
                Samples = SampleCountFlags.Count1Bit, SharingMode = SharingMode.Exclusive,
            };
            if (_vk.CreateImage(_device, in imageInfo, null, out _shadowImage[f]) != Result.Success)
                throw new InvalidOperationException("vkCreateImage (shadow) failed.");
            _vk.GetImageMemoryRequirements(_device, _shadowImage[f], out var memReq);
            var alloc = new MemoryAllocateInfo
            {
                SType = StructureType.MemoryAllocateInfo, AllocationSize = memReq.Size,
                MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
            };
            if (_vk.AllocateMemory(_device, in alloc, null, out _shadowMemory[f]) != Result.Success)
                throw new InvalidOperationException("vkAllocateMemory (shadow) failed.");
            _vk.BindImageMemory(_device, _shadowImage[f], _shadowMemory[f], 0);

            var viewInfo = new ImageViewCreateInfo
            {
                SType = StructureType.ImageViewCreateInfo, Image = _shadowImage[f], ViewType = ImageViewType.Type2D,
                Format = DepthFormat,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1),
            };
            if (_vk.CreateImageView(_device, in viewInfo, null, out _shadowView[f]) != Result.Success)
                throw new InvalidOperationException("vkCreateImageView (shadow) failed.");

            // transition to depth-read-only once so the world descriptor set's binding-2
            // image is in a valid sampled layout even on frames where shadows are off
            {
                var c = BeginOneShot();
                var range = new ImageSubresourceRange(ImageAspectFlags.DepthBit, 0, 1, 0, 1);
                var toRead = new ImageMemoryBarrier
                {
                    SType = StructureType.ImageMemoryBarrier, OldLayout = ImageLayout.Undefined,
                    NewLayout = ImageLayout.DepthStencilReadOnlyOptimal, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
                    DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = _shadowImage[f], SubresourceRange = range,
                    SrcAccessMask = 0, DstAccessMask = AccessFlags.ShaderReadBit,
                };
                _vk.CmdPipelineBarrier(c, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit,
                    0, 0, null, 0, null, 1, in toRead);
                EndOneShot(c);
            }

            var view = _shadowView[f];
            var fbInfo = new FramebufferCreateInfo
            {
                SType = StructureType.FramebufferCreateInfo, RenderPass = _shadowRenderPass,
                AttachmentCount = 1, PAttachments = &view, Width = ShadowSize, Height = ShadowSize, Layers = 1,
            };
            if (_vk.CreateFramebuffer(_device, in fbInfo, null, out _shadowFramebuffer[f]) != Result.Success)
                throw new InvalidOperationException("vkCreateFramebuffer (shadow) failed.");
        }

        // shadow-only descriptor + pipeline layout: just the UBO (binding 0). The
        // shadow pass never samples the atlas or the shadow map, so we avoid binding
        // the shadow image as a sampled resource while it's the depth attachment.
        ulong uboSize = (ulong)Marshal.SizeOf<ChunkShared>();
        var b0 = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit,
        };
        var dlInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 1, PBindings = &b0,
        };
        if (_vk.CreateDescriptorSetLayout(_device, in dlInfo, null, out _shadowDescLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout (shadow) failed.");
        var sLayout = _shadowDescLayout;
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit, Offset = 0, Size = (uint)Marshal.SizeOf<PushConstants>(),
        };
        var plInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &sLayout,
            PushConstantRangeCount = 1, PPushConstantRanges = &pushRange,
        };
        if (_vk.CreatePipelineLayout(_device, in plInfo, null, out _shadowPipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout (shadow) failed.");

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = MaxFramesInFlight };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &poolSize, MaxSets = MaxFramesInFlight,
        };
        DescriptorPool shadowPool;
        if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out shadowPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool (shadow) failed.");
        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            CreateBuffer(uboSize, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _shadowUbo[f], out _shadowUboMem[f]);
            void* m; _vk.MapMemory(_device, _shadowUboMem[f], 0, uboSize, 0, &m); _shadowUboMapped[f] = m;
            var aiShadow = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = shadowPool,
                DescriptorSetCount = 1, PSetLayouts = &sLayout,
            };
            if (_vk.AllocateDescriptorSets(_device, in aiShadow, out _shadowDescSet[f]) != Result.Success)
                throw new InvalidOperationException("vkAllocateDescriptorSets (shadow) failed.");
            var bi = new DescriptorBufferInfo { Buffer = _shadowUbo[f], Offset = 0, Range = uboSize };
            var sset = _shadowDescSet[f];
            var sw = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = sset, DstBinding = 0,
                DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bi,
            };
            _vk.UpdateDescriptorSets(_device, 1, in sw, 0, null);
        }
        _shadowReady = true;
    }

    private void CreateShadowPipeline()
    {
        var vsMod = CreateShaderModule(CompileGlsl(WorldShaders.ShadowVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "shadow.vert"));
        var fsMod = CreateShaderModule(CompileGlsl(WorldShaders.ShadowFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "shadow.frag"));
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            // same chunk vertex layout (pos f3, uv f2, A uint, B uint), stride 28
            var binding = new VertexInputBindingDescription { Binding = 0, Stride = VertexStride, InputRate = VertexInputRate.Vertex };
            var attrs = stackalloc VertexInputAttributeDescription[4]
            {
                new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 12 },
                new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32Uint, Offset = 20 },
                new VertexInputAttributeDescription { Location = 3, Binding = 0, Format = Format.R32Uint, Offset = 24 },
            };
            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 4, PVertexAttributeDescriptions = attrs,
            };
            var ia = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates,
            };
            // front-face cull + depth bias to fight shadow acne / peter-panning,
            // mirroring the Swift renderer's setDepthBias(6, slopeScale: 8, clamp: 0.02)
            // and front-face rendering. The slope bias is CLAMPED so it stays finite.
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill,
                LineWidth = 1.0f, CullMode = CullModeFlags.FrontBit, FrontFace = FrontFace.Clockwise,
                DepthBiasEnable = true, DepthBiasConstantFactor = 6.0f, DepthBiasSlopeFactor = 8.0f, DepthBiasClamp = 0.02f,
            };
            var msaa = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            var depth = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.LessOrEqual,
            };
            // no color attachment in the shadow pass
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 0,
            };
            var stages = stackalloc PipelineShaderStageCreateInfo[2]
            {
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsMod, PName = entry,
                },
                new PipelineShaderStageCreateInfo
                {
                    SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fsMod, PName = entry,
                },
            };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &viewportState,
                PRasterizationState = &raster, PMultisampleState = &msaa, PDepthStencilState = &depth,
                PColorBlendState = &blend, PDynamicState = &dynamicState,
                Layout = _shadowPipelineLayout, RenderPass = _shadowRenderPass, Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out _shadowPipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (shadow) failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vsMod, null);
            _vk.DestroyShaderModule(_device, fsMod, null);
        }
    }

    /// Render the scene depth from the sun direction into the shadow map. Recorded
    /// at the start of EndFrame, BEFORE the main render pass (separate render pass).
    private void RecordShadowPass(CommandBuffer cmd)
    {
        if (!_shadowReady || !_frameShadowsOn || _frameHost == null) return;

        // shadow UBO: shadowMat in the viewProj slot (shadow_vs reads viewProj)
        var su = new ChunkShared { viewProj = _frameShadowMat, shadowMat = _frameShadowMat };
        System.Buffer.MemoryCopy(&su, _shadowUboMapped[_currentFrame], (long)Marshal.SizeOf<ChunkShared>(), (long)Marshal.SizeOf<ChunkShared>());

        var clear = new ClearValue { DepthStencil = new ClearDepthStencilValue(1.0f, 0) };
        var rpBegin = new RenderPassBeginInfo
        {
            SType = StructureType.RenderPassBeginInfo, RenderPass = _shadowRenderPass,
            Framebuffer = _shadowFramebuffer[_currentFrame],   // double-buffered: write frame N's own image
            RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D(ShadowSize, ShadowSize)),
            ClearValueCount = 1, PClearValues = &clear,
        };
        _vk.CmdBeginRenderPass(cmd, in rpBegin, SubpassContents.Inline);
        var viewport = new Viewport(0, 0, ShadowSize, ShadowSize, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), new Extent2D(ShadowSize, ShadowSize));
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _shadowPipeline);
        var set = _shadowDescSet[_currentFrame];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _shadowPipelineLayout, 0, 1, in set, 0, null);

        // draw the opaque layer of every nearby section (camera-relative origin),
        // using the same section-origin math as the world pass.
        //
        // Two-stage cull keeps the shadow workload bounded (avoids a TDR-class GPU
        // hang during the heavy streaming spike): (1) a sun-aligned radius bound
        // (matches the Swift renderer's `abs(ox+8) > r+24` test), then (2) a
        // light-space frustum cull against shadowMat so we only rasterize sections
        // that actually land inside the shadow map.
        var cam = _frameCam;
        const float r = 72f;
        var shadowVp = _frameShadowMat;
        foreach (var kv in _sectionCache)
        {
            var layers = kv.Value;
            if (layers.opaque == null) continue;
            var (cx, sy, cz) = kv.Key;
            if (!_frameHost.sections.TryGetValue(kv.Key, out var sm)) continue;
            float ox = (float)(cx * 16 - cam.x);
            float oy = (float)(sm.minY + sy * 16 - cam.y);
            float oz = (float)(cz * 16 - cam.z);
            if (Math.Abs(ox + 8) > r + 24 || Math.Abs(oz + 8) > r + 24) continue;
            if (!FrustumCull.SectionVisible(new Vector3(ox, oy, oz), shadowVp)) continue;
            var push = new PushConstants { origin = new Vector4(ox, oy, oz, 0) };
            _vk.CmdPushConstants(cmd, _shadowPipelineLayout, ShaderStageFlags.VertexBit, 0, (uint)Marshal.SizeOf<PushConstants>(), &push);
            DrawSectionGpu(cmd, layers.opaque);
        }
        _vk.CmdEndRenderPass(cmd);
    }

    // ── camera / matrices (camera-relative; Vulkan clip: +Y down, z in [0,1]) ─
    private Matrix4x4 BuildViewProj(in PebbleCore.CamState cam)
    {
        float aspect = _swapchainExtent.Height == 0 ? 1f
            : (float)_swapchainExtent.Width / _swapchainExtent.Height;
        float fovY = (float)(cam.fov * Math.PI / 180.0);
        const float near = 0.05f, far = 512f;

        // Right-handed perspective mapping z to [0,1] (Vulkan), with Y flipped.
        float f = 1.0f / MathF.Tan(fovY * 0.5f);
        var proj = new Matrix4x4(
            f / aspect, 0, 0, 0,
            0, -f, 0, 0,            // negate row for Y → flip for Vulkan +Y-down clip space
            0, 0, far / (near - far), -1,
            0, 0, (near * far) / (near - far), 0);

        double yaw = cam.yaw, pitch = cam.pitch;
        var dir = new Vector3(
            (float)(Math.Cos(pitch) * -Math.Sin(yaw)),
            (float)Math.Sin(-pitch),
            (float)(Math.Cos(pitch) * Math.Cos(yaw)));
        var view = Matrix4x4.CreateLookAt(Vector3.Zero, dir, new Vector3(0, 1, 0));

        // System.Numerics matrices are row-vector / row-major (v * M). The shader
        // expects column-major M * v (u.viewProj * vec4(rel,1)). Multiplying in
        // the order (view * proj) here and uploading the struct as-is gives the
        // GLSL std140 mat4 the transpose it needs for column-major M*v semantics.
        return Matrix4x4.Multiply(view, proj);
    }

    // ── per-section GPU upload (cache; re-upload only on mesh change) ─────────
    private SectionGpu? UploadLayer(in PebbleCore.MeshLayer layer)
    {
        if (layer.idx == null || layer.idx.Length == 0 || layer.data == null || layer.data.Length == 0)
            return null;

        var gpu = new SectionGpu { indexCount = (uint)layer.idx.Length };

        ulong vSize = (ulong)layer.data.Length * sizeof(uint);
        ulong iSize = (ulong)layer.idx.Length * sizeof(uint);
        gpu.vbufSize = vSize;
        gpu.ibufSize = iSize;

        CreateBuffer(vSize, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out gpu.vbuf, out gpu.vmem);
        CreateBuffer(iSize, BufferUsageFlags.IndexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out gpu.ibuf, out gpu.imem);

        void* vmapped;
        _vk.MapMemory(_device, gpu.vmem, 0, vSize, 0, &vmapped);
        layer.data.AsSpan().CopyTo(new Span<uint>(vmapped, layer.data.Length));
        _vk.UnmapMemory(_device, gpu.vmem);

        void* imapped;
        _vk.MapMemory(_device, gpu.imem, 0, iSize, 0, &imapped);
        layer.idx.AsSpan().CopyTo(new Span<uint>(imapped, layer.idx.Length));
        _vk.UnmapMemory(_device, gpu.imem);

        return gpu;
    }

    private void FreeSectionGpu(SectionGpu? gpu)
    {
        if (gpu == null) return;
        if (gpu.vbuf.Handle != 0) _vk.DestroyBuffer(_device, gpu.vbuf, null);
        if (gpu.vmem.Handle != 0) _vk.FreeMemory(_device, gpu.vmem, null);
        if (gpu.ibuf.Handle != 0) _vk.DestroyBuffer(_device, gpu.ibuf, null);
        if (gpu.imem.Handle != 0) _vk.FreeMemory(_device, gpu.imem, null);
    }

    /// Reconcile the GPU section cache with the live host.sections set: upload
    /// new/changed meshes, drop sections that disappeared. Returns the set of
    /// live keys so the draw loop can iterate the cache directly.
    private void SyncSections(HostBridge host)
    {
        // Snapshot the live sections (ConcurrentDictionary enumeration is safe).
        var live = new HashSet<(int, int, int)>();
        foreach (var kv in host.sections)
        {
            var key = kv.Key;
            var sm = kv.Value;
            live.Add(key);

            if (_sectionCache.TryGetValue(key, out var existing) && ReferenceEquals(existing.meshRef, sm))
                continue; // unchanged

            // New or changed: free old GPU buffers, re-upload.
            if (existing != null)
            {
                FreeSectionGpu(existing.opaque);
                FreeSectionGpu(existing.cutout);
                FreeSectionGpu(existing.translucent);
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

        // Remove cache entries no longer live.
        if (_sectionCache.Count > live.Count)
        {
            var dead = new List<(int, int, int)>();
            foreach (var key in _sectionCache.Keys)
                if (!live.Contains(key)) dead.Add(key);
            foreach (var key in dead)
            {
                var l = _sectionCache[key];
                FreeSectionGpu(l.opaque);
                FreeSectionGpu(l.cutout);
                FreeSectionGpu(l.translucent);
                _sectionCache.Remove(key);
            }
        }
    }

    public void RenderSky(in SkyState sky, PebbleCore.CamState cam, double timeSeconds)
    {
        // Capture only; the sky/celestial draws are recorded inside EndFrame's
        // render pass (RecordSky), BEFORE the world, so they sit behind it.
        _frameSky = sky;
        _frameCam = cam;
        _frameTime = timeSeconds;
        _haveSky = true;

        // Build the sun-direction shadow matrix for this frame (Vulkan flips Y).
        _frameShadowsOn = _shadowsEnabled && sky.ShadowsOn;
        if (_frameShadowsOn)
            _frameShadowMat = SkyState.BuildShadowMatrix(sky.SunDir, cam, ShadowSize, flipY: true);
    }

    public void RenderWorld(HostBridge host, PebbleCore.CamState cam, double timeSeconds)
    {
        // Capture for the EndFrame recording pass (which owns the render pass).
        _frameHost = host;
        _frameCam = cam;
        _frameTime = timeSeconds;
        _haveWorldFrame = true;
        // Clear to the frame's fog/horizon color (the sky pass repaints every pixel,
        // but unfilled fog-distance fragments blend toward this). Falls back to the
        // classic flat sky tint if RenderSky wasn't called this frame.
        _clearValue = _haveSky
            ? new ClearColorValue(_frameSky.Fog.X, _frameSky.Fog.Y, _frameSky.Fog.Z, 1f)
            : new ClearColorValue(0.62f, 0.74f, 1.0f, 1f);
    }

    /// Record the world draw into the active render pass (called from EndFrame).
    private void RecordWorld(CommandBuffer cmd)
    {
        if (!_haveWorldFrame || _frameHost == null) return;

        // Lazy first-use init: the atlas tile list is only complete once the
        // engine has registered all blocks (after backend.Initialize).
        if (!_worldReady)
            InitWorldRenderer();
        if (!_worldReady) return;

        // Normally SyncSections already ran at EndFrame start (before the shadow pass).
        // Guard against a double-run; only sync here if it hasn't happened this frame.
        if (!_sectionsSynced)
            SyncSections(_frameHost);

        var cam = _frameCam;

        // Update the shared uniform once for the whole frame.
        //
        // The mapped UBO is shared across all draws in this command buffer, so we
        // can't safely patch it between draws (a mid-command-buffer CPU write is
        // not synchronized against in-flight GPU reads). Instead we set a single
        // alpha test of 0.5 that is correct for the cutout layer AND harmless for
        // opaque tiles (whose atlas alpha is 1.0, so 0.5 never discards them).
        //
        // Day-night lighting + fog come from the frame's SkyState (cached by
        // RenderSky), so terrain darkens at night and fades into the matching
        // horizon color. Falls back to full daylight if RenderSky was skipped.
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
            light = new Vector4(dayLight, 0.0f, 0.1f, shadowsOn),  // dayLight, gamma, ambient, shadowsOn
            fogColor = new Vector4(fogCol, 1f),
            misc = new Vector4((float)_frameTime, 0, 0, shadowTexel),  // time, packFluidDamp, ultraOn, shadowTexel
            fog = new Vector4(fogStart, fogEnd, 0.5f, 1f),     // start, end, alphaTest, globalAlpha
        };
        System.Buffer.MemoryCopy(&ubo, _uboMapped, Marshal.SizeOf<ChunkShared>(), Marshal.SizeOf<ChunkShared>());

        // Viewport + scissor (dynamic).
        var viewport = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);

        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _opaquePipeline);
        // Per-frame set: samples this frame's double-buffered shadow image[_currentFrame].
        var descSet = _descSetFrame[_currentFrame];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in descSet, 0, null);

        // Same viewProj uploaded to the GPU this frame, used to cull off-screen
        // sections so we don't draw all ~5400 every frame (near the GPU TDR limit).
        var viewProj = ubo.viewProj;
        int totalSections = 0, drawnSections = 0;
        bool anyTranslucent = false;

        foreach (var kv in _sectionCache)
        {
            var (cx, sy, cz) = kv.Key;
            // sectionWorld = (cx*16, minY + sy*16, cz*16). minY comes from the live mesh.
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

            var push = new PushConstants { origin = new Vector4(ox, oy, oz, 0) };
            _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0,
                (uint)Marshal.SizeOf<PushConstants>(), &push);

            DrawSectionGpu(cmd, kv.Value.opaque);
            DrawSectionGpu(cmd, kv.Value.cutout);
            if (kv.Value.translucent != null) anyTranslucent = true;
        }

        // Translucent (water/glass/ice) pass: depth-test ON, depth-write OFF,
        // alpha-blended, drawn AFTER all opaque/cutout (and entities, which were
        // recorded earlier this pass). Same chunk shader; globalAlpha 0.82 so
        // water reads as see-through. Frustum-culled like the opaque pass.
        if (anyTranslucent)
        {
            var tubo = ubo;
            tubo.fog.Z = 0f;      // no alpha test on translucent
            tubo.fog.W = 0.82f;   // globalAlpha
            System.Buffer.MemoryCopy(&tubo, _uboMappedTrans, Marshal.SizeOf<ChunkShared>(), Marshal.SizeOf<ChunkShared>());
            var transSet = _descSetTransFrame[_currentFrame];
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _translucentPipeline);
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in transSet, 0, null);
            foreach (var kv in _sectionCache)
            {
                if (kv.Value.translucent == null) continue;
                var (cx, sy, cz) = kv.Key;
                if (!_frameHost.sections.TryGetValue(kv.Key, out var sm)) continue;
                float ox = (float)(cx * 16 - cam.x);
                float oy = (float)(sm.minY + sy * 16 - cam.y);
                float oz = (float)(cz * 16 - cam.z);
                if (!FrustumCull.SectionVisible(new Vector3(ox, oy, oz), viewProj)) continue;
                var push = new PushConstants { origin = new Vector4(ox, oy, oz, 0) };
                _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0,
                    (uint)Marshal.SizeOf<PushConstants>(), &push);
                DrawSectionGpu(cmd, kv.Value.translucent);
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
        int trans = 0;
        foreach (var l in _sectionCache.Values) if (l.translucent != null) trans++;
        Console.WriteLine($"[Vulkan] sections drawn {drawn}/{total} (frustum-culled {total - drawn}), translucent sections={trans}");
    }

    private void DrawSectionGpu(CommandBuffer cmd, SectionGpu? gpu)
    {
        if (gpu == null || gpu.indexCount == 0) return;
        var vbuf = gpu.vbuf;
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, in vbuf, in offset);
        _vk.CmdBindIndexBuffer(cmd, gpu.ibuf, 0, IndexType.Uint32);
        _vk.CmdDrawIndexed(cmd, gpu.indexCount, 1, 0, 0, 0);
    }

    // ═══════════════════════════════════════════════════════════════ Atmosphere

    private void InitSkyRenderer()
    {
        // One UBO descriptor at binding 0 (vertex+fragment).
        var binding0 = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBuffer,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding0,
        };
        if (_vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out _skyDescLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout (sky) failed.");

        var setLayout = _skyDescLayout;
        var plInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
        };
        if (_vk.CreatePipelineLayout(_device, in plInfo, null, out _skyPipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout (sky) failed.");

        CreateSkyPipelines();
        CreateSkyDescriptorSets();
        InitSkyExtras();
        _skyReady = true;
    }

    // ── stars + clouds: resources, pipelines and descriptor sets ──────────────
    private void InitSkyExtras()
    {
        // cloud descriptor layout: UBO@0 (vs+fs) + combined sampler@1 (fs)
        var b0 = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        var b1 = new DescriptorSetLayoutBinding
        {
            Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var binds = stackalloc DescriptorSetLayoutBinding[2] { b0, b1 };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = binds,
        };
        if (_vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out _cloudDescLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout (cloud) failed.");
        var cloudSet = _cloudDescLayout;
        var cloudPl = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &cloudSet,
        };
        if (_vk.CreatePipelineLayout(_device, in cloudPl, null, out _cloudPipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout (cloud) failed.");

        BuildCloudTexture();
        BuildStarBuffer();
        CreateSkyExtraPipelines();
        CreateSkyExtraDescriptorSets();
        _skyExtraReady = true;
    }

    private void BuildCloudTexture()
    {
        int size = SkyAssets.CloudSize;
        var px = SkyAssets.BuildCloudTexture();
        int bytes = size * size * 4;
        CreateBuffer((ulong)bytes, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer staging, out DeviceMemory stagingMem);
        void* mapped;
        _vk.MapMemory(_device, stagingMem, 0, (ulong)bytes, 0, &mapped);
        px.AsSpan(0, bytes).CopyTo(new Span<byte>(mapped, bytes));
        _vk.UnmapMemory(_device, stagingMem);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo, ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)size, (uint)size, 1), MipLevels = 1, ArrayLayers = 1,
            Format = Format.R8G8B8A8Unorm, Tiling = ImageTiling.Optimal, InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit, SharingMode = SharingMode.Exclusive,
        };
        if (_vk.CreateImage(_device, in imageInfo, null, out _cloudImage) != Result.Success)
            throw new InvalidOperationException("vkCreateImage (cloud) failed.");
        _vk.GetImageMemoryRequirements(_device, _cloudImage, out var memReq);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo, AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        if (_vk.AllocateMemory(_device, in alloc, null, out _cloudMemory) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory (cloud) failed.");
        _vk.BindImageMemory(_device, _cloudImage, _cloudMemory, 0);

        var cmd = BeginOneShot();
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var toDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.TransferDstOptimal, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = _cloudImage, SubresourceRange = range,
            SrcAccessMask = 0, DstAccessMask = AccessFlags.TransferWriteBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, in toDst);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0), ImageExtent = new Extent3D((uint)size, (uint)size, 1),
        };
        _vk.CmdCopyBufferToImage(cmd, staging, _cloudImage, ImageLayout.TransferDstOptimal, 1, in copy);
        var toRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier, OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal, SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored, Image = _cloudImage, SubresourceRange = range,
            SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in toRead);
        EndOneShot(cmd);
        _vk.DestroyBuffer(_device, staging, null);
        _vk.FreeMemory(_device, stagingMem, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = _cloudImage,
            ViewType = ImageViewType.Type2D, Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        if (_vk.CreateImageView(_device, in viewInfo, null, out _cloudView) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView (cloud) failed.");
        var sInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo, MagFilter = Filter.Linear, MinFilter = Filter.Linear,
            MipmapMode = SamplerMipmapMode.Linear, AddressModeU = SamplerAddressMode.Repeat,
            AddressModeV = SamplerAddressMode.Repeat, AddressModeW = SamplerAddressMode.Repeat,
            MinLod = 0, MaxLod = 0,
        };
        if (_vk.CreateSampler(_device, in sInfo, null, out _cloudSampler) != Result.Success)
            throw new InvalidOperationException("vkCreateSampler (cloud) failed.");
    }

    private void BuildStarBuffer()
    {
        var data = SkyAssets.BuildStars();
        _starsCount = SkyAssets.StarCount;
        ulong size = (ulong)data.Length * sizeof(float);
        CreateBuffer(size, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _starsVbuf, out _starsVmem);
        void* mapped;
        _vk.MapMemory(_device, _starsVmem, 0, size, 0, &mapped);
        data.AsSpan().CopyTo(new Span<float>(mapped, data.Length));
        _vk.UnmapMemory(_device, _starsVmem);
    }

    private void CreateSkyExtraPipelines()
    {
        var starVs = CreateShaderModule(CompileGlsl(SkyShaders.StarsVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "stars.vert"));
        var starFs = CreateShaderModule(CompileGlsl(SkyShaders.StarsFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "stars.frag"));
        var cloudVs = CreateShaderModule(CompileGlsl(SkyShaders.CloudVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "cloud.vert"));
        var cloudFs = CreateShaderModule(CompileGlsl(SkyShaders.CloudFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "cloud.frag"));
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            // stars: per-vertex pos f3 @0 + mag f1 @12, stride 16
            var starBind = new VertexInputBindingDescription { Binding = 0, Stride = 16, InputRate = VertexInputRate.Vertex };
            var starAttrs = stackalloc VertexInputAttributeDescription[2]
            {
                new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 },
                new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32Sfloat, Offset = 12 },
            };
            var starVI = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1, PVertexBindingDescriptions = &starBind,
                VertexAttributeDescriptionCount = 2, PVertexAttributeDescriptions = starAttrs,
            };
            var emptyVI = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };

            var iaPoints = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.PointList,
            };
            var iaTris = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates,
            };
            var raster = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill,
                LineWidth = 1.0f, CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise,
            };
            var msaa = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            // stars: depth always (far plane), additive blend
            var depthStars = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false, DepthWriteEnable = false, DepthCompareOp = CompareOp.Always,
            };
            // clouds: depth test LessEqual read-only (terrain occludes), alpha blend
            var depthCloud = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true, DepthWriteEnable = false, DepthCompareOp = CompareOp.LessOrEqual,
            };
            var additive = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.One, ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.One, AlphaBlendOp = BlendOp.Add,
            };
            var blendAdd = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &additive,
            };
            var alpha = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add,
            };
            var blendAlpha = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &alpha,
            };

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            void SetStages(ShaderModule vs, ShaderModule fs)
            {
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vs, PName = entry };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fs, PName = entry };
            }

            // stars pipeline (sky UBO-only layout)
            SetStages(starVs, starFs);
            var starInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &starVI, PInputAssemblyState = &iaPoints, PViewportState = &viewportState,
                PRasterizationState = &raster, PMultisampleState = &msaa, PDepthStencilState = &depthStars,
                PColorBlendState = &blendAdd, PDynamicState = &dynamicState,
                Layout = _skyPipelineLayout, RenderPass = _hdrRenderPass, Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in starInfo, null, out _starsPipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (stars) failed.");

            // cloud pipeline (UBO + sampler layout)
            SetStages(cloudVs, cloudFs);
            var cloudInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &emptyVI, PInputAssemblyState = &iaTris, PViewportState = &viewportState,
                PRasterizationState = &raster, PMultisampleState = &msaa, PDepthStencilState = &depthCloud,
                PColorBlendState = &blendAlpha, PDynamicState = &dynamicState,
                Layout = _cloudPipelineLayout, RenderPass = _hdrRenderPass, Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in cloudInfo, null, out _cloudPipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (cloud) failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, starVs, null);
            _vk.DestroyShaderModule(_device, starFs, null);
            _vk.DestroyShaderModule(_device, cloudVs, null);
            _vk.DestroyShaderModule(_device, cloudFs, null);
        }
    }

    private void CreateSkyExtraDescriptorSets()
    {
        ulong starsSize = (ulong)Marshal.SizeOf<StarsUniform>();
        ulong cloudSize = (ulong)Marshal.SizeOf<CloudUniform>();

        var poolSizes = stackalloc DescriptorPoolSize[2]
        {
            new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)(MaxFramesInFlight * 2) },
            new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)MaxFramesInFlight },
        };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 2, PPoolSizes = poolSizes,
            MaxSets = (uint)(MaxFramesInFlight * 2),
        };
        if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out _skyExtraDescPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool (skyextra) failed.");

        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            // stars set (sky UBO-only layout)
            CreateBuffer(starsSize, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _starsUbo[f], out _starsUboMem[f]);
            void* sm; _vk.MapMemory(_device, _starsUboMem[f], 0, starsSize, 0, &sm); _starsUboMapped[f] = sm;
            var skyL = _skyDescLayout;
            var aiStars = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _skyExtraDescPool,
                DescriptorSetCount = 1, PSetLayouts = &skyL,
            };
            if (_vk.AllocateDescriptorSets(_device, in aiStars, out _starsDescSet[f]) != Result.Success)
                throw new InvalidOperationException("vkAllocateDescriptorSets (stars) failed.");
            var sBuf = new DescriptorBufferInfo { Buffer = _starsUbo[f], Offset = 0, Range = starsSize };
            var sSet = _starsDescSet[f];
            var sWrite = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = sSet, DstBinding = 0, DstArrayElement = 0,
                DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &sBuf,
            };
            _vk.UpdateDescriptorSets(_device, 1, in sWrite, 0, null);

            // cloud set (UBO + sampler layout)
            CreateBuffer(cloudSize, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _cloudUbo[f], out _cloudUboMem[f]);
            void* cm; _vk.MapMemory(_device, _cloudUboMem[f], 0, cloudSize, 0, &cm); _cloudUboMapped[f] = cm;
            var cloudL = _cloudDescLayout;
            var aiCloud = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _skyExtraDescPool,
                DescriptorSetCount = 1, PSetLayouts = &cloudL,
            };
            if (_vk.AllocateDescriptorSets(_device, in aiCloud, out _cloudDescSet[f]) != Result.Success)
                throw new InvalidOperationException("vkAllocateDescriptorSets (cloud) failed.");
            var cBuf = new DescriptorBufferInfo { Buffer = _cloudUbo[f], Offset = 0, Range = cloudSize };
            var cImg = new DescriptorImageInfo
            {
                ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _cloudView, Sampler = _cloudSampler,
            };
            var cSet = _cloudDescSet[f];
            var cWrites = stackalloc WriteDescriptorSet[2];
            cWrites[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = cSet, DstBinding = 0, DstArrayElement = 0,
                DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &cBuf,
            };
            cWrites[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = cSet, DstBinding = 1, DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &cImg,
            };
            _vk.UpdateDescriptorSets(_device, 2, cWrites, 0, null);
        }
    }

    /// Stars: drawn just after the sky gradient (depth far), at night.
    private void RecordStars(CommandBuffer cmd, Matrix4x4 viewProj)
    {
        if (!_skyExtraReady || _frameSky.VoidSky) return;
        if (_frameCam.underwater || _frameCam.underLava) return;
        float alpha = _frameSky.StarAlpha;
        if (alpha <= 0.01f) return;

        var su = new StarsUniform
        {
            viewProj = viewProj,
            paramsv = new Vector4((float)_frameTime, alpha, 0, 0),
        };
        System.Buffer.MemoryCopy(&su, _starsUboMapped[_currentFrame], Marshal.SizeOf<StarsUniform>(), Marshal.SizeOf<StarsUniform>());
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _starsPipeline);
        var set = _starsDescSet[_currentFrame];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _skyPipelineLayout, 0, 1, in set, 0, null);
        var vb = _starsVbuf; ulong off = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, in vb, in off);
        _vk.CmdDraw(cmd, (uint)_starsCount, 1, 0, 0);
    }

    /// Clouds: drawn after the world (depth-read) in the overworld, daytime.
    private void RecordClouds(CommandBuffer cmd)
    {
        if (!_skyExtraReady || !_haveSky || _frameSky.VoidSky) return;
        if (_frameCam.underwater || _frameCam.underLava) return;
        var cam = _frameCam;
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
        System.Buffer.MemoryCopy(&cu, _cloudUboMapped[_currentFrame], Marshal.SizeOf<CloudUniform>(), Marshal.SizeOf<CloudUniform>());

        var viewport = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);

        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _cloudPipeline);
        var set = _cloudDescSet[_currentFrame];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _cloudPipelineLayout, 0, 1, in set, 0, null);
        _vk.CmdDraw(cmd, 6, 1, 0, 0);
    }

    // ── post chain: ultra (SSAO + volumetrics) -> bloom, between the HDR scene pass
    // and the swapchain composite. Each fullscreen pass is its own _postColorPass
    // render pass into a bloom/ultra framebuffer (the render pass handles the
    // dontCare->store->shaderRead layout). The composite is recorded separately by
    // RecordComposite into the open swapchain pass.
    private void RecordPost(CommandBuffer cmd)
    {
        if (!_postReady) return;
        int f = _currentFrame;
        bool ultraOn = _ultraEnabled;

        void Fullscreen(RenderPass pass, Framebuffer fb, int w, int h, Pipeline pipeline,
            PipelineLayout layout, DescriptorSet set)
        {
            var begin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo, RenderPass = pass, Framebuffer = fb,
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h)),
                ClearValueCount = 0, PClearValues = null,
            };
            _vk.CmdBeginRenderPass(cmd, in begin, SubpassContents.Inline);
            var vp = new Viewport(0, 0, w, h, 0f, 1f);
            _vk.CmdSetViewport(cmd, 0, 1, in vp);
            var sc = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)w, (uint)h));
            _vk.CmdSetScissor(cmd, 0, 1, in sc);
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, pipeline);
            var s = set;
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, layout, 0, 1, in s, 0, null);
            _vk.CmdDraw(cmd, 3, 1, 0, 0);
            _vk.CmdEndRenderPass(cmd);
        }

        void SetBlurDir(int idx, float dx, float dy)
        {
            var c = new CompositeUniform { tint = new Vector4(dx, dy, 0, 0) };
            System.Buffer.MemoryCopy(&c, _blurUboMapped[f * 4 + idx], (long)Marshal.SizeOf<CompositeUniform>(), (long)Marshal.SizeOf<CompositeUniform>());
        }

        // ── ultra: half-res SSAO (alpha) + volumetric light (rgb), then 1 blur ──
        if (ultraOn)
        {
            var cam = _frameCam;
            var viewProj = BuildViewProj(cam);
            Matrix4x4.Invert(viewProj, out var invViewProj);
            float far = Math.Max(256f, _frameSky.FogEnd / 0.95f);
            var uu = new UltraUniform
            {
                invViewProj = invViewProj,
                viewProj = viewProj,
                shadowMat = _frameShadowsOn ? _frameShadowMat : Matrix4x4.Identity,
                sunDir = new Vector4(_frameSky.SunDir, _frameSky.DayLight),
                paramsv = new Vector4((float)_frameTime, far, (_frameShadowsOn && !cam.underwater) ? 1f : 0f, cam.underwater ? 1f : 0f),
                fogColor = new Vector4(_frameSky.Fog, _frameSky.FogEnd),
                texel = new Vector4(1f / _ultraW, 1f / _ultraH, 0, 0),
            };
            System.Buffer.MemoryCopy(&uu, _ultraUboMapped[f], (long)Marshal.SizeOf<UltraUniform>(), (long)Marshal.SizeOf<UltraUniform>());
            Fullscreen(_postColorPass, _ultraFbA[f], _ultraW, _ultraH, _ultraPipeline, _ultraPipeLayout, _ultraSet[f]);
            // separable blur ultraA -> ultraB (H) -> ultraA (V), preserving alpha.
            SetBlurDir(2, 1f / _ultraW, 0f);
            SetBlurDir(3, 0f, 1f / _ultraH);
            Fullscreen(_postColorPass, _ultraFbB[f], _ultraW, _ultraH, _ultraBlurPipeline, _postOneTexPipeLayout, _ultraBlurSetA[f]);
            Fullscreen(_postColorPass, _ultraFbA[f], _ultraW, _ultraH, _ultraBlurPipeline, _postOneTexPipeLayout, _ultraBlurSetB[f]);
        }
        else
        {
            // Ultra off: still run an empty pass so ultraA lands in SHADER_READ_ONLY
            // (the composite binds it unconditionally; its dynamic branch ignores the
            // content). loadOp=dontCare + final-layout transition makes it valid.
            var begin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo, RenderPass = _postColorPass, Framebuffer = _ultraFbA[f],
                RenderArea = new Rect2D(new Offset2D(0, 0), new Extent2D((uint)_ultraW, (uint)_ultraH)),
                ClearValueCount = 0, PClearValues = null,
            };
            _vk.CmdBeginRenderPass(cmd, in begin, SubpassContents.Inline);
            _vk.CmdEndRenderPass(cmd);
        }

        // ── bloom: bright-pass HDR -> bloomA, then 2x separable blur (A<->B) ──
        Fullscreen(_postColorPass, _bloomFbA[f], _bloomW, _bloomH, _bloomExtractPipeline, _postOneTexPipeLayout, _bloomExtractSet[f]);
        SetBlurDir(0, 1f / _bloomW, 0f);
        SetBlurDir(1, 0f, 1f / _bloomH);
        for (int pass = 0; pass < 2; pass++)
        {
            Fullscreen(_postColorPass, _bloomFbB[f], _bloomW, _bloomH, _blurPipeline, _postOneTexPipeLayout, _bloomBlurSetA[f]);
            Fullscreen(_postColorPass, _bloomFbA[f], _bloomW, _bloomH, _blurPipeline, _postOneTexPipeLayout, _bloomBlurSetB[f]);
        }
    }

    // Composite the HDR scene + bloom + ultra with the ACES tonemap into the
    // swapchain. Recorded INSIDE the already-open swapchain render pass (before UI).
    private void RecordComposite(CommandBuffer cmd)
    {
        if (!_postReady) return;
        int f = _currentFrame;
        var cam = _frameCam;
        bool ultraOn = _ultraEnabled;

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
        System.Buffer.MemoryCopy(&cu, _compositeUboMapped[f], (long)Marshal.SizeOf<CompositeUniform>(), (long)Marshal.SizeOf<CompositeUniform>());

        var vp = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in vp);
        var sc = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in sc);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _compositePipeline);
        var set = _compositeSet[f];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _compositePipeLayout, 0, 1, in set, 0, null);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);
    }

    private void CreateSkyPipelines()
    {
        var skyVs = CreateShaderModule(CompileGlsl(SkyShaders.SkyVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "sky.vert"));
        var skyFs = CreateShaderModule(CompileGlsl(SkyShaders.SkyFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "sky.frag"));
        var celVs = CreateShaderModule(CompileGlsl(SkyShaders.CelestialVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "cel.vert"));
        var celFs = CreateShaderModule(CompileGlsl(SkyShaders.CelestialFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "cel.frag"));
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            // No vertex input: positions come from gl_VertexIndex.
            var emptyVI = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var ia = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates,
            };
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill, LineWidth = 1.0f,
                CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise,
            };
            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };

            // Sky gradient: depth compare ALWAYS, no write (it paints behind world).
            var depthSky = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false, DepthWriteEnable = false, DepthCompareOp = CompareOp.Always,
            };
            // Celestials: depth test (LessEqual) read-only so terrain occludes them,
            // alpha-blended onto the sky.
            var depthCel = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true, DepthWriteEnable = false, DepthCompareOp = CompareOp.LessOrEqual,
            };

            var noBlend = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = false,
            };
            var blendNone = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &noBlend,
            };
            var alpha = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add,
            };
            var blendAlpha = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &alpha,
            };

            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            void SetStages(ShaderModule vs, ShaderModule fs)
            {
                stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vs, PName = entry };
                stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fs, PName = entry };
            }

            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &emptyVI, PInputAssemblyState = &ia, PViewportState = &viewportState,
                PRasterizationState = &rasterizer, PMultisampleState = &multisampling,
                PDynamicState = &dynamicState, Layout = _skyPipelineLayout, RenderPass = _hdrRenderPass, Subpass = 0,
            };

            SetStages(skyVs, skyFs);
            info.PDepthStencilState = &depthSky;
            info.PColorBlendState = &blendNone;
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out _skyPipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (sky) failed.");

            SetStages(celVs, celFs);
            info.PDepthStencilState = &depthCel;
            info.PColorBlendState = &blendAlpha;
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out _celestialPipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (celestial) failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, skyVs, null);
            _vk.DestroyShaderModule(_device, skyFs, null);
            _vk.DestroyShaderModule(_device, celVs, null);
            _vk.DestroyShaderModule(_device, celFs, null);
        }
    }

    private void CreateSkyDescriptorSets()
    {
        int total = MaxFramesInFlight * SkySetsPerFrame;
        ulong size = (ulong)Math.Max(Marshal.SizeOf<SkyUniform>(), Marshal.SizeOf<CelestialUniform>());

        var poolSize = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)total };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 1, PPoolSizes = &poolSize, MaxSets = (uint)total,
        };
        if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out _skyDescPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool (sky) failed.");

        for (int i = 0; i < total; i++)
        {
            CreateBuffer(size, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _skyUbo[i], out _skyUboMem[i]);
            void* mapped;
            _vk.MapMemory(_device, _skyUboMem[i], 0, size, 0, &mapped);
            _skyUboMapped[i] = mapped;

            var setLayout = _skyDescLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _skyDescPool,
                DescriptorSetCount = 1, PSetLayouts = &setLayout,
            };
            if (_vk.AllocateDescriptorSets(_device, in allocInfo, out _skyDescSet[i]) != Result.Success)
                throw new InvalidOperationException("vkAllocateDescriptorSets (sky) failed.");

            var bufferInfo = new DescriptorBufferInfo { Buffer = _skyUbo[i], Offset = 0, Range = size };
            var set = _skyDescSet[i];
            var write = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DstArrayElement = 0,
                DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bufferInfo,
            };
            _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
        }
    }

    /// Record the sky gradient + sun/moon billboards (called first in EndFrame).
    private void RecordSky(CommandBuffer cmd)
    {
        if (!_haveSky) return;
        if (!_skyReady) InitSkyRenderer();
        if (!_skyReady) return;

        var cam = _frameCam;
        var viewProj = BuildViewProj(cam);
        if (!Matrix4x4.Invert(viewProj, out var invViewProj))
            invViewProj = Matrix4x4.Identity;

        var viewport = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);

        int baseSet = _currentFrame * SkySetsPerFrame;

        // ── gradient (full-screen triangle) ──
        var skyU = new SkyUniform
        {
            invViewProj = invViewProj,
            zenith = new Vector4(_frameSky.Zenith, 0),
            horizon = new Vector4(_frameSky.Horizon, 0),
            horizonSun = _frameSky.HorizonSun,
            sunDir = new Vector4(_frameSky.SunDir, _frameSky.VoidSky ? 1f : 0f),
        };
        System.Buffer.MemoryCopy(&skyU, _skyUboMapped[baseSet], Marshal.SizeOf<SkyUniform>(), Marshal.SizeOf<SkyUniform>());
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _skyPipeline);
        var skySet = _skyDescSet[baseSet];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _skyPipelineLayout, 0, 1, in skySet, 0, null);
        _vk.CmdDraw(cmd, 3, 1, 0, 0);

        // ── stars (night) — behind the celestials, far plane ──
        RecordStars(cmd, viewProj);

        // ── sun + moon billboards ──
        // The End has no sun/moon; the Nether's sun lean is straight up — only the
        // overworld draws celestials (matching WorldRenderer).
        if (!_frameSky.VoidSky && !cam.underwater && !cam.underLava)
        {
            _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _celestialPipeline);

            void DrawCelestial(int slot, Vector3 cdir, float size, float moonPhase)
            {
                int idx = baseSet + slot;
                // Build an orthonormal billboard frame around the celestial direction.
                var up0 = new Vector3(0, 0, 1);
                var right = Vector3.Cross(cdir, up0);
                float rl = right.Length();
                right = rl < 1e-6f ? new Vector3(1, 0, 0) : right / rl;
                var up2 = Vector3.Cross(right, cdir);
                var cu = new CelestialUniform
                {
                    viewProj = viewProj,
                    center = new Vector4(cdir * 500f, size),
                    right = new Vector4(right, 0),     // texMode 0 = procedural
                    up = new Vector4(up2, moonPhase),
                };
                System.Buffer.MemoryCopy(&cu, _skyUboMapped[idx], Marshal.SizeOf<CelestialUniform>(), Marshal.SizeOf<CelestialUniform>());
                var set = _skyDescSet[idx];
                _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _skyPipelineLayout, 0, 1, in set, 0, null);
                _vk.CmdDraw(cmd, 6, 1, 0, 0);
            }

            DrawCelestial(1, _frameSky.SunDir, 55f, -1f);                       // sun (moonPhase < 0)
            float phase = (float)(((_frameSky.MoonPhase / 8.0) + 0.5) % 1.0);
            DrawCelestial(2, -_frameSky.SunDir, 38f, phase);                    // moon
        }
    }

    // ═══════════════════════════════════════════════════════════════ UI / HUD

    private void InitUiRenderer()
    {
        EnsureIconTexture();
        CreateUiDescriptorSetLayout();
        CreateUiPipelineLayout();
        CreateUiPipeline();
        CreateUiDescriptorSet();
        _uiReady = true;
    }

    private bool _iconTextureReady;
    private void EnsureIconTexture()
    {
        if (_iconTextureReady) return;
        _iconTextureReady = true;
        int tile = 16;
        uint layers = UiIconLayers;
        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)tile, (uint)tile, 1),
            MipLevels = 1,
            ArrayLayers = layers,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };
        if (_vk.CreateImage(_device, in imageInfo, null, out _iconImage) != Result.Success)
            throw new InvalidOperationException("vkCreateImage (icons) failed.");

        _vk.GetImageMemoryRequirements(_device, _iconImage, out var memReq);
        var allocInfo = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        if (_vk.AllocateMemory(_device, in allocInfo, null, out _iconMemory) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory (icons) failed.");
        _vk.BindImageMemory(_device, _iconImage, _iconMemory, 0);

        // Transition the whole array UNDEFINED -> SHADER_READ_ONLY so it can be
        // sampled before any layer is uploaded (blank/transparent until written).
        var cmd = BeginOneShot();
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, layers);
        var toRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _iconImage,
            SubresourceRange = range,
            SrcAccessMask = 0,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in toRead);
        EndOneShot(cmd);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = _iconImage,
            ViewType = ImageViewType.Type2DArray,
            Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, layers),
        };
        if (_vk.CreateImageView(_device, in viewInfo, null, out _iconView) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView (icons) failed.");

        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest,
            MinFilter = Filter.Nearest,
            MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge,
            AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge,
            MinLod = 0,
            MaxLod = 0,
        };
        if (_vk.CreateSampler(_device, in samplerInfo, null, out _iconSampler) != Result.Success)
            throw new InvalidOperationException("vkCreateSampler (icons) failed.");
    }

    /// Upload a 16×16 RGBA icon into one array layer (one-shot copy with barriers).
    private void UploadIconLayer(int layer, byte[] pixels)
    {
        int layerBytes = 16 * 16 * 4;
        CreateBuffer((ulong)layerBytes, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer staging, out DeviceMemory stagingMem);
        void* mapped;
        _vk.MapMemory(_device, stagingMem, 0, (ulong)layerBytes, 0, &mapped);
        pixels.AsSpan(0, layerBytes).CopyTo(new Span<byte>(mapped, layerBytes));
        _vk.UnmapMemory(_device, stagingMem);

        var cmd = BeginOneShot();
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, (uint)layer, 1);
        var toDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.ShaderReadOnlyOptimal,
            NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _iconImage,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.ShaderReadBit,
            DstAccessMask = AccessFlags.TransferWriteBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.FragmentShaderBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, in toDst);

        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, (uint)layer, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(16, 16, 1),
        };
        _vk.CmdCopyBufferToImage(cmd, staging, _iconImage, ImageLayout.TransferDstOptimal, 1, in copy);

        var toRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal,
            NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = _iconImage,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.TransferWriteBit,
            DstAccessMask = AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in toRead);
        EndOneShot(cmd);

        _vk.DestroyBuffer(_device, staging, null);
        _vk.FreeMemory(_device, stagingMem, null);
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

    private void CreateUiDescriptorSetLayout()
    {
        var binding = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var layoutInfo = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 1,
            PBindings = &binding,
        };
        if (_vk.CreateDescriptorSetLayout(_device, in layoutInfo, null, out _uiDescSetLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout (ui) failed.");
    }

    private void CreateUiPipelineLayout()
    {
        var setLayout = _uiDescSetLayout;
        var pushRange = new PushConstantRange
        {
            StageFlags = ShaderStageFlags.VertexBit,
            Offset = 0,
            Size = (uint)Marshal.SizeOf<UiPush>(),
        };
        var layoutInfo = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
            PushConstantRangeCount = 1,
            PPushConstantRanges = &pushRange,
        };
        if (_vk.CreatePipelineLayout(_device, in layoutInfo, null, out _uiPipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout (ui) failed.");
    }

    private void CreateUiPipeline()
    {
        var vertSpirv = CompileGlsl(UiShaders.VulkanVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "ui.vert");
        var fragSpirv = CompileGlsl(UiShaders.VulkanFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "ui.frag");
        var vertModule = CreateShaderModule(vertSpirv);
        var fragModule = CreateShaderModule(fragSpirv);
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit, Module = vertModule, PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = entry,
            };

            var binding = new VertexInputBindingDescription
            {
                Binding = 0, Stride = UiVertexStride, InputRate = VertexInputRate.Vertex,
            };
            var attrs = stackalloc VertexInputAttributeDescription[4];
            attrs[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 };   // pos
            attrs[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32Sfloat, Offset = 8 };   // uv
            attrs[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32Sfloat, Offset = 16 };     // layer
            attrs[3] = new VertexInputAttributeDescription { Location = 3, Binding = 0, Format = Format.R32G32B32A32Sfloat, Offset = 20 }; // color

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 4,
                PVertexAttributeDescriptions = attrs,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates,
            };
            // No back-face culling for the UI (quads are axis-aligned screen-space).
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill, LineWidth = 1.0f, CullMode = CullModeFlags.None,
                FrontFace = FrontFace.CounterClockwise,
            };
            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            // No depth test/write — UI always draws on top of the world.
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = false, DepthWriteEnable = false, StencilTestEnable = false,
            };
            var alphaBlend = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1, PAttachments = &alphaBlend,
            };
            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2, PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &blend,
                PDynamicState = &dynamicState,
                Layout = _uiPipelineLayout,
                RenderPass = _renderPass,
                Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _uiPipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (ui) failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
        }
    }

    private void CreateUiDescriptorSet()
    {
        var poolSize = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = 1 };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 1, PPoolSizes = &poolSize, MaxSets = 1,
        };
        if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out _uiDescPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool (ui) failed.");

        var setLayout = _uiDescSetLayout;
        var allocInfo = new DescriptorSetAllocateInfo
        {
            SType = StructureType.DescriptorSetAllocateInfo,
            DescriptorPool = _uiDescPool, DescriptorSetCount = 1, PSetLayouts = &setLayout,
        };
        if (_vk.AllocateDescriptorSets(_device, in allocInfo, out _uiDescSet) != Result.Success)
            throw new InvalidOperationException("vkAllocateDescriptorSets (ui) failed.");

        var imageInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal,
            ImageView = _iconView,
            Sampler = _iconSampler,
        };
        var write = new WriteDescriptorSet
        {
            SType = StructureType.WriteDescriptorSet,
            DstSet = _uiDescSet, DstBinding = 0, DstArrayElement = 0,
            DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1,
            PImageInfo = &imageInfo,
        };
        _vk.UpdateDescriptorSets(_device, 1, in write, 0, null);
    }

    public void RenderUI(Pebble.UI.UICanvas canvas)
    {
        // Capture the batched vertex stream for replay inside EndFrame's render
        // pass (which is recorded after RecordWorld so the UI draws on top).
        _uiVertCount = canvas.VertexCount;
        if (_uiVertCount == 0) { _uiVerts = null; return; }
        int floats = _uiVertCount * Pebble.UI.UICanvas.FloatsPerVertex;
        if (_uiVerts == null || _uiVerts.Length < floats) _uiVerts = new float[floats];
        for (int i = 0; i < floats; i++) _uiVerts[i] = canvas.Verts[i];
        _uiScreenW = (float)canvas.width;
        _uiScreenH = (float)canvas.height;
    }

    /// Record the UI draw into the active render pass (after RecordWorld).
    private void RecordUi(CommandBuffer cmd)
    {
        if (_uiVerts == null || _uiVertCount == 0) return;
        if (!_uiReady) InitUiRenderer();
        if (!_uiReady) return;

        // Upload this frame's vertices into the per-frame dynamic vertex buffer.
        ulong bytes = (ulong)(_uiVertCount * Pebble.UI.UICanvas.FloatsPerVertex * sizeof(float));
        int f = _currentFrame;
        if (_uiVbufSize[f] < bytes)
        {
            if (_uiVbuf[f].Handle != 0) _vk.DestroyBuffer(_device, _uiVbuf[f], null);
            if (_uiVmem[f].Handle != 0) _vk.FreeMemory(_device, _uiVmem[f], null);
            CreateBuffer(Math.Max(bytes * 2, 65536), BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _uiVbuf[f], out _uiVmem[f]);
            _uiVbufSize[f] = Math.Max(bytes * 2, 65536);
        }
        void* mapped;
        _vk.MapMemory(_device, _uiVmem[f], 0, bytes, 0, &mapped);
        _uiVerts.AsSpan(0, _uiVertCount * Pebble.UI.UICanvas.FloatsPerVertex)
            .CopyTo(new Span<float>(mapped, _uiVertCount * Pebble.UI.UICanvas.FloatsPerVertex));
        _vk.UnmapMemory(_device, _uiVmem[f]);

        var viewport = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);

        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _uiPipeline);
        var descSet = _uiDescSet;
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _uiPipelineLayout, 0, 1, in descSet, 0, null);

        var push = new UiPush { screen = new Vector4(_uiScreenW, _uiScreenH, 0, 0) };
        _vk.CmdPushConstants(cmd, _uiPipelineLayout, ShaderStageFlags.VertexBit, 0,
            (uint)Marshal.SizeOf<UiPush>(), &push);

        var vbuf = _uiVbuf[f];
        ulong offset = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, in vbuf, in offset);
        _vk.CmdDraw(cmd, (uint)_uiVertCount, 1, 0, 0);
    }

    // ═══════════════════════════════════════════════════════════ Entities (3D)

    public void RenderEntities(Pebble.EntityRenderer renderer, Pebble.EntityDrawList list,
                               PebbleCore.CamState cam, double timeSeconds)
    {
        // Capture for replay inside EndFrame's render pass (after RecordWorld so
        // entities depth-test against the already-written terrain). The model
        // geometry/skin build (renderer.Geom) is pure-CPU and happens here; the
        // GPU uploads (VB + skin) are done lazily inside RecordEntities via the
        // one-shot upload queue, NOT mid-render-pass.
        _entityRenderer = renderer;
        _entityList = list;
        _entityCam = cam;
        _haveEntityFrame = list.draws.Count > 0;
    }

    private void InitEntityRenderer()
    {
        // Dynamic-UBO alignment from device limits.
        _vk.GetPhysicalDeviceProperties(_physicalDevice, out var props);
        ulong minAlign = props.Limits.MinUniformBufferOffsetAlignment;
        _entityUAligned = (uint)(((EntityUSize + (int)minAlign - 1) / (int)minAlign) * (int)minAlign);

        CreateEntityDescriptorSetLayout();
        CreateEntityPipelineLayout();
        CreateEntityPipeline();
        // Descriptor pool: one set per model (binding1 skin) — allow a generous cap.
        CreateEntityDescriptorPool();
        _entityReady = true;
    }

    private void CreateEntityDescriptorSetLayout()
    {
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0,
            DescriptorType = DescriptorType.UniformBufferDynamic,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1,
            DescriptorType = DescriptorType.CombinedImageSampler,
            DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var info = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo,
            BindingCount = 2,
            PBindings = bindings,
        };
        if (_vk.CreateDescriptorSetLayout(_device, in info, null, out _entityDescLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout (entity) failed.");
    }

    private void CreateEntityPipelineLayout()
    {
        var setLayout = _entityDescLayout;
        var info = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo,
            SetLayoutCount = 1,
            PSetLayouts = &setLayout,
        };
        if (_vk.CreatePipelineLayout(_device, in info, null, out _entityPipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout (entity) failed.");
    }

    private void CreateEntityDescriptorPool()
    {
        // 2 sets per model (one per in-flight frame); cap ~64 distinct models.
        const uint maxSets = 64 * MaxFramesInFlight;
        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBufferDynamic, DescriptorCount = maxSets };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = maxSets };
        var info = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo,
            PoolSizeCount = 2,
            PPoolSizes = poolSizes,
            MaxSets = maxSets,
        };
        if (_vk.CreateDescriptorPool(_device, in info, null, out _entityDescPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool (entity) failed.");
    }

    private void CreateEntityPipeline()
    {
        var vertSpirv = CompileGlsl(EntityShaders.VulkanVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "entity.vert");
        var fragSpirv = CompileGlsl(EntityShaders.VulkanFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "entity.frag");
        var vertModule = CreateShaderModule(vertSpirv);
        var fragModule = CreateShaderModule(fragSpirv);
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.VertexBit, Module = vertModule, PName = entry,
            };
            stages[1] = new PipelineShaderStageCreateInfo
            {
                SType = StructureType.PipelineShaderStageCreateInfo,
                Stage = ShaderStageFlags.FragmentBit, Module = fragModule, PName = entry,
            };

            var binding = new VertexInputBindingDescription
            {
                Binding = 0, Stride = EntityVertexStride, InputRate = VertexInputRate.Vertex,
            };
            var attrs = stackalloc VertexInputAttributeDescription[4];
            attrs[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 0 };   // pos
            attrs[1] = new VertexInputAttributeDescription { Location = 1, Binding = 0, Format = Format.R32G32B32Sfloat, Offset = 12 };  // normal
            attrs[2] = new VertexInputAttributeDescription { Location = 2, Binding = 0, Format = Format.R32G32Sfloat, Offset = 24 };     // uv
            attrs[3] = new VertexInputAttributeDescription { Location = 3, Binding = 0, Format = Format.R32Sfloat, Offset = 32 };        // part

            var vertexInput = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 1,
                PVertexBindingDescriptions = &binding,
                VertexAttributeDescriptionCount = 4,
                PVertexAttributeDescriptions = attrs,
            };
            var inputAssembly = new PipelineInputAssemblyStateCreateInfo
            {
                SType = StructureType.PipelineInputAssemblyStateCreateInfo,
                Topology = PrimitiveTopology.TriangleList,
            };
            var viewportState = new PipelineViewportStateCreateInfo
            {
                SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1,
            };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dynamicState = new PipelineDynamicStateCreateInfo
            {
                SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates,
            };
            // Back-face cull. The entity geometry winds the same way as the world
            // mesh (CCW-front in the right-handed world); the viewProj Y-flip for
            // Vulkan clip inverts apparent winding, so FrontFace = Clockwise keeps
            // outward faces visible — identical to the world pipeline.
            var rasterizer = new PipelineRasterizationStateCreateInfo
            {
                SType = StructureType.PipelineRasterizationStateCreateInfo,
                PolygonMode = PolygonMode.Fill, LineWidth = 1.0f,
                CullMode = CullModeFlags.BackBit, FrontFace = FrontFace.Clockwise,
            };
            var multisampling = new PipelineMultisampleStateCreateInfo
            {
                SType = StructureType.PipelineMultisampleStateCreateInfo,
                RasterizationSamples = SampleCountFlags.Count1Bit,
            };
            // Depth test + write ON: entities occlude / are occluded by terrain.
            var depthStencil = new PipelineDepthStencilStateCreateInfo
            {
                SType = StructureType.PipelineDepthStencilStateCreateInfo,
                DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.Less,
                StencilTestEnable = false,
            };
            // Alpha blend (the fs straight-alphas; cutout pixels are discarded < 0.1).
            var alphaBlend = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit |
                                 ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true,
                SrcColorBlendFactor = BlendFactor.SrcAlpha,
                DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha,
                ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One,
                DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha,
                AlphaBlendOp = BlendOp.Add,
            };
            var blend = new PipelineColorBlendStateCreateInfo
            {
                SType = StructureType.PipelineColorBlendStateCreateInfo,
                AttachmentCount = 1, PAttachments = &alphaBlend,
            };
            var pipelineInfo = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo,
                StageCount = 2, PStages = stages,
                PVertexInputState = &vertexInput,
                PInputAssemblyState = &inputAssembly,
                PViewportState = &viewportState,
                PRasterizationState = &rasterizer,
                PMultisampleState = &multisampling,
                PDepthStencilState = &depthStencil,
                PColorBlendState = &blend,
                PDynamicState = &dynamicState,
                Layout = _entityPipelineLayout,
                RenderPass = _hdrRenderPass,
                Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in pipelineInfo, null, out _entityPipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (entity) failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vertModule, null);
            _vk.DestroyShaderModule(_device, fragModule, null);
        }
    }

    /// Build (or fetch cached) the GPU vertex buffer + skin texture + descriptor
    /// set for a model. Uploads use the one-shot queue (safe outside the frame's
    /// render pass — RecordEntities calls this BEFORE binding the pipeline).
    private EntityModelGpu GetEntityModel(string name)
    {
        if (_entityModels.TryGetValue(name, out var g)) return g;
        var data = _entityRenderer!.Geom(name);
        g = new EntityModelGpu { vertexCount = (uint)data.vertexCount };

        // --- vertex buffer: convert the 9-float (pos3 normal3 uv2 part1) stream
        // into the 10-float entity vertex layout (no change; it's already 9 f32 ==
        // 36 bytes... but the pipeline stride is 40). Repack to 10 floats/vertex so
        // the part attribute sits at offset 32 with a 40-byte stride.
        int vc = data.vertexCount;
        var packed = new float[vc * 10];
        for (int i = 0; i < vc; i++)
        {
            int s = i * 9, d = i * 10;
            packed[d + 0] = data.verts[s + 0]; packed[d + 1] = data.verts[s + 1]; packed[d + 2] = data.verts[s + 2];
            packed[d + 3] = data.verts[s + 3]; packed[d + 4] = data.verts[s + 4]; packed[d + 5] = data.verts[s + 5];
            packed[d + 6] = data.verts[s + 6]; packed[d + 7] = data.verts[s + 7];
            packed[d + 8] = data.verts[s + 8]; packed[d + 9] = 0f; // pad to 40 bytes
        }
        ulong vSize = (ulong)packed.Length * sizeof(float);
        CreateBuffer(vSize, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out g.vbuf, out g.vmem);
        void* vmapped;
        _vk.MapMemory(_device, g.vmem, 0, vSize, 0, &vmapped);
        packed.AsSpan().CopyTo(new Span<float>(vmapped, packed.Length));
        _vk.UnmapMemory(_device, g.vmem);

        // --- skin texture (2D RGBA8) ---
        UploadEntitySkin(g, data.skinW, data.skinH, data.skinRgba);

        // --- per-frame descriptor sets (one per in-flight frame's UBO) ---
        var imgInfo = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = g.skinView, Sampler = g.skinSampler,
        };
        var writes = stackalloc WriteDescriptorSet[2];
        for (int fi = 0; fi < MaxFramesInFlight; fi++)
        {
            var setLayout = _entityDescLayout;
            var allocInfo = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo,
                DescriptorPool = _entityDescPool, DescriptorSetCount = 1, PSetLayouts = &setLayout,
            };
            if (_vk.AllocateDescriptorSets(_device, in allocInfo, out g.descSet[fi]) != Result.Success)
                throw new InvalidOperationException("vkAllocateDescriptorSets (entity) failed.");

            var bufInfo = new DescriptorBufferInfo
            {
                Buffer = _entityUBuf[fi], Offset = 0, Range = (ulong)EntityUSize,
            };
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = g.descSet[fi], DstBinding = 0, DstArrayElement = 0,
                DescriptorType = DescriptorType.UniformBufferDynamic, DescriptorCount = 1, PBufferInfo = &bufInfo,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = g.descSet[fi], DstBinding = 1, DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &imgInfo,
            };
            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);
        }

        _entityModels[name] = g;
        return g;
    }

    private void UploadEntitySkin(EntityModelGpu g, int w, int h, byte[] rgba)
    {
        int bytes = w * h * 4;
        CreateBuffer((ulong)bytes, BufferUsageFlags.TransferSrcBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer staging, out DeviceMemory stagingMem);
        void* mapped;
        _vk.MapMemory(_device, stagingMem, 0, (ulong)bytes, 0, &mapped);
        rgba.AsSpan(0, bytes).CopyTo(new Span<byte>(mapped, bytes));
        _vk.UnmapMemory(_device, stagingMem);

        var imageInfo = new ImageCreateInfo
        {
            SType = StructureType.ImageCreateInfo,
            ImageType = ImageType.Type2D,
            Extent = new Extent3D((uint)w, (uint)h, 1),
            MipLevels = 1, ArrayLayers = 1,
            Format = Format.R8G8B8A8Unorm,
            Tiling = ImageTiling.Optimal,
            InitialLayout = ImageLayout.Undefined,
            Usage = ImageUsageFlags.TransferDstBit | ImageUsageFlags.SampledBit,
            Samples = SampleCountFlags.Count1Bit,
            SharingMode = SharingMode.Exclusive,
        };
        if (_vk.CreateImage(_device, in imageInfo, null, out g.skinImage) != Result.Success)
            throw new InvalidOperationException("vkCreateImage (skin) failed.");
        _vk.GetImageMemoryRequirements(_device, g.skinImage, out var memReq);
        var alloc = new MemoryAllocateInfo
        {
            SType = StructureType.MemoryAllocateInfo,
            AllocationSize = memReq.Size,
            MemoryTypeIndex = FindMemoryType(memReq.MemoryTypeBits, MemoryPropertyFlags.DeviceLocalBit),
        };
        if (_vk.AllocateMemory(_device, in alloc, null, out g.skinMem) != Result.Success)
            throw new InvalidOperationException("vkAllocateMemory (skin) failed.");
        _vk.BindImageMemory(_device, g.skinImage, g.skinMem, 0);

        var cmd = BeginOneShot();
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);
        var toDst = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.Undefined, NewLayout = ImageLayout.TransferDstOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = g.skinImage, SubresourceRange = range,
            SrcAccessMask = 0, DstAccessMask = AccessFlags.TransferWriteBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, in toDst);
        var copy = new BufferImageCopy
        {
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0), ImageExtent = new Extent3D((uint)w, (uint)h, 1),
        };
        _vk.CmdCopyBufferToImage(cmd, staging, g.skinImage, ImageLayout.TransferDstOptimal, 1, in copy);
        var toRead = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferDstOptimal, NewLayout = ImageLayout.ShaderReadOnlyOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored, DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = g.skinImage, SubresourceRange = range,
            SrcAccessMask = AccessFlags.TransferWriteBit, DstAccessMask = AccessFlags.ShaderReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.FragmentShaderBit,
            0, 0, null, 0, null, 1, in toRead);
        EndOneShot(cmd);
        _vk.DestroyBuffer(_device, staging, null);
        _vk.FreeMemory(_device, stagingMem, null);

        var viewInfo = new ImageViewCreateInfo
        {
            SType = StructureType.ImageViewCreateInfo, Image = g.skinImage,
            ViewType = ImageViewType.Type2D, Format = Format.R8G8B8A8Unorm,
            SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1),
        };
        if (_vk.CreateImageView(_device, in viewInfo, null, out g.skinView) != Result.Success)
            throw new InvalidOperationException("vkCreateImageView (skin) failed.");
        var samplerInfo = new SamplerCreateInfo
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = Filter.Nearest, MinFilter = Filter.Nearest, MipmapMode = SamplerMipmapMode.Nearest,
            AddressModeU = SamplerAddressMode.ClampToEdge, AddressModeV = SamplerAddressMode.ClampToEdge,
            AddressModeW = SamplerAddressMode.ClampToEdge, MinLod = 0, MaxLod = 0,
        };
        if (_vk.CreateSampler(_device, in samplerInfo, null, out g.skinSampler) != Result.Success)
            throw new InvalidOperationException("vkCreateSampler (skin) failed.");
    }

    /// Ensure BOTH in-flight frames' dynamic EntityU buffers are at least `needed`
    /// bytes. Both must exist before any per-model descriptor set is created (each
    /// model gets one set per frame, pointing at that frame's buffer). On a grow we
    /// drop the model cache + reset the descriptor pool so the sets re-bind the new
    /// buffers; geometry/skins re-upload (rare — only when the entity count climbs).
    private void EnsureEntityUBuffer(ulong needed)
    {
        bool grew = false;
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_entityUSizeBytes[i] >= needed && _entityUBuf[i].Handle != 0) continue;
            if (_entityUBuf[i].Handle != 0) _vk.DestroyBuffer(_device, _entityUBuf[i], null);
            if (_entityUMem[i].Handle != 0) _vk.FreeMemory(_device, _entityUMem[i], null);
            ulong size = Math.Max(needed, (ulong)_entityUAligned * 64);
            CreateBuffer(size, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _entityUBuf[i], out _entityUMem[i]);
            _entityUSizeBytes[i] = size;
            grew = true;
        }
        if (grew)
        {
            foreach (var m in _entityModels.Values) DestroyEntityModel(m);
            _entityModels.Clear();
            if (_entityDescPool.Handle != 0) _vk.ResetDescriptorPool(_device, _entityDescPool, 0);
        }
    }

    private void RecordEntities(CommandBuffer cmd)
    {
        if (!_haveEntityFrame || _entityList == null || _entityRenderer == null) return;
        if (!_worldReady) return; // need depth from the world pass
        if (!_entityReady) InitEntityRenderer();
        if (!_entityReady) return;

        var draws = _entityList.draws;
        int n = Math.Min(draws.Count, MaxEntitiesPerFrame);
        if (n == 0) return;

        int f = _currentFrame;
        ulong needed = (ulong)_entityUAligned * (ulong)n;
        EnsureEntityUBuffer(needed);

        // Pack every entity's EntityU block into the scratch, then one upload.
        if (_entityUScratch.Length < (int)_entityUAligned * n)
            _entityUScratch = new byte[(int)_entityUAligned * n];
        Array.Clear(_entityUScratch, 0, (int)_entityUAligned * n);
        for (int i = 0; i < n; i++)
            PackEntityU(draws[i], _entityUScratch, i * (int)_entityUAligned);

        ulong bytes = (ulong)_entityUAligned * (ulong)n;
        void* mapped;
        _vk.MapMemory(_device, _entityUMem[f], 0, bytes, 0, &mapped);
        _entityUScratch.AsSpan(0, (int)bytes).CopyTo(new Span<byte>(mapped, (int)bytes));
        _vk.UnmapMemory(_device, _entityUMem[f]);

        var viewport = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _entityPipeline);

        for (int i = 0; i < n; i++)
        {
            var d = draws[i];
            var g = GetEntityModel(d.model);
            if (g.vertexCount == 0) continue;
            uint dynOffset = (uint)(i * (int)_entityUAligned);
            var descSet = g.descSet[f];
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _entityPipelineLayout, 0,
                1, in descSet, 1, in dynOffset);
            var vbuf = g.vbuf;
            ulong offset = 0;
            _vk.CmdBindVertexBuffers(cmd, 0, 1, in vbuf, in offset);
            _vk.CmdDraw(cmd, g.vertexCount, 1, 0, 0);
        }
    }

    /// Serialize one EntityDraw into the std140 EntityU layout (matches the GLSL).
    private void PackEntityU(Pebble.EntityDraw d, byte[] dst, int off)
    {
        var f = new float[EntityUSize / 4];
        int p = 0;
        // viewProj — same upload convention as the world pass (row-major bytes of
        // System.Numerics view*proj, read column-major by the shader).
        var vp = BuildViewProj(_entityCam);
        WriteMat4RowMajor(f, ref p, vp);
        // model — column-major bytes of the EntityMat (read column-major directly).
        WriteEntityMat(f, ref p, d.modelMat);
        for (int i = 0; i < 24; i++) WriteEntityMat(f, ref p, d.parts[i]);
        f[p++] = d.lightSky; f[p++] = d.lightBlock; f[p++] = d.lightDay; f[p++] = d.lightGamma;
        f[p++] = d.ambient; f[p++] = d.alpha; f[p++] = d.fogStart; f[p++] = d.fogEnd;
        f[p++] = d.overlayR; f[p++] = d.overlayG; f[p++] = d.overlayB; f[p++] = d.overlayA;
        f[p++] = d.fogR; f[p++] = d.fogG; f[p++] = d.fogB; f[p++] = 1f;
        System.Buffer.BlockCopy(f, 0, dst, off, EntityUSize);
    }

    private static void WriteMat4RowMajor(float[] f, ref int p, System.Numerics.Matrix4x4 m)
    {
        // System.Numerics is row-major in memory (M11..M14, M21..). Upload as-is.
        f[p++] = m.M11; f[p++] = m.M12; f[p++] = m.M13; f[p++] = m.M14;
        f[p++] = m.M21; f[p++] = m.M22; f[p++] = m.M23; f[p++] = m.M24;
        f[p++] = m.M31; f[p++] = m.M32; f[p++] = m.M33; f[p++] = m.M34;
        f[p++] = m.M41; f[p++] = m.M42; f[p++] = m.M43; f[p++] = m.M44;
    }

    private static void WriteEntityMat(float[] f, ref int p, Pebble.EntityMat m)
    {
        m.WriteColumnMajor(f, p); p += 16;
    }

    // ═══════════════════════════════════════════════════════ Break-crack overlay

    public void RenderBreakOverlay(Pebble.BreakOverlay overlay, PebbleCore.CamState cam)
    {
        _frameOverlay = overlay;
        _frameCam = cam;
        _haveOverlayFrame = overlay.active && overlay.indexCount > 0;
    }

    /// Draw the crack mesh with the world translucent pipeline (depth-test lessEqual,
    /// depth-write OFF, alpha blend). Reuses _descSetTrans (binds atlas array).
    private void RecordBreakOverlay(CommandBuffer cmd)
    {
        if (!_haveOverlayFrame || _frameOverlay == null) return;
        if (!_worldReady) return;
        var ov = _frameOverlay;

        int f = _currentFrame;
        ulong vBytes = (ulong)ov.verts.Length * sizeof(uint);
        ulong iBytes = (ulong)ov.idx.Length * sizeof(uint);
        if (_overlayVbufSize[f] < vBytes)
        {
            if (_overlayVbuf[f].Handle != 0) _vk.DestroyBuffer(_device, _overlayVbuf[f], null);
            if (_overlayVmem[f].Handle != 0) _vk.FreeMemory(_device, _overlayVmem[f], null);
            CreateBuffer(Math.Max(vBytes * 2, 16384), BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _overlayVbuf[f], out _overlayVmem[f]);
            _overlayVbufSize[f] = Math.Max(vBytes * 2, 16384);
        }
        if (_overlayIbufSize[f] < iBytes)
        {
            if (_overlayIbuf[f].Handle != 0) _vk.DestroyBuffer(_device, _overlayIbuf[f], null);
            if (_overlayImem[f].Handle != 0) _vk.FreeMemory(_device, _overlayImem[f], null);
            CreateBuffer(Math.Max(iBytes * 2, 8192), BufferUsageFlags.IndexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _overlayIbuf[f], out _overlayImem[f]);
            _overlayIbufSize[f] = Math.Max(iBytes * 2, 8192);
        }
        void* vmapped, imapped;
        _vk.MapMemory(_device, _overlayVmem[f], 0, vBytes, 0, &vmapped);
        ov.verts.AsSpan().CopyTo(new Span<uint>(vmapped, ov.verts.Length));
        _vk.UnmapMemory(_device, _overlayVmem[f]);
        _vk.MapMemory(_device, _overlayImem[f], 0, iBytes, 0, &imapped);
        ov.idx.AsSpan().CopyTo(new Span<uint>(imapped, ov.idx.Length));
        _vk.UnmapMemory(_device, _overlayImem[f]);

        // The translucent UBO (fog.z=0 alphaTest off, fog.w=0.82) is already mapped
        // for this frame's translucent world pass — fine to reuse here. But we want
        // alphaTest ON (crack atlas tiles have transparent gutters); patch a dedicated
        // value: alphaTest 0.05 matches drawCrack (fog.z=0.05) via _uboBufferTrans is
        // shared, so instead bind the OPAQUE set (alphaTest 0.5 is harmless for the
        // crack since its tiles are >0.5 where opaque). Use _descSetTrans for blending.
        var viewport = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _translucentPipeline);
        var descSet = _descSetTransFrame[_currentFrame];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _pipelineLayout, 0, 1, in descSet, 0, null);
        var push = new PushConstants { origin = new Vector4(ov.originX, ov.originY, ov.originZ, 0) };
        _vk.CmdPushConstants(cmd, _pipelineLayout, ShaderStageFlags.VertexBit, 0,
            (uint)Marshal.SizeOf<PushConstants>(), &push);
        var vbuf = _overlayVbuf[f];
        ulong off = 0;
        _vk.CmdBindVertexBuffers(cmd, 0, 1, in vbuf, in off);
        _vk.CmdBindIndexBuffer(cmd, _overlayIbuf[f], 0, IndexType.Uint32);
        _vk.CmdDrawIndexed(cmd, (uint)ov.indexCount, 1, 0, 0, 0);
    }

    // ═══════════════════════════════════════════════════════════════ Sprites

    public void RenderSprites(Pebble.SpriteList sprites, PebbleCore.CamState cam)
    {
        _frameSprites = sprites;
        _frameCam = cam;
        _haveSpriteFrame = sprites.sprites.Count > 0;
    }

    private void InitSpriteRenderer()
    {
        EnsureIconTexture();
        // binding 0 = SpriteU (UBO, vs+fs), binding 1 = icon array (fs).
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.VertexBit | ShaderStageFlags.FragmentBit,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1,
            StageFlags = ShaderStageFlags.FragmentBit,
        };
        var li = new DescriptorSetLayoutCreateInfo
        {
            SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings,
        };
        if (_vk.CreateDescriptorSetLayout(_device, in li, null, out _spriteDescLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout (sprite) failed.");
        var setLayout = _spriteDescLayout;
        var pli = new PipelineLayoutCreateInfo
        {
            SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout,
        };
        if (_vk.CreatePipelineLayout(_device, in pli, null, out _spritePipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout (sprite) failed.");

        CreateSpritePipeline();

        // pool: one UBO + one sampler per (frame × sprite) set.
        int total = MaxFramesInFlight * MaxSpritesPerFrame;
        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = (uint)total };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = (uint)total };
        var poolInfo = new DescriptorPoolCreateInfo
        {
            SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 2, PPoolSizes = poolSizes, MaxSets = (uint)total,
        };
        if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out _spriteDescPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool (sprite) failed.");

        ulong uboSize = (ulong)Marshal.SizeOf<SpriteUniform>();
        var iconImg = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _iconView, Sampler = _iconSampler,
        };
        for (int i = 0; i < total; i++)
        {
            CreateBuffer(uboSize, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _spriteUbo[i], out _spriteUboMem[i]);
            void* m;
            _vk.MapMemory(_device, _spriteUboMem[i], 0, uboSize, 0, &m);
            _spriteUboMapped[i] = m;
            var sl = _spriteDescLayout;
            var ai = new DescriptorSetAllocateInfo
            {
                SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _spriteDescPool,
                DescriptorSetCount = 1, PSetLayouts = &sl,
            };
            if (_vk.AllocateDescriptorSets(_device, in ai, out _spriteDescSet[i]) != Result.Success)
                throw new InvalidOperationException("vkAllocateDescriptorSets (sprite) failed.");
            var bi = new DescriptorBufferInfo { Buffer = _spriteUbo[i], Offset = 0, Range = uboSize };
            var set = _spriteDescSet[i];
            var imgLocal = iconImg;
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DstArrayElement = 0,
                DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bi,
            };
            writes[1] = new WriteDescriptorSet
            {
                SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DstArrayElement = 0,
                DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &imgLocal,
            };
            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);
        }
        _spriteReady = true;
    }

    private void CreateSpritePipeline()
    {
        var vsMod = CreateShaderModule(CompileGlsl(SpriteShaders.VulkanVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "sprite.vert"));
        var fsMod = CreateShaderModule(CompileGlsl(SpriteShaders.VulkanFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "sprite.frag"));
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsMod, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fsMod, PName = entry };
            var emptyVI = new PipelineVertexInputStateCreateInfo { SType = StructureType.PipelineVertexInputStateCreateInfo };
            var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dyn = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates };
            var rast = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, LineWidth = 1.0f, CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            // depth test+write ON: sprites occlude / are occluded by terrain.
            var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = true, DepthCompareOp = CompareOp.Less };
            var ab = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true, SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add,
            };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &ab };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &emptyVI, PInputAssemblyState = &ia, PViewportState = &vp,
                PRasterizationState = &rast, PMultisampleState = &ms, PDepthStencilState = &ds,
                PColorBlendState = &blend, PDynamicState = &dyn, Layout = _spritePipelineLayout, RenderPass = _hdrRenderPass, Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out _spritePipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (sprite) failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vsMod, null);
            _vk.DestroyShaderModule(_device, fsMod, null);
        }
    }

    private void RecordSprites(CommandBuffer cmd)
    {
        if (!_haveSpriteFrame || _frameSprites == null) return;
        if (!_worldReady) return;
        if (!_spriteReady) InitSpriteRenderer();
        if (!_spriteReady) return;
        var list = _frameSprites;
        int n = Math.Min(list.sprites.Count, MaxSpritesPerFrame);
        if (n == 0) return;

        var viewProj = BuildViewProj(_frameCam);
        var viewport = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _spritePipeline);

        int baseSet = _currentFrame * MaxSpritesPerFrame;
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
            int idx = baseSet + i;
            System.Buffer.MemoryCopy(&u, _spriteUboMapped[idx], Marshal.SizeOf<SpriteUniform>(), Marshal.SizeOf<SpriteUniform>());
            var set = _spriteDescSet[idx];
            _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _spritePipelineLayout, 0, 1, in set, 0, null);
            _vk.CmdDraw(cmd, 6, 1, 0, 0);
        }
    }

    // ═══════════════════════════════════════════════════════════════ Particles

    public void RenderParticles(Pebble.Particles particles, PebbleCore.CamState cam)
    {
        _frameParticles = particles;
        _frameCam = cam;
        _haveParticleFrame = particles.Count > 0;
    }

    private void InitParticleRenderer()
    {
        // binding 0 = ParticleU (UBO, vs), binding 1 = atlas array (fs).
        var bindings = stackalloc DescriptorSetLayoutBinding[2];
        bindings[0] = new DescriptorSetLayoutBinding
        {
            Binding = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, StageFlags = ShaderStageFlags.VertexBit,
        };
        bindings[1] = new DescriptorSetLayoutBinding
        {
            Binding = 1, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, StageFlags = ShaderStageFlags.FragmentBit,
        };
        var li = new DescriptorSetLayoutCreateInfo { SType = StructureType.DescriptorSetLayoutCreateInfo, BindingCount = 2, PBindings = bindings };
        if (_vk.CreateDescriptorSetLayout(_device, in li, null, out _particleDescLayout) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorSetLayout (particle) failed.");
        var setLayout = _particleDescLayout;
        var pli = new PipelineLayoutCreateInfo { SType = StructureType.PipelineLayoutCreateInfo, SetLayoutCount = 1, PSetLayouts = &setLayout };
        if (_vk.CreatePipelineLayout(_device, in pli, null, out _particlePipelineLayout) != Result.Success)
            throw new InvalidOperationException("vkCreatePipelineLayout (particle) failed.");

        CreateParticlePipeline();

        // static quad VB (6 corners, [-1..1]).
        float[] quad = { -1, -1, 1, -1, 1, 1, -1, -1, 1, 1, -1, 1 };
        ulong qBytes = (ulong)quad.Length * sizeof(float);
        CreateBuffer(qBytes, BufferUsageFlags.VertexBufferBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out _particleQuadBuf, out _particleQuadMem);
        void* qm;
        _vk.MapMemory(_device, _particleQuadMem, 0, qBytes, 0, &qm);
        quad.AsSpan().CopyTo(new Span<float>(qm, quad.Length));
        _vk.UnmapMemory(_device, _particleQuadMem);

        // per-frame UBO + descriptor set.
        ulong uboSize = (ulong)Marshal.SizeOf<ParticleUniform>();
        var poolSizes = stackalloc DescriptorPoolSize[2];
        poolSizes[0] = new DescriptorPoolSize { Type = DescriptorType.UniformBuffer, DescriptorCount = MaxFramesInFlight };
        poolSizes[1] = new DescriptorPoolSize { Type = DescriptorType.CombinedImageSampler, DescriptorCount = MaxFramesInFlight };
        var poolInfo = new DescriptorPoolCreateInfo { SType = StructureType.DescriptorPoolCreateInfo, PoolSizeCount = 2, PPoolSizes = poolSizes, MaxSets = MaxFramesInFlight };
        if (_vk.CreateDescriptorPool(_device, in poolInfo, null, out _particleDescPool) != Result.Success)
            throw new InvalidOperationException("vkCreateDescriptorPool (particle) failed.");

        var atlasImg = new DescriptorImageInfo
        {
            ImageLayout = ImageLayout.ShaderReadOnlyOptimal, ImageView = _atlasView, Sampler = _atlasSampler,
        };
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            CreateBuffer(uboSize, BufferUsageFlags.UniformBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _particleUbo[i], out _particleUboMem[i]);
            void* m;
            _vk.MapMemory(_device, _particleUboMem[i], 0, uboSize, 0, &m);
            _particleUboMapped[i] = m;
            var sl = _particleDescLayout;
            var ai = new DescriptorSetAllocateInfo { SType = StructureType.DescriptorSetAllocateInfo, DescriptorPool = _particleDescPool, DescriptorSetCount = 1, PSetLayouts = &sl };
            if (_vk.AllocateDescriptorSets(_device, in ai, out _particleDescSet[i]) != Result.Success)
                throw new InvalidOperationException("vkAllocateDescriptorSets (particle) failed.");
            var bi = new DescriptorBufferInfo { Buffer = _particleUbo[i], Offset = 0, Range = uboSize };
            var set = _particleDescSet[i];
            var imgLocal = atlasImg;
            var writes = stackalloc WriteDescriptorSet[2];
            writes[0] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 0, DstArrayElement = 0, DescriptorType = DescriptorType.UniformBuffer, DescriptorCount = 1, PBufferInfo = &bi };
            writes[1] = new WriteDescriptorSet { SType = StructureType.WriteDescriptorSet, DstSet = set, DstBinding = 1, DstArrayElement = 0, DescriptorType = DescriptorType.CombinedImageSampler, DescriptorCount = 1, PImageInfo = &imgLocal };
            _vk.UpdateDescriptorSets(_device, 2, writes, 0, null);
        }
        _particleReady = true;
    }

    private void CreateParticlePipeline()
    {
        var vsMod = CreateShaderModule(CompileGlsl(ParticleShaders.VulkanVertex, Silk.NET.Shaderc.ShaderKind.VertexShader, "particle.vert"));
        var fsMod = CreateShaderModule(CompileGlsl(ParticleShaders.VulkanFragment, Silk.NET.Shaderc.ShaderKind.FragmentShader, "particle.frag"));
        var entry = (byte*)SilkMarshal.StringToPtr("main");
        try
        {
            var stages = stackalloc PipelineShaderStageCreateInfo[2];
            stages[0] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.VertexBit, Module = vsMod, PName = entry };
            stages[1] = new PipelineShaderStageCreateInfo { SType = StructureType.PipelineShaderStageCreateInfo, Stage = ShaderStageFlags.FragmentBit, Module = fsMod, PName = entry };

            var bindings2 = stackalloc VertexInputBindingDescription[2];
            bindings2[0] = new VertexInputBindingDescription { Binding = 0, Stride = 8, InputRate = VertexInputRate.Vertex };
            bindings2[1] = new VertexInputBindingDescription { Binding = 1, Stride = ParticleInstanceStride, InputRate = VertexInputRate.Instance };
            var attrs = stackalloc VertexInputAttributeDescription[5];
            attrs[0] = new VertexInputAttributeDescription { Location = 0, Binding = 0, Format = Format.R32G32Sfloat, Offset = 0 };       // corner
            attrs[1] = new VertexInputAttributeDescription { Location = 1, Binding = 1, Format = Format.R32G32B32Sfloat, Offset = 0 };    // pos
            attrs[2] = new VertexInputAttributeDescription { Location = 2, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 12 };// uvRect
            attrs[3] = new VertexInputAttributeDescription { Location = 3, Binding = 1, Format = Format.R32Sfloat, Offset = 28 };         // layerSize
            attrs[4] = new VertexInputAttributeDescription { Location = 4, Binding = 1, Format = Format.R32G32B32A32Sfloat, Offset = 32 };// colorLight

            var vi = new PipelineVertexInputStateCreateInfo
            {
                SType = StructureType.PipelineVertexInputStateCreateInfo,
                VertexBindingDescriptionCount = 2, PVertexBindingDescriptions = bindings2,
                VertexAttributeDescriptionCount = 5, PVertexAttributeDescriptions = attrs,
            };
            var ia = new PipelineInputAssemblyStateCreateInfo { SType = StructureType.PipelineInputAssemblyStateCreateInfo, Topology = PrimitiveTopology.TriangleList };
            var vp = new PipelineViewportStateCreateInfo { SType = StructureType.PipelineViewportStateCreateInfo, ViewportCount = 1, ScissorCount = 1 };
            var dynStates = stackalloc DynamicState[2] { DynamicState.Viewport, DynamicState.Scissor };
            var dyn = new PipelineDynamicStateCreateInfo { SType = StructureType.PipelineDynamicStateCreateInfo, DynamicStateCount = 2, PDynamicStates = dynStates };
            var rast = new PipelineRasterizationStateCreateInfo { SType = StructureType.PipelineRasterizationStateCreateInfo, PolygonMode = PolygonMode.Fill, LineWidth = 1.0f, CullMode = CullModeFlags.None, FrontFace = FrontFace.Clockwise };
            var ms = new PipelineMultisampleStateCreateInfo { SType = StructureType.PipelineMultisampleStateCreateInfo, RasterizationSamples = SampleCountFlags.Count1Bit };
            // depth test ON, write OFF (particles blend over solid geometry).
            var ds = new PipelineDepthStencilStateCreateInfo { SType = StructureType.PipelineDepthStencilStateCreateInfo, DepthTestEnable = true, DepthWriteEnable = false, DepthCompareOp = CompareOp.LessOrEqual };
            var ab = new PipelineColorBlendAttachmentState
            {
                ColorWriteMask = ColorComponentFlags.RBit | ColorComponentFlags.GBit | ColorComponentFlags.BBit | ColorComponentFlags.ABit,
                BlendEnable = true, SrcColorBlendFactor = BlendFactor.SrcAlpha, DstColorBlendFactor = BlendFactor.OneMinusSrcAlpha, ColorBlendOp = BlendOp.Add,
                SrcAlphaBlendFactor = BlendFactor.One, DstAlphaBlendFactor = BlendFactor.OneMinusSrcAlpha, AlphaBlendOp = BlendOp.Add,
            };
            var blend = new PipelineColorBlendStateCreateInfo { SType = StructureType.PipelineColorBlendStateCreateInfo, AttachmentCount = 1, PAttachments = &ab };
            var info = new GraphicsPipelineCreateInfo
            {
                SType = StructureType.GraphicsPipelineCreateInfo, StageCount = 2, PStages = stages,
                PVertexInputState = &vi, PInputAssemblyState = &ia, PViewportState = &vp,
                PRasterizationState = &rast, PMultisampleState = &ms, PDepthStencilState = &ds,
                PColorBlendState = &blend, PDynamicState = &dyn, Layout = _particlePipelineLayout, RenderPass = _hdrRenderPass, Subpass = 0,
            };
            if (_vk.CreateGraphicsPipelines(_device, default, 1, in info, null, out _particlePipeline) != Result.Success)
                throw new InvalidOperationException("vkCreateGraphicsPipelines (particle) failed.");
        }
        finally
        {
            SilkMarshal.Free((nint)entry);
            _vk.DestroyShaderModule(_device, vsMod, null);
            _vk.DestroyShaderModule(_device, fsMod, null);
        }
    }

    private void RecordParticles(CommandBuffer cmd)
    {
        if (!_haveParticleFrame || _frameParticles == null) return;
        if (!_worldReady) return;
        if (!_particleReady) InitParticleRenderer();
        if (!_particleReady) return;

        var cam = _frameCam;
        int n = _frameParticles.Pack(cam.x, cam.y, cam.z);
        if (n == 0) return;
        var data = _frameParticles.Instances;

        int f = _currentFrame;
        ulong bytes = (ulong)n * ParticleInstanceStride;
        if (_particleInstSize[f] < bytes)
        {
            if (_particleInstBuf[f].Handle != 0) _vk.DestroyBuffer(_device, _particleInstBuf[f], null);
            if (_particleInstMem[f].Handle != 0) _vk.FreeMemory(_device, _particleInstMem[f], null);
            CreateBuffer(Math.Max(bytes * 2, (ulong)(256 * ParticleInstanceStride)), BufferUsageFlags.VertexBufferBit,
                MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
                out _particleInstBuf[f], out _particleInstMem[f]);
            _particleInstSize[f] = Math.Max(bytes * 2, (ulong)(256 * ParticleInstanceStride));
        }
        void* m;
        _vk.MapMemory(_device, _particleInstMem[f], 0, bytes, 0, &m);
        data.AsSpan(0, n * 12).CopyTo(new Span<float>(m, n * 12));
        _vk.UnmapMemory(_device, _particleInstMem[f]);

        // build the camera billboard frame (right = cam right xz; up = view up).
        var pr = new Vector3((float)PebbleCore.DetMath.detCos(cam.yaw), 0, (float)PebbleCore.DetMath.detSin(cam.yaw));
        var pu = new Vector3(
            (float)(PebbleCore.DetMath.detSin(cam.yaw) * PebbleCore.DetMath.detSin(cam.pitch)),
            (float)PebbleCore.DetMath.detCos(cam.pitch),
            (float)(-PebbleCore.DetMath.detCos(cam.yaw) * PebbleCore.DetMath.detSin(cam.pitch)));
        float dayLight = _haveSky ? _frameSky.DayLight : 1.0f;
        var u = new ParticleUniform
        {
            viewProj = BuildViewProj(cam),
            right = new Vector4(pr, 0),
            up = new Vector4(pu, dayLight),
        };
        System.Buffer.MemoryCopy(&u, _particleUboMapped[f], Marshal.SizeOf<ParticleUniform>(), Marshal.SizeOf<ParticleUniform>());

        var viewport = new Viewport(0, 0, _swapchainExtent.Width, _swapchainExtent.Height, 0f, 1f);
        _vk.CmdSetViewport(cmd, 0, 1, in viewport);
        var scissor = new Rect2D(new Offset2D(0, 0), _swapchainExtent);
        _vk.CmdSetScissor(cmd, 0, 1, in scissor);
        _vk.CmdBindPipeline(cmd, PipelineBindPoint.Graphics, _particlePipeline);
        var set2 = _particleDescSet[f];
        _vk.CmdBindDescriptorSets(cmd, PipelineBindPoint.Graphics, _particlePipelineLayout, 0, 1, in set2, 0, null);
        var bufs = stackalloc Buffer[2] { _particleQuadBuf, _particleInstBuf[f] };
        var offs = stackalloc ulong[2] { 0, 0 };
        _vk.CmdBindVertexBuffers(cmd, 0, 2, bufs, offs);
        _vk.CmdDraw(cmd, 6, (uint)n, 0, 0);
    }

    private void DestroyEntityModel(EntityModelGpu g)
    {
        if (g.vbuf.Handle != 0) _vk.DestroyBuffer(_device, g.vbuf, null);
        if (g.vmem.Handle != 0) _vk.FreeMemory(_device, g.vmem, null);
        if (g.skinSampler.Handle != 0) _vk.DestroySampler(_device, g.skinSampler, null);
        if (g.skinView.Handle != 0) _vk.DestroyImageView(_device, g.skinView, null);
        if (g.skinImage.Handle != 0) _vk.DestroyImage(_device, g.skinImage, null);
        if (g.skinMem.Handle != 0) _vk.FreeMemory(_device, g.skinMem, null);
        g.vbuf = default; g.vmem = default; g.skinSampler = default;
        g.skinView = default; g.skinImage = default; g.skinMem = default;
    }

    private void CleanupEntityRenderer()
    {
        foreach (var g in _entityModels.Values) DestroyEntityModel(g);
        _entityModels.Clear();
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_entityUBuf[i].Handle != 0) { _vk.DestroyBuffer(_device, _entityUBuf[i], null); _entityUBuf[i] = default; }
            if (_entityUMem[i].Handle != 0) { _vk.FreeMemory(_device, _entityUMem[i], null); _entityUMem[i] = default; }
            _entityUSizeBytes[i] = 0;
        }
        if (_entityPipeline.Handle != 0) { _vk.DestroyPipeline(_device, _entityPipeline, null); _entityPipeline = default; }
        if (_entityPipelineLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _entityPipelineLayout, null); _entityPipelineLayout = default; }
        if (_entityDescPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _entityDescPool, null); _entityDescPool = default; }
        if (_entityDescLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _entityDescLayout, null); _entityDescLayout = default; }
        _entityReady = false;
    }

    public bool RequestScreenshot(string path)
    {
        _screenshotPath = path;
        return true;
    }

    /// Copy the just-rendered swapchain color image to a host-visible buffer and
    /// encode it as PNG. Called inside EndFrame AFTER the render pass (image is in
    /// PRESENT_SRC layout) but BEFORE present so the contents are stable.
    private void CaptureScreenshot(CommandBuffer cmd)
    {
        uint w = _swapchainExtent.Width, h = _swapchainExtent.Height;
        ulong size = (ulong)w * h * 4;
        CreateBuffer(size, BufferUsageFlags.TransferDstBit,
            MemoryPropertyFlags.HostVisibleBit | MemoryPropertyFlags.HostCoherentBit,
            out Buffer readback, out DeviceMemory readbackMem);

        var image = _swapchainImages[_imageIndex];
        var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1);

        // PRESENT_SRC -> TRANSFER_SRC
        var toSrc = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.PresentSrcKhr,
            NewLayout = ImageLayout.TransferSrcOptimal,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.MemoryReadBit,
            DstAccessMask = AccessFlags.TransferReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.TransferBit,
            0, 0, null, 0, null, 1, in toSrc);

        var copy = new BufferImageCopy
        {
            BufferOffset = 0,
            BufferRowLength = 0,
            BufferImageHeight = 0,
            ImageSubresource = new ImageSubresourceLayers(ImageAspectFlags.ColorBit, 0, 0, 1),
            ImageOffset = new Offset3D(0, 0, 0),
            ImageExtent = new Extent3D(w, h, 1),
        };
        _vk.CmdCopyImageToBuffer(cmd, image, ImageLayout.TransferSrcOptimal, readback, 1, in copy);

        // TRANSFER_SRC -> PRESENT_SRC (so present is still valid)
        var toPresent = new ImageMemoryBarrier
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = ImageLayout.TransferSrcOptimal,
            NewLayout = ImageLayout.PresentSrcKhr,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = range,
            SrcAccessMask = AccessFlags.TransferReadBit,
            DstAccessMask = AccessFlags.MemoryReadBit,
        };
        _vk.CmdPipelineBarrier(cmd, PipelineStageFlags.TransferBit, PipelineStageFlags.BottomOfPipeBit,
            0, 0, null, 0, null, 1, in toPresent);

        // We finish/submit the command buffer in EndFrame and wait there before
        // reading. Stash the readback buffer/mem + format for post-submit encode.
        _pendingReadback = (readback, readbackMem, w, h, _swapchainFormat, _screenshotPath!);
        _screenshotPath = null;
    }

    private (Buffer buf, DeviceMemory mem, uint w, uint h, Format fmt, string path)? _pendingReadback;

    private void EncodePendingScreenshot()
    {
        if (_pendingReadback == null) return;
        var (buf, mem, w, h, fmt, path) = _pendingReadback.Value;
        _pendingReadback = null;

        ulong size = (ulong)w * h * 4;
        void* mapped;
        _vk.MapMemory(_device, mem, 0, size, 0, &mapped);
        var src = new ReadOnlySpan<byte>(mapped, (int)size);

        // Swapchain format is typically B8G8R8A8 (sRGB or UNORM); ImageSharp wants RGBA8.
        bool bgr = fmt is Format.B8G8R8A8Srgb or Format.B8G8R8A8Unorm;
        var rgba = new byte[size];
        for (int i = 0; i < (int)(w * h); i++)
        {
            int o = i * 4;
            byte b0 = src[o], b1 = src[o + 1], b2 = src[o + 2], a = src[o + 3];
            if (bgr) { rgba[o] = b2; rgba[o + 1] = b1; rgba[o + 2] = b0; rgba[o + 3] = a; }
            else { rgba[o] = b0; rgba[o + 1] = b1; rgba[o + 2] = b2; rgba[o + 3] = a; }
        }
        _vk.UnmapMemory(_device, mem);

        using (var img = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
            rgba, (int)w, (int)h))
        using (var fs = System.IO.File.Create(path))
        {
            img.Save(fs, new SixLabors.ImageSharp.Formats.Png.PngEncoder());
        }

        _vk.DestroyBuffer(_device, buf, null);
        _vk.FreeMemory(_device, mem, null);
        _screenshotComplete = true;
        Console.WriteLine($"[Vulkan] screenshot written: {path} ({w}x{h})");
    }

    // ───────────────────────────────────────────────────────────────── Per-frame

    public void BeginFrame()
    {
        _frameActive = false;

        // If a resize/out-of-date was flagged, rebuild before touching the swapchain.
        if (_framebufferResized)
        {
            RecreateSwapchain();
            _framebufferResized = false;
        }

        // No drawable surface (minimized): skip this frame.
        if (_swapchainExtent.Width == 0 || _swapchainExtent.Height == 0)
            return;

        var fence = _inFlightFences[_currentFrame];
        _vk.WaitForFences(_device, 1, in fence, true, ulong.MaxValue);

        uint imageIndex = 0;
        var acquire = _khrSwapchain.AcquireNextImage(
            _device, _swapchain, ulong.MaxValue,
            _imageAvailable[_currentFrame], default, ref imageIndex);

        if (acquire == Result.ErrorOutOfDateKhr)
        {
            _framebufferResized = true;
            return; // rebuild next BeginFrame; nothing recorded this frame
        }
        if (acquire != Result.Success && acquire != Result.SuboptimalKhr)
            throw new InvalidOperationException($"vkAcquireNextImageKHR failed: {acquire}");

        _imageIndex = imageIndex;

        // Only reset the fence once we know we'll submit work that signals it.
        _vk.ResetFences(_device, 1, in fence);

        var cmd = _commandBuffers[_currentFrame];
        _vk.ResetCommandBuffer(cmd, 0);

        var beginInfo = new CommandBufferBeginInfo { SType = StructureType.CommandBufferBeginInfo };
        if (_vk.BeginCommandBuffer(cmd, in beginInfo) != Result.Success)
            throw new InvalidOperationException("vkBeginCommandBuffer failed.");

        _frameActive = true;
    }

    public void ClearColor(float r, float g, float b, float a)
    {
        // Captured at EndFrame as the render pass attachment clear value.
        _clearValue = new ClearColorValue(r, g, b, a);
    }

    public void EndFrame()
    {
        if (!_frameActive)
            return; // frame was skipped (minimized / out-of-date); nothing to submit

        var cmd = _commandBuffers[_currentFrame];

        // Advance + flush animated tiles (water/lava/fire/portal) BEFORE the render
        // pass so the slice copies are GPU-ordered ahead of this frame's sampling.
        if (_animator != null && _animator.HasAnimations)
        {
            double now = _frameTime;
            double dtMs = _lastAnimTime < 0 ? 50.0 : Math.Max(0, (now - _lastAnimTime) * 1000.0);
            _lastAnimTime = now;
            _animator.Tick(dtMs);
            FlushAtlasAnimations(cmd);
        }

        // Reconcile the GPU section cache ONCE up front, BEFORE the shadow pass.
        // SyncSections frees/destroys GPU buffers for changed/removed sections; the
        // shadow pass records draws against this cache. If the sync ran later (inside
        // RecordWorld), the shadow pass would record draws against buffers SyncSections
        // then destroyed in the same command buffer → use-after-free → device-loss.
        // This is the root cause that forced shadows off; ordering the sync first
        // makes both the shadow pass and the world pass draw only valid buffers.
        if (_haveWorldFrame && _frameHost != null)
        {
            if (!_worldReady)
                InitWorldRenderer();
            if (_worldReady)
            {
                if (!_postReady) InitPostRenderer();
                SyncSections(_frameHost);
                _sectionsSynced = true;
            }
        }

        // Sun shadow depth pass (separate render pass) BEFORE the main pass, so the
        // chunk_fs can sample the shadow map in the main pass. On by default.
        RecordShadowPass(cmd);

        bool usePost = _postReady;
        if (usePost)
        {
            // ── HDR scene pass: all 3D into the offscreen R16F target + depth ──
            var hdrClears = stackalloc ClearValue[2];
            hdrClears[0] = new ClearValue { Color = _clearValue };
            hdrClears[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1.0f, 0) };
            var hdrBegin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo, RenderPass = _hdrRenderPass,
                Framebuffer = _hdrFramebuffer[_currentFrame],
                RenderArea = new Rect2D(new Offset2D(0, 0), _swapchainExtent),
                ClearValueCount = 2, PClearValues = hdrClears,
            };
            _vk.CmdBeginRenderPass(cmd, in hdrBegin, SubpassContents.Inline);
            RecordSky(cmd);
            RecordWorld(cmd);
            RecordEntities(cmd);
            RecordBreakOverlay(cmd);
            RecordSprites(cmd);
            RecordParticles(cmd);
            RecordClouds(cmd);
            _vk.CmdEndRenderPass(cmd);

            // ── post chain (ultra -> bloom), then composite + UI into the swapchain ──
            RecordPost(cmd);

            var swapClears = stackalloc ClearValue[2];
            swapClears[0] = new ClearValue { Color = _clearValue };
            swapClears[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1.0f, 0) };
            var swapBegin = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo, RenderPass = _renderPass,
                Framebuffer = _framebuffers[_imageIndex],
                RenderArea = new Rect2D(new Offset2D(0, 0), _swapchainExtent),
                ClearValueCount = 2, PClearValues = swapClears,
            };
            _vk.CmdBeginRenderPass(cmd, in swapBegin, SubpassContents.Inline);
            RecordComposite(cmd);
            RecordUi(cmd);
            _vk.CmdEndRenderPass(cmd);
        }
        else
        {
            // Fallback (post not yet ready — e.g. title screen / first world frame):
            // render the scene straight to the swapchain like the pre-HDR path.
            var clearValues = stackalloc ClearValue[2];
            clearValues[0] = new ClearValue { Color = _clearValue };
            clearValues[1] = new ClearValue { DepthStencil = new ClearDepthStencilValue(1.0f, 0) };
            var renderPassInfo = new RenderPassBeginInfo
            {
                SType = StructureType.RenderPassBeginInfo,
                RenderPass = _renderPass,
                Framebuffer = _framebuffers[_imageIndex],
                RenderArea = new Rect2D(new Offset2D(0, 0), _swapchainExtent),
                ClearValueCount = 2,
                PClearValues = clearValues,
            };
            _vk.CmdBeginRenderPass(cmd, in renderPassInfo, SubpassContents.Inline);
            RecordUi(cmd);
            _vk.CmdEndRenderPass(cmd);
        }
        _haveWorldFrame = false;
        _sectionsSynced = false;
        _haveEntityFrame = false;
        _haveSky = false;
        _haveOverlayFrame = false;
        _haveSpriteFrame = false;
        _haveParticleFrame = false;

        // Screenshot: after the render pass the swapchain image is in PRESENT_SRC;
        // copy it to a host-visible buffer before present.
        bool capturing = _screenshotPath != null;
        if (capturing)
            CaptureScreenshot(cmd);

        if (_vk.EndCommandBuffer(cmd) != Result.Success)
            throw new InvalidOperationException("vkEndCommandBuffer failed.");

        var waitSemaphore = _imageAvailable[_currentFrame];
        var signalSemaphore = _renderFinished[_currentFrame];
        var waitStage = PipelineStageFlags.ColorAttachmentOutputBit;

        var submitInfo = new SubmitInfo
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore,
        };

        var submitResult = _vk.QueueSubmit(_graphicsQueue, 1, in submitInfo, _inFlightFences[_currentFrame]);
        if (submitResult != Result.Success)
            throw new InvalidOperationException($"vkQueueSubmit failed: {submitResult}");

        var swapchain = _swapchain;
        var imageIndex = _imageIndex;
        var presentInfo = new PresentInfoKHR
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchain,
            PImageIndices = &imageIndex,
        };

        var present = _khrSwapchain.QueuePresent(_presentQueue, in presentInfo);
        if (present == Result.ErrorOutOfDateKhr || present == Result.SuboptimalKhr)
            _framebufferResized = true;
        else if (present != Result.Success)
            throw new InvalidOperationException($"vkQueuePresentKHR failed: {present}");

        // If we captured this frame, wait for the GPU to finish the copy, then
        // encode the readback buffer to PNG on the CPU.
        if (capturing && _pendingReadback != null)
        {
            var capFence = _inFlightFences[_currentFrame];
            _vk.WaitForFences(_device, 1, in capFence, true, ulong.MaxValue);
            EncodePendingScreenshot();
        }

        _frameActive = false;
        _currentFrame = (_currentFrame + 1) % MaxFramesInFlight;
    }

    // ───────────────────────────────────────────────────────────────── Resize

    public void Resize(int width, int height)
    {
        // Guard minimized / zero-size: defer until we have a real surface again.
        if (width <= 0 || height <= 0)
        {
            _framebufferResized = true;
            return;
        }
        RecreateSwapchain();
    }

    private void RecreateSwapchain()
    {
        // If the framebuffer is currently zero-sized (minimized), don't build a
        // swapchain — flag so the next non-zero BeginFrame/Resize rebuilds.
        var fb = _window.FramebufferSize;
        if (fb.X == 0 || fb.Y == 0)
        {
            _framebufferResized = true;
            return;
        }

        _vk.DeviceWaitIdle(_device);

        CleanupSwapchain();

        CreateSwapchain();
        CreateImageViews();
        CreateDepthResources();
        CreateRenderPass();
        CreateFramebuffers();
        // Recreate the framebuffer-sized HDR/bloom/ultra targets (the HDR render
        // passes + sampler persist). Post descriptor sets reference the views, so
        // refresh them too.
        CreateHdrResources();
        if (_postReady) RefreshPostDescriptorSets();
        // The entity pipeline also references the (recreated) render pass.
        if (_entityReady)
        {
            if (_entityPipeline.Handle != 0) { _vk.DestroyPipeline(_device, _entityPipeline, null); _entityPipeline = default; }
            CreateEntityPipeline();
        }
        // The UI pipeline also references the (recreated) render pass.
        if (_uiReady)
        {
            if (_uiPipeline.Handle != 0) { _vk.DestroyPipeline(_device, _uiPipeline, null); _uiPipeline = default; }
            CreateUiPipeline();
        }

        _framebufferResized = false;
    }

    private void CleanupSwapchain()
    {
        if (_framebuffers.Length > 0)
        {
            foreach (var fb in _framebuffers)
                if (fb.Handle != 0) _vk.DestroyFramebuffer(_device, fb, null);
            _framebuffers = Array.Empty<Framebuffer>();
        }

        // Depth attachments (per swapchain image).
        for (int i = 0; i < _depthViews.Length; i++)
            if (_depthViews[i].Handle != 0) _vk.DestroyImageView(_device, _depthViews[i], null);
        for (int i = 0; i < _depthImages.Length; i++)
            if (_depthImages[i].Handle != 0) _vk.DestroyImage(_device, _depthImages[i], null);
        for (int i = 0; i < _depthMemories.Length; i++)
            if (_depthMemories[i].Handle != 0) _vk.FreeMemory(_device, _depthMemories[i], null);
        _depthViews = Array.Empty<ImageView>();
        _depthImages = Array.Empty<Image>();
        _depthMemories = Array.Empty<DeviceMemory>();

        if (_renderPass.Handle != 0)
        {
            _vk.DestroyRenderPass(_device, _renderPass, null);
            _renderPass = default;
        }

        if (_swapchainImageViews.Length > 0)
        {
            foreach (var view in _swapchainImageViews)
                if (view.Handle != 0) _vk.DestroyImageView(_device, view, null);
            _swapchainImageViews = Array.Empty<ImageView>();
        }

        if (_swapchain.Handle != 0)
        {
            _khrSwapchain.DestroySwapchain(_device, _swapchain, null);
            _swapchain = default;
        }
    }

    private void CleanupWorldRenderer()
    {
        // Section GPU buffers.
        foreach (var l in _sectionCache.Values)
        {
            FreeSectionGpu(l.opaque);
            FreeSectionGpu(l.cutout);
        }
        _sectionCache.Clear();

        if (_pendingReadback != null)
        {
            var r = _pendingReadback.Value;
            _vk.DestroyBuffer(_device, r.buf, null);
            _vk.FreeMemory(_device, r.mem, null);
            _pendingReadback = null;
        }

        if (_opaquePipeline.Handle != 0) { _vk.DestroyPipeline(_device, _opaquePipeline, null); _opaquePipeline = default; }
        if (_translucentPipeline.Handle != 0) { _vk.DestroyPipeline(_device, _translucentPipeline, null); _translucentPipeline = default; }
        if (_pipelineLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _pipelineLayout, null); _pipelineLayout = default; }
        if (_descPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _descPool, null); _descPool = default; }
        if (_descSetLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _descSetLayout, null); _descSetLayout = default; }

        if (_atlasSampler.Handle != 0) { _vk.DestroySampler(_device, _atlasSampler, null); _atlasSampler = default; }
        if (_atlasView.Handle != 0) { _vk.DestroyImageView(_device, _atlasView, null); _atlasView = default; }
        if (_atlasImage.Handle != 0) { _vk.DestroyImage(_device, _atlasImage, null); _atlasImage = default; }
        if (_atlasMemory.Handle != 0) { _vk.FreeMemory(_device, _atlasMemory, null); _atlasMemory = default; }

        if (_animStagingMapped != null) { _vk.UnmapMemory(_device, _animStagingMem); _animStagingMapped = null; }
        if (_animStaging.Handle != 0) { _vk.DestroyBuffer(_device, _animStaging, null); _animStaging = default; }
        if (_animStagingMem.Handle != 0) { _vk.FreeMemory(_device, _animStagingMem, null); _animStagingMem = default; }
        _animator = null;

        if (_uboMapped != null) { _vk.UnmapMemory(_device, _uboMemory); _uboMapped = null; }
        if (_uboBuffer.Handle != 0) { _vk.DestroyBuffer(_device, _uboBuffer, null); _uboBuffer = default; }
        if (_uboMemory.Handle != 0) { _vk.FreeMemory(_device, _uboMemory, null); _uboMemory = default; }

        // Sun shadow map (double-buffered: per-frame image/view/framebuffer + UBO).
        if (_shadowReady)
        {
            if (_shadowPipeline.Handle != 0) { _vk.DestroyPipeline(_device, _shadowPipeline, null); _shadowPipeline = default; }
            if (_shadowPipelineLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _shadowPipelineLayout, null); _shadowPipelineLayout = default; }
            if (_shadowDescLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _shadowDescLayout, null); _shadowDescLayout = default; }
            if (_shadowSampler.Handle != 0) { _vk.DestroySampler(_device, _shadowSampler, null); _shadowSampler = default; }
            if (_shadowRenderPass.Handle != 0) { _vk.DestroyRenderPass(_device, _shadowRenderPass, null); _shadowRenderPass = default; }
            for (int f = 0; f < MaxFramesInFlight; f++)
            {
                if (_shadowFramebuffer[f].Handle != 0) { _vk.DestroyFramebuffer(_device, _shadowFramebuffer[f], null); _shadowFramebuffer[f] = default; }
                if (_shadowView[f].Handle != 0) { _vk.DestroyImageView(_device, _shadowView[f], null); _shadowView[f] = default; }
                if (_shadowImage[f].Handle != 0) { _vk.DestroyImage(_device, _shadowImage[f], null); _shadowImage[f] = default; }
                if (_shadowMemory[f].Handle != 0) { _vk.FreeMemory(_device, _shadowMemory[f], null); _shadowMemory[f] = default; }
                if (_shadowUboMapped[f] != null) { _vk.UnmapMemory(_device, _shadowUboMem[f]); _shadowUboMapped[f] = null; }
                if (_shadowUbo[f].Handle != 0) { _vk.DestroyBuffer(_device, _shadowUbo[f], null); _shadowUbo[f] = default; }
                if (_shadowUboMem[f].Handle != 0) { _vk.FreeMemory(_device, _shadowUboMem[f], null); _shadowUboMem[f] = default; }
            }
            _shadowReady = false;
        }

        _worldReady = false;

        CleanupEntityRenderer();
        CleanupUiRenderer();
        CleanupFxRenderers();
    }

    private void CleanupFxRenderers()
    {
        // particles
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_particleInstBuf[i].Handle != 0) { _vk.DestroyBuffer(_device, _particleInstBuf[i], null); _particleInstBuf[i] = default; }
            if (_particleInstMem[i].Handle != 0) { _vk.FreeMemory(_device, _particleInstMem[i], null); _particleInstMem[i] = default; }
            if (_particleUbo[i].Handle != 0) { _vk.DestroyBuffer(_device, _particleUbo[i], null); _particleUbo[i] = default; }
            if (_particleUboMem[i].Handle != 0) { _vk.FreeMemory(_device, _particleUboMem[i], null); _particleUboMem[i] = default; }
        }
        if (_particleQuadBuf.Handle != 0) { _vk.DestroyBuffer(_device, _particleQuadBuf, null); _particleQuadBuf = default; }
        if (_particleQuadMem.Handle != 0) { _vk.FreeMemory(_device, _particleQuadMem, null); _particleQuadMem = default; }
        if (_particlePipeline.Handle != 0) { _vk.DestroyPipeline(_device, _particlePipeline, null); _particlePipeline = default; }
        if (_particlePipelineLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _particlePipelineLayout, null); _particlePipelineLayout = default; }
        if (_particleDescPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _particleDescPool, null); _particleDescPool = default; }
        if (_particleDescLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _particleDescLayout, null); _particleDescLayout = default; }
        _particleReady = false;

        // sprites
        for (int i = 0; i < _spriteUbo.Length; i++)
        {
            if (_spriteUbo[i].Handle != 0) { _vk.DestroyBuffer(_device, _spriteUbo[i], null); _spriteUbo[i] = default; }
            if (_spriteUboMem[i].Handle != 0) { _vk.FreeMemory(_device, _spriteUboMem[i], null); _spriteUboMem[i] = default; }
        }
        if (_spritePipeline.Handle != 0) { _vk.DestroyPipeline(_device, _spritePipeline, null); _spritePipeline = default; }
        if (_spritePipelineLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _spritePipelineLayout, null); _spritePipelineLayout = default; }
        if (_spriteDescPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _spriteDescPool, null); _spriteDescPool = default; }
        if (_spriteDescLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _spriteDescLayout, null); _spriteDescLayout = default; }
        _spriteReady = false;

        // break overlay (uses the world pipeline; only its per-frame buffers are ours)
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_overlayVbuf[i].Handle != 0) { _vk.DestroyBuffer(_device, _overlayVbuf[i], null); _overlayVbuf[i] = default; }
            if (_overlayVmem[i].Handle != 0) { _vk.FreeMemory(_device, _overlayVmem[i], null); _overlayVmem[i] = default; }
            if (_overlayIbuf[i].Handle != 0) { _vk.DestroyBuffer(_device, _overlayIbuf[i], null); _overlayIbuf[i] = default; }
            if (_overlayImem[i].Handle != 0) { _vk.FreeMemory(_device, _overlayImem[i], null); _overlayImem[i] = default; }
        }

        // stars + clouds
        if (_starsPipeline.Handle != 0) { _vk.DestroyPipeline(_device, _starsPipeline, null); _starsPipeline = default; }
        if (_cloudPipeline.Handle != 0) { _vk.DestroyPipeline(_device, _cloudPipeline, null); _cloudPipeline = default; }
        if (_cloudPipelineLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _cloudPipelineLayout, null); _cloudPipelineLayout = default; }
        if (_cloudDescLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _cloudDescLayout, null); _cloudDescLayout = default; }
        if (_skyExtraDescPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _skyExtraDescPool, null); _skyExtraDescPool = default; }
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_starsUbo[i].Handle != 0) { _vk.DestroyBuffer(_device, _starsUbo[i], null); _starsUbo[i] = default; }
            if (_starsUboMem[i].Handle != 0) { _vk.FreeMemory(_device, _starsUboMem[i], null); _starsUboMem[i] = default; }
            if (_cloudUbo[i].Handle != 0) { _vk.DestroyBuffer(_device, _cloudUbo[i], null); _cloudUbo[i] = default; }
            if (_cloudUboMem[i].Handle != 0) { _vk.FreeMemory(_device, _cloudUboMem[i], null); _cloudUboMem[i] = default; }
        }
        if (_starsVbuf.Handle != 0) { _vk.DestroyBuffer(_device, _starsVbuf, null); _starsVbuf = default; }
        if (_starsVmem.Handle != 0) { _vk.FreeMemory(_device, _starsVmem, null); _starsVmem = default; }
        if (_cloudSampler.Handle != 0) { _vk.DestroySampler(_device, _cloudSampler, null); _cloudSampler = default; }
        if (_cloudView.Handle != 0) { _vk.DestroyImageView(_device, _cloudView, null); _cloudView = default; }
        if (_cloudImage.Handle != 0) { _vk.DestroyImage(_device, _cloudImage, null); _cloudImage = default; }
        if (_cloudMemory.Handle != 0) { _vk.FreeMemory(_device, _cloudMemory, null); _cloudMemory = default; }
        _skyExtraReady = false;
    }

    private void CleanupUiRenderer()
    {
        for (int i = 0; i < MaxFramesInFlight; i++)
        {
            if (_uiVbuf[i].Handle != 0) { _vk.DestroyBuffer(_device, _uiVbuf[i], null); _uiVbuf[i] = default; }
            if (_uiVmem[i].Handle != 0) { _vk.FreeMemory(_device, _uiVmem[i], null); _uiVmem[i] = default; }
            _uiVbufSize[i] = 0;
        }
        if (_uiPipeline.Handle != 0) { _vk.DestroyPipeline(_device, _uiPipeline, null); _uiPipeline = default; }
        if (_uiPipelineLayout.Handle != 0) { _vk.DestroyPipelineLayout(_device, _uiPipelineLayout, null); _uiPipelineLayout = default; }
        if (_uiDescPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _uiDescPool, null); _uiDescPool = default; }
        if (_uiDescSetLayout.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, _uiDescSetLayout, null); _uiDescSetLayout = default; }
        if (_iconSampler.Handle != 0) { _vk.DestroySampler(_device, _iconSampler, null); _iconSampler = default; }
        if (_iconView.Handle != 0) { _vk.DestroyImageView(_device, _iconView, null); _iconView = default; }
        if (_iconImage.Handle != 0) { _vk.DestroyImage(_device, _iconImage, null); _iconImage = default; }
        if (_iconMemory.Handle != 0) { _vk.FreeMemory(_device, _iconMemory, null); _iconMemory = default; }
        _iconTextureReady = false;
        _uiReady = false;
    }

    // ───────────────────────────────────────────────────────────────── Dispose

    private void CleanupPostRenderer()
    {
        DestroyHdrImages();
        if (_hdrRenderPass.Handle != 0) { _vk.DestroyRenderPass(_device, _hdrRenderPass, null); _hdrRenderPass = default; }
        if (_postColorPass.Handle != 0) { _vk.DestroyRenderPass(_device, _postColorPass, null); _postColorPass = default; }
        if (_postSampler.Handle != 0) { _vk.DestroySampler(_device, _postSampler, null); _postSampler = default; }
        if (!_postReady) return;

        void DP(ref Pipeline p) { if (p.Handle != 0) { _vk.DestroyPipeline(_device, p, null); p = default; } }
        DP(ref _bloomExtractPipeline); DP(ref _blurPipeline); DP(ref _ultraPipeline);
        DP(ref _ultraBlurPipeline); DP(ref _compositePipeline);
        void DPL(ref PipelineLayout p) { if (p.Handle != 0) { _vk.DestroyPipelineLayout(_device, p, null); p = default; } }
        DPL(ref _postOneTexPipeLayout); DPL(ref _ultraPipeLayout); DPL(ref _compositePipeLayout);
        void DSL(ref DescriptorSetLayout p) { if (p.Handle != 0) { _vk.DestroyDescriptorSetLayout(_device, p, null); p = default; } }
        DSL(ref _postOneTexLayout); DSL(ref _ultraLayout); DSL(ref _compositeLayout);
        if (_postDescPool.Handle != 0) { _vk.DestroyDescriptorPool(_device, _postDescPool, null); _postDescPool = default; }
        for (int f = 0; f < MaxFramesInFlight; f++)
        {
            if (_compositeUboMapped[f] != null) { _vk.UnmapMemory(_device, _compositeUboMem[f]); _compositeUboMapped[f] = null; }
            if (_compositeUbo[f].Handle != 0) { _vk.DestroyBuffer(_device, _compositeUbo[f], null); _compositeUbo[f] = default; }
            if (_compositeUboMem[f].Handle != 0) { _vk.FreeMemory(_device, _compositeUboMem[f], null); _compositeUboMem[f] = default; }
            if (_ultraUboMapped[f] != null) { _vk.UnmapMemory(_device, _ultraUboMem[f]); _ultraUboMapped[f] = null; }
            if (_ultraUbo[f].Handle != 0) { _vk.DestroyBuffer(_device, _ultraUbo[f], null); _ultraUbo[f] = default; }
            if (_ultraUboMem[f].Handle != 0) { _vk.FreeMemory(_device, _ultraUboMem[f], null); _ultraUboMem[f] = default; }
            for (int d = 0; d < 4; d++)
            {
                int i = f * 4 + d;
                if (_blurUboMapped[i] != null) { _vk.UnmapMemory(_device, _blurUboMem[i]); _blurUboMapped[i] = null; }
                if (_blurUbo[i].Handle != 0) { _vk.DestroyBuffer(_device, _blurUbo[i], null); _blurUbo[i] = default; }
                if (_blurUboMem[i].Handle != 0) { _vk.FreeMemory(_device, _blurUboMem[i], null); _blurUboMem[i] = default; }
            }
        }
        _postReady = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Device may not have been created if Initialize threw early.
        if (_device.Handle != 0)
            _vk.DeviceWaitIdle(_device);

        // Sync objects.
        for (int i = 0; i < _inFlightFences.Length; i++)
            if (_inFlightFences[i].Handle != 0) _vk.DestroyFence(_device, _inFlightFences[i], null);
        for (int i = 0; i < _renderFinished.Length; i++)
            if (_renderFinished[i].Handle != 0) _vk.DestroySemaphore(_device, _renderFinished[i], null);
        for (int i = 0; i < _imageAvailable.Length; i++)
            if (_imageAvailable[i].Handle != 0) _vk.DestroySemaphore(_device, _imageAvailable[i], null);
        _inFlightFences = Array.Empty<Fence>();
        _renderFinished = Array.Empty<Semaphore>();
        _imageAvailable = Array.Empty<Semaphore>();

        // World renderer (pipelines, descriptors, atlas, UBO, section buffers).
        if (_device.Handle != 0)
            CleanupWorldRenderer();

        // Offscreen-HDR + post chain.
        if (_device.Handle != 0)
            CleanupPostRenderer();

        // Command pool (frees its command buffers).
        if (_commandPool.Handle != 0)
        {
            _vk.DestroyCommandPool(_device, _commandPool, null);
            _commandPool = default;
        }

        // Swapchain-dependent objects (framebuffers, render pass, views, swapchain).
        if (_device.Handle != 0)
            CleanupSwapchain();

        // Device.
        if (_device.Handle != 0)
        {
            _vk.DestroyDevice(_device, null);
            _device = default;
        }

        // Debug messenger.
        if (_debugUtils is not null && _debugMessenger.Handle != 0)
        {
            _debugUtils.DestroyDebugUtilsMessenger(_instance, _debugMessenger, null);
            _debugMessenger = default;
        }
        _debugUtils?.Dispose();

        // Surface.
        if (_khrSurface is not null && _surface.Handle != 0)
        {
            _khrSurface.DestroySurface(_instance, _surface, null);
            _surface = default;
        }

        // Extension wrappers + instance.
        _khrSwapchain?.Dispose();
        _khrSurface?.Dispose();

        if (_instance.Handle != 0)
        {
            _vk.DestroyInstance(_instance, null);
            _instance = default;
        }

        _vk.Dispose();
    }
}
