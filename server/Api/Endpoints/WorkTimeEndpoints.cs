using System.Globalization;
using System.Text;
using EndpointPlatform.Api.Security;
using EndpointPlatform.Domain.Authorization;
using EndpointPlatform.Infrastructure.Security;
using EndpointPlatform.Infrastructure.WorkTime;

namespace EndpointPlatform.Api.Endpoints;

/// <summary>
/// Work-time / attendance: the shift configuration and the group-wise report of how long
/// each employee's device was present, actively working and idle during the shift.
/// </summary>
/// <remarks>
/// The report rides <see cref="Permissions.Activity.View"/> and is device-scoped like
/// every other report; changing the shift, idle threshold or break needs
/// <see cref="Permissions.Activity.Configure"/>. The data is activity presence only —
/// no content, apps or screenshots ever reach it.
/// </remarks>
public static class WorkTimeEndpoints
{
    private const int DefaultRangeDays = 7;

    public static IEndpointRouteBuilder MapWorkTimeEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/admin/v1/settings/work-time", GetSettingsAsync)
            .WithName("GetWorkTimeSettings").RequirePermission(Permissions.Activity.View);
        endpoints.MapPut("/admin/v1/settings/work-time", PutSettingsAsync)
            .WithName("UpdateWorkTimeSettings").RequirePermission(Permissions.Activity.Configure);
        endpoints.MapGet("/admin/v1/work-time/report", GetReportAsync)
            .WithName("GetWorkTimeReport").RequirePermission(Permissions.Activity.View);
        endpoints.MapGet("/admin/v1/work-time/report.csv", GetReportCsvAsync)
            .WithName("GetWorkTimeReportCsv").RequirePermission(Permissions.Activity.View);
        return endpoints;
    }

    private static async Task<IResult> GetSettingsAsync(
        WorkTimeSettingService service, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        var setting = await service.GetAsync(actor.OrganizationId, cancellationToken);
        return Results.Ok(new WorkTimeSettingDto(
            setting.Enabled, setting.ShiftStartMinutes, setting.ShiftEndMinutes, setting.IdleThresholdMinutes,
            setting.BreakMinutes, setting.UtcOffsetMinutes, setting.ShiftLengthMinutes));
    }

    public sealed record WorkTimeSettingUpdate(
        bool Enabled, int ShiftStartMinutes, int ShiftEndMinutes, int IdleThresholdMinutes, int BreakMinutes, int UtcOffsetMinutes);

    private static async Task<IResult> PutSettingsAsync(
        WorkTimeSettingUpdate request,
        WorkTimeSettingService service,
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        var actor = AdminActor.Required(httpContext.User);
        try
        {
            await service.SetAsync(
                actor.OrganizationId, actor.UserId, actor.Email, request.Enabled,
                request.ShiftStartMinutes, request.ShiftEndMinutes, request.IdleThresholdMinutes,
                request.BreakMinutes, request.UtcOffsetMinutes, cancellationToken);
        }
        catch (Exception ex) when (ex is ArgumentException or ArgumentOutOfRangeException)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status400BadRequest);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetReportAsync(
        WorkTimeReportService service,
        DeviceScopeAuthorizer scope,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? groupId = null)
    {
        var actor = AdminActor.Required(httpContext.User);
        var (fromDate, toDate) = ResolveRange(from, to, timeProvider);
        var scoped = await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, actor.OrganizationId, cancellationToken);
        var report = await service.GetReportAsync(actor.OrganizationId, scoped, fromDate, toDate, groupId, cancellationToken);
        return Results.Ok(report);
    }

    private static async Task<IResult> GetReportCsvAsync(
        WorkTimeReportService service,
        DeviceScopeAuthorizer scope,
        TimeProvider timeProvider,
        HttpContext httpContext,
        CancellationToken cancellationToken,
        DateOnly? from = null,
        DateOnly? to = null,
        Guid? groupId = null)
    {
        var actor = AdminActor.Required(httpContext.User);
        var (fromDate, toDate) = ResolveRange(from, to, timeProvider);
        var scoped = await scope.ScopedDeviceIdsOrNullAsync(actor.UserId, actor.OrganizationId, cancellationToken);
        var report = await service.GetReportAsync(actor.OrganizationId, scoped, fromDate, toDate, groupId, cancellationToken);

        var csv = new StringBuilder();
        csv.AppendLine("Date,Name,Group,Device,Gross (h:mm),Working (h:mm),Idle/Break (h:mm),Gross min,Working min,Idle min");
        foreach (var r in report.Rows)
        {
            csv.Append(r.ShiftDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(',')
               .Append(Escape(r.UserName)).Append(',')
               .Append(Escape(r.GroupName)).Append(',')
               .Append(Escape(r.Hostname)).Append(',')
               .Append(Hhmm(r.PresentMinutes)).Append(',')
               .Append(Hhmm(r.ActiveMinutes)).Append(',')
               .Append(Hhmm(r.IdleMinutes)).Append(',')
               .Append(r.PresentMinutes).Append(',')
               .Append(r.ActiveMinutes).Append(',')
               .Append(r.IdleMinutes).Append('\n');
        }

        var fileName = $"work-time-{fromDate:yyyy-MM-dd}_to_{toDate:yyyy-MM-dd}.csv";
        return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", fileName);
    }

    private static (DateOnly From, DateOnly To) ResolveRange(DateOnly? from, DateOnly? to, TimeProvider timeProvider)
    {
        var today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);
        var resolvedTo = to ?? today;
        var resolvedFrom = from ?? resolvedTo.AddDays(-(DefaultRangeDays - 1));
        return (resolvedFrom, resolvedTo);
    }

    private static string Hhmm(int minutes) => $"{minutes / 60}:{minutes % 60:D2}";

    private static string Escape(string value) =>
        value.Contains(',') || value.Contains('"') || value.Contains('\n')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;
}
