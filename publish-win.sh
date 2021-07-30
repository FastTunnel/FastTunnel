#!/bin/bash
rm -rf publish/*
projects=("FastTunnel.Client" "FastTunnel.Server")
plates=("win-x64")
for project in ${projects[*]}; do
    echo
    echo "=========开始发布：${project} ========="
    echo
    for plate in ${plates[*]}; do
        echo "plate=${plate}"
        echo src/$project/$project.csproj
        dotnet publish $project/$project.csproj -o=publish/$project.$plate -c=release #-p:PublishTrimmed=true --nologo
        echo
        echo "=========开始打包 ========="
        echo
        cd publish && tar -zcvf $project.$plate.tar.gz $project.$plate
        cd ../
        # rm -rf publish/$project.$plate
    done
done
