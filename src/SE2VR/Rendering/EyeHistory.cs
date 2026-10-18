using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Keen.VRage.Render12.Core;
using Keen.VRage.Render12.Core.CommandLists;
using Keen.VRage.Render12.Resources.BindableTextures;
using Keen.VRage.Render12.Resources.Utilities;

namespace SE2VR.Rendering;

/// <summary>
/// Gives each eye its own history for the engine's temporal effects (clouds, water effects, stochastic transparency,
/// raytraced GI).
/// <para>
/// Those effects keep last frame's result in a ping-pong pair (<c>TemporalResource</c>) that flips on every scene draw,
/// and reproject it with the previous camera's <i>view</i> but the <i>current</i> projection. Drawing two eyes per
/// frame from one pair hands each eye the other eye's result, rendered with a very different asymmetric projection
/// (the eyes' optical centers sit at about a third and two thirds of the image), so the history is misplaced or
/// rejected and clouds turn into noisy blotches. Here the right eye gets a second pair: its contents are exchanged
/// with the engine's pair around the right eye's draw, and each eye's previous view is its own last frame.
/// </para>
/// The pairs are found by walking the renderer's objects (fields only, a few levels deep); the paths are remembered,
/// so each frame only follows a handful of field references. Render thread only.
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

        public void Dispose()
        {
            Resource0?.Dispose();
            Resource1?.Dispose();
            Resource0 = Resource1 = null;
        }
    }

    private const int MaxDepth = 4;
    private const int RediscoverEveryFrames = 600;

    private static readonly List<Path> _paths = new();
    private static readonly Dictionary<TemporalResource<ResizableRWRenderTargetTexture>, Pair> _pairs = new(ReferenceEqualityComparer.Instance);
    private static readonly List<TemporalResource<ResizableRWRenderTargetTexture>> _exchanged = new();
    private static readonly Dictionary<Type, FieldInfo[]> _fieldCache = new();
    private static int _framesUntilDiscovery;
    private static bool _loggedDiscovery;

    /// <summary>Before the right eye's draw: swap the right eye's history into the engine's pairs.</summary>
    public static void BeginRightEye()
    {
        if (--_framesUntilDiscovery <= 0)
        {
            _framesUntilDiscovery = RediscoverEveryFrames;
            Discover();
        }
        var live = new HashSet<TemporalResource<ResizableRWRenderTargetTexture>>(ReferenceEqualityComparer.Instance);
        foreach (Path path in _paths)
        {
            if (path.Resolve() is { } resource)
            {
                live.Add(resource);
            }
        }
        // Pairs whose engine pair was replaced between frames: the engine disposed its own textures, ours are unused.
        foreach (TemporalResource<ResizableRWRenderTargetTexture> stale in new List<TemporalResource<ResizableRWRenderTargetTexture>>(_pairs.Keys))
        {
            if (!live.Contains(stale))
            {
                _pairs[stale].Dispose();
                _pairs.Remove(stale);
            }
        }

        DirectCommandList? commandList = null;
        try
        {
            foreach (TemporalResource<ResizableRWRenderTargetTexture> resource in live)
            {
                if (!_pairs.TryGetValue(resource, out Pair? pair))
                {
                    commandList ??= CoreSystems.FrameDispatcher.CreateDirectCommandList("VR.EyeHistory");
                    pair = CreatePair(commandList, resource);
                    _pairs[resource] = pair;
                }
                Exchange(resource, pair);
                _exchanged.Add(resource);
            }
        }
        finally
        {
            commandList?.Dispose();
        }
    }

    /// <summary>After the right eye's draw (always, also after failures): give the engine its own pairs back.</summary>
    public static void EndRightEye()
    {
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

    /// <summary>Releases the right eye's textures. Render thread, GPU idle (or the engine's deferred release).</summary>
    public static void Reset()
    {
        foreach (Pair pair in _pairs.Values)
        {
            pair.Dispose();
        }
        _pairs.Clear();
        _exchanged.Clear();
        _paths.Clear();
        _framesUntilDiscovery = 0;
    }

    private static void Exchange(TemporalResource<ResizableRWRenderTargetTexture> resource, Pair pair)
    {
        (resource._resource0, pair.Resource0) = (pair.Resource0!, resource._resource0);
        (resource._resource1, pair.Resource1) = (pair.Resource1!, resource._resource1);
        (resource._swapped, pair.Swapped) = (pair.Swapped, resource._swapped);
    }

    /// <summary>A second pair like the engine's, starting from the engine's current contents (the left eye's latest).</summary>
    private static Pair CreatePair(DirectCommandList commandList, TemporalResource<ResizableRWRenderTargetTexture> resource)
    {
        return new Pair
        {
            Resource0 = CloneTexture(commandList, resource._resource0),
            Resource1 = CloneTexture(commandList, resource._resource1),
            Swapped = resource._swapped
        };
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
        if (!_loggedDiscovery)
        {
            _loggedDiscovery = true;
            Log.Info($"Per-eye history for {_paths.Count} temporal effect buffers (found in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0.0} ms, {visited.Count} objects).");
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
