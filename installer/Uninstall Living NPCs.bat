@echo off
title Living NPCs uninstaller
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-LivingNPCs.ps1" -Uninstall
