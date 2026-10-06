using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory;
using ExileImGui;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ExileClusterJewels;

public class ClusterEnchant
{
    public int Id { get; set; }
    public string Text { get; set; }
}

public class ClusterNotable
{
    public string Name { get; set; }
    public int Order { get; set; }
    public int Level { get; set; }
    public string Group { get; set; }
    public bool Suffix { get; set; }
    public string TradeId { get; set; }
    public List<int> Enchants { get; set; } = new();
}

public class ClusterData
{
    public string EnchantStatId { get; set; }
    public string PassiveCountStatId { get; set; }
    public List<ClusterEnchant> Enchants { get; set; } = new();
    public List<ClusterNotable> Notables { get; set; } = new();
}

public record ClusterSearch(string Label, List<ClusterNotable> Middles, JObject Query);

public class ExileClusterJewels : BaseSettingsPlugin<ExileClusterJewelsSettings>
{
    const string TradeSearchUrl = "https://www.pathofexile.com/trade/search/";
    const string LargeClusterJewel = "Large Cluster Jewel";
    const string TreeButtonLabel = "Cluster Jewels";
    const int TreeButtonsIndex = 3;
    const float TreeButtonGap = 12f;
    static readonly Vector2 TreeButtonPadding = new(32f, 16f);

    ClusterData _data;
    (string key, string label)[] _candidates, _secondCandidates;
    bool _windowOpen;
    string _firstFilter = "", _secondFilter = "";
    string _error, _status;
    List<ClusterSearch> _searches = new();

    public override bool Initialise()
    {
        _data = JsonConvert.DeserializeObject<ClusterData>(File.ReadAllText(Path.Combine(DirectoryFullName, "notables.json")));
        _candidates = _data.Notables.Select(Candidate).ToArray();
        Settings.Presets.NameStem = () => "Cluster";
        Recalculate();
        return true;
    }

    public override void Render()
    {
        if (!GameController.InGame || GameController.IsLoading) return;
        if (Settings.ShowTreeButton && GameController.IngameState.IngameUi.TreePanel is { IsVisible: true } tree) DrawTreeButton(tree);
        if (_windowOpen) DrawWindow();
    }

    void DrawTreeButton(Element tree)
    {
        var buttons = tree.GetChildAtIndex(TreeButtonsIndex);
        if (buttons == null) return;

        var anchor = buttons.GetClientRectCache;
        var size = ImGui.CalcTextSize(TreeButtonLabel) + TreeButtonPadding;
        var pos = new Vector2(anchor.Center.X - size.X / 2, anchor.Bottom + TreeButtonGap);
        if (Floating.Button("ecj_tree", TreeButtonLabel, ref pos, size)) _windowOpen = !_windowOpen;
    }

    void DrawWindow()
    {
        if (ExileImGui.Windows.Begin("Cluster Jewels", "ecj_window", ref _windowOpen, 0, new Vector2(460, 420))) DrawFinder();
        ExileImGui.Windows.End();
    }

    public override void DrawSettings()
    {
        var show = Settings.ShowTreeButton.Value;
        if (ImGui.Checkbox("Show button on passive tree", ref show)) Settings.ShowTreeButton.Value = show;
        ImGui.Separator();
        DrawFinder();
    }

    void DrawFinder()
    {
        var changed = Settings.Presets.Bar("ecj_presets", 200f);
        var preset = Settings.Presets.Current;
        if (NotablePicker("ecj_first", preset.First, _candidates, ref _firstFilter) is { } first) (preset.First, changed) = (first, true);
        if (NotablePicker("ecj_second", preset.Second, _secondCandidates, ref _secondFilter) is { } second) (preset.Second, changed) = (second, true);
        if (changed) Recalculate();

        if (_error != null) ImGui.TextWrapped(Pct(_error));
        for (var i = 0; i < _searches.Count; i++) DrawSearch(i, _searches[i]);
        if (_status == null) return;
        ImGui.Separator();
        ImGui.TextWrapped(Pct(_status));
    }

    static (string key, string label) Candidate(ClusterNotable n) => (n.Name, $"{n.Name}  (ilvl {n.Level})");

    static string NotablePicker(string id, string selected, (string key, string label)[] candidates, ref string filter) =>
        Combo.SearchCombo(id, selected ?? "Pick a notable", candidates, ref filter, out var picked, -1) ? picked : null;

