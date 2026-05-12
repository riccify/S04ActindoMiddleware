using System.Globalization;
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

        var filters = CreateDateFilters(request);
        filters.Add(new
        {
            property = "type",
            @operator = "=",
            value = "RB"
        });
        filters.Add(new
        {
            property = "type",
            @operator = "=",
            value = "GU"
        });

        var payload = new
        {
            filter = filters,
            serializeOptionals = new[] { "legacyProperties", "positions._schalke_position_flock_name", "positions._schalke_position_flock_number" },
            start = 0,
            limit = 500
        };

        var endpoints = await _endpoints.GetAsync(cancellationToken);
        return await _client.PostAsync(
            endpoints.GetTransactions,
            payload,
            cancellationToken);
    }

    private static List<object> CreateDateFilters(GetTransactionsRequest request)
    {
        var action = string.IsNullOrWhiteSpace(request.Action)
            ? "from"
            : request.Action.Trim().ToLowerInvariant();

        return action switch
        {
            "from" => new List<object>
            {
                CreatedFilter(">", FormatDateTime(ParseDate(request.Date)))
            },
            "just" => CreateSingleDayFilters(request.Date),
            "fromto" => CreateFromToFilters(request.Date),
            _ => throw new ArgumentException($"Unsupported transaction action '{request.Action}'. Supported actions are 'from', 'just' and 'fromto'.")
        };
    }

    private static List<object> CreateSingleDayFilters(string date)
    {
        var day = ParseDate(date).Date;
        return CreateRangeFilters(day, EndOfDay(day));
    }

    private static List<object> CreateFromToFilters(string dateRange)
    {
        var separatorIndex = dateRange.IndexOf("TO", StringComparison.OrdinalIgnoreCase);
        if (separatorIndex < 0)
            throw new ArgumentException("For action 'fromto', date must use the format 'yyyy-MM-ddTOyyyy-MM-dd'.");

        var fromText = dateRange[..separatorIndex].Trim();
        var toText = dateRange[(separatorIndex + 2)..].Trim();
        if (string.IsNullOrWhiteSpace(fromText) || string.IsNullOrWhiteSpace(toText))
            throw new ArgumentException("For action 'fromto', date must use the format 'yyyy-MM-ddTOyyyy-MM-dd'.");

        var from = ParseDate(fromText).Date;
        var to = EndOfDay(ParseDate(toText).Date);
        if (to < from)
            throw new ArgumentException("For action 'fromto', the end date must be greater than or equal to the start date.");

        return CreateRangeFilters(from, to);
    }

    private static List<object> CreateRangeFilters(DateTime from, DateTime to)
    {
        return new List<object>
        {
            CreatedFilter(">=", FormatDateTime(from)),
            CreatedFilter("<=", FormatDateTime(to))
        };
    }

    private static DateTime ParseDate(string value)
    {
        if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return parsed;

        throw new ArgumentException($"Invalid transaction date '{value}'.");
    }

    private static string FormatDateTime(DateTime value) => value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static DateTime EndOfDay(DateTime day) => day.Date.AddDays(1).AddSeconds(-1);

    private static object CreatedFilter(string op, string value) => new
    {
        property = "created",
        @operator = op,
        value
    };
}
