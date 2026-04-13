using ActindoMiddleware.Application.Monitoring;
using ActindoMiddleware.Application.Security;
using ActindoMiddleware.Application.Services;
using ActindoMiddleware.DTOs.Requests;
using ActindoMiddleware.DTOs.Responses;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ActindoMiddleware.Controllers;

[ApiController]
[Route("api/actindo/customer")]
[Authorize(Policy = AuthPolicies.Write)]
public sealed class ActindoCustomersController : ControllerBase
{
    private readonly CustomerCreateService _customerCreateService;
    private readonly CustomerSaveService _customerSaveService;
    private readonly IDashboardMetricsService _dashboardMetrics;
    private readonly ProductJobQueue _jobQueue;

    public ActindoCustomersController(
        CustomerCreateService customerCreateService,
        CustomerSaveService customerSaveService,
        IDashboardMetricsService dashboardMetrics,
        ProductJobQueue jobQueue)
    {
        _customerCreateService = customerCreateService;
        _customerSaveService = customerSaveService;
        _dashboardMetrics = dashboardMetrics;
        _jobQueue = jobQueue;
    }

    [HttpPost("create")]
    [ProducesResponseType(typeof(CreateCustomerResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateCustomerResponse>> CreateCustomer(
        [FromBody] CreateCustomerRequest request,
        CancellationToken _)
    {
        if (request?.Customer == null || request.PrimaryAddress == null)
            return BadRequest("Customer and primaryAddress are required");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;
        var syncJobId = Guid.NewGuid();
        var debtorNumber = request.Customer._customer_debitorennumber ?? request.Customer.shortName ?? "customer";
        var success = false;
        string? syncJobError = null;

        _jobQueue.RegisterSyncJob(syncJobId, debtorNumber, "customer-create", JsonSerializer.Serialize(request));
        try
        {
            var result = await _customerCreateService.CreateAsync(request, cancellationToken);

            // Save customer to Customers table
            await _dashboardMetrics.SaveCustomerAsync(
                Guid.NewGuid(),
                result.CustomerId,
                request.Customer._customer_debitorennumber ?? string.Empty,
                request.Customer.shortName ?? string.Empty,
                cancellationToken);

            success = true;
            return Created(string.Empty, result);
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

    [HttpPost("save")]
    [ProducesResponseType(typeof(CreateCustomerResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<CreateCustomerResponse>> SaveCustomer(
        [FromBody] SaveCustomerRequest request,
        CancellationToken _)
    {
        if (request?.Customer == null || request.PrimaryAddress == null)
            return BadRequest("Customer and primaryAddress are required");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;
        var syncJobId = Guid.NewGuid();
        var debtorNumber = request.Customer._customer_debitorennumber ?? request.Customer.shortName ?? "customer";
        var success = false;
        string? syncJobError = null;

        _jobQueue.RegisterSyncJob(syncJobId, debtorNumber, "customer-save", JsonSerializer.Serialize(request));
        try
        {
            var result = await _customerSaveService.SaveAsync(request, cancellationToken);

            // Update customer in Customers table (upsert)
            await _dashboardMetrics.SaveCustomerAsync(
                Guid.NewGuid(),
                result.CustomerId,
                request.Customer._customer_debitorennumber ?? string.Empty,
                request.Customer.shortName ?? string.Empty,
                cancellationToken);

            success = true;
            return Ok(result);
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
