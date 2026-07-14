using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using Emby.Naming.ExternalFiles;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.Logging;

namespace SkyLibraryEnhancer.Classes
{
    internal class DiscoveryWorker(
        ILogger logger,
        ILibraryManager libraryManager,
        IDirectoryService directoryService,
        IMediaStreamRepository mediaStreamRepository,
        IMediaEncoder mediaEncoder,
        ILocalizationManager localizationManager,
        NamingOptions namingOptions)
    {
        private readonly ExternalPathParser _subtitlePathParser = new(namingOptions, localizationManager, DlnaProfileType.Subtitle);
        private readonly ExternalPathParser _audioPathParser = new(namingOptions, localizationManager, DlnaProfileType.Audio);

        public async Task Discover(IReadOnlyCollection<Guid> videos, CancellationToken cancellationToken = default)
        {
            var isCanceled = false;
            var options = new ParallelOptions()
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken,
            };
#if DEBUG
            var token = cancellationToken;
            foreach (var videoId in videos)
#else
            await Parallel.ForEachAsync(videos, options, async (videoId, token) =>
#endif
            {
                try
                {
                    if (videoId == Guid.Empty)
                    {
                        return;
                    }

                    var video = libraryManager.GetItemById<Video>(videoId);
                    if (video is null)
                    {
                        return;
                    }

                    var parent = libraryManager.GetItemById(video.ParentId);
                    if (parent is null)
                    {
                        return;
                    }

                    await SearchSubtitlesAndAudios(video, parent, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    logger.LogInformation("Discovery was canceled");
                    isCanceled = true;
                }
#if DEBUG
            }
#else
        }).ConfigureAwait(false);
#endif

            if (!isCanceled)
            {
                logger.LogInformation("Discovery finished");
            }
        }

        private async Task SearchSubtitlesAndAudios(Video video, BaseItem parent, CancellationToken cancellationToken = default)
        {
            var dirs = directoryService.GetDirectories(video.ContainingFolderPath);
            var searchQueue = new Queue<FileSystemMetadata>(dirs);
            var files = new List<ExternalPathParserResult>();

            while (searchQueue.TryDequeue(out var dir))
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var nestedDir in directoryService.GetDirectories(dir.FullName))
                {
                    searchQueue.Enqueue(nestedDir);
                }

                var foundFiles = GetExternalFiles(video, dir.FullName, [_subtitlePathParser, _audioPathParser]);

                foreach (var file in foundFiles)
                {
                    file.Title = dir.FullName[video.ContainingFolderPath.Length..].Trim('\\', '/', ' ');
                }

                files.AddRange(foundFiles);
            }

            if (!files.Any())
            {
                return;
            }

            var streams = await ExtractStreams(files, 1, DlnaProfileType.Subtitle, cancellationToken).ConfigureAwait(false);
            var audioStreams = await ExtractStreams(files, 1, DlnaProfileType.Audio, cancellationToken).ConfigureAwait(false);
            streams.AddRange(audioStreams);

            var originalStreams = video.GetMediaStreams();

            streams = streams
                .Where(c => !originalStreams.Any(d => c.Path == d.Path))
                .ToList();

            if (streams.Count == 0)
            {
                var externalCount = originalStreams.Count(c => c.IsExternal);
                logger.LogInformation("Item [s{Season}e{Episode}]'{Name}' already contains external audio/subs ({Count})", video.ParentIndexNumber, video.IndexNumber, video.Name, externalCount);
                return;
            }

            var startIndex = originalStreams.Max(i => i.Index) + 1;

            foreach (var stream in streams)
            {
                stream.Index = startIndex++;
            }

            originalStreams = [.. originalStreams, .. streams];

            mediaStreamRepository.SaveMediaStreams(video.Id, originalStreams, cancellationToken);
            logger.LogInformation("Item [s{Season}e{Episode}]'{Name}' extended with external audio/subs ({Count})", video.ParentIndexNumber, video.IndexNumber, video.Name, streams.Count);
        }

        // ref: MediaBrowser.Providers.MediaInfo.MediaInfoResolver::GetExternalFiles()

        private IReadOnlyList<ExternalPathParserResult> GetExternalFiles(Video video, string folder, IEnumerable<ExternalPathParser> externalPathParsers)
        {
            var files = directoryService.GetFilePaths(folder).ToList();

            var externalPathInfos = new List<ExternalPathParserResult>();
            ReadOnlySpan<char> prefix = video.FileNameWithoutExtension;
            foreach (var file in files)
            {
                var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(file.AsSpan());
                if (fileNameWithoutExtension.Length >= prefix.Length
                    && prefix.Equals(fileNameWithoutExtension[..prefix.Length], StringComparison.OrdinalIgnoreCase)
                    && (fileNameWithoutExtension.Length == prefix.Length || namingOptions.MediaFlagDelimiters.Contains(fileNameWithoutExtension[prefix.Length])))
                {
                    foreach (var externalPathParser in externalPathParsers)
                    {
                        var externalPathInfo = externalPathParser.ParseFile(file, fileNameWithoutExtension[prefix.Length..].ToString());

                        if (externalPathInfo is not null)
                        {
                            externalPathInfos.Add(externalPathInfo);
                        }
                    }
                }
            }

            return externalPathInfos.DistinctBy(c => c.Path).ToList();
        }

        private async Task<List<MediaStream>> ExtractStreams(
            IEnumerable<ExternalPathParserResult> pathInfos,
            int startIndex,
            DlnaProfileType targetType,
            CancellationToken cancellationToken)
        {
            var mediaStreams = new List<MediaStream>();

            foreach (var pathInfo in pathInfos)
            {
                if (!pathInfo.Path.AsSpan().EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var mediaInfo = await GetMediaInfo(pathInfo.Path, targetType, cancellationToken).ConfigureAwait(false);

                        if (mediaInfo.MediaStreams.Count == 1)
                        {
                            MediaStream mediaStream = mediaInfo.MediaStreams[0];

                            if ((mediaStream.Type == MediaStreamType.Audio && targetType == DlnaProfileType.Audio)
                                || (mediaStream.Type == MediaStreamType.Subtitle && targetType == DlnaProfileType.Subtitle))
                            {
                                mediaStream.Index = startIndex++;
                                mediaStream.IsDefault = pathInfo.IsDefault || mediaStream.IsDefault;
                                mediaStream.IsForced = pathInfo.IsForced || mediaStream.IsForced;
                                mediaStream.IsHearingImpaired = pathInfo.IsHearingImpaired || mediaStream.IsHearingImpaired;

                                mediaStreams.Add(MergeMetadata(mediaStream, pathInfo));
                            }
                        }
                        else
                        {
                            foreach (MediaStream mediaStream in mediaInfo.MediaStreams)
                            {
                                if ((mediaStream.Type == MediaStreamType.Audio && targetType == DlnaProfileType.Audio)
                                    || (mediaStream.Type == MediaStreamType.Subtitle && targetType == DlnaProfileType.Subtitle))
                                {
                                    mediaStream.Index = startIndex++;

                                    mediaStreams.Add(MergeMetadata(mediaStream, pathInfo));
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error getting external streams from {Path}", pathInfo.Path);

                        continue;
                    }
                }
            }

            return mediaStreams;
        }

        private Task<MediaInfo> GetMediaInfo(string path, DlnaProfileType type, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            return mediaEncoder.GetMediaInfo(
                new MediaInfoRequest
                {
                    MediaType = type,
                    MediaSource = new MediaSourceInfo
                    {
                        Path = path,
                        Protocol = MediaProtocol.File
                    }
                },
                cancellationToken);
        }

        private MediaStream MergeMetadata(MediaStream mediaStream, ExternalPathParserResult pathInfo)
        {
            mediaStream.Path = pathInfo.Path;
            mediaStream.IsExternal = true;
            mediaStream.Title = string.IsNullOrEmpty(mediaStream.Title) ? (string.IsNullOrEmpty(pathInfo.Title) ? null : pathInfo.Title) : mediaStream.Title;
            mediaStream.Language = string.IsNullOrEmpty(mediaStream.Language) ? (string.IsNullOrEmpty(pathInfo.Language) ? null : pathInfo.Language) : mediaStream.Language;

            return mediaStream;
        }
    }
}
