// Tests for role-based access (CSKH vs Quản lý).
using ShipmentSla.Core;

namespace ShipmentSla.Core.Tests;

public class AccessRulesTests
{
    [Theory]
    [InlineData("[manager]", new[] { "manager" })]
    [InlineData("[cskh manager]", new[] { "cskh", "manager" })]      // API Gateway HTTP API format
    [InlineData("[\"cskh\",\"manager\"]", new[] { "cskh", "manager" })] // raw JSON array
    [InlineData("cskh,manager", new[] { "cskh", "manager" })]
    [InlineData("  [ MANAGER ] ", new[] { "manager" })]
    public void Groups_claim_formats_are_parsed(string claim, string[] expected)
    {
        var groups = AccessRules.ParseGroups(claim);
        Assert.Equal(expected.Length, groups.Count);
        foreach (var g in expected) Assert.Contains(g, groups);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    public void Missing_groups_give_empty_set(string? claim) =>
        Assert.Empty(AccessRules.ParseGroups(claim));

    [Theory]
    // groups claim,      tracking, sla reports
    [InlineData("[cskh]",          true,  false)]
    [InlineData("[manager]",       true,  true)]
    [InlineData("[cskh manager]",  true,  true)]
    [InlineData("[]",              false, false)]   // logged in but no role
    [InlineData("[admins]",        false, false)]   // unknown group
    [InlineData("[cskhx]",         false, false)]   // no prefix tricks
    public void Role_matrix(string claim, bool tracking, bool sla)
    {
        var groups = AccessRules.ParseGroups(claim);
        Assert.Equal(tracking, AccessRules.CanAccess(AccessArea.Tracking, groups));
        Assert.Equal(sla, AccessRules.CanAccess(AccessArea.SlaReports, groups));
    }
}
