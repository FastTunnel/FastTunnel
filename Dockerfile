#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:7.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:7.0 AS build
WORKDIR /src
COPY ["FastTunnel.Server/FastTunnel.Server.csproj", "FastTunnel.Server/"]
COPY ["FastTunnel.Core/FastTunnel.Core.csproj", "FastTunnel.Core/"]
COPY ["FastTunnel.Api/FastTunnel.Api.csproj", "FastTunnel.Api/"]
RUN dotnet restore "FastTunnel.Server/FastTunnel.Server.csproj"
COPY . .
WORKDIR "/src/FastTunnel.Server"
RUN dotnet build "FastTunnel.Server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "FastTunnel.Server.csproj" -c Release -o /app/publish

FROM base AS final
#RUN mkdir -p /vols
WORKDIR /app
COPY --from=publish /app/publish/config /vols/config
COPY --from=publish /app/publish .
COPY ./start.sh .
ENTRYPOINT ["/bin/bash","./start.sh"]
#ENTRYPOINT ["dotnet", "FastTunnel.Server.dll"]
