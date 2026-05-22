FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/CommendFarm/CommendFarm.csproj CommendFarm/
RUN dotnet restore CommendFarm/CommendFarm.csproj
COPY src/CommendFarm/ CommendFarm/
RUN dotnet publish CommendFarm/CommendFarm.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app

# SteamCMD + CS2 server dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    lib32gcc-s1 \
    lib32stdc++6 \
    libstdc++6 \
    libgcc-s1 \
    libcurl4 \
    libcurl4t64 \
    libsdl2-2.0-0 \
    locales \
    && sed -i '/en_US.UTF-8/s/^# //g' /etc/locale.gen && locale-gen \
    && rm -rf /var/lib/apt/lists/*

ENV LC_ALL=en_US.UTF-8
ENV LANG=en_US.UTF-8

COPY --from=build /app/publish .

VOLUME /app/data
EXPOSE 5050 27015/udp 27015/tcp 27020/udp

ENTRYPOINT ["dotnet", "commend-farm.dll", "--loop"]
