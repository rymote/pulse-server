using System.Text.RegularExpressions;

namespace Rymote.Pulse.Core;

public class HandlePattern
{
    public string OriginalPattern { get; }
    public Regex Regex { get; }

    public HandlePattern(string pattern)
    {
        OriginalPattern = pattern;
        Regex = ConvertPatternToRegex(pattern);
    }

    private static Regex ConvertPatternToRegex(string pattern)
    {
        string regexPattern = Regex.Replace(pattern, @"\{(\w+)\}", "(?<$1>[^/]+)");
        regexPattern = $"^{regexPattern}$";

        return new Regex(regexPattern, RegexOptions.Compiled | RegexOptions.IgnoreCase);
    }

    public static implicit operator HandlePattern(string pattern) => new HandlePattern(pattern);
}
