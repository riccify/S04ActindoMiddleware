using ActindoMiddleware.Application.Security;
using ActindoMiddleware.Application.Services;
using ActindoMiddleware.DTOs.Requests;
using ActindoMiddleware.DTOs.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ActindoMiddleware.Controllers;

[ApiController]
[Route("api/actindo/products/image")]
[Authorize(Policy = AuthPolicies.Write)]
public sealed class ActindoProductImagesController : ControllerBase
{
    private readonly ProductImageService _productImageService;
    private readonly ProductJobQueue _jobQueue;

    public ActindoProductImagesController(
        ProductImageService productImageService,
        ProductJobQueue jobQueue)
    {
        _productImageService = productImageService;
        _jobQueue = jobQueue;
    }

    [HttpPost]
    [ProducesResponseType(typeof(CreateProductResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateProductResponse>> UploadImages(
        [FromBody] UploadProductImagesRequest request,
        CancellationToken _)
    {
        if (request?.Images == null || request.Paths == null)
            return BadRequest("Images and paths are required.");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;
        var syncJobId = Guid.NewGuid();
        var success = false;
        string? syncJobError = null;

        _jobQueue.RegisterSyncJob(syncJobId, $"product-image:{request.Id}", "image-upload", JsonSerializer.Serialize(request));
        try
        {
            var response = await _productImageService.UploadAsync(request, cancellationToken);
            success = true;
            return Created(string.Empty, response);
        }
        catch (Exception ex)
        {
            syncJobError = ex.Message;
            throw;
        }
        finally
        {
            _jobQueue.CompleteSyncJob(syncJobId, success, syncJobError);
        }
    }
}
