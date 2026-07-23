namespace GenshinBrowser.Browser;

public static class DownloadUriComparer
{
    public static bool Matches(string expected, string actual)
    {
        if (!Uri.TryCreate(expected, UriKind.Absolute, out Uri? expectedUri) ||
            !Uri.TryCreate(actual, UriKind.Absolute, out Uri? actualUri))
        {
            return false;
        }

        if (!string.Equals(expectedUri.Scheme, actualUri.Scheme, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(expectedUri.Host, actualUri.Host, StringComparison.OrdinalIgnoreCase) ||
            expectedUri.Port != actualUri.Port)
        {
            return false;
        }

        string expectedPath = expectedUri.AbsolutePath.TrimEnd('/');
        string actualPath = actualUri.AbsolutePath.TrimEnd('/');
        return string.Equals(expectedPath, actualPath, StringComparison.Ordinal) &&
               QuerySetsEqual(expectedUri.Query, actualUri.Query);
    }

    private static bool QuerySetsEqual(string expectedQuery, string actualQuery)
    {
        Dictionary<string, string> expected = ParseQuery(expectedQuery);
        Dictionary<string, string> actual = ParseQuery(actualQuery);
        if (expected.Count != actual.Count)
        {
            return false;
        }

        foreach ((string key, string value) in expected)
        {
            if (!actual.TryGetValue(key, out string? other) ||
                !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        string value = query.Length > 0 && query[0] == '?' ? query[1..] : query;
        foreach (string part in value.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int equalsIndex = part.IndexOf('=');
            if (equalsIndex < 0)
            {
                result[Uri.UnescapeDataString(part)] = string.Empty;
            }
            else
            {
                result[Uri.UnescapeDataString(part[..equalsIndex])] =
                    Uri.UnescapeDataString(part[(equalsIndex + 1)..]);
            }
        }
        return result;
    }
}
