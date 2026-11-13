#See https://aka.ms/containerfastmode to understand how Visual Studio uses this Dockerfile to build your images for faster debugging.

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS base
WORKDIR /app
EXPOSE 80
EXPOSE 443

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Directory.Build.props 里放着 TargetFramework=net10.0，必须在 restore 之前就位，
# 否则 restore 阶段各工程拿不到目标框架
COPY ["Directory.Build.props", "./"]
COPY ["FastTunnel.Server/FastTunnel.Server.csproj", "FastTunnel.Server/"]
COPY ["FastTunnel.Core/FastTunnel.Core.csproj", "FastTunnel.Core/"]
COPY ["FastTunnel.Api/FastTunnel.Api.csproj", "FastTunnel.Api/"]
COPY ["FastTunnel.Hosting/FastTunnel.Hosting.csproj", "FastTunnel.Hosting/"]
RUN dotnet restore "FastTunnel.Server/FastTunnel.Server.csproj"
COPY . .
WORKDIR "/src/FastTunnel.Server"
# 关掉打包：Directory.Build.props 默认 GeneratePackageOnBuild=true，而 .dockerignore
# 排除了 README.md、Hosting 又把它当包内 README，打包会 NU5019 直接构建失败；
# 镜像构建不需要 nupkg
RUN dotnet build "FastTunnel.Server.csproj" -c Release -p:GeneratePackageOnBuild=false -o /app/build

FROM build AS publish
RUN dotnet publish "FastTunnel.Server.csproj" -c Release -p:GeneratePackageOnBuild=false -o /app/publish

FROM base AS final
#RUN mkdir -p /vols
WORKDIR /app
COPY --from=publish /app/publish/config /vols/config
COPY --from=publish /app/publish .
COPY ./start.sh .
ENTRYPOINT ["/bin/bash","./start.sh"]
#ENTRYPOINT ["dotnet", "FastTunnel.Server.dll"]
