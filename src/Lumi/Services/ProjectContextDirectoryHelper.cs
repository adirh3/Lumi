using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services;

public static class ProjectContextDirectoryHelper
{
    public static List<string> ParseFolderList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return NormalizeFolderList(value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    public static string FormatFolderList(IEnumerable<string>? folders)
        => folders is null ? string.Empty : string.Join(Environment.NewLine, NormalizeFolderList(folders));

    public static List<string> NormalizeFolderList(IEnumerable<string>? folders)
    {
        if (folders is null)
            return [];

        var normalized = new List<string>();
        foreach (var folder in folders)
        {
            var path = NormalizePathOrNull(folder);
            if (path is null)
                continue;

            if (normalized.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
                continue;

            normalized.Add(path);
        }

        return normalized;
    }

    public static IReadOnlyList<string> GetExistingContextDirectories(string? primaryDirectory, Project? project)
    {
        var directories = new List<string>();
        AddExistingDirectory(directories, primaryDirectory);

        if (project is not null)
        {
            foreach (var directory in project.AdditionalContextDirectories)
                AddExistingDirectory(directories, directory);
        }

        return directories;
    }

    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        return Environment.ExpandEnvironmentVariables(path.Trim());
    }

    private static void AddExistingDirectory(List<string> directories, string? directory)
    {
        var path = NormalizePathOrNull(directory);
        if (path is null || !Directory.Exists(path))
            return;

        if (directories.Any(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase)))
            return;

        directories.Add(path);
    }
}
