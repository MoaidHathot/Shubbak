<#
.SYNOPSIS
    Fails unless every given file carries a valid, timestamped Authenticode signature.

.DESCRIPTION
    Run by the release workflow after each signing step. "Valid" here means Windows
    itself accepts the chain - the same check a user's machine makes - and the
    timestamp is required rather than nice to have: Artifact Signing issues
    certificates that live for three days, so a signature without a countersigned
    timestamp stops verifying the week after the release.

.EXAMPLE
    .\tools\check-signatures.ps1 (Get-ChildItem artifacts -Recurse -Filter *.exe).FullName
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory, ValueFromRemainingArguments)]
    [string[]] $Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$failures = 0

foreach ($file in $Path) {
    $signature = Get-AuthenticodeSignature -LiteralPath $file
    $name = Split-Path $file -Leaf
    $subject = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { '(no signer)' }
    $timestamped = $null -ne $signature.TimeStamperCertificate

    if ($signature.Status -ne 'Valid') {
        Write-Output "FAIL  $name  status=$($signature.Status)  $($signature.StatusMessage)"
        $failures++
    }
    elseif (-not $timestamped) {
        Write-Output "FAIL  $name  signed but not timestamped; the signature will expire with the certificate"
        $failures++
    }
    else {
        Write-Output ("ok    {0,-18} {1}  (timestamped by {2})" -f $name, $subject, $signature.TimeStamperCertificate.Subject)
    }
}

if ($failures -gt 0) {
    throw "$failures file(s) failed signature verification."
}

Write-Output "$($Path.Count) file(s) carry a valid, timestamped signature."
