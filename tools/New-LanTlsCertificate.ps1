<#
.SYNOPSIS
    Issues an HTTPS certificate for the SecureChat server reachable over LAN.
.DESCRIPTION
    Creates a self-signed certificate with the server addresses in Subject Alternative Name,
    saves it to src\SecureChat.Server\App_Data (lan-tls.pfx, lan-tls.password.txt, lan-tls.cer)
    and trusts it for the current Windows account. Administrator rights are not required.
    Other Windows accounts install lan-tls.cer into "Trusted Root Certification Authorities"
    (Current User) by double-clicking the file.
.PARAMETER IpAddresses
    IPv4 addresses to put into the certificate. Default: all active IPv4 addresses of this PC plus 127.0.0.1.
.PARAMETER DnsNames
    Extra DNS names for public tunnels (for example, secure.playit.plus).
    Without them the browser/client rejects the certificate with "name does not match".
#>
param(
    [string[]]$IpAddresses,
    [string[]]$DnsNames
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$dataDir = Join-Path $repoRoot 'src\SecureChat.Server\App_Data'
New-Item -ItemType Directory -Force -Path $dataDir | Out-Null

if (-not $IpAddresses -or $IpAddresses.Count -eq 0) {
    $detected = Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue |
        Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' } |
        ForEach-Object { $_.IPAddress }
    $IpAddresses = @('127.0.0.1') + @($detected)
}
$IpAddresses = @($IpAddresses | Sort-Object -Unique)
$allDnsNames = @('localhost', $env:COMPUTERNAME) | Sort-Object -Unique
if ($DnsNames -and $DnsNames.Count -gt 0) {
    $allDnsNames = @($allDnsNames + $DnsNames | Where-Object { $_ } | Sort-Object -Unique)
}

# Публичные туннели Playit пробрасывают HTTPS на 443, и сервер отдаёт этот же
# сертификат. Без доменов туннеля в SAN клиент отклоняет его как
# «имя узла не совпадает» — добавляем их автоматически.
$tunnelDomains = @('secure.playit.plus', 'svcea.playit.plus', 'playit.plus', 'tun.ply.gg')
$allDnsNames = @($allDnsNames + $tunnelDomains | Sort-Object -Unique)

$sanParts = @()
$sanParts += $allDnsNames | ForEach-Object { "DNS=$_" }
$sanParts += $IpAddresses | ForEach-Object { "IPAddress=$_" }
$sanExtension = '2.5.29.17={text}' + ($sanParts -join '&')
$serverAuthExtension = '2.5.29.37={text}1.3.6.1.5.5.7.3.1'

# Remove previously issued LAN certificates from the trusted roots so they do not accumulate.
Get-ChildItem -Path 'Cert:\CurrentUser\Root' | Where-Object { $_.Subject -eq 'CN=SecureChat LAN server' } | Remove-Item

$cert = New-SelfSignedCertificate `
    -Subject 'CN=SecureChat LAN server' `
    -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 `
    -KeyUsage DigitalSignature, KeyEncipherment `
    -KeyExportPolicy Exportable `
    -NotAfter (Get-Date).AddYears(2) `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -TextExtension @($sanExtension, $serverAuthExtension)

$rng = New-Object System.Security.Cryptography.RNGCryptoServiceProvider
$bytes = New-Object byte[] 24
$rng.GetBytes($bytes)
$password = [Convert]::ToBase64String($bytes)

$pfxPath = Join-Path $dataDir 'lan-tls.pfx'
$cerPath = Join-Path $dataDir 'lan-tls.cer'
$passwordPath = Join-Path $dataDir 'lan-tls.password.txt'

Export-PfxCertificate -Cert $cert -FilePath $pfxPath -Password (ConvertTo-SecureString -String $password -AsPlainText -Force) | Out-Null
Export-Certificate -Cert $cert -FilePath $cerPath -Type CERT | Out-Null
Set-Content -Path $passwordPath -Value $password -Encoding ASCII

Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
Remove-Item -Path "Cert:\CurrentUser\My\$($cert.Thumbprint)"

Write-Host "Certificate issued: $pfxPath"
Write-Host "Addresses in the certificate: $($IpAddresses -join ', ') ; names: $($dnsNames -join ', ')"
Write-Host "Trust: the current account ($env:USERNAME) already trusts it."
Write-Host "Other accounts: open $cerPath, click Install certificate, choose Current user,"
Write-Host "and place it into 'Trusted Root Certification Authorities'. Administrator rights are not required."
Write-Host "Restart the server so it loads the new certificate."
