using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SCIMServer.Emulator.GoogleWorkspace.Auth;
using SCIMServer.Emulator.GoogleWorkspace.Infrastructure;
using SCIMServer.Emulator.GoogleWorkspace.Repositories;

namespace SCIMServer.Emulator.GoogleWorkspace.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = GoogleBearerDefaults.Scheme)]
[Route("admin/directory/v1/customers/{customerKey}")]
public sealed class CustomersController : ControllerBase
{
    private readonly GwCustomerRepository _customers;

    public CustomersController(GwCustomerRepository customers) => _customers = customers;

    [HttpGet]
    public async Task<IActionResult> Get(string customerKey)
    {
        var customer = customerKey == "my_customer"
            ? await _customers.GetDefaultAsync()
            : await _customers.GetAsync(customerKey);
        return customer is null
            ? GoogleErrorResult.NotFound("customerKey", $"Resource Not Found: {customerKey}")
            : Ok(customer);
    }
}
