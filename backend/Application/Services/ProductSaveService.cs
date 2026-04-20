using System;
using System.Threading;
using System.Threading.Tasks;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.DTOs.Requests;
using ActindoMiddleware.DTOs.Responses;
using ActindoMiddleware.Infrastructure.Actindo;
using Microsoft.Extensions.Logging;

namespace ActindoMiddleware.Application.Services;

public sealed class ProductSaveService : ProductSynchronizationService
{
    public ProductSaveService(
        ActindoClient client,
        IActindoEndpointProvider endpoints,
        ILogger<ProductSaveService> logger)
        : base(client, endpoints, logger)
    {
    }

    public Task<CreateProductResponse> SaveAsync(
        SaveProductRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Product);

        return SyncAsync(
            request.Product,
            useSaveEndpoint: true,
            stripVariantSetInformation: true,
            cancellationToken);
    }
}
