CHCP 65001
@echo off
color 0e
@echo ==================================
@echo 提醒：请右键本文件，用管理员方式打开。
@echo ==================================
@echo Start Remove FastTunnel.Server

Net stop FastTunnel.Server
sc delete FastTunnel.Server
pause