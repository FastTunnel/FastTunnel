#!/bin/bash
projects=("FastTunnel.Client" "FastTunnel.Server")
plates=("win-x64" "win-x86" "win-arm" "win-arm64" "linux-x64" "linux-musl-x64" "linux-arm" "linux-arm64" "osx-x64")
for project in ${projects[*]}; do
    echo
    echo "=========开始发布：${project} ========="
    echo
    for plate in ${plates[*]}; do
        echo "plate=${plate}"
        echo src/$project/$project.csproj
        rm -rf publish/$project.$plate/*
        dotnet publish $project/$project.csproj -o=publish/$project.$plate -r=$plate -c=release --nologo  #-p:PublishTrimmed=true
        echo
        echo "=========开始打包 ========="
        echo
        cd publish && tar -zcvf $project.$plate.tar.gz $project.$plate
        cd ../
        # rm -rf publish/$project.$plate
    done
done

