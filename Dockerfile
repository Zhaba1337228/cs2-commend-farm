FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/CommendFarm/CommendFarm.csproj CommendFarm/
RUN dotnet restore CommendFarm/CommendFarm.csproj
COPY src/CommendFarm/ CommendFarm/
RUN dotnet publish CommendFarm/CommendFarm.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app/publish .

VOLUME /app/data
EXPOSE 5050

ENTRYPOINT ["dotnet", "commend-farm.dll", "--loop"]
