CHCP 65001
@echo off
color 0e
@echo ==================================
@echo 提醒：请右键本文件，用管理员方式打开。
@echo ==================================
@echo Start Install FastTunnel.Client

sc create FastTunnel.Client binPath=%~dp0\FastTunnel.Client.exe start= auto 
sc description FastTunnel.Client "FastTunnel-开源内网穿透服务，仓库地址：https://github.com/SpringHgui/FastTunnel star项目以支持作者"
Net Start FastTunnel.Client
pause