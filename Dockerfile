FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ZendeskMcp.sln ./
COPY ZendeskMcp/*.csproj ZendeskMcp/
COPY ZendeskMcp.Tests/*.csproj ZendeskMcp.Tests/
RUN dotnet restore ZendeskMcp/ZendeskMcp.csproj

COPY ZendeskMcp/. ZendeskMcp/
RUN dotnet publish ZendeskMcp/ZendeskMcp.csproj -c Release -o /out --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS runtime
WORKDIR /app
COPY --from=build /out ./
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "ZendeskMcp.dll"]
