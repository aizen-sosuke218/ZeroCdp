namespace ZeroCdp.Browser;

/// <summary>
/// Internal locator scope. A <see cref="FrameLocator"/> resolves its owner element on every
/// operation, while a <see cref="Frame"/> already carries the committed frame id.
/// </summary>
internal readonly struct FrameScope
{
    private readonly FrameLocator? _locator;
    private readonly Frame? _frame;

    private FrameScope(FrameLocator locator) => _locator = locator;

    private FrameScope(Frame frame) => _frame = frame;

    public static FrameScope From(FrameLocator locator)
    {
        ArgumentNullException.ThrowIfNull(locator);
        return new FrameScope(locator);
    }

    public static FrameScope From(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return new FrameScope(frame);
    }

    public static implicit operator FrameScope(FrameLocator locator) => From(locator);

    public static implicit operator FrameScope(Frame frame) => From(frame);

    public ValueTask<string> ResolveFrameIdAsync(CancellationToken cancellationToken)
    {
        if (_locator is not null)
        {
            return _locator.ResolveFrameIdAsync(cancellationToken);
        }

        Frame frame = _frame ?? throw new InvalidOperationException("The frame scope is empty.");
        if (frame.IsDetached)
        {
            throw new CdpException($"Frame '{frame.FrameId}' is detached.");
        }

        return ValueTask.FromResult(frame.FrameId);
    }
}
