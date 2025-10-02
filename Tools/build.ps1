Set-Variable -Name "currentDir" -Value (Get-Location).Path
Push-Location
Set-Location ..\TinyCam
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --self-contained true -p:OutputType=WinExe
Write-Output "Path of the released files:"
Write-Output "${currentDir}\..\TinyCam\bin\Release\net8.0\win-x64\publish\"
Pop-Location