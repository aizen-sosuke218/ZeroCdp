using System.Text;
using ZeroCdp.Json;
using ZeroCdp.Protocol;
using ZeroCdp.Runtime;

namespace ZeroCdp.Browser;

public sealed partial class CdpPage
{
    private readonly Lock _frameTreeGate = new();
    private readonly Dictionary<string, Frame> _framesById = new(StringComparer.Ordinal);
    private readonly List<Frame> _framesInOrder = new();
    private Frame? _mainFrame;
    private Action<Frame>? _frameAttachedHandlers;
    private Action<Frame>? _frameDetachedHandlers;
    private Action<Frame>? _frameNavigatedHandlers;

    /// <summary>Raised when a child frame is attached to this page.</summary>
    public event Action<Frame>? FrameAttached
    {
        add
        {
            lock (_frameTreeGate)
            {
                _frameAttachedHandlers += value;
            }
        }

        remove
        {
            lock (_frameTreeGate)
            {
                _frameAttachedHandlers -= value;
            }
        }
    }

    /// <summary>Raised when a frame is detached from this page.</summary>
    public event Action<Frame>? FrameDetached
    {
        add
        {
            lock (_frameTreeGate)
            {
                _frameDetachedHandlers += value;
            }
        }

        remove
        {
            lock (_frameTreeGate)
            {
                _frameDetachedHandlers -= value;
            }
        }
    }

    /// <summary>Raised when any frame commits a new document.</summary>
    public event Action<Frame>? FrameNavigated
    {
        add
        {
            lock (_frameTreeGate)
            {
                _frameNavigatedHandlers += value;
            }
        }

        remove
        {
            lock (_frameTreeGate)
            {
                _frameNavigatedHandlers -= value;
            }
        }
    }

    /// <summary>The page's main frame. Available after <see cref="EnableAsync"/> has completed.</summary>
    public Frame MainFrame
    {
        get
        {
            lock (_frameTreeGate)
            {
                return _mainFrame
                    ?? throw new CdpException("The page frame tree is not initialized yet. Call EnableAsync first.");
            }
        }
    }

    /// <summary>All live frames in frame-tree order, including the main frame.</summary>
    public IReadOnlyList<Frame> Frames
    {
        get
        {
            lock (_frameTreeGate)
            {
                Frame[] frames = new Frame[_framesInOrder.Count];
                int count = 0;
                foreach (Frame frame in _framesInOrder)
                {
                    if (!frame.IsDetached)
                    {
                        frames[count++] = frame;
                    }
                }

                if (count != frames.Length)
                {
                    Array.Resize(ref frames, count);
                }

                return frames;
            }
        }
    }

    /// <summary>Finds a frame by its name or by its exact URL.</summary>
    public Frame? Frame(string nameOrUrl)
    {
        ArgumentException.ThrowIfNullOrEmpty(nameOrUrl);

        lock (_frameTreeGate)
        {
            foreach (Frame frame in _framesInOrder)
            {
                if (!frame.IsDetached && frame.Name.Equals(nameOrUrl, StringComparison.Ordinal))
                {
                    return frame;
                }
            }

            foreach (Frame frame in _framesInOrder)
            {
                if (!frame.IsDetached && frame.Url.Equals(nameOrUrl, StringComparison.Ordinal))
                {
                    return frame;
                }
            }
        }

        return null;
    }

    internal IReadOnlyList<Frame> GetChildFrames(Frame parent)
    {
        lock (_frameTreeGate)
        {
            if (parent.IsDetached)
            {
                return Array.Empty<Frame>();
            }

            List<Frame> children = new();
            foreach (Frame frame in _framesInOrder)
            {
                if (!frame.IsDetached && ReferenceEquals(frame.ParentFrame, parent))
                {
                    children.Add(frame);
                }
            }

            return children;
        }
    }

