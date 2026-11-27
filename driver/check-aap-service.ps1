<#
  PodBridge driver pre-check. READ-ONLY, no administrator rights needed, installs nothing.

  Lists the Bluetooth service device nodes Windows created for your paired Apple audio devices,
  and says whether one exists for the AirPods control service (AAP, UUID 74ec2172-...). The
  optional PodBridge driver can only attach that way if it exists.

  Run:   powershell -ExecutionPolicy Bypass -File .\check-aap-service.ps1
  Then paste the output into the PodBridge issue/chat (it contains your device's name and address;
  remove those if you like).
#>

$aap = '74ec2172-0bad-4d01-8f77-997b2be0722a'

$nodes = Get-PnpDevice | Where-Object { $_.InstanceId -like 'BTHENUM\*' -and $_.InstanceId -match '_VID&0001004C' }

if (-not $nodes) {
    Write-Output "No Apple Bluetooth service nodes found. Are your AirPods paired with this PC?"
    return
}

Write-Output "Apple Bluetooth service nodes on this PC:`n"
foreach ($n in $nodes | Sort-Object InstanceId) {
    $service = if ($n.InstanceId -match '\{([0-9A-Fa-f-]{36})\}') { $Matches[1].ToLower() } else { '?' }
    $hwids = (Get-PnpDeviceProperty -InstanceId $n.InstanceId -KeyName 'DEVPKEY_Device_HardwareIds' -ErrorAction SilentlyContinue).Data
    $tag = if ($service -eq $aap) { '   <== AAP control service' } else { '' }
    Write-Output ("{0}  [{1}]  status={2}{3}" -f $n.FriendlyName, $service, $n.Status, $tag)
    foreach ($id in $hwids) { Write-Output "    hardware id: $id" }
    Write-Output ""
}

if ($nodes.InstanceId -match [regex]::Escape($aap)) {
    Write-Output "RESULT: the AAP service node exists. The driver's INF must list the hardware id shown for it."
} else {
    Write-Output "RESULT: no AAP service node. Windows didn't create one, so the driver needs a different way to attach."
}
