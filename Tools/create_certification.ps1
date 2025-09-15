$cert = New-SelfSignedCertificate `
  -DnsName "localhost" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -FriendlyName "TinyCam Dev Cert" `
  -KeyExportPolicy Exportable `
  -KeySpec Signature `
  -KeyLength 4096 `
  -HashAlgorithm sha256 `
  -NotAfter (Get-Date).AddYears(100)

$pwd = ConvertTo-SecureString -String "yourpassword" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath ".\tinycam_dev.pfx" -Password $pwd