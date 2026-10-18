using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Keen.VRage.Library.Mathematics;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.PostProcessStage.Upsampling.FSR4;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.Resources.Utilities;

namespace SE2VR.Rendering;

/// <summary>
/// Gives each eye its own history for the engine's temporal effects: FSR (anti-aliasing and upscaling), clouds, water
/// effects, stochastic transparency and raytraced GI.
/// <para>
/// FSR keeps its history inside an upscaler context, the other effects in ping-pong pairs (<c>TemporalResource</c>)
/// that flip on every scene draw; all of them reproject last frame's result with the previous camera. Drawing two eyes
/// per frame from one history hands each eye the other eye's result, rendered from another position with a very
/// different asymmetric projection (the eyes' optical centers sit at about a third and two thirds of the image), so it
/// is misplaced or rejected: clouds turn into noisy blotches and FSR smears. Here the right eye gets its own FSR
/// context and its own pairs; they are exchanged with the engine's around the right eye's draw, and each eye's
/// previous view is its own last frame.
/// </para>
/// <para>
/// The pairs are found by walking the renderer's objects (fields only, a few levels deep); the paths are remembered,
/// so each frame only follows a handful of field references. A right-eye pair is only made for pairs the left eye's
/// draw actually used (water effects, for example, only run near water) and is released again after a while unused,
/// which keeps the extra video memory down.
/// </para>
/// Render thread only.
/// </summary>
internal static class EyeHistory
{
    private sealed class Path
    {
        public required object Root { get; init; }
        public required FieldInfo[] Fields { get; init; }

        public TemporalResource<ResizableRWRenderTargetTexture>? Resolve()
        {
            object? current = Root;
            foreach (FieldInfo field in Fields)
            {
                current = current == null ? null : field.GetValue(current);
            }
            return current as TemporalResource<ResizableRWRenderTargetTexture>;
        }
    }

    /// <summary>The right eye's pair for one engine pair, held here between right-eye draws.</summary>
    private sealed class Pair
    {
        public ResizableRWRenderTargetTexture? Resource0;
        public ResizableRWRenderTargetTexture? Resource1;
        public bool Swapped;
        public long Bytes;
        public long LastUsedFrame;

        public void Dispose()
        {
            Resource0?.Dispose();
            Resource1?.Dispose();
            Resource0 = Resource1 = null;
        }
    }

    private const int MaxDepth = 4;
    private const int RediscoverEveryFrames = 600;
    private const int ReleaseUnusedAfterFrames = 900;

    private static readonly List<Path> _paths = new();
    private static readonly Dictionary<TemporalResource<ResizableRWRenderTargetTexture>, Pair> _pairs = new(ReferenceEqualityComparer.Instance);
    /// <summary>Each live engine pair and its state before the left eye's draw; a pair the draw used has flipped since.</summary>
    private static readonly Dictionary<TemporalResource<ResizableRWRenderTargetTexture>, bool> _beforeLeftEye = new(ReferenceEqualityComparer.Instance);
    private static readonly List<TemporalResource<ResizableRWRenderTargetTexture>> _exchanged = new();
    private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new();
    private static int _framesUntilDiscovery;
    private static bool _discoveredWithRaytracing;
    private static long _frame;
    private static bool _loggedDiscovery;
    private static long _loggedBytes;
    private static int _loggedCount;

    // The right eye's FSR context, and the engine's own while the right eye draws.
    private static FSR4_1Context? _rightFsr;
    private static Vector2I _rightFsrResolution;
    private static bool _rightFsrForce31;
    private static FSR4_1Context? _heldEngineFsr;
    private static bool _loggedFsr;

    /// <summary>Before the left eye's draw: find the engine's pairs and note their state.</summary>
    public static void BeginLeftEye()
    {
        _frame++;
        // Raytracing creates (and drops) a whole set of pairs; look again right away instead of at the next interval.
        bool raytracing = CoreSystems.Settings.IsRaytracingSupportedAndEnabled;
        if (--_framesUntilDiscovery <= 0 || raytracing != _discoveredWithRaytracing)
        {
            _framesUntilDiscovery = RediscoverEveryFrames;
            _discoveredWithRaytracing = raytracing;
            Discover();
        }
        _beforeLeftEye.Clear();
        foreach (Path path in _paths)
        {
            if (path.Resolve() is { } resource)
            {
                _beforeLeftEye[resource] = resource._swapped;
            }
        }
        // Pairs whose engine pair is gone (the engine disposed its own textures; ours are unused) or that have not
        // been needed for a while.
        List<TemporalResource<ResizableRWRenderTargetTexture>>? release = null;
        foreach ((TemporalResource<ResizableRWRenderTargetTexture> resource, Pair pair) in _pairs)
        {
            if (!_beforeLeftEye.ContainsKey(resource) || _frame - pair.LastUsedFrame > ReleaseUnusedAfterFrames)
            {
                (release ??= new()).Add(resource);
            }
        }
        if (release != null)
        {
            foreach (TemporalResource<ResizableRWRenderTargetTexture> resource in release)
            {
                _pairs[resource].Dispose();
                _pairs.Remove(resource);
            }
            LogMemory();
        }
    }

