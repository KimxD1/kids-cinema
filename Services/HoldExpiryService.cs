using CinemaBooking.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace CinemaBooking.Services;

/// <summary>
/// 5초마다 "선택만 해두고 결제하지 않은" 좌석(5분 경과)을 자동으로 풀어주고
/// 모든 접속자에게 최신 좌석 상태를 방송합니다.
/// </summary>
public class HoldExpiryService(SeatStore store, IHubContext<SeatHub> hub) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            if (store.ExpireHolds() > 0)
                await hub.Clients.All.SendAsync("SeatsUpdated", store.Snapshot(), stoppingToken);
        }
    }
}
