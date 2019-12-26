@echo off

for /d %%p in (FastTunnel.Client,FastTunnel.Server) do (
  CD ./%%p 
  for  %%I in (win-x64,osx-x64,linux-x64) do (
	dotnet publish -o=../publish/%%p.%%I -c=release -f=netcoreapp3.1 -r=%%I --self-contained & 7z a -tzip ../publish/%%p.%%I.zip ../publish/%%p.%%I
  )
  cd ../
)

pause