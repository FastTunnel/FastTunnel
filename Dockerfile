#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:5.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:5.0 AS build
WORKDIR /src
COPY ["FastTunnel.Server/FastTunnel.Server.csproj", "FastTunnel.Server/"]
COPY ["FastTunnel.Core/FastTunnel.Core.csproj", "FastTunnel.Core/"]
RUN dotnet restore "FastTunnel.Server/FastTunnel.Server.csproj"
COPY . .
WORKDIR "/src/FastTunnel.Server"
RUN dotnet build "FastTunnel.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FastTunnel.Server.csproj" -c Release -o /app/publish

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
COPY --from=publish /app/publish/config /config
ENTRYPOINT ["dotnet", "FastTunnel.Server.dll"]