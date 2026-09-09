using CinemaBooking.Hubs;
using CinemaBooking.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSignalR();
builder.Services.AddSingleton<SeatStore>();
builder.Services.AddHostedService<HoldExpiryService>();

var app = builder.Build();

app.UseDefaultFiles();   // wwwroot/index.html = 손님 화면, wwwroot/admin.html = 관리자 화면
app.UseStaticFiles();
app.MapHub<SeatHub>("/seathub");
app.MapHub<AdminHub>("/adminhub");
app.MapGet("/health", () => "ok");   // 호스팅 서비스가 서버 생존 확인용으로 사용

app.Run();
