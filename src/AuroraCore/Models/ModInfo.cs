namespace AuroraCore.Models;

public class ModInfo
{
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string ModId { get; set; } = "";
    public string Loader { get; set; } = ""; // fabric / quilt / forge / neoforge / unknown
    public bool Enabled { get; set; } = true;
    public long Size { get; set; }
}
