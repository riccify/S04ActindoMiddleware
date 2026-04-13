using System.Linq;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.DTOs.Requests;
using ActindoMiddleware.DTOs.Responses;
using ActindoMiddleware.Infrastructure.Actindo;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Application.Services;

public sealed class ProductImageService
{
    private readonly ActindoClient _client;
    private readonly IActindoEndpointProvider _endpoints;
    private readonly ILogger<ProductImageService> _logger;

    public ProductImageService(
        ActindoClient client,
        IActindoEndpointProvider endpoints,
        ILogger<ProductImageService> logger)
    {
        _client = client;
        _endpoints = endpoints;
        _logger = logger;
    }

    public async Task<CreateProductResponse> UploadAsync(
        UploadProductImagesRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Images);
        ArgumentNullException.ThrowIfNull(request.Paths);

        var endpoints = await _endpoints.GetAsync(cancellationToken);
        _logger.LogInformation(
            "Starting product image upload for ProductId={ProductId} with {ImageCount} images and {PathCount} relation paths. CreateFile={CreateFileEndpoint}, ProductFilesSave={ProductFilesSaveEndpoint}",
            request.Id,
            request.Images.Count,
            request.Paths.Count,
            endpoints.CreateFile,
            endpoints.ProductFilesSave);

        foreach (var image in request.Images)
        {
            _logger.LogInformation(
                "Uploading product image for ProductId={ProductId}: Path={Path}, Type={Type}, RenameOnExistingFile={RenameOnExistingFile}, CreateDirectoryStructure={CreateDirectoryStructure}, ContentLength={ContentLength}",
                request.Id,
                image.Path,
                image.Type,
                image.RenameOnExistingFile,
                image.CreateDirectoryStructure,
                image.Content?.Length ?? 0);

            await _client.PostAsync(
                endpoints.CreateFile,
                new
                {
                    path = image.Path,
                    type = image.Type,
                    renameOnExistingFile = image.RenameOnExistingFile,
                    createDirectoryStructure = image.CreateDirectoryStructure,
                    content = image.Content
                },
                cancellationToken);

            _logger.LogDebug(
                "CreateFile finished for ProductId={ProductId}: Path={Path}",
                request.Id,
                image.Path);

            await Task.Delay(100, cancellationToken);
        }

        if (request.Paths.Count > 0)
        {
            _logger.LogInformation(
                "Relating {Count} images to product {ProductId}: ImageIds={ImageIds}",
                request.Paths.Count,
                request.Id,
                string.Join(", ", request.Paths.Select(path => path.Id)));

            await _client.PostAsync(
                endpoints.ProductFilesSave,
                new
                {
                    product = new
                    {
                        id = request.Id,
                        _pim_images = new
                        {
                            images = request.Paths.Select(path => new { id = path.Id }).ToArray()
                        }
                    }
                },
                cancellationToken);

            _logger.LogDebug(
                "ProductFilesSave finished for ProductId={ProductId}",
                request.Id);
        }

        _logger.LogInformation("Completed product image upload for ProductId={ProductId}", request.Id);

        return new CreateProductResponse
        {
            Message = "Images created and related to product",
            ProductId = request.Id,
            Success = true
        };
    }
}
