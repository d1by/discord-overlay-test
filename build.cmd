@echo off
"%WINDIR%\Microsoft.NET\Framework644.0.30319\csc.exe" -nologo -optimize+ -target:winexe -platform:x64 -out:OverlayHost.exe OverlayHost.cs
