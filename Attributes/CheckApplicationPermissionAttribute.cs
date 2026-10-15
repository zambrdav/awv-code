using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using SmartRoutingAPI.Models.Accounts;
using SmartRoutingAPI.Services;
using System;
using System.Threading.Tasks;

public class CheckApplicationPermissionAttribute : Attribute, IAsyncActionFilter
{
    private readonly ApplicationRights[] _requiredRights;

    public CheckApplicationPermissionAttribute(params ApplicationRights[] requiredRights)
    {
        _requiredRights = requiredRights;
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

        // check if atleast one of required rights are present (via securityService.HasApplicationRight)
        var hasRight = false;
        foreach (var right in _requiredRights)
        {
            if (securityService.HasApplicationRight(user, right))
            {
                hasRight = true;
                break;
            }
        }

        if (securityService == null || !hasRight)
        {
            context.Result = new ForbidResult();
            return;
        }

        await next();
    }
}