    private void InitializeFrameTree(FrameTreeResult tree)
    {
        List<FrameTreeEntry> entries = new();
        if (!tree.RawUtf8.IsEmpty && !FrameTreeParser.TryParse(tree.RawUtf8.Span, out entries))
        {
            entries.Clear();
        }

        List<Frame> attached = new();
        lock (_frameTreeGate)
        {
            _framesById.Clear();
            _framesInOrder.Clear();
            _mainFrame = null;

            if (entries.Count == 0)
            {
                if (!string.IsNullOrEmpty(tree.MainFrameId))
                {
                    entries.Add(new FrameTreeEntry
                    {
                        FrameId = tree.MainFrameId,
                        Name = string.Empty,
                        Url = tree.MainFrameUrl,
                    });
                }
            }

            foreach (FrameTreeEntry entry in entries)
            {
                if (GetOrCreateFrameLocked(entry, out Frame frame, out bool created))
                {
                    if (created)
                    {
                        attached.Add(frame);
                    }
                }
            }

            if (!string.IsNullOrEmpty(tree.MainFrameId)
                && _framesById.TryGetValue(tree.MainFrameId, out Frame? main))
            {
                _mainFrame = main;
                main.SetParent(null);
            }
        }

        foreach (Frame frame in attached)
        {
            RaiseFrameAttached(frame);
        }
    }

    private bool GetOrCreateFrameLocked(
        FrameTreeEntry entry,
        out Frame frame,
        out bool created)
    {
        created = false;
        if (_framesById.TryGetValue(entry.FrameId, out frame!))
        {
            Frame? parent = null;
            if (!string.IsNullOrEmpty(entry.ParentFrameId)
                && _framesById.TryGetValue(entry.ParentFrameId, out Frame? knownParent))
            {
                parent = knownParent;
            }

            if (parent is not null)
            {
                frame.SetParent(parent);
            }

            if (entry.Name.Length != 0 || frame.Name.Length == 0)
            {
                frame.SetName(entry.Name);
            }

            if (entry.Url.Length != 0 || frame.Url.Length == 0)
            {
                frame.SetUrl(entry.Url);
            }

            return true;
        }

        Frame? parentFrame = null;
        if (!string.IsNullOrEmpty(entry.ParentFrameId))
        {
            _framesById.TryGetValue(entry.ParentFrameId, out parentFrame);
        }

        frame = new Frame(this, entry.FrameId, parentFrame, entry.Name, entry.Url);
        _framesById.Add(entry.FrameId, frame);
        _framesInOrder.Add(frame);
        created = true;
        return true;
    }

    private void RegisterFrameTree(FrameTreeResult tree, string? fallbackParentFrameId)
    {
        List<FrameTreeEntry> entries = new();
        if (!tree.RawUtf8.IsEmpty && !FrameTreeParser.TryParse(tree.RawUtf8.Span, out entries))
        {
            entries.Clear();
        }

        if (entries.Count == 0 && !string.IsNullOrEmpty(tree.MainFrameId))
        {
            entries.Add(new FrameTreeEntry
            {
                FrameId = tree.MainFrameId,
                ParentFrameId = fallbackParentFrameId,
                Name = string.Empty,
                Url = tree.MainFrameUrl,
            });
        }

        List<Frame> attached = new();
        lock (_frameTreeGate)
        {
            for (int i = 0; i < entries.Count; i++)
            {
                FrameTreeEntry entry = entries[i];
                if (i == 0 && string.IsNullOrEmpty(entry.ParentFrameId))
                {
                    entry = new FrameTreeEntry
                    {
                        FrameId = entry.FrameId,
                        ParentFrameId = fallbackParentFrameId,
                        Name = entry.Name,
                        Url = entry.Url,
                        LoaderId = entry.LoaderId,
                        SecurityOrigin = entry.SecurityOrigin,
                        MimeType = entry.MimeType,
                    };
                }

                if (GetOrCreateFrameLocked(entry, out Frame frame, out bool created) && created)
                {
                    attached.Add(frame);
                }
            }
        }

        foreach (Frame frame in attached)
        {
            RaiseFrameAttached(frame);
        }
    }

