#!/bin/bash
rm -rf publish/*
projects=("FastTunnel.Client" "FastTunnel.Server")

for project in ${projects[*]}; do
    echo
    echo "=========开始发布：${project} ========="
    echo
    rm -rf publish/$project/*
    echo src/$project/$project.csproj
    dotnet publish $project/$project.csproj -o=publish/$project -c=release --nologo
    echo
    echo "=========开始打包 ========="
    echo
    cd publish && tar -zcvf $project.tar.gz $project
    cd ../
done
