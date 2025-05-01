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
        private IEnumerable<string> SubExtentions => Plugin.Instance?.Configuration.IgnoreExtentionsList ?? [];

        private IEnumerable<string> IgnoreNames => Plugin.Instance?.Configuration.IgnoreNamesList ?? [];

        public bool ShouldIgnore(FileSystemMetadata fileInfo, BaseItem? parent)
        {
            if (parent is not null && parent is Series)
            {
                if (fileInfo.IsDirectory)
                {
                    var files = directoryService.GetFiles(fileInfo.FullName);
                    var dirs = directoryService.GetDirectories(fileInfo.FullName);
                    var allFilesAreSubtitles = files.All(c => SubExtentions.Any(d => d == c.Extension[1..]));

                    if (allFilesAreSubtitles && !dirs.Any())
                    {
                        logger.LogInformation("Ignoring directory with subs/sounds only '{Name}' for '{SeriesName}'", fileInfo.Name, parent.Name);
                        return true;
                    }

                    var nestedFiles = dirs.Select(c => directoryService.GetFiles(c.FullName)).SelectMany(c => c).ToList();
                    var allNestedFilesAreSubtitles = nestedFiles.Any() && nestedFiles.All(c => SubExtentions.Any(d => d == c.Extension[1..]));

                    if (allNestedFilesAreSubtitles)
                    {
                        logger.LogInformation("Ignoring nested directory with subs/sounds only '{Name}' for '{SeriesName}'", fileInfo.Name, parent.Name);
                        return true;
                    }

                    foreach (var ignoreName in IgnoreNames)
                    {
                        if (fileInfo.Name.Contains(ignoreName, System.StringComparison.OrdinalIgnoreCase))
                        {
                            logger.LogInformation("Ignoring directory '{FolderName}' for '{SeriesName}' matched '{Pattern}'", fileInfo.Name, parent.Name, ignoreName);
                            return true;
                        }
                    }
                }
            }

            return false;
        }
    }
}
