namespace ZeroCdp.Protocol;

/// <summary>
/// Implemented by structs that describe the parameters of a CDP command.
/// The generator emits an implementation for every domain method.
/// </summary>
/// <remarks>
/// Called through a <c>struct</c> generic constraint, so the interface dispatch never boxes.
/// </remarks>
public interface ICdpCommandParams
{
    void Write(ref CommandWriter writer);
}

/// <summary>A command that takes no parameters.</summary>
public readonly struct NoParams : ICdpCommandParams
{
    public void Write(ref CommandWriter writer)
    {
        // Intentionally empty: the caller still opens and closes the params object.
    }
}
