FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/CommendFarm/CommendFarm.csproj CommendFarm/
RUN dotnet restore CommendFarm/CommendFarm.csproj
COPY src/CommendFarm/ CommendFarm/
RUN dotnet publish CommendFarm/CommendFarm.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# Docker CLI for managing cs2-server container
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates \
    curl \
    docker.io \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

VOLUME /app/data
EXPOSE 5050

ENTRYPOINT ["dotnet", "commend-farm.dll", "--loop"]
