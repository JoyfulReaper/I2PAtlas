using System.Net.Http.Headers;
using System.Text;

namespace DN42Atlas.I2pProbing;

/// <summary>Conservative heuristics, never origin authentication or error-page fingerprinting.</summary>
internal static class I2pProxyResponseClassifier
{
    internal static I2pProxyResponseClassification ClassifyStatus(int status) => status is 502 or 503 or 504
        ? I2pProxyResponseClassification.LikelyProxyFailure
        : I2pProxyResponseClassification.Ambiguous;

    internal static I2pProxyResponseClassification ClassifyRobots(HttpResponseMessage response,
        I2pRobotsObservation robots, byte[] body)
    {
        var status = (int)response.StatusCode;
        if (status != 200) return ClassifyStatus(status);
        if (robots is not (I2pRobotsObservation.AllowsRoot or I2pRobotsObservation.DisallowsRoot) ||
            body.Length > I2pHttpProbe.MaxRobotsBytes || response.Content.Headers.ContentEncoding.Count != 0 ||
            !response.Content.Headers.TryGetValues("Content-Type", out var values))
            return I2pProxyResponseClassification.Ambiguous;

        var types = values.Take(2).ToArray();
        if (types.Length != 1 || !MediaTypeHeaderValue.TryParse(types[0], out var type) ||
            !string.Equals(type.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
            type.Parameters.Count(p => p.Name.Equals("charset", StringComparison.OrdinalIgnoreCase)) > 1 ||
            (type.CharSet != null && !string.Equals(type.CharSet.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase)) ||
            response.Content.Headers.ContentRange != null)
            return I2pProxyResponseClassification.Ambiguous;

        // The observation above guarantees successful strict UTF-8 decoding and
        // policy parsing. Require recognizable policy as additional evidence:
        // empty/comment-only text and permissive unknown directives are insufficient.
        var sawAgent = false;
        foreach (var raw in Encoding.UTF8.GetString(body).TrimStart('\uFEFF').Split('\n'))
        {
            var line = raw.Split('#', 2)[0].Trim();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var field = line[..colon].Trim();
            if (field.Equals("User-agent", StringComparison.OrdinalIgnoreCase)) sawAgent = true;
            else if (sawAgent && (field.Equals("Allow", StringComparison.OrdinalIgnoreCase) ||
                field.Equals("Disallow", StringComparison.OrdinalIgnoreCase)))
                return I2pProxyResponseClassification.LikelyOriginResponse;
        }
        return I2pProxyResponseClassification.Ambiguous;
    }
}
