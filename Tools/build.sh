cd ../TinyCam
dotnet publish -c Release -r linux-x64 -p:PublishSingleFile=true -p:PublishTrimmed=false --self-contained true
cd ..