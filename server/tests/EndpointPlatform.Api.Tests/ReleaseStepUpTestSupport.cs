using System.Net.Http.Json;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Domain.Identity;
using EndpointPlatform.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace EndpointPlatform.Api.Tests;

/// <summary>
/// Shared helpers for the three agent-release routes that require step-up:
/// publish, artifact replace, and update-agent.
/// </summary>
/// <remarks>
/// <para>
/// Those routes now take the caller's own password and a justification, mirror
/// of the BitLocker reveal gate, and they share ONE per-user rate-limit budget:
/// five gated actions per fifteen-minute fixed window, where a success does not
/// reset the counter. Two consequences for tests, both the same as the escrow
/// suite drew: every request body or form must carry the step-up fields, and
/// every test that drives a gated route needs <b>its own administrator</b> —
/// a shared fixture account would exhaust the window across tests and fail
/// whichever ones ran last. Weakening the limit to make tests share an account
/// would remove the control being tested.
/// </para>
/// <para>
/// The administrator is minted directly rather than signed in, exactly like the
/// escrow tests' accounts: the login endpoint's rate limit is a budget shared
/// by the whole assembly, and minting produces the same token hash and security
/// stamp the login path would have.
/// </para>
/// </remarks>
internal static class ReleaseStepUpTestSupport
{
    public const string DefaultJustification = "staged rollout, release test";

    /// <summary>The JSON body the publish route requires.</summary>
    public static JsonContent PublishBody(
        string? currentPassword = null, string justification = DefaultJustification) =>
        JsonContent.Create(new
        {
            currentPassword = currentPassword ?? AdminApiPostgresFixture.Password,
            justification,
        });

    /// <summary>The JSON body the update-agent route requires.</summary>
    public static JsonContent UpdateAgentBody(
        Guid releaseId, string? currentPassword = null, string justification = DefaultJustification) =>
        JsonContent.Create(new
        {
            releaseId,
            currentPassword = currentPassword ?? AdminApiPostgresFixture.Password,
            justification,
        });

    /// <summary>Adds the step-up form fields the artifact-replace route requires.</summary>
    public static MultipartFormDataContent WithStepUp(
        this MultipartFormDataContent form,
        string? currentPassword = null,
        string justification = DefaultJustification)
    {
        form.Add(new StringContent(currentPassword ?? AdminApiPostgresFixture.Password), "currentPassword");
        form.Add(new StringContent(justification), "justification");
        return form;
    }

    /// <summary>
    /// A fresh Super Administrator with organization-wide device scope and a
    /// minted session, isolated from every other test's rate-limit window.
    /// </summary>
    public static async Task<(HttpClient Client, string Email)> MintDeployAdminAsync(
        this AdminApiPostgresFixture fixture, string tag)
    {
        var email = $"{tag}-{Guid.CreateVersion7():N}@test.local";

        await using (var db = fixture.CreateDbContext())
        {
            var org = await db.Organizations.OrderBy(o => o.CreatedAt).FirstAsync();
            var role = await db.Roles.SingleAsync(r => r.Key == SystemRoles.SuperAdministrator);

            var user = new PlatformUser(org.Id, email, "Release Admin");
            user.SetPasswordHash(
                PasswordHasher.Hash(AdminApiPostgresFixture.Password), DateTimeOffset.UtcNow);
            AdminApiPostgresFixture.EnrolMfa(user);
            user.AssignRole(role.Id);
            user.GrantAllDeviceScope();

            db.PlatformUsers.Add(user);
            await db.SaveChangesAsync();
        }

        var token = SecretGenerator.GenerateSecret();

        await using (var db = fixture.CreateDbContext())
        {
            var user = await db.PlatformUsers.SingleAsync(u => u.Email == email);
            db.AdminSessions.Add(new AdminSession(
                user.Id, SecretGenerator.HashSecret(token), user.SecurityStamp,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1),
                sourceIp: null, userAgent: "release-step-up-tests"));
            await db.SaveChangesAsync();
        }

        return (fixture.CreateClientFor(token), email);
    }
}
