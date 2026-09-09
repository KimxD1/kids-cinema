using CinemaBooking.Services;
using Microsoft.AspNetCore.SignalR;

namespace CinemaBooking.Hubs;

/// <summary>손님(원생)용 실시간 통신 창구</summary>
public class SeatHub(SeatStore store) : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("SeatsUpdated", store.Snapshot());
        await Clients.Caller.SendAsync("SiteState", store.SiteOpen, store.Show);
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (store.ReleaseAllOf(Context.ConnectionId) > 0) await Broadcast();
        await base.OnDisconnectedAsync(exception);
    }

    public async Task<bool> HoldSeat(string seatId)
    {
        var ok = store.TryHold(seatId, Context.ConnectionId);
        if (ok) await Broadcast();
        return ok;
    }

    public async Task<bool> ReleaseSeat(string seatId)
    {
        var ok = store.Release(seatId, Context.ConnectionId);
        if (ok) await Broadcast();
        return ok;
    }

    public async Task<string?> ConfirmBooking(string customerName)
    {
        var code = store.Confirm(Context.ConnectionId, string.IsNullOrWhiteSpace(customerName) ? "손님" : customerName.Trim());
        if (code != null) await Broadcast();
        return code;
    }

    public async Task<List<string>?> CancelBooking(string code)
    {
        var seats = store.CancelBooking(code.Trim());
        if (seats != null) await Broadcast();
        return seats;
    }

    private Task Broadcast() => Clients.All.SendAsync("SeatsUpdated", store.Snapshot());
}
