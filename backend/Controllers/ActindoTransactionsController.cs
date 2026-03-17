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

    public ActindoTransactionsController(
        TransactionService transactionService)
    {
        _transactionService = transactionService;
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

        var result = await _transactionService.GetTransactionsAsync(request, cancellationToken);
        return StatusCode(StatusCodes.Status201Created, result);
    }
}
