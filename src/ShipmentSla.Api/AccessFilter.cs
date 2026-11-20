// Role check – the second layer after API Gateway's JWT authorizer.
// API Gateway already rejects missing/invalid/expired tokens with 401. Here we read the verified claims it
// passes along and allow the call only for the right Cognito group (else 403). Moved out of Program.cs so it
// can be unit-tested; RequireAccess also tags the endpoint so a test can prove no read route was left open.
// (Lớp phân quyền thứ 2: đúng nhóm Cognito mới được gọi, sai nhóm → 403.)
using ShipmentSla.Core;

namespace ShipmentSla.Api;

/// <summary>Endpoint metadata: this route is protected by <see cref="AccessFilter"/> for <paramref name="Area"/>.</summary>
public sealed record RequiresAccess(AccessArea Area);

public static class AccessFilter
{
    /// <summary>Key under which the Lambda ASP.NET Core bridge stores the original API Gateway event.</summary>
    public const string LambdaRequestItem = "LambdaRequestObject";

    /// <summary>Protect a route: 401 without verified claims, 403 for the wrong group.</summary>
    public static RouteHandlerBuilder RequireAccess(this RouteHandlerBuilder route, AccessArea area) =>
        route.WithMetadata(new RequiresAccess(area)).AddEndpointFilter(Require(area));

    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> Require(AccessArea area) =>
        async (ctx, next) =>
        {
            var claims = VerifiedClaims(ctx.HttpContext);
            if (claims is null) return Results.Unauthorized();   // e.g. route deployed without the authorizer

            var groups = AccessRules.ParseGroups(claims.TryGetValue(AccessRules.GroupsClaim, out var g) ? g : null);
            return AccessRules.CanAccess(area, groups)
                ? await next(ctx)
                : Results.Json(new { error = $"your account has no access to {area} (groups: [{string.Join(", ", groups)}])" },
                    statusCode: StatusCodes.Status403Forbidden);
        };

    /// <summary>Claims verified by the API Gateway JWT authorizer, taken from the original Lambda event.</summary>
    public static IDictionary<string, string>? VerifiedClaims(HttpContext http)
    {
        if (http.Items[LambdaRequestItem] is Amazon.Lambda.APIGatewayEvents.APIGatewayHttpApiV2ProxyRequest req
            && req.RequestContext?.Authorizer?.Jwt?.Claims is { Count: > 0 } jwtClaims)
            return jwtClaims;

        // Fallback: the Lambda ASP.NET Core bridge may also expose them as the request's User.
        return http.User.Identity?.IsAuthenticated == true
            ? http.User.Claims.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.First().Value)
            : null;
    }
}
