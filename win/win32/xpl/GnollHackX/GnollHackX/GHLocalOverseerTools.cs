using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace GnollHackX
{
    /* Tools of the local AI Overseer that need no game: searching and reading the GnollHack source code and looking up the
       game's encyclopedia (dat/data.base). The source folder is GHApp.LocalAiSourceFolder, or found automatically above the
       app's folder. Files are read once and kept in memory. */
    public static class GHLocalOverseerTools
    {
        private const int DefaultSearchResults = 40;
        private const int MaxSearchResults = 60;
        private const int MaxResultLineLength = 220;
        private const int DefaultReadLines = 200;
        private const int MaxReadLines = 400;
        private const int MaxEncyclopediaEntries = 3;

        private static readonly string[] SearchedFolders = { "src", "include", "dat" };
        private static readonly string[] SearchedExtensions = { ".c", ".h", ".des", ".txt", ".base", ".tru", ".fal" };

        private static readonly object _lock = new object();
        private static string _loadedRoot;
        private static List<KeyValuePair<string, string[]>> _files;
        private static List<EncyclopediaEntry> _encyclopedia;

        private sealed class EncyclopediaEntry
        {
            public readonly List<string> Keys = new List<string>();
            public readonly StringBuilder Text = new StringBuilder();
        }

        public static bool IsLocalTool(string name)
        {
            return name == "search_gnollhack_code" || name == "read_gnollhack_file" || name == "lookup_encyclopedia";
        }

        private static bool IsSourceFolder(string folder)
        {
            return !string.IsNullOrEmpty(folder)
                && File.Exists(Path.Combine(folder, "src", "invent.c"))
                && File.Exists(Path.Combine(folder, "include", "hack.h"));
        }

        /* The GnollHack source folder, or null if there is none */
        public static string FindSourceFolder()
        {
            string configured = GHApp.LocalAiSourceFolder;
            if (!string.IsNullOrWhiteSpace(configured))
                return IsSourceFolder(configured.Trim()) ? Path.GetFullPath(configured.Trim()) : null;

            /* A build from the source tree runs below it */
            try
            {
                DirectoryInfo dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 12 && dir != null; i++)
                {
                    if (IsSourceFolder(dir.FullName))
                        return dir.FullName;
                    dir = dir.Parent;
                }
            }
            catch (Exception)
            {
            }
            return null;
        }

        private static string RequireSourceFolder()
        {
            string root = FindSourceFolder();
            if (root == null)
                throw new InvalidOperationException("The GnollHack source code is not available. The player can set its folder in Settings (Local AI Source Folder).");
            return root;
        }

        private static void EnsureLoaded(string root)
        {
            lock (_lock)
            {
                if (_files != null && _loadedRoot == root)
                    return;
                List<KeyValuePair<string, string[]>> files = new List<KeyValuePair<string, string[]>>();
                foreach (string folder in SearchedFolders)
                {
                    string dir = Path.Combine(root, folder);
                    if (!Directory.Exists(dir))
                        continue;
                    foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string ext = Path.GetExtension(path).ToLowerInvariant();
                        if (Array.IndexOf(SearchedExtensions, ext) < 0)
                            continue;
                        try
                        {
                            string rel = path.Substring(root.Length).TrimStart('\\', '/').Replace('\\', '/');
                            files.Add(new KeyValuePair<string, string[]>(rel, File.ReadAllLines(path)));
                        }
                        catch (Exception)
                        {
                        }
                    }
                }
                files.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
                _files = files;
                _encyclopedia = null;
                _loadedRoot = root;
            }
        }

        public static string Execute(string name, JObject arguments)
        {
            switch (name)
            {
                case "search_gnollhack_code":
                    return SearchCode(arguments);
                case "read_gnollhack_file":
                    return ReadFile(arguments);
                case "lookup_encyclopedia":
                    return LookupEncyclopedia(arguments);
                default:
                    throw new NotSupportedException("Tool not implemented: " + name);
            }
        }

        private static int GetInt(JObject arguments, string name, int defaultValue)
        {
            JToken token = arguments?[name];
            int value;
            if (token != null && int.TryParse(token.ToString(), out value))
                return value;
            return defaultValue;
        }

        private static string SearchCode(JObject arguments)
        {
            string query = arguments?["query"]?.ToString();
            if (string.IsNullOrWhiteSpace(query))
                throw new ArgumentException("query required");
            string pathFilter = arguments?["path_filter"]?.ToString();
            int maxResults = Math.Max(1, Math.Min(MaxSearchResults, GetInt(arguments, "max_results", DefaultSearchResults)));

            string root = RequireSourceFolder();
            EnsureLoaded(root);

            StringBuilder sb = new StringBuilder();
            int total = 0;
            lock (_lock)
            {
                foreach (KeyValuePair<string, string[]> file in _files)
                {
                    if (!string.IsNullOrEmpty(pathFilter) && file.Key.IndexOf(pathFilter.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase) < 0)
                        continue;
                    string[] lines = file.Value;
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        total++;
                        if (total <= maxResults)
                        {
                            string line = lines[i].Trim();
                            if (line.Length > MaxResultLineLength)
                                line = line.Substring(0, MaxResultLineLength) + "...";
                            sb.Append(file.Key).Append(':').Append(i + 1).Append(": ").Append(line).Append('\n');
                        }
                    }
                }
            }
            if (total == 0)
                return "No matches for \"" + query + "\".";
            return total + " matches" + (total > maxResults ? " (showing the first " + maxResults + "; narrow the search with path_filter or a longer query)" : "") + ":\n" + sb.ToString();
        }

        private static string ReadFile(JObject arguments)
        {
            string path = arguments?["path"]?.ToString();
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("path required");
            string root = RequireSourceFolder();
            string fullPath = Path.GetFullPath(Path.Combine(root, path.Trim().TrimStart('/', '\\')));
            string rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString()) ? root : root + Path.DirectorySeparatorChar;
            if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("The path must be inside the GnollHack source folder.");
            if (!File.Exists(fullPath))
                throw new FileNotFoundException("No such file: " + path);

            string[] lines = File.ReadAllLines(fullPath);
            int start = Math.Max(1, GetInt(arguments, "start_line", 1));
            int end = GetInt(arguments, "end_line", start + DefaultReadLines - 1);
            end = Math.Min(Math.Min(end, start + MaxReadLines - 1), lines.Length);
            if (start > lines.Length)
                return path + " has only " + lines.Length + " lines.";

            StringBuilder sb = new StringBuilder();
            sb.Append(path).Append(", lines ").Append(start).Append('-').Append(end).Append(" of ").Append(lines.Length).Append(":\n");
            for (int i = start; i <= end; i++)
                sb.Append(i).Append(": ").Append(lines[i - 1]).Append('\n');
            return sb.ToString();
        }

        /*
         * The encyclopedia: entries of dat/data.base are key lines (lowercase, with * and ? wildcards; a key starting with ~
         * excludes what it matches from the entry) followed by text lines starting with a tab
         */

        private static List<EncyclopediaEntry> LoadEncyclopedia(string root)
        {
            lock (_lock)
            {
                if (_encyclopedia != null && _loadedRoot == root)
                    return _encyclopedia;
                List<EncyclopediaEntry> entries = new List<EncyclopediaEntry>();
                string path = Path.Combine(root, "dat", "data.base");
                if (File.Exists(path))
                {
                    EncyclopediaEntry current = null;
                    foreach (string rawLine in File.ReadAllLines(path))
                    {
                        if (rawLine.StartsWith("#"))
                            continue;
                        if (rawLine.StartsWith("\t"))
                        {
                            if (current != null)
                                current.Text.Append(rawLine.TrimStart('\t')).Append('\n');
                        }
                        else if (rawLine.Trim().Length > 0)
                        {
                            if (current == null || current.Text.Length > 0)
                            {
                                current = new EncyclopediaEntry();
                                entries.Add(current);
                            }
                            current.Keys.Add(rawLine.Trim().ToLowerInvariant());
                        }
                    }
                }
                if (_loadedRoot == root)
                    _encyclopedia = entries;
                return entries;
            }
        }

        private static bool WildcardMatch(string text, string pattern)
        {
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(text, regex);
        }

        private static string LookupEncyclopedia(JObject arguments)
        {
            string term = arguments?["term"]?.ToString();
            if (string.IsNullOrWhiteSpace(term))
                throw new ArgumentException("term required");
            term = term.Trim().ToLowerInvariant();
            foreach (string article in new string[] { "a ", "an ", "the " })
            {
                if (term.StartsWith(article))
                    term = term.Substring(article.Length);
            }

            string root = RequireSourceFolder();
            EnsureLoaded(root);
            List<EncyclopediaEntry> entries = LoadEncyclopedia(root);
            StringBuilder sb = new StringBuilder();
            int found = 0;
            foreach (EncyclopediaEntry entry in entries)
            {
                bool excluded = false;
                bool matched = false;
                foreach (string key in entry.Keys)
                {
                    if (key.StartsWith("~"))
                    {
                        if (WildcardMatch(term, key.Substring(1)))
                            excluded = true;
                    }
                    else if (!excluded && WildcardMatch(term, key))
                    {
                        matched = true;
                        break;
                    }
                }
                if (!matched)
                    continue;
                found++;
                sb.Append("Entry (").Append(string.Join(", ", entry.Keys)).Append("):\n").Append(entry.Text.ToString().TrimEnd()).Append("\n\n");
                if (found >= MaxEncyclopediaEntries)
                    break;
            }
            if (found == 0)
                return "The encyclopedia has no entry for \"" + term + "\". Try the singular form or a more general name, or search the source code.";
            return sb.ToString().TrimEnd();
        }
    }
}
