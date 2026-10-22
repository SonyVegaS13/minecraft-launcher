FROM mcr.microsoft.com/dotnet/sdk:8.0.426 AS build
WORKDIR /src
COPY Solaris.Id.Api.csproj ./
RUN dotnet restore Solaris.Id.Api.csproj
COPY . ./
RUN dotnet publish Solaris.Id.Api.csproj -c Release --no-restore -p:UseAppHost=false -o /out

FROM mcr.microsoft.com/dotnet/aspnet:8.0.32
WORKDIR /app
COPY --from=build /out/ ./
USER app
ENV ASPNETCORE_URLS=https://+:8443
EXPOSE 8443
# Mount an operator-owned certificate and persistent protected key directory;
# provide credentials via the host secret manager. No embedded test credentials.
ENTRYPOINT ["dotnet", "Solaris.Id.Api.dll"]
