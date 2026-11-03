@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0app\setup.ps1" -Action Install
