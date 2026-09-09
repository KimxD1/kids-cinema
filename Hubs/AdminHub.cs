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
    public Task<object> GetDashboard()
    {
        RequireAdmin();
        return Task.FromResult<object>(new
        {
            seats = store.Snapshot(),
            bookings = store.AllBookings(),
            siteOpen = store.SiteOpen,
            show = store.Show
        });
    }

    // ---- 슈퍼 권한 기능 ----
    /// <summary>예매번호 없이도 어떤 예매든 취소</summary>
    public async Task<List<string>?> ForceCancel(string code)
    {
        RequireAdmin();
        var seats = store.CancelBooking(code.Trim());
        if (seats != null) await NotifyAll();
        return seats;
    }

    /// <summary>선택 중(노랑/빨강) 좌석 전부 해제</summary>
    public async Task<int> ReleaseAllHolds()
    {
        RequireAdmin();
        var n = store.AdminReleaseAllHolds();
        await NotifyAll();
        return n;
    }

    /// <summary>모든 예매 삭제 + 좌석 초기화</summary>
    public async Task ResetAll()
    {
        RequireAdmin();
        store.AdminResetAll();
        await NotifyAll();
    }

    /// <summary>사이트 열기/닫기</summary>
    public async Task SetSiteOpen(bool open)
    {
        RequireAdmin();
        store.AdminSetSiteOpen(open);
        await NotifyAll();
    }

    /// <summary>영화 제목 / 상영시간 / 포스터 URL 설정 (비워도 됨)</summary>
    public async Task SetShow(string title, string time, string posterUrl)
    {
        RequireAdmin();
        store.AdminSetShow(new ShowInfo(title?.Trim() ?? "", time?.Trim() ?? "", posterUrl?.Trim() ?? ""));
        await NotifyAll();
    }

    private async Task NotifyAll()
    {
        // 손님 화면 갱신
        await seatHub.Clients.All.SendAsync("SeatsUpdated", store.Snapshot());
        await seatHub.Clients.All.SendAsync("SiteState", store.SiteOpen, store.Show);
        // 다른 관리자 화면 갱신
        await Clients.All.SendAsync("AdminRefresh");
    }
}
