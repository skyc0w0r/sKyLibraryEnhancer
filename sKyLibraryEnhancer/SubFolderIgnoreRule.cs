using System;
using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.IO;
using Microsoft.Extensions.Logging;

namespace SkyLibraryEnhancer
{
    public class SubFolderIgnoreRule(
        ILogger<SubFolderIgnoreRule> logger,
        IDirectoryService directoryService) : IResolverIgnoreRule
    {
        private IEnumerable<string> IgnoreExtentions => Plugin.Instance?.Configuration.IgnoreExtentionsList ?? [];

        private IEnumerable<(string Name, bool Exact)> IgnoreNames => Plugin.Instance?.Configuration.IgnoreNamesList ?? [];

        public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem? parent)
        {
            bool MatchExt(string ext)
                => IgnoreExtentions.Any(c => c.Equals(ext, StringComparison.OrdinalIgnoreCase));

            if (parent is null || parent is not Series)
            {
                return false;
            }

            if (fileInfo is null || !fileInfo.IsDirectory)
            {
                return false;
            }

            var files = directoryService.GetFiles(fileInfo.FullName);
            var dirs = directoryService.GetDirectories(fileInfo.FullName);
            var allFilesShallBeIgnored = files.All(c => MatchExt(c.Extension[1..]));

            if (allFilesShallBeIgnored && dirs.Count == 0)
            {
                logger.LogInformation("Ignoring directory with subs/sounds only '{Name}' for '{SeriesName}'", fileInfo.Name, parent.Name);
                return true;
            }

            var nestedFiles = dirs.Select(c => directoryService.GetFiles(c.FullName)).SelectMany(c => c).ToList();
            var allNestedFilesShallBeIgnored = nestedFiles.All(c => MatchExt(c.Extension[1..]));

            if (allNestedFilesShallBeIgnored && allFilesShallBeIgnored)
            {
                logger.LogInformation("Ignoring nested directory with subs/sounds only '{Name}' for '{SeriesName}'", fileInfo.Name, parent.Name);
                return true;
            }

            foreach ((var name, var exact) in IgnoreNames)
            {
                if ((exact && fileInfo.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) || fileInfo.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                {
                    logger.LogInformation("Ignoring directory '{FolderName}' for '{SeriesName}' matched '{Pattern}'", fileInfo.Name, parent.Name, name);
                    return true;
                }
            }

            return false;
        }
    }
}
