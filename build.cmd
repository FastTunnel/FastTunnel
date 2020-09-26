@echo off

for /d %%p in (FastTunnel.Client,FastTunnel.Server,SuiDao.Client,SuiDao.Server) do (
  CD ./%%p 
 
  for  %%I in (win-x64,osx-x64,linux-x64) do (
	dotnet publish -o=../build/%%p.%%I -c=release -r=%%I & echo f |xcopy %~dp0%%p\appsettings.json %~dp0publish\%%p.%%I\appsettings.json
  )
  cd ../
)

pause