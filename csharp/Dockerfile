FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY csharp/Directory.Build.props csharp/Directory.Build.props
COPY csharp/RelicFrame.Core/ csharp/RelicFrame.Core/
COPY csharp/RelicFrame.Bot/ csharp/RelicFrame.Bot/
RUN dotnet publish csharp/RelicFrame.Bot/RelicFrame.Bot.csproj -c Release -o /out --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /out/ ./
COPY relicframe/data/relics_from_official_data.csv ./relicframe/data/relics_from_official_data.csv
COPY relicframe/data/rivens/attributes.json relicframe/data/rivens/roll_rules.json relicframe/data/rivens/weapon_variants.json relicframe/data/rivens/weapons.json ./relicframe/data/rivens/
COPY Untitled.txt ./Untitled.txt
ENV RELICFRAME_DATA_DIR=/app/relicframe/data \
    RELICFRAME_RUNTIME_DIR=/data \
    ARBITRATION_SCHEDULE_PATH=/app/Untitled.txt \
    DOTNET_GCConserveMemory=7 \
    DOTNET_EnableDiagnostics=0
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "/app/RelicFrame.Bot.dll", "--test-bot"]
