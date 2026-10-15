using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Services;
using System;
using System.Threading.Tasks;

public class CheckAdministrativePermissionAttribute : Attribute, IAsyncActionFilter
{
    private readonly AdministrativeRights _requiredRight;

    public CheckAdministrativePermissionAttribute(AdministrativeRights requiredRight)
    {
        _requiredRight = requiredRight;
    }

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var securityService = (ISecurityService?)context.HttpContext.RequestServices.GetService(typeof(ISecurityService));

        if (securityService == null)
        {
            context.Result = new ForbidResult();
            return;
        }

        var user = context.HttpContext.User;

        if (securityService == null || !securityService.HasAdministrativeRight(user, _requiredRight))
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }
}
