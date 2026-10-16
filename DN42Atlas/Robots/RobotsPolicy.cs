using System.Text.RegularExpressions;

namespace DN42Atlas.Robots;

// Shared policy evaluation only: callers own network provenance and body limits.
internal static class RobotsPolicy
{
    internal static bool? IsPathAllowed(
        string robotsText,
        string userAgent,
        string path,
        CancellationToken cancellationToken = default)
    {
        try { return Evaluate(robotsText, userAgent, path, cancellationToken); }
        catch (RegexMatchTimeoutException) { return null; }
    }

    private static bool? Evaluate(
        string robotsText, string userAgent, string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        robotsText = robotsText.TrimStart('\uFEFF');
        // A nonempty response without a recognizable policy is ambiguous, not permission.
        if (!string.IsNullOrWhiteSpace(robotsText) &&
            robotsText.Split('\n').Any(line =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var value = line.Split('#', 2)[0].Trim();
                if (value.Length == 0) return false;
                var colon = value.IndexOf(':');
                if (colon <= 0) return true;
                var field = value[..colon].Trim();
                var directive = value[(colon + 1)..].Trim();
                if (field.Equals("User-agent", StringComparison.OrdinalIgnoreCase))
                    return directive.Length == 0;
                if (field.Equals("Allow", StringComparison.OrdinalIgnoreCase) ||
                    field.Equals("Disallow", StringComparison.OrdinalIgnoreCase))
                    return directive.Length > 0 && !directive.StartsWith('/');
                return false;
            }))
            return null;

        var groups =
            ParseRobotsGroups(
                robotsText, cancellationToken);

        if (groups.Count == 0 && robotsText.Split('\n').Any(line => line.Split('#', 2)[0].Trim().Length > 0))
            return null;

        var applicable =
            groups
                .Where(
                    g => g.Agents.Any(
                        a => a.Equals(
                            userAgent,
                            StringComparison.OrdinalIgnoreCase)))
                .ToList();

        if (applicable.Count == 0)
        {
            applicable =
                groups
                    .Where(
                        g => g.Agents.Any(
                            a => a == "*"))
                    .ToList();
        }

        if (applicable.Count == 0)
            return true;

        var matchingRules =
            applicable
                .SelectMany(g => g.Rules)
                .Where(
                    rule =>
                        !string.IsNullOrEmpty(
                            rule.Pattern) &&
                        RobotsPatternMatches(
                            rule.Pattern,
                            path, cancellationToken))
                .Select(
                    rule => new
                    {
                        Rule = rule,

                        Specificity =
                            rule.Pattern.Count(
                                c =>
                                    c != '*' &&
                                    c != '$')
                    })
                .OrderByDescending(
                    x => x.Specificity)
                .ThenByDescending(
                    x => x.Rule.Allow)
                .ToList();

        if (matchingRules.Count == 0)
            return true;

        return matchingRules[0]
            .Rule
            .Allow;
    }

    private static List<RobotsGroup>
        ParseRobotsGroups(
            string robotsText,
            CancellationToken cancellationToken)
    {
        var groups =
            new List<RobotsGroup>();

        RobotsGroup? current = null;

        var sawRule = false;

        foreach (
            var rawLine in
            robotsText.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line =
                rawLine
                    .Split('#', 2)[0]
                    .Trim();

            if (string.IsNullOrWhiteSpace(line))
                continue;

            var colon =
                line.IndexOf(':');

            if (colon <= 0)
                continue;

            var field =
                line[..colon].Trim();

            var value =
                line[(colon + 1)..].Trim();

            if (field.Equals(
                "User-agent",
                StringComparison.OrdinalIgnoreCase))
            {
                if (current == null ||
                    sawRule)
                {
                    current =
                        new RobotsGroup();

                    groups.Add(current);

                    sawRule = false;
                }

                current.Agents.Add(value);

                continue;
            }

            if (current == null)
                continue;

            if (field.Equals(
                "Allow",
                StringComparison.OrdinalIgnoreCase))
            {
                current.Rules.Add(
                    new RobotsRule(
                        true,
                        value));

                sawRule = true;

                continue;
            }

            if (field.Equals(
                "Disallow",
                StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrEmpty(value))
                {
                    current.Rules.Add(
                        new RobotsRule(
                            false,
                            value));
                }

                sawRule = true;
            }
        }

        return groups;
    }

    private static bool RobotsPatternMatches(
        string pattern,
        string path,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endAnchored =
            pattern.EndsWith('$');

        if (endAnchored)
            pattern = pattern[..^1];

        var regexPattern =
            Regex.Escape(pattern)
                .Replace(
                    @"\*",
                    ".*");

        regexPattern =
            "^" + regexPattern;

        if (endAnchored)
            regexPattern += "$";

        return Regex.IsMatch(
            path,
            regexPattern, RegexOptions.None, TimeSpan.FromMilliseconds(100));
    }

    private sealed class RobotsGroup
    {
        public List<string> Agents { get; } = [];

        public List<RobotsRule> Rules { get; } = [];
    }

    private sealed record RobotsRule(
        bool Allow,
        string Pattern);
}
