# dotnet tool install --global wix
# wix extension add -g WixToolset.Util.wixext
# wix extension add -g WixToolset.Firewall.wixext

wix build .\Product.wxs `
  -arch x64 `
  -ext WixToolset.Util.wixext `
  -ext WixToolset.Firewall.wixext `
  -d PublishDir="..\TinyCam\bin\Release\net8.0\win-x64\publish" `
  -o "..\TinyCam\bin\Release\net8.0\win-x64\publish\TinyCam.msi"
