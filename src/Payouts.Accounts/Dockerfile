# syntax=docker/dockerfile:1
# Build context: the repository root.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
ARG TARGETARCH
WORKDIR /src

# Restore first so source edits reuse the cached layer.
COPY Directory.Build.props Directory.Packages.props global.json ./
COPY src/Payouts.Shared/Payouts.Shared.csproj src/Payouts.Shared/
COPY src/Payouts.Accounts/Payouts.Accounts.csproj src/Payouts.Accounts/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked     dotnet restore src/Payouts.Accounts/Payouts.Accounts.csproj --os linux-musl -a $TARGETARCH

COPY src/Payouts.Shared/ src/Payouts.Shared/
COPY src/Payouts.Accounts/ src/Payouts.Accounts/
RUN --mount=type=cache,id=nuget,target=/root/.nuget/packages,sharing=locked     dotnet publish src/Payouts.Accounts/Payouts.Accounts.csproj -c Release --os linux-musl -a $TARGETARCH --no-restore -p:UseAppHost=false -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app ./
USER $APP_UID
EXPOSE 8080
HEALTHCHECK --interval=2s --timeout=5s --start-period=30s --retries=30     CMD wget -q -O /dev/null http://127.0.0.1:8080/health || exit 1
ENTRYPOINT ["dotnet", "Payouts.Accounts.dll"]
