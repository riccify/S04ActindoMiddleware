using System.Linq;
using System.IO;
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

        var renamedImages = request.Images
            .Select(image => new RenamedProductImage(image, AppendRandomSuffixToPath(image.Path)))
            .ToList();

        var renamedPaths = request.Paths
            .Select(path => new ProductImagePathDto
            {
                Id = ResolveRenamedImageId(path.Id, renamedImages)
            })
            .ToList();

        foreach (var image in renamedImages)
        {
            _logger.LogInformation(
                "Uploading product image for ProductId={ProductId}: Path={Path}, Type={Type}, RenameOnExistingFile={RenameOnExistingFile}, CreateDirectoryStructure={CreateDirectoryStructure}, ContentLength={ContentLength}",
                request.Id,
                image.Path,
                image.Source.Type,
                image.Source.RenameOnExistingFile,
                image.Source.CreateDirectoryStructure,
                image.Source.Content?.Length ?? 0);

            await _client.PostAsync(
                endpoints.CreateFile,
                new
                {
                    path = image.Path,
                    type = image.Source.Type,
                    renameOnExistingFile = image.Source.RenameOnExistingFile,
                    createDirectoryStructure = image.Source.CreateDirectoryStructure,
                    content = image.Source.Content
                },
                cancellationToken);

            _logger.LogDebug(
                "CreateFile finished for ProductId={ProductId}: Path={Path}",
                request.Id,
                image.Path);

            await Task.Delay(100, cancellationToken);
        }

        if (renamedPaths.Count > 0)
        {
            _logger.LogInformation(
                "Relating {Count} images to product {ProductId}: ImageIds={ImageIds}",
                renamedPaths.Count,
                request.Id,
                string.Join(", ", renamedPaths.Select(path => path.Id)));

            await _client.PostAsync(
                endpoints.ProductFilesSave,
                new
                {
                    product = new
                    {
                        id = request.Id,
                        _pim_images = new
                        {
                            images = renamedPaths.Select(path => new { id = path.Id }).ToArray()
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

    private static string ResolveRenamedImageId(string originalId, IReadOnlyCollection<RenamedProductImage> renamedImages)
    {
        var matchedImage = renamedImages.FirstOrDefault(image =>
            string.Equals(image.Source.Path, originalId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(image.Source.Path), originalId, StringComparison.OrdinalIgnoreCase));

        return matchedImage?.Path ?? originalId;
    }

    private static string AppendRandomSuffixToPath(string path)
    {
        var directory = Path.GetDirectoryName(path);
        var extension = Path.GetExtension(path);
        var fileNameWithoutExtension = Path.GetFileNameWithoutExtension(path);
        var randomSuffix = Random.Shared.Next(10000, 100000);
        var renamedFileName = $"{fileNameWithoutExtension}_{randomSuffix}{extension}";

        if (string.IsNullOrEmpty(directory))
            return renamedFileName;

        return $"{directory.Replace('\\', '/')}/{renamedFileName}";
    }

    private sealed record RenamedProductImage(ProductImageDto Source, string Path);
}
