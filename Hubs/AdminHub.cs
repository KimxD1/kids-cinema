using CinemaBooking.Services;
using Microsoft.AspNetCore.SignalR;

namespace CinemaBooking.Hubs;

/// <summary>
/// 관리자(선생님)용 통신 창구. 모든 기능은 먼저 Login(비밀번호)에 성공해야 사용 가능.
/// 비밀번호는 환경변수 ADMIN_PASSWORD 로 설정 (없으면 appsettings.json 의 값).
/// </summary>
public class AdminHub(SeatStore store, IHubContext<SeatHub> seatHub, IConfiguration config) : Hub
{
    private const string AuthKey = "isAdmin";

    public Task<bool> Login(string password)
    {
        var expected = config["ADMIN_PASSWORD"] ?? "teacher1234";
        var ok = !string.IsNullOrEmpty(password) && password == expected;
        Context.Items[AuthKey] = ok;
        return Task.FromResult(ok);
    }

    private void RequireAdmin()
    {
        if (Context.Items.TryGetValue(AuthKey, out var v) && v is true) return;
        throw new HubException("관리자 로그인이 필요합니다.");
    }

    // ---- 조회 ----
    /// <summary>
    /// sessionId 로 지정한 상영관의 대시보드를 반환한다.
    /// sessionId 가 없거나 존재하지 않으면 서버가 알아서 첫 번째 상영관을 골라 그 id 를 currentSessionId 로 돌려준다.
    /// </summary>
    public Task<object> GetDashboard(string? sessionId)
    {
        RequireAdmin();
        var sessions = store.ListSessions();
        var resolvedId = sessions.Any(s => s.Id == sessionId) ? sessionId! : (sessions.FirstOrDefault()?.Id ?? "");

        return Task.FromResult<object>(new
        {
            sessions,
            currentSessionId = resolvedId,
            seats = store.Snapshot(resolvedId),
            bookings = store.AllBookings(resolvedId),
            siteOpen = store.SiteOpen
        });
    }

    // ---- 슈퍼 권한 기능 ----
    /// <summary>예매번호 없이도 어떤 예매든 취소</summary>
    public async Task<List<string>?> ForceCancel(string code)
    {
        RequireAdmin();
        var result = store.CancelBooking(code.Trim());
        if (result != null) await NotifySeats(result.Value.sessionId);
        return result?.seatIds;
    }

    /// <summary>지정한 상영관에서 선택 중(노랑/빨강) 좌석 전부 해제</summary>
    public async Task<int> ReleaseAllHolds(string sessionId)
    {
        RequireAdmin();
        var n = store.AdminReleaseAllHolds(sessionId);
        await NotifySeats(sessionId);
        return n;
    }

    /// <summary>지정한 상영관의 예매를 전부 삭제하고 좌석을 초기화</summary>
    public async Task ResetSession(string sessionId)
    {
        RequireAdmin();
        store.AdminResetAll(sessionId);
        await NotifySeats(sessionId);
    }

    /// <summary>사이트 열기/닫기 (모든 상영관에 공통 적용)</summary>
    public async Task SetSiteOpen(bool open)
    {
        RequireAdmin();
        store.AdminSetSiteOpen(open);
        await seatHub.Clients.All.SendAsync("SiteState", store.SiteOpen);
        await Clients.All.SendAsync("AdminRefresh");
    }

    /// <summary>영화 세션(상영관) 추가 또는 수정. id 예: "cinema_1", "cinema_2"</summary>
    public async Task<bool> AddOrUpdateSession(string id, string title, string startTime, string endTime)
    {
        RequireAdmin();
        var ok = store.AdminAddOrUpdateSession(id, title, startTime, endTime);
        if (ok) await NotifySessions();
        return ok;
    }

    /// <summary>영화 세션 삭제 (예매가 남아있거나 마지막 하나 남은 세션이면 실패)</summary>
    public async Task<bool> RemoveSession(string id)
    {
        RequireAdmin();
        var ok = store.AdminRemoveSession(id);
        if (ok) await NotifySessions();
        return ok;
    }

    private async Task NotifySeats(string sessionId)
    {
        // 손님 화면(그 세션을 보고 있는 사람) 갱신
        await seatHub.Clients.Group(sessionId).SendAsync("SeatsUpdated", store.Snapshot(sessionId));
        // 다른 관리자 화면 갱신
        await Clients.All.SendAsync("AdminRefresh");
    }

    private async Task NotifySessions()
    {
        // 손님 화면의 "영화 선택" 목록 갱신
        await seatHub.Clients.All.SendAsync("SessionsUpdated", store.ListSessions());
        // 다른 관리자 화면 갱신
        await Clients.All.SendAsync("AdminRefresh");
    }
}
