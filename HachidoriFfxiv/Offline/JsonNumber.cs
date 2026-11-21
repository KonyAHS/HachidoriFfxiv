using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HachidoriFfxiv.Offline;

internal static class JsonNumber
{
    /// <summary>
    /// Any JSON number as a double. <c>JsonValue.TryGetValue&lt;double&gt;</c> alone
    /// fails for a value built from an int (as a request message is), which once
    /// made every offline lookup ignore its maxResults.
    /// </summary>
    public static bool TryGet(JsonNode? node, out double value)
    {
        value = 0;
        if (node is not JsonValue v || v.GetValueKind() != JsonValueKind.Number) return false;
        if (v.TryGetValue(out value)) return true;
        return double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    public static double Or(JsonNode? node, double fallback) => TryGet(node, out var value) ? value : fallback;

    /// <summary>A positive count, else 0.</summary>
    public static long Count(JsonNode? node) => TryGet(node, out var value) && value > 0 ? (long)value : 0;
}