    private void OnFrameAttached(string frameId, string? parentFrameId)
    {
        if (string.IsNullOrEmpty(frameId) || frameId == _mainFrameId)
        {
            return;
        }

        Frame? frame = null;
        bool created = false;
        lock (_frameTreeGate)
        {
            if (!_framesById.ContainsKey(frameId))
            {
                created = GetOrCreateFrameLocked(
                    new FrameTreeEntry
                    {
                        FrameId = frameId,
                        ParentFrameId = parentFrameId,
                        Name = string.Empty,
                        Url = string.Empty,
                    },
                    out frame,
                    out _);
            }
        }

        if (created && frame is not null)
        {
            RaiseFrameAttached(frame);
        }
    }

    private void OnFrameDetached(string frameId)
    {
        if (string.IsNullOrEmpty(frameId))
        {
            return;
        }

        List<Frame> detached = new();
        lock (_frameTreeGate)
        {
            if (!_framesById.TryGetValue(frameId, out Frame? frame))
            {
                return;
            }

            CollectDetachedLocked(frame, detached);
        }

        foreach (Frame frame in detached)
        {
            RaiseFrameDetached(frame);
        }
    }

    private void CollectDetachedLocked(Frame frame, List<Frame> detached)
    {
        Frame[] snapshot = _framesInOrder.ToArray();
        foreach (Frame child in snapshot)
        {
            if (!child.IsDetached && ReferenceEquals(child.ParentFrame, frame))
            {
                CollectDetachedLocked(child, detached);
            }
        }

        if (_framesById.Remove(frame.FrameId))
        {
            _framesInOrder.Remove(frame);
        }

        frame.MarkDetached();
        frame.SetParent(null);
        detached.Add(frame);
    }

    private void OnFrameNavigated(FrameTreeEntry entry)
        => OnFrameNavigated(entry, fallbackParentFrameId: null);

    private void OnFrameNavigated(FrameTreeEntry entry, string? fallbackParentFrameId)
    {
        if (string.IsNullOrEmpty(entry.ParentFrameId) && !string.IsNullOrEmpty(fallbackParentFrameId))
        {
            entry = new FrameTreeEntry
            {
                FrameId = entry.FrameId,
                ParentFrameId = fallbackParentFrameId,
                Name = entry.Name,
                Url = entry.Url,
                LoaderId = entry.LoaderId,
                SecurityOrigin = entry.SecurityOrigin,
                MimeType = entry.MimeType,
            };
        }

        Frame? frame = null;
        bool created = false;
        bool isMain;
        lock (_frameTreeGate)
        {
            isMain = entry.FrameId == _mainFrameId;
            if (isMain && _mainFrame is null && _framesById.TryGetValue(entry.FrameId, out Frame? existingMain))
            {
                _mainFrame = existingMain;
            }

            if (GetOrCreateFrameLocked(entry, out frame, out created))
            {
                if (isMain)
                {
                    _mainFrame = frame;
                    frame.SetParent(null);
                }
            }
        }

        if (frame is null)
        {
            return;
        }

        if (created)
        {
            RaiseFrameAttached(frame);
        }

        RaiseFrameNavigated(frame);
    }

    private void OnFrameUrlChanged(string frameId, string url)
    {
        Frame? frame;
        lock (_frameTreeGate)
        {
            _framesById.TryGetValue(frameId, out frame);
            frame?.SetUrl(url);
        }
    }

    private void RaiseFrameAttached(Frame frame)
    {
        Action<Frame>? handlers;
        lock (_frameTreeGate)
        {
            handlers = _frameAttachedHandlers;
        }

        handlers?.Invoke(frame);
    }

    private void RaiseFrameDetached(Frame frame)
    {
        Action<Frame>? handlers;
        lock (_frameTreeGate)
        {
            handlers = _frameDetachedHandlers;
        }

        handlers?.Invoke(frame);
    }

