FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ZendeskMcp.sln ./
COPY ZendeskMcp/*.csproj ZendeskMcp/
COPY ZendeskMcp.Tests/*.csproj ZendeskMcp.Tests/
RUN dotnet restore ZendeskMcp/ZendeskMcp.csproj

COPY ZendeskMcp/. ZendeskMcp/
RUN dotnet publish ZendeskMcp/ZendeskMcp.csproj -c Release -o /out --no-restore /p:UseAppHost=false

# `-extra`, not plain chiseled: plain chiseled carries no time-zone data (/usr/share/zoneinfo), so
# TimeZoneInfo.FindSystemTimeZoneById fails for every IANA zone and the reporting tools
# (AnalyticsStats.ResolveTimeZone, TicketCountHelpers.Today) fall back to UTC, putting days and
# buckets on the wrong boundaries for anyone not on UTC (#13). `-extra` adds tzdata and ICU; the
# invariant-globalisation switch plain chiseled sets is set again below, so culture behaviour is
# unchanged and only the time-zone data is new. security-scan.yml checks the zone data is there.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
ENV DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true
WORKDIR /app
COPY --from=build /out ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ZendeskMcp.dll"]
