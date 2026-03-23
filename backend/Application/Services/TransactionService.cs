using System.Linq;
using System.Text.Json;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.DTOs.Requests;
using ActindoMiddleware.Infrastructure.Actindo;

namespace ActindoMiddleware.Application.Services;

public sealed class TransactionService
{
    private readonly ActindoClient _client;
    private readonly IActindoEndpointProvider _endpoints;

    public TransactionService(ActindoClient client, IActindoEndpointProvider endpoints)
    {
        _client = client;
        _endpoints = endpoints;
    }

    public async Task<JsonElement> GetTransactionsAsync(
        GetTransactionsRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var formattedDate = request.Date;

        if (DateTime.TryParse(request.Date, out var parsed))
            formattedDate = parsed.ToString("yyyy-MM-dd HH:mm:ss");

        var payload = new
        {
            filter = new object[]
            {
                new
                {
                    property = "created",
                    @operator = ">",
                    value = formattedDate
                },
                new
                {
                    property = "type",
                    @operator = "=",
                    value = "RB"
                },
                new
                {
                    property = "type",
                    @operator = "=",
                    value = "GU"
                }
            },
            serializeOptionals = new[] { "legacyProperties", "_schalke_position_flock_name" },
            start = 0,
            limit = 50
        };

        var endpoints = await _endpoints.GetAsync(cancellationToken);
        return await _client.PostAsync(
            endpoints.GetTransactions,
            payload,
            cancellationToken);
    }
}