    private void RaiseFrameNavigated(Frame frame)
    {
        Action<Frame>? handlers;
        lock (_frameTreeGate)
        {
            handlers = _frameNavigatedHandlers;
        }

        handlers?.Invoke(frame);
    }

    /// <summary>Forwards frame lifecycle events from an OOPIF session into the page frame tree.</summary>
    private sealed class ChildFrameEventSink : ICdpEventSink
    {
        private readonly CdpPage _page;
        private readonly CdpSession _session;

        public ChildFrameEventSink(CdpPage page, CdpSession session)
        {
            _page = page;
            _session = session;
        }

        public void OnEvent(
            ReadOnlySpan<byte> sessionId,
            ReadOnlySpan<byte> method,
            ReadOnlySpan<byte> paramsUtf8)
        {
            if (method.SequenceEqual("Page.frameNavigated"u8))
            {
                JsonCursor cursor = new(paramsUtf8);
                if (cursor.TryEnterObject()
                    && cursor.TrySeekProperty("frame"u8)
                    && FrameTreeEntry.TryParse(ref cursor, inheritedParentFrameId: null, out FrameTreeEntry entry))
                {
                    _page.OnFrameNavigated(entry, _page.GetParentFrameIdForSession(_session));
                }
            }
            else if (method.SequenceEqual("Page.frameAttached"u8))
            {
                if (TryReadFrameAttached(paramsUtf8, out string frameId, out string? parentFrameId))
                {
                    _page.OnFrameAttached(frameId, parentFrameId ?? _page.GetParentFrameIdForSession(_session));
                }
            }
            else if (method.SequenceEqual("Page.frameDetached"u8))
            {
                if (TryReadFrameId(paramsUtf8, out string frameId))
                {
                    _page.OnFrameDetached(frameId);
                }
            }
            else if (method.SequenceEqual("Page.navigatedWithinDocument"u8))
            {
                if (TryReadFrameId(paramsUtf8, out string frameId)
                    && TryReadStringProperty(paramsUtf8, "url"u8, out string? url)
                    && url is not null)
                {
                    _page.OnFrameUrlChanged(frameId, url);
                }
            }
        }

        private static bool TryReadFrameAttached(
            ReadOnlySpan<byte> paramsUtf8,
            out string frameId,
            out string? parentFrameId)
        {
            frameId = string.Empty;
            parentFrameId = null;
            JsonCursor cursor = new(paramsUtf8);
            if (!cursor.TryEnterObject())
            {
                return false;
            }

            while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
            {
                if (property.SequenceEqual("frameId"u8))
                {
                    if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                    {
                        frameId = Encoding.UTF8.GetString(raw);
                    }
                }
                else if (property.SequenceEqual("parentFrameId"u8))
                {
                    if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                    {
                        parentFrameId = Encoding.UTF8.GetString(raw);
                    }
                }
                else if (!cursor.SkipValue())
                {
                    return false;
                }
            }

            return frameId.Length != 0;
        }

        private static bool TryReadFrameId(ReadOnlySpan<byte> paramsUtf8, out string frameId)
        {
            frameId = string.Empty;
            if (!TryReadStringProperty(paramsUtf8, "frameId"u8, out string? value) || value is null)
            {
                return false;
            }

            frameId = value;
            return true;
        }

        private static bool TryReadStringProperty(
            ReadOnlySpan<byte> paramsUtf8,
            ReadOnlySpan<byte> propertyName,
            out string? value)
        {
            value = null;
            JsonCursor cursor = new(paramsUtf8);
            if (!cursor.TryEnterObject())
            {
                return false;
            }

            while (cursor.TryNextProperty(out ReadOnlySpan<byte> property))
            {
                if (property.SequenceEqual(propertyName))
                {
                    if (cursor.TryReadString(out ReadOnlySpan<byte> raw, out _))
                    {
                        value = Encoding.UTF8.GetString(raw);
                    }

                    return value is not null;
                }

                if (!cursor.SkipValue())
                {
                    return false;
                }
            }

            return false;
        }
    }
}
