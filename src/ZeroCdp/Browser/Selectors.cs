namespace ZeroCdp.Browser;

/// <summary>Global selector configuration.</summary>
public static class Selectors
{
    private static string _testIdAttribute = "data-testid";

    /// <summary>The attribute used by <c>GetByTestId</c>. Defaults to <c>data-testid</c>.</summary>
    public static string TestIdAttribute
    {
        get => Volatile.Read(ref _testIdAttribute);
        set
        {
            ArgumentException.ThrowIfNullOrEmpty(value);
            Volatile.Write(ref _testIdAttribute, value);
        }
    }

    /// <summary>Changes the attribute used by <c>GetByTestId</c>.</summary>
    public static void SetTestIdAttribute(string attributeName)
        => TestIdAttribute = attributeName;
}
