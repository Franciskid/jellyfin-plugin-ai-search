using System;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Jellyfin.Plugin.AiSearch.Api;

/// <summary>
/// Marks an action that only serves the AI search popup (the injected script,
/// or one of the endpoints it calls). Refuses the request with a bare, empty
/// 404 when <see cref="Configuration.PluginConfiguration.NativeSearchOnly"/> is
/// on, since that option hides the popup entirely. This is the one place that
/// check lives; every marked action shares it instead of repeating an
/// <c>if</c> in its own body.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class AiPopupEndpointAttribute : ActionFilterAttribute
{
    /// <inheritdoc />
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        if (Plugin.Instance!.Configuration.NativeSearchOnly)
        {
            context.Result = new StatusCodeResult(StatusCodes.Status404NotFound);
            return;
        }

        base.OnActionExecuting(context);
    }
}
