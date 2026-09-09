# Render / Railway / Fly.io 등에서 사용하는 배포용 파일. 수정할 필요 없음.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
ENV DATA_DIR=/data
EXPOSE 8080
ENTRYPOINT ["dotnet", "CinemaBooking.dll"]
