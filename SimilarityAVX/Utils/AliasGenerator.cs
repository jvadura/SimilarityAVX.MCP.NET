using System;
using System.Text.RegularExpressions;

namespace CSharpMcpServer.Utils
{
    /// <summary>
    /// Utility class for generating memory aliases from memory names
    /// </summary>
    public static class AliasGenerator
    {
        /// <summary>
        /// Generate a URL-friendly alias from a memory name
        /// Examples:
        /// "API Design Decisions" -> "api-design-decisions"
        /// "Authentication Flow" -> "authentication-flow"
        /// "Bug Fix: Login Issue" -> "bug-fix-login-issue"
        /// </summary>
        public static string GenerateAlias(string memoryName)
        {
            if (string.IsNullOrWhiteSpace(memoryName))
                return string.Empty;

            return memoryName
                .ToLowerInvariant()
                .Replace(" ", "-")
                .Replace("_", "-")
                .Replace(":", "-")
                .Replace("/", "-")
                .Replace("\\", "-")
                .Replace("(", "")
                .Replace(")", "")
                .Replace("[", "")
                .Replace("]", "")
                .Replace("{", "")
                .Replace("}", "")
                .Replace("\"", "")
                .Replace("'", "")
                .Replace(".", "-")
                .Replace(",", "")
                .Replace(";", "")
                .Replace("!", "")
                .Replace("?", "")
                .Replace("@", "at")
                .Replace("#", "number")
                .Replace("$", "")
                .Replace("%", "percent")
                .Replace("^", "")
                .Replace("&", "and")
                .Replace("*", "")
                .Replace("+", "plus")
                .Replace("=", "equals")
                .Replace("|", "")
                .Replace("<", "")
                .Replace(">", "")
                .Replace("~", "")
                .Replace("`", "")
                // Remove any non-alphanumeric characters except hyphens
                .Replace(@"[^a-z0-9\-]", "")
                // Replace multiple consecutive hyphens with single hyphen
                .Replace(@"-+", "-")
                // Remove leading and trailing hyphens
                .Trim('-');
        }

        /// <summary>
        /// Check if an alias is valid (alphanumeric and hyphens only, not empty)
        /// </summary>
        public static bool IsValidAlias(string alias)
        {
            if (string.IsNullOrWhiteSpace(alias))
                return false;

            return Regex.IsMatch(alias, @"^[a-z0-9\-]+$") && 
                   !alias.StartsWith("-") && 
                   !alias.EndsWith("-");
        }

        /// <summary>
        /// Generate a unique alias by appending a number if needed
        /// </summary>
        public static string GenerateUniqueAlias(string memoryName, Func<string, bool> aliasExists)
        {
            var baseAlias = GenerateAlias(memoryName);
            
            if (string.IsNullOrEmpty(baseAlias))
                baseAlias = "memory";

            var alias = baseAlias;
            var counter = 1;

            while (aliasExists(alias))
            {
                alias = $"{baseAlias}-{counter}";
                counter++;
            }

            return alias;
        }
    }

    /// <summary>
    /// Extension methods for Regex to improve readability
    /// </summary>
    internal static class RegexExtensions
    {
        public static string Replace(this string input, string pattern, string replacement)
        {
            return new Regex(pattern, RegexOptions.Compiled).Replace(input, replacement);
        }
        
        public static Regex Regex(this string pattern)
        {
            return new Regex(pattern, RegexOptions.Compiled);
        }
    }
}