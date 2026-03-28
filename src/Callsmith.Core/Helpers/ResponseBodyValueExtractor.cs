using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Xml.XPath;
using Callsmith.Core.Models;

namespace Callsmith.Core.Helpers;

/// <summary>
/// Extracts a single value from a response body using JSONPath, XPath, or regex.
/// </summary>
public static class ResponseBodyValueExtractor
{
    /// <summary>
    /// Extracts a value from <paramref name="responseBody"/> using the selected
    /// <paramref name="matcher"/> and <paramref name="expression"/>.
    /// Returns <see langword="null"/> when extraction fails or no match is found.
    /// </summary>
    public static string? Extract(string responseBody, ResponseValueMatcher matcher, string expression)
    {
        if (string.IsNullOrWhiteSpace(responseBody) || string.IsNullOrWhiteSpace(expression))
            return null;

        return matcher switch
        {
            ResponseValueMatcher.JsonPath => JsonPathHelper.Extract(responseBody, expression),
            ResponseValueMatcher.XPath => ExtractXPath(responseBody, expression),
            ResponseValueMatcher.Regex => ExtractRegex(responseBody, expression),
            _ => null,
        };
    }

    private static string? ExtractRegex(string body, string pattern)
    {
        try
        {
            var match = Regex.Match(body, pattern, RegexOptions.Singleline);
            return match.Success ? match.Value : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? ExtractXPath(string xml, string xpath)
    {
        try
        {
            var document = XDocument.Parse(xml, LoadOptions.PreserveWhitespace);
            var navigator = document.CreateNavigator();
            if (navigator is null)
                return null;

            var nsManager = new XmlNamespaceManager(navigator.NameTable);
            var root = document.Root;
            if (root is not null)
            {
                foreach (var attribute in root.Attributes().Where(a => a.IsNamespaceDeclaration))
                {
                    var prefix = attribute.Name.LocalName == "xmlns" ? string.Empty : attribute.Name.LocalName;
                    nsManager.AddNamespace(prefix, attribute.Value);
                }
            }

            var result = navigator.Evaluate(xpath, nsManager);
            return result switch
            {
                XPathNodeIterator iterator => iterator.MoveNext() ? iterator.Current?.Value : null,
                string text => string.IsNullOrEmpty(text) ? null : text,
                bool boolean => boolean.ToString(),
                double number => number.ToString(CultureInfo.InvariantCulture),
                _ => null,
            };
        }
        catch (XmlException)
        {
            return null;
        }
        catch (XPathException)
        {
            return null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
