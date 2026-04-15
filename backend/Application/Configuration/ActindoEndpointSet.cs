using System;
using ActindoMiddleware.Application;

namespace ActindoMiddleware.Application.Configuration;

public sealed class ActindoEndpointSet
{
    public required string CreateProduct { get; init; }
    public required string SaveProduct { get; init; }
    public required string CreateInventory { get; init; }
    public required string CreateInventoryMovement { get; init; }
    public required string CreateRelation { get; init; }
    public required string CreateCustomer { get; init; }
    public required string SaveCustomer { get; init; }
    public required string SavePrimaryAddress { get; init; }
    public required string GetTransactions { get; init; }
    public required string CreateFile { get; init; }
    public required string ProductFilesSave { get; init; }
    public required string GetProductList { get; init; }
    public required string DeleteProduct { get; init; }
    public required string GetProduct { get; init; }
    public required string GetVariantsList { get; init; }

    public static ActindoEndpointSet FromDictionary(IDictionary<string, string> values, string? actindoBaseUrl)
    {
        string Get(string key, string fallback) =>
            values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value
                : fallback;

        string Build(string key, string fallback) => BuildEndpointUrl(Get(key, fallback), actindoBaseUrl);

        return new ActindoEndpointSet
        {
            CreateProduct = Build("CREATE_PRODUCT", ActindoEndpoints.CREATE_PRODUCT),
            SaveProduct = Build("SAVE_PRODUCT", ActindoEndpoints.SAVE_PRODUCT),
            CreateInventory = Build("CREATE_INVENTORY", ActindoEndpoints.CREATE_INVENTORY),
            CreateInventoryMovement = Build("CREATE_INVENTORY_MOVEMENT", ActindoEndpoints.CREATE_INVENTORY_MOVEMENT),
            CreateRelation = Build("CREATE_RELATION", ActindoEndpoints.CREATE_RELATION),
            CreateCustomer = Build("CREATE_CUSTOMER", ActindoEndpoints.CREATE_CUSTOMER),
            SaveCustomer = Build("SAVE_CUSTOMER", ActindoEndpoints.SAVE_CUSTOMER),
            SavePrimaryAddress = Build("SAVE_PRIMARY_ADDRESS", ActindoEndpoints.SAVE_PRIMARY_ADDRESS),
            GetTransactions = Build("GET_TRANSACTIONS", ActindoEndpoints.GET_TRANSACTIONS),
            CreateFile = Build("CREATE_FILE", ActindoEndpoints.CREATE_FILE),
            ProductFilesSave = Build("PRODUCT_FILES_SAVE", ActindoEndpoints.PRODUCT_FILES_SAVE),
            GetProductList = Build("GET_PRODUCT_LIST", ActindoEndpoints.GET_PRODUCT_LIST),
            DeleteProduct = Build("DELETE_PRODUCT", ActindoEndpoints.DELETE_PRODUCT),
            GetProduct = Build("GET_PRODUCT", ActindoEndpoints.GET_PRODUCT),
            GetVariantsList = Build("GET_VARIANTS_LIST", ActindoEndpoints.GET_VARIANTS_LIST)
        };
    }

    private static string BuildEndpointUrl(string endpoint, string? actindoBaseUrl)
    {
        endpoint = (endpoint ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(endpoint))
            return string.Empty;

        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absoluteEndpoint))
            return absoluteEndpoint.ToString();

        var baseUrl = (actindoBaseUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
            return endpoint.TrimStart('/');

        if (!baseUrl.EndsWith('/'))
            baseUrl += "/";

        return $"{baseUrl}{endpoint.TrimStart('/')}";
    }
}
