using ActindoMiddleware.Application.Security;
using ActindoMiddleware.Application.Services;
using ActindoMiddleware.DTOs.Requests;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ActindoMiddleware.Controllers;

[ApiController]
[Route("api/actindo/transactions")]
[Authorize(Policy = AuthPolicies.Write)]
public sealed class ActindoTransactionsController : ControllerBase
{
    private readonly TransactionService _transactionService;
    private readonly ProductJobQueue _jobQueue;

    public ActindoTransactionsController(
        TransactionService transactionService,
        ProductJobQueue jobQueue)
    {
        _transactionService = transactionService;
        _jobQueue = jobQueue;
    }

    [HttpPost("get")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetTransactions(
        [FromBody] GetTransactionsRequest request,
        CancellationToken _)
    {
        if (request == null)
            return BadRequest("Request payload is required");

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var cancellationToken = cts.Token;
        var jobId = Guid.NewGuid();
        var jobReference = $"transactions:{request.Date}";
        var requestPayload = System.Text.Json.JsonSerializer.Serialize(request);
        var success = false;
        string? error = null;

        _jobQueue.RegisterSyncJob(jobId, jobReference, "transaction-get", requestPayload);

        try
        {
            var result = await _transactionService.GetTransactionsAsync(request, cancellationToken);
            success = true;
            return StatusCode(StatusCodes.Status201Created, result);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            _jobQueue.CompleteSyncJob(jobId, success, error);
        }
    }
}
