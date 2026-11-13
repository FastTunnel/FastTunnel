#!/bin/bash

# /vols/config 是留给用户挂载的配置目录，/app/config 里是镜像内置的默认配置。
# 这里必须用 cp 覆盖，不能用 mv：
#   1) mv -n 遇到同名文件直接跳过，用户挂载的 appsettings.json 永远盖不掉内置那份；
#   2) mv 跨文件系统（宿主机 bind mount -> 容器层）等于"复制后删除源文件"，
#      会把宿主机上的配置文件搬走。
# 用 "/." 而不是 "/*"：能带上隐藏文件，且目录为空时不会报错。
if [ -d /vols/config ]; then
    cp -rf /vols/config/. /app/config/ || echo "!!! 同步 /vols/config 到 /app/config 失败，将使用镜像内置配置"
fi

# exec 让 dotnet 直接接管 PID 1：否则 PID 1 是这个 bash，
# docker stop 发的 SIGTERM 到不了 dotnet，只能干等 10 秒再被 SIGKILL。
exec dotnet FastTunnel.Server.dll
