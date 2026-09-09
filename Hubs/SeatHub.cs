using CinemaBooking.Services;
using Microsoft.AspNetCore.SignalR;

namespace CinemaBooking.Hubs;

/// <summary>손님(원생)용 실시간 통신 창구. 영화 세션(cinema_1 등)마다 좌석이 독립적이므로,
/// 손님은 먼저 SelectSession 으로 세션을 고른 뒤 그 세션의 좌석을 조작한다.</summary>
public class SeatHub(SeatStore store) : Hub
{
    private const string SessionKey = "sessionId";

    public override async Task OnConnectedAsync()
    {
        // 접속하면 우선 "어떤 영화가 있는지" 목록을 보내준다. 좌석은 세션을 고른 뒤에 받는다.
        await Clients.Caller.SendAsync("SessionsUpdated", store.ListSessions());
        await Clients.Caller.SendAsync("SiteState", store.SiteOpen);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        var released = store.ReleaseAllOf(Context.ConnectionId);
        foreach (var sessionId in released.Keys)
            await Clients.Group(sessionId).SendAsync("SeatsUpdated", store.Snapshot(sessionId));
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>한 사람이 한 세션에서 선택할 수 있는 최대 좌석 수 (현재 4석)</summary>
    public int GetMaxHold() => SeatStore.MaxHoldPerConnection;

    /// <summary>손님이 영화(상영관)를 고르면 그 세션의 실시간 그룹에 참가하고 좌석 스냅샷을 돌려준다.</summary>
    public async Task<List<SeatDto>> SelectSession(string sessionId)
    {
        if (Context.Items.TryGetValue(SessionKey, out var prev) && prev is string prevId && prevId != sessionId)
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, prevId);

        await Groups.AddToGroupAsync(Context.ConnectionId, sessionId);
        Context.Items[SessionKey] = sessionId;
        return store.Snapshot(sessionId);
    }

    public async Task<bool> HoldSeat(string sessionId, string seatId)
    {
        var ok = store.TryHold(sessionId, seatId, Context.ConnectionId);
        if (ok) await Broadcast(sessionId);
        return ok;
    }

    public async Task<bool> ReleaseSeat(string sessionId, string seatId)
    {
        var ok = store.Release(sessionId, seatId, Context.ConnectionId);
        if (ok) await Broadcast(sessionId);
        return ok;
    }

    public async Task<string?> ConfirmBooking(string sessionId, string customerName)
    {
        var code = store.Confirm(sessionId, Context.ConnectionId, string.IsNullOrWhiteSpace(customerName) ? "손님" : customerName.Trim());
        if (code != null) await Broadcast(sessionId);
        return code;
    }

    /// <summary>예매번호만으로 취소 (어느 세션인지는 서버가 알아서 찾는다)</summary>
    public async Task<List<string>?> CancelBooking(string code)
    {
        var result = store.CancelBooking(code.Trim());
        if (result != null) await Broadcast(result.Value.sessionId);
        return result?.seatIds;
    }

    private Task Broadcast(string sessionId) => Clients.Group(sessionId).SendAsync("SeatsUpdated", store.Snapshot(sessionId));
}
