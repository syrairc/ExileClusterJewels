using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ExileImGui;

namespace ExileClusterJewels;

public class ClusterPreset
{
    public string First { get; set; }
    public string Second { get; set; }
}

public class ExileClusterJewelsSettings : ISettings
{
    public ToggleNode Enable { get; set; } = new ToggleNode(true);
    public ToggleNode ShowTreeButton { get; set; } = new ToggleNode(true);
    public Profiles<ClusterPreset> Presets { get; set; } = new();
}
