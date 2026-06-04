FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY MesServer.csproj ./
RUN dotnet restore MesServer.csproj

COPY . ./
RUN dotnet publish MesServer.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

WORKDIR /app
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish ./
COPY appsettings.json ./appsettings.json

ENTRYPOINT ["dotnet", "MesServer.dll"]
