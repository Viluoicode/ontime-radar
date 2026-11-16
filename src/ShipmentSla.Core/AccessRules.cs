// Who may read what. Users belong to Cognito groups; the group list arrives in the
// access token claim "cognito:groups". API Gateway has already verified the token (401 if not);
// this decides 403 vs allowed. Pure logic → unit-tested without AWS.
// (Phân quyền theo nhóm: CSKH tra cứu đơn, Quản lý xem thêm báo cáo SLA.)
using System.Text.Json;

namespace ShipmentSla.Core;

public enum AccessArea
{
    Tracking,     // GET /shipments, GET /shipments/{carrier}/{tracking}
    SlaReports    // GET /sla/summary, GET /sla/trend
}

public static class AccessRules
{
    public const string CskhGroup = "cskh";
    public const string ManagerGroup = "manager";
    public const string GroupsClaim = "cognito:groups";

    /// <summary>
    /// Parse the groups claim. API Gateway HTTP API flattens the JSON array into the string
    /// "[cskh manager]"; a raw token has ["cskh","manager"]. Both (and "a,b") are accepted.
    /// </summary>
    public static IReadOnlySet<string> ParseGroups(string? claim)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(claim)) return result;

        var text = claim.Trim();
        if (text.StartsWith("[\"", StringComparison.Ordinal))
        {
            try
            {
                foreach (var g in JsonSerializer.Deserialize<string[]>(text) ?? []) result.Add(g.Trim());
                return result;
            }
            catch (JsonException) { /* fall through to the plain format */ }
        }

        foreach (var g in text.Trim('[', ']').Split([' ', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            result.Add(g);
        return result;
    }

    public static bool CanAccess(AccessArea area, IReadOnlySet<string> groups) => area switch
    {
        AccessArea.Tracking => groups.Contains(CskhGroup) || groups.Contains(ManagerGroup),
        AccessArea.SlaReports => groups.Contains(ManagerGroup),
        _ => false
    };
}
