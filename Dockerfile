FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/CommendFarm/ .
RUN dotnet publish -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

ENV FARM_DATA_DIR=/app/data
ENV ASPNETCORE_ENVIRONMENT=Production

EXPOSE 5050

ENTRYPOINT ["dotnet", "commend-farm.dll"]
