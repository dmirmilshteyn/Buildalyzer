﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Buildalyzer.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Buildalyzer
{
    public class AnalyzerResult
    {
        private readonly Dictionary<string, string> _properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ProjectItem[]> _items = new Dictionary<string, ProjectItem[]>(StringComparer.OrdinalIgnoreCase);
        private readonly Guid _projectGuid;
        private List<(string, string)> _cscCommandLineArguments;

        internal AnalyzerResult(string projectFilePath, AnalyzerManager manager, ProjectAnalyzer analyzer)
        {
            ProjectFilePath = projectFilePath;
            Manager = manager;
            Analyzer = analyzer;

            string projectGuid = GetProperty(nameof(ProjectGuid));
            if (string.IsNullOrEmpty(projectGuid) || !Guid.TryParse(projectGuid, out _projectGuid))
            {
                _projectGuid = analyzer == null
                    ? GuidUtility.Create(GuidUtility.UrlNamespace, ProjectFilePath)
                    : analyzer.ProjectGuid;
            }
        }

        /// <summary>
        /// The full normalized path to the project file.
        /// </summary>
        public string ProjectFilePath { get; }

        public AnalyzerManager Manager { get; }

        /// <summary>
        /// Gets the <see cref="ProjectAnalyzer"/> that generated this result
        /// or <c>null</c> if the result came from a binary log file.
        /// </summary>
        public ProjectAnalyzer Analyzer { get; }

        public bool Succeeded { get; internal set; }

        public IReadOnlyDictionary<string, string> Properties => _properties;

        public IReadOnlyDictionary<string, ProjectItem[]> Items => _items;

        /// <summary>
        /// Gets a GUID for the project. This first attempts to get the <c>ProjectGuid</c>
        /// MSBuild property. If that's not available, checks for a GUID from the
        /// solution (if originally provided). If neither of those are available, it
        /// will generate a UUID GUID by hashing the project path relative to the solution path (so it's repeatable).
        /// </summary>
        public Guid ProjectGuid => _projectGuid;

        /// <summary>
        /// Gets the value of the specified property and returns <c>null</c>
        /// if the property could not be found.
        /// </summary>
        /// <param name="name">The name of the property.</param>
        /// <returns>The value of the property or <c>null</c>.</returns>
        public string GetProperty(string name) =>
            Properties.TryGetValue(name, out string value) ? value : null;

        public string TargetFramework =>
            ProjectFile.GetTargetFrameworks(
                null,  // Don't want all target frameworks since the result is just for one
                new[] { GetProperty(ProjectFileNames.TargetFramework) },
                new[] { (GetProperty(ProjectFileNames.TargetFrameworkIdentifier), GetProperty(ProjectFileNames.TargetFrameworkVersion)) })
            .FirstOrDefault();

        public string[] SourceFiles =>
            _cscCommandLineArguments
                ?.Where(x => x.Item1 == null
                    && !string.Equals(Path.GetFileName(x.Item2), "csc.dll", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(Path.GetFileName(x.Item2), "csc.exe", StringComparison.OrdinalIgnoreCase))
                .Select(x => AnalyzerManager.NormalizePath(Path.Combine(Path.GetDirectoryName(ProjectFilePath), x.Item2)))
                .ToArray() ?? Array.Empty<string>();

        public string[] References =>
            _cscCommandLineArguments
                ?.Where(x => x.Item1 == "reference")
                .Select(x => x.Item2)
                .ToArray() ?? Array.Empty<string>();

        public IEnumerable<string> ProjectReferences =>
            Items.TryGetValue("ProjectReference", out ProjectItem[] items)
                ? items.Select(x => AnalyzerManager.NormalizePath(
                    Path.Combine(Path.GetDirectoryName(ProjectFilePath), x.ItemSpec)))
                : Array.Empty<string>();

        /// <summary>
        /// Contains the <c>PackageReference</c> items for the project.
        /// The key is a package ID and the value is a <see cref="IReadOnlyDictionary{TKey, TValue}"/>
        /// that includes all the package reference metadata, typically including a "Version" key.
        /// </summary>
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> PackageReferences =>
            Items.TryGetValue("PackageReference", out ProjectItem[] items)
                ? items.Distinct(new ProjectItemItemSpecEqualityComparer()).ToDictionary(x => x.ItemSpec, x => x.Metadata)
                : new Dictionary<string, IReadOnlyDictionary<string, string>>();

        internal void ProcessProject(ProjectStartedEventArgs e)
        {
            // Add properties
            foreach (DictionaryEntry entry in e.Properties.Cast<DictionaryEntry>())
            {
                _properties[entry.Key.ToString()] = entry.Value.ToString();
            }

            // Add items
            foreach (IGrouping<string, DictionaryEntry> itemGroup in e.Items.Cast<DictionaryEntry>().GroupBy(x => x.Key.ToString()))
            {
                _items[itemGroup.Key] = itemGroup.Select(x => new ProjectItem((ITaskItem)x.Value)).ToArray();
            }
        }

        internal void ProcessCscCommandLine(string commandLine, bool coreCompile)
        {
            // Some projects can have multiple Csc calls (see #92) so if this is the one inside CoreCompile use it, otherwise use the first
            if (string.IsNullOrWhiteSpace(commandLine) || (_cscCommandLineArguments != null && !coreCompile))
            {
                return;
            }
            _cscCommandLineArguments = ProcessCscCommandLine(commandLine);
        }

        internal static List<(string, string)> ProcessCscCommandLine(string commandLine)
        {
            List<(string, string)> args = new List<(string, string)>();

            string[] parts = commandLine.Split(new[] { ' ' });

            // Combine the initial command (find the first argument by looking for the last part that contains "csc.")
            int start = Array.FindIndex(parts, x => x.Length > 0 && x.IndexOf("csc.", StringComparison.InvariantCultureIgnoreCase) >= 0) + 1;
            args.Add((null, string.Join(" ", parts.Take(start)).Trim('"')));

            // Iterate the rest of them
            for (int c = start; c < parts.Length; c++)
            {
                if (parts[c].Length > 0)
                {
                    int valueStart = 0;
                    // Attempt to differentiate between unix-like absolute paths (/path/to/file) and csc arguments by testing if the file exists on disk
                    if (parts[c][0] == '/' && !File.Exists(parts[c]))
                    {
                        valueStart = parts[c].IndexOf(':');
                        if (valueStart == -1 || valueStart >= parts[c].Length - 1)
                        {
                            // Argument without a value
                            args.Add(
                                (valueStart == -1 ? parts[c].Substring(1) : parts[c].Substring(1, valueStart - 1), null));
                            continue;
                        }
                        valueStart++;  // Move to the value
                    }

                    if (parts[c][valueStart] == '"')
                    {
                        // The value is quoted, find the end quote
                        int first = c;
                        while (c < parts.Length)
                        {
                            // The last char is a quote
                            if (((c == first && parts[c].Length > valueStart + 1) || (c != first && parts[c].Length > 0))
                                && parts[c][parts[c].Length - 1] == '"')
                            {
                                // ...and it's not preceded by an escape
                                if (parts[c].Length < 2 || parts[c][parts[c].Length - 2] != '\\')
                                {
                                    break;
                                }
                            }
                            c++;
                        }

                        if (first == c)
                        {
                            // The end quote was in the same part
                            args.Add((
                                valueStart == 0 ? null : parts[c].Substring(1, valueStart - 2),
                                RemoveQuotes(parts[c].Substring(valueStart))));
                            continue;
                        }

                        // The end quote is in another part, join them
                        args.Add((
                            valueStart == 0 ? null : parts[first].Substring(1, valueStart - 2),
                            RemoveQuotes(string.Join(" ", parts.Skip(first).Take(c - first + 1)).Substring(valueStart))));
                        continue;
                    }

                    // Not quoted, return the value
                    args.Add((
                        valueStart == 0 ? null : parts[c].Substring(1, valueStart - 2),
                        parts[c].Substring(valueStart)));
                }
            }

            return args;
        }

        /// <summary>
        /// Removes quotes from the string, but only from the first and last character.
        /// </summary>>
        private static string RemoveQuotes(string str)
        {
            if (str.Length < 2)
            {
                return str.Trim('"');
            }
            int start = str[0] == '"' ? 1 : 0;
            int length = str[str.Length - 1] == '"' ? str.Length - start - 1 : str.Length - start;
            return str.Length > 1 ? str.Substring(start, length) : str;
        }

        private class ProjectItemItemSpecEqualityComparer : IEqualityComparer<ProjectItem>
        {
            public bool Equals(ProjectItem x, ProjectItem y) => x.ItemSpec.Equals(y.ItemSpec);
            public int GetHashCode(ProjectItem obj) => obj.ItemSpec.GetHashCode();
        }
    }
}