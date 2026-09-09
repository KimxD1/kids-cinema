using CinemaBooking.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CinemaBooking.Services;

/// <summary>
/// 5초마다 "선택만 해두고 결제하지 않은" 좌석(5분 경과)을 세션별로 자동 해제하고
/// 해당 세션을 보고 있는 접속자에게만 최신 좌석 상태를 방송합니다.
/// </summary>
public class HoldExpiryService(SeatStore store, IHubContext<SeatHub> hub) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            var expiredBySession = store.ExpireHolds();
            foreach (var sessionId in expiredBySession.Keys)
                await hub.Clients.Group(sessionId).SendAsync("SeatsUpdated", store.Snapshot(sessionId), stoppingToken);
        }
    }
}
