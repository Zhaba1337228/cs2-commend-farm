FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Никаких docker.io, никаких 60GB серверов — только бот

COPY src/CommendFarm/publish/ .

ENV FARM_DATA_DIR=/app/data
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5050

ENTRYPOINT ["dotnet", "commend-farm.dll"]