using System.Security.Claims;
using FitOS.Application.Common;

namespace FitOS.Api.Members;

internal static class CustomerAccess
{
    public static bool IsCustomer(ClaimsPrincipal user) => user.FindFirstValue("role") == "customer";

    /// <summary>A customer may only read their own record. Anyone else's id looks like a missing one.</summary>
    public static void EnsureCanSee(ClaimsPrincipal user, Guid customerId)
    {
        if (!IsCustomer(user))
        {
            return;
        }

        if (!Guid.TryParse(user.FindFirstValue("customer_id"), out var own) || own != customerId)
        {
            throw new NotFoundException("customer");
        }
    }
}
