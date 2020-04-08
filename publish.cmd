@echo off

for /d %%p in (FastTunnel.Client,FastTunnel.Server,SuiDao.Client) do (
  CD ./%%p 
  for  %%I in (win-x64,osx-x64,linux-x64) do (
	dotnet publish -o=../publish/%%p.%%I -c=release -r=%%I --nologo & 7z a -tzip ../publish/%%p.%%I.zip ../publish/%%p.%%I
  )
  cd ../
)

pause