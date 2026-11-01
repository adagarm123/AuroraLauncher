namespace AuroraCore.Models;

/// <summary>assets/indexes/&lt;id&gt;.json — maps virtual path -> object hash.</summary>
public class AssetIndex
{
    public bool Virtual { get; set; }
    public Dictionary<string, AssetObject> Objects { get; set; } = new();
}

public class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}
