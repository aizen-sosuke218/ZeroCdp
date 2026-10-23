namespace ZeroCdp.Protocol;

/// <summary>Parameters of <c>DOMStorage.setDOMStorageItem</c>.</summary>
public readonly struct SetDOMStorageItemParams : ICdpCommandParams
{
    private readonly string _securityOrigin;
    private readonly string _key;
    private readonly string _value;

    public SetDOMStorageItemParams(string securityOrigin, string key, string value)
    {
        _securityOrigin = securityOrigin;
        _key = key;
        _value = value;
    }

    public void Write(ref CommandWriter writer)
    {
        writer.Name("storageId"u8);
        writer.BeginObject();
        writer.Name("securityOrigin"u8);
        writer.StringValue(_securityOrigin);
        writer.Name("isLocalStorage"u8);
        writer.BoolValue(true);
        writer.EndObject();

        writer.Name("key"u8);
        writer.StringValue(_key);
        writer.Name("value"u8);
        writer.StringValue(_value);
    }
}
