@echo off
title Install CoreScope
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" -Desktop
if errorlevel 1 pause