    /// <summary>Before the right eye's draw: swap the right eye's history into the engine's pairs and FSR.</summary>
    public static void BeginRightEye()
    {
        DirectCommandList? commandList = null;
        bool created = false;
        try
        {
            foreach ((TemporalResource<ResizableRWRenderTargetTexture> resource, bool before) in _beforeLeftEye)
            {
                if (resource._swapped == before)
                {
                    continue; // not used by this frame's draws (an effect that is off, or water far away)
                }
                if (!_pairs.TryGetValue(resource, out Pair? pair))
                {
                    commandList ??= CoreSystems.FrameDispatcher.CreateDirectCommandList("VR.EyeHistory");
                    pair = CreatePair(commandList, resource);
                    _pairs[resource] = pair;
                    created = true;
                }
                pair.LastUsedFrame = _frame;
                Exchange(resource, pair);
                _exchanged.Add(resource);
            }
        }
        finally
        {
            commandList?.Dispose();
        }
        if (created)
        {
            LogMemory();
        }
        BeginRightFsr();
    }

    /// <summary>After the right eye's draw (always, also after failures): give the engine its own history back.</summary>
    public static void EndRightEye()
    {
        EndRightFsr();
        if (_exchanged.Count == 0)
        {
            return;
        }
        var live = new HashSet<TemporalResource<ResizableRWRenderTargetTexture>>(ReferenceEqualityComparer.Instance);
        foreach (Path path in _paths)
        {
            if (path.Resolve() is { } resource)
            {
                live.Add(resource);
            }
        }
        foreach (TemporalResource<ResizableRWRenderTargetTexture> resource in _exchanged)
        {
            Pair pair = _pairs[resource];
            if (live.Contains(resource))
            {
                Exchange(resource, pair);
                continue;
            }
            // The engine replaced the pair during the draw and disposed it - with our textures inside. What we hold now
            // are its original textures, which nothing references any more.
            pair.Dispose();
            _pairs.Remove(resource);
        }
        _exchanged.Clear();
    }

    /// <summary>Releases the right eye's textures and FSR context. Render thread, GPU idle (or the engine's deferred release).</summary>
    public static void Reset()
    {
        foreach (Pair pair in _pairs.Values)
        {
            pair.Dispose();
        }
        _pairs.Clear();
        _exchanged.Clear();
        _beforeLeftEye.Clear();
        _paths.Clear();
        _framesUntilDiscovery = 0;
        _rightFsr?.Dispose();
        _rightFsr = null;
        _heldEngineFsr = null;
    }

    private static void Exchange(TemporalResource<ResizableRWRenderTargetTexture> resource, Pair pair)
    {
        (resource._resource0, pair.Resource0) = (pair.Resource0!, resource._resource0);
        (resource._resource1, pair.Resource1) = (pair.Resource1!, resource._resource1);
        (resource._swapped, pair.Swapped) = (pair.Swapped, resource._swapped);
    }

    /// <summary>
    /// The upscaler's history lives in its context (created by the AMD FidelityFX library), so the right eye gets a
    /// second context made exactly like the engine's. While the right eye draws it sits in the engine's slot.
    /// </summary>
    private static void BeginRightFsr()
    {
        FSR4_1? fsr = CoreSystems.SceneDrawSystem?._upsamplingJob?._fsr4_1;
        FSR4_1Context? engineContext = fsr?._context;
        if (fsr?._resources == null || engineContext == null)
        {
            // FSR is off (FXAA, or no world): the right eye's context is not needed.
            _rightFsr?.Dispose();
            _rightFsr = null;
            return;
        }
        Vector2I resolution = fsr._resources.FinalResolution;
        bool force31 = fsr._contextForceFsr31;
        if (_rightFsr != null && (_rightFsrResolution.X != resolution.X || _rightFsrResolution.Y != resolution.Y || _rightFsrForce31 != force31))
        {
            _rightFsr.Dispose();
            _rightFsr = null;
        }
        if (_rightFsr == null)
        {
            CoreSystems.GPUResourcePool.Borrow(out FSR4_1Context context);
            context.Initialize(FSR4_1.DEFAULT_FLAGS, resolution, resolution, force31);
            _rightFsr = context;
            _rightFsrResolution = resolution;
            _rightFsrForce31 = force31;
            if (!_loggedFsr)
            {
                _loggedFsr = true;
                Log.Info($"FSR: the right eye has its own upscaler history ({resolution.X}x{resolution.Y} output).");
            }
        }
        _heldEngineFsr = engineContext;
        fsr._context = _rightFsr;
    }

    private static void EndRightFsr()
    {
        FSR4_1Context? engineContext = _heldEngineFsr;
        if (engineContext == null)
        {
            return;
        }
        _heldEngineFsr = null;
        FSR4_1? fsr = CoreSystems.SceneDrawSystem?._upsamplingJob?._fsr4_1;
        if (fsr != null && fsr._context == _rightFsr)
        {
            fsr._context = engineContext;
            return;
        }
        // During the right eye's draw the engine replaced or dropped its context (resolution, FSR version or
        // anti-aliasing change) and disposed ours with it. Its own old context is outdated just the same.
        _rightFsr = null;
        engineContext.Dispose();
    }

    /// <summary>A second pair like the engine's, starting from the engine's current contents (the left eye's latest).</summary>
    private static Pair CreatePair(DirectCommandList commandList, TemporalResource<ResizableRWRenderTargetTexture> resource)
    {
        var pair = new Pair
        {
            Resource0 = CloneTexture(commandList, resource._resource0),
            Resource1 = CloneTexture(commandList, resource._resource1),
            Swapped = resource._swapped
        };
        pair.Bytes = SizeOf(pair.Resource0) + SizeOf(pair.Resource1);
        return pair;
    }

    private static ResizableRWRenderTargetTexture CloneTexture(DirectCommandList commandList, ResizableRWRenderTargetTexture source)
    {
        ResizableRWRenderTargetTexture copy = CoreSystems.BindableTextures.CreateRWResizableRenderTargetTexture(
            source._debugName + " (right eye)", source._srvFormat, source._maxResolution, source._uavFormat,
            source._d3dResourceDescription.MipLevels, source._d3dClearColor, source._allocationGroup);
        copy.Resize(commandList, source.Resolution);
        commandList.CopyResource(copy, source);
        return copy;
    }

    private static long SizeOf(ResizableRWRenderTargetTexture texture)
    {
        try
        {
            return (long)CoreSystems.DeviceWrap.D3DDevice.GetResourceAllocationInfo(texture.D3DResourceDescription).SizeInBytes;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static void LogMemory()
    {
        long bytes = 0;
        foreach (Pair pair in _pairs.Values)
        {
            bytes += pair.Bytes;
        }
        if (_pairs.Count == _loggedCount && Math.Abs(bytes - _loggedBytes) < 16L << 20)
        {
            return;
        }
        _loggedBytes = bytes;
        _loggedCount = _pairs.Count;
        Log.Info($"Right-eye history: {_pairs.Count} effect buffers, {bytes / (1024.0 * 1024.0):0} MB of video memory" +
                 (_rightFsr != null ? " (plus its FSR context)." : "."));
    }

    /// <summary>Finds every temporal pair reachable from the renderer's roots and remembers the field paths to them.</summary>
    private static void Discover()
    {
        long start = Stopwatch.GetTimestamp();
        _paths.Clear();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var chain = new List<FieldInfo>();
        foreach (object? root in new object?[] { CoreSystems.SceneDrawSystem, CoreSystems.DrawContexts, CoreSystems.RaytraceGIResources })
        {
            if (root != null)
            {
                Walk(root, root, chain, visited);
            }
        }
        if (!_loggedDiscovery || Log.Verbose)
        {
            _loggedDiscovery = true;
            Log.Info($"Found {_paths.Count} temporal effect buffers (in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0.0} ms, {visited.Count} objects).");
        }
    }

    private static void Walk(object root, object value, List<FieldInfo> chain, HashSet<object> visited)
    {
        if (!visited.Add(value))
        {
            return;
        }
        foreach (FieldInfo field in FieldsOf(value.GetType()))
        {
            object? child = field.GetValue(value);
            if (child == null)
            {
                continue;
            }
            chain.Add(field);
            if (child is TemporalResource<ResizableRWRenderTargetTexture>)
            {
                if (visited.Add(child))
                {
                    _paths.Add(new Path { Root = root, Fields = chain.ToArray() });
                }
            }
            else if (chain.Count < MaxDepth && child.GetType().Assembly == typeof(CoreSystems).Assembly)
            {
                Walk(root, child, chain, visited);
            }
            chain.RemoveAt(chain.Count - 1);
        }
    }

    /// <summary>Instance fields that can hold renderer objects (no value types, strings, delegates or arrays).</summary>
    private static FieldInfo[] FieldsOf(Type type)
    {
        if (_fieldCache.TryGetValue(type, out FieldInfo[]? cached))
        {
            return cached;
        }
        var fields = new List<FieldInfo>();
        for (Type? t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (FieldInfo field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                Type fieldType = field.FieldType;
                if (fieldType.IsValueType || fieldType.IsPointer || fieldType == typeof(string) || fieldType.IsArray ||
                    typeof(Delegate).IsAssignableFrom(fieldType))
                {
                    continue;
                }
                fields.Add(field);
            }
        }
        FieldInfo[] result = fields.ToArray();
        _fieldCache[type] = result;
        return result;
    }
}
