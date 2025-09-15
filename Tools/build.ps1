cd ..\TinyCam
dotnet publish -c Release -r win-x64 -p:PublishSingleFile=true -p:PublishTrimmed=false --self-contained true
cd ..