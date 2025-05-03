using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Emby.Naming.Common;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SkyLibraryEnhancer.Classes;
using SkyLibraryEnhancer.Configuration;
using SkyLibraryEnhancer.Enums;

namespace SkyLibraryEnhancer.Services
{
    internal class ExternalMediaService(
        ILogger<ExternalMediaService> logger,
        ILibraryManager libraryManager,
        ITaskManager taskManager,
        ILoggerFactory loggerFactory,
        IDirectoryService directoryService,
        IItemRepository itemRepository,
        IMediaEncoder mediaEncoder,
        ILocalizationManager localizationManager,
        NamingOptions namingOptions) : IHostedService, IDisposable
    {
        private Timer Timer => _timer ??= new Timer(OnTimer, null, Timeout.Infinite, Timeout.Infinite);

        private static DiscoverTaskState TaskState => _cancellationTokenSource switch
        {
            null => DiscoverTaskState.Idle,
            { IsCancellationRequested: true } => DiscoverTaskState.Cancelling,
            _ => DiscoverTaskState.Running
        };

        private readonly HashSet<Guid> _videosToProcess = [];
        private PluginConfiguration _configuration = Plugin.Instance?.Configuration ?? new PluginConfiguration();
        private Timer? _timer;
        private bool _runAgain;
        private bool _disposedValue;

        private static readonly SemaphoreSlim _discoverySemaphore = new(1, 1);
        private static CancellationTokenSource? _cancellationTokenSource;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            libraryManager.ItemAdded += OnLibraryItem;
            libraryManager.ItemUpdated += OnLibraryItem;
            taskManager.TaskCompleted += OnLibraryRefresh;
            Plugin.Instance!.ConfigurationChanged += OnSettingsChanged;

            logger.LogInformation("Service started");
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            libraryManager.ItemAdded -= OnLibraryItem;
            libraryManager.ItemUpdated -= OnLibraryItem;
            taskManager.TaskCompleted -= OnLibraryRefresh;
            Plugin.Instance!.ConfigurationChanged -= OnSettingsChanged;

            Timer.Change(Timeout.Infinite, Timeout.Infinite);
            return Task.CompletedTask;
        }

        private void OnLibraryItem(object? sender, ItemChangeEventArgs e)
        {
            if (e.UpdateReason == ItemUpdateType.ImageUpdate)
            {
                return;
            }

            // skip seasons as they do not contain children
            if (e.Item.LocationType == MediaBrowser.Model.Entities.LocationType.Virtual)
            {
                return;
            }

            if (e.Item is Video v)
            {
                _videosToProcess.Add(v.Id);
            }
            else if (e.Item is Series s)
            {
                foreach (var child in s.Children)
                {
                    _videosToProcess.Add(child.Id);
                }
            }
            else
            {
                return;
            }

            StartTimer(30);
        }

        private void OnLibraryRefresh(object? sender, TaskCompletionEventArgs e)
        {
            if (e.Result.Key == "RefreshLibrary" && e.Result.Status == TaskCompletionStatus.Completed && TaskState != DiscoverTaskState.Running)
            {
                StartTimer();
            }
        }

        private void OnSettingsChanged(object? sender, BasePluginConfiguration e)
        {
            if (e is PluginConfiguration cfg)
            {
                _configuration = cfg;
            }
        }

        private void StartTimer(int delay = 10)
        {
            if (TaskState == DiscoverTaskState.Running)
            {
                _runAgain = true;
            }
            else if (TaskState == DiscoverTaskState.Idle)
            {
                Timer.Change(TimeSpan.FromSeconds(delay), Timeout.InfiniteTimeSpan);
            }
        }

        private void OnTimer(object? state)
            => _ = RunMediaDiscovery();

        private async Task RunMediaDiscovery()
        {
            try
            {
                await PerformDiscovery().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("External media disovery canceled");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error during discovery");
            }

            _cancellationTokenSource = null;
        }

        private async Task PerformDiscovery()
        {
            await _discoverySemaphore.WaitAsync().ConfigureAwait(false);
            try
            {
                using (_cancellationTokenSource = new CancellationTokenSource())
                {
                    logger.LogInformation("Starting external media discovery...");
                    var ids = new HashSet<Guid>(_videosToProcess);
                    _videosToProcess.Clear();
                    _runAgain = false;

                    var analyzer = new DiscoveryWorker(
                        loggerFactory.CreateLogger<DiscoveryWorker>(),
                        libraryManager,
                        directoryService,
                        itemRepository,
                        mediaEncoder,
                        localizationManager,
                        namingOptions);
                    await analyzer.Discover(ids, _cancellationTokenSource.Token).ConfigureAwait(false);

                    if (_runAgain && !_cancellationTokenSource.IsCancellationRequested)
                    {
                        logger.LogInformation("Media discovery finished, but we need to run once again!");
                        Timer.Change(TimeSpan.FromSeconds(60), Timeout.InfiniteTimeSpan);
                    }
                }
            }
            finally
            {
                _discoverySemaphore.Release();
            }
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects)
                    _timer?.Dispose();
                    _cancellationTokenSource?.Dispose();
                    _discoverySemaphore?.Dispose();
                }

                // TODO: free unmanaged resources (unmanaged objects) and override finalizer
                // TODO: set large fields to null
                _disposedValue = true;
            }
        }

        // // TODO: override finalizer only if 'Dispose(bool disposing)' has code to free unmanaged resources
        // ~ExternalMediaService()
        // {
        //     // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
        //     Dispose(disposing: false);
        // }

        public void Dispose()
        {
            // Do not change this code. Put cleanup code in 'Dispose(bool disposing)' method
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
