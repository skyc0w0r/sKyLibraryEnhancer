using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Model.Plugins;

namespace SkyLibraryEnhancer.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the comma separated list of extensions that belong to subtitle files.
    /// </summary>
    public string IgnoreExtensions { get; set; } = "ass, srt, ttf, otf";

    public IReadOnlyCollection<string> IgnoreExtentionsList => [.. IgnoreExtensions.Split(',').Select(c => c.Trim())];

    /// <summary>
    /// Gets or sets a value indicating whether folders shall be ignored based of <see cref="IgnoreNames"/>.
    /// </summary>
    public bool EnableNameBasedIgnore { get; set; } = true;

    /// <summary>
    /// Gets or sets the comma separated list of folders that contain ignored media (subtitles, audio tracks, etc).
    /// </summary>
    public string IgnoreNames { get; set; } = "subs, sound";

    public IReadOnlyCollection<(string Name, bool Exact)> IgnoreNamesList => [..IgnoreNames
        .Split(',')
        .Select(c => c.Trim())
        .Select(c =>
        {
            if (c.StartsWith('^') && c.EndsWith('$'))
            {
                return (c[1..^1], true);
            }

            return (c, false);
        })];
}
