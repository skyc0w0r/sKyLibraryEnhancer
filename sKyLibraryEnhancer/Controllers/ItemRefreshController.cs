using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Net.Mime;
using Jellyfin.Extensions.Json;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using SkyLibraryEnhancer.Classes;

namespace SkyLibraryEnhancer.Controllers;

/// <summary>
/// Item Refresh Controller.
/// </summary>
[Route("Items")]
[Authorize(Policy = Policies.RequiresElevation)]
[ApiController]
[Route("[controller]")]
[Produces(
    MediaTypeNames.Application.Json,
    JsonDefaults.CamelCaseMediaType,
    JsonDefaults.PascalCaseMediaType)]
public class ItemRefreshController : ControllerBase
{
    private readonly ILogger _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly IProviderManager _providerManager;
    private readonly IFileSystem _fileSystem;

    /// <summary>
    /// Initializes a new instance of the <see cref="ItemRefreshController"/> class.
    /// </summary>
    /// <param name="libraryManager">Instance of <see cref="ILibraryManager"/> interface.</param>
    /// <param name="providerManager">Instance of <see cref="IProviderManager"/> interface.</param>
    /// <param name="fileSystem">Instance of <see cref="IFileSystem"/> interface.</param>
    public ItemRefreshController(
        ILogger<ItemRefreshController> logger,
        ILibraryManager libraryManager,
        IProviderManager providerManager,
        IFileSystem fileSystem)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _providerManager = providerManager;
        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Refreshes metadata for an item.
    /// </summary>
    /// <param name="itemId">Item id.</param>
    /// <response code="204">Item metadata refresh queued.</response>
    /// <response code="404">Item to refresh not found.</response>
    /// <returns>An <see cref="NoContentResult"/> on success, or a <see cref="NotFoundResult"/> if the item could not be found.</returns>
    [HttpPost("{itemId}/Refresh2")]
    [Description("Refreshes metadata for an item.")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult RefreshItem(
        [FromRoute, Required] Guid itemId)
    {
        var item = _libraryManager.GetItemById<BaseItem>(itemId, User.GetUserId());
        if (item is null)
        {
            return NotFound();
        }

        var refreshOptions = new MetadataRefreshOptions(new DirectoryService(_fileSystem))
        {
            MetadataRefreshMode = MetadataRefreshMode.Default,
            ImageRefreshMode = MetadataRefreshMode.FullRefresh,
            ReplaceAllImages = true,
            ReplaceAllMetadata = false,
            ForceSave = true,
            IsAutomated = false,
            RemoveOldMetadata = false,
            RegenerateTrickplay = false,
        };

        _logger.LogInformation("Queued refresh metadata task for [{Index}]{Name}", item.IndexNumber, item.Name);

        item.DateLastRefreshed = DateTime.MinValue;
        item.Name = null;
        item.CommunityRating = null;
        item.Overview = null;
        item.ProductionYear = null;
        _providerManager.QueueRefresh(item.Id, refreshOptions, RefreshPriority.High);

        return NoContent();
    }
}