    void DrawSearch(int index, ClusterSearch search)
    {
        ImGui.Separator();
        if (ImGui.Button($"Trade##ecj_trade{index}")) _status = OpenTrade(search.Query);
        ImGui.SameLine();
        ImGui.TextWrapped(Pct(search.Label));
        ImGui.TextDisabled(Pct("Middle: " + string.Join(", ", search.Middles.Select(m => $"{m.Name} ({m.Level})"))));
    }

    static string Pct(string text) => text.Replace("%", "%%");

    void Recalculate()
    {
        _searches = new List<ClusterSearch>();
        _error = null;
        var a = Find(Settings.Presets.Current.First);
        var b = Find(Settings.Presets.Current.Second);
        _secondCandidates = a == null ? _candidates : _data.Notables.Where(n => Pairing(a, n, out _, out _) == null).Select(Candidate).ToArray();
        if (a == null || b == null) return;

        _error = Pairing(a, b, out var shared, out var middles);
        if (_error != null) return;

        foreach (var enchant in shared)
        {
            var fits = middles.Where(m => m.Enchants.Contains(enchant)).ToList();
            if (fits.Count > 0) _searches.Add(new ClusterSearch(EnchantText(enchant), fits, Query(a, b, fits, enchant)));
        }

        if (shared.Count > 1) _searches.Add(new ClusterSearch("Any enchant", middles, Query(a, b, middles, null)));
    }

    ClusterNotable Find(string name) => _data.Notables.FirstOrDefault(n => n.Name == name);

    string Pairing(ClusterNotable a, ClusterNotable b, out List<int> shared, out List<ClusterNotable> middles)
    {
        shared = null;
        middles = null;
        if (a.Group == b.Group) return "Those two share a mod group - they can't roll on the same jewel.";

        var common = a.Enchants.Intersect(b.Enchants).ToList();
        if (common.Count == 0) return "No large cluster type can roll both of those.";

        var low = Math.Min(a.Order, b.Order);
        var high = Math.Max(a.Order, b.Order);
        var between = _data.Notables.Where(n => n.Order > low && n.Order < high
                                                && n.Group != a.Group && n.Group != b.Group
                                                && n.Enchants.Any(common.Contains)
                                                && new[] { a, b, n }.Count(x => !x.Suffix) < 3).ToList();
        if (between.Count == 0) return "Nothing sorts between those two, so they can't land in the outer slots.";

        (shared, middles) = (common, between);
        return null;
    }

    string EnchantText(int id) => _data.Enchants.First(e => e.Id == id).Text.Replace("\n", " / ");

    JObject Query(ClusterNotable a, ClusterNotable b, List<ClusterNotable> middles, int? enchant)
    {
        var required = new JArray(new JObject { ["id"] = _data.PassiveCountStatId, ["value"] = new JObject { ["min"] = 8, ["max"] = 8 } });
        if (enchant.HasValue) required.Add(new JObject { ["id"] = _data.EnchantStatId, ["value"] = new JObject { ["option"] = enchant.Value } });
        required.Add(new JObject { ["id"] = a.TradeId });
        required.Add(new JObject { ["id"] = b.TradeId });

        return new JObject
        {
            ["query"] = new JObject
            {
                ["status"] = new JObject { ["option"] = "securable" },
                ["type"] = LargeClusterJewel,
                ["stats"] = new JArray(
                    new JObject { ["type"] = "and", ["filters"] = required },
                    new JObject { ["type"] = "count", ["value"] = new JObject { ["min"] = 1 }, ["filters"] = new JArray(middles.Select(m => new JObject { ["id"] = m.TradeId })) })
            },
            ["sort"] = new JObject { ["price"] = "asc" }
        };
    }

    string OpenTrade(JObject query)
    {
        var league = GameController.IngameState.ServerData.League;
        if (string.IsNullOrEmpty(league)) return "Couldn't read your league.";

        var url = $"{TradeSearchUrl}{Uri.EscapeDataString(league)}?q={Uri.EscapeDataString(query.ToString(Formatting.None))}";
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return $"Opened the {league} trade site.";
        }
        catch (Exception ex)
        {
            return $"Couldn't open the browser: {ex.Message}";
        }
    }
}
