[CmdletBinding()]
param(
    [ValidateRange(1, 120)]
    [int] $TimeoutSeconds = 30,

    [int] $ParentProcessId,

    [string] $ReadyPath,

    [string] $FailurePath,

    [string] $StopPath,

    [string] $ReleasedPath,

    [switch] $ProbeOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-CurrentUserSid {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent(
        [Security.Principal.TokenAccessLevels]::Query)
    try {
        return $identity.User
    }
    finally {
        $identity.Dispose()
    }
}

function Get-CurrentUserPipeName {
    param([Parameter(Mandatory)][Security.Principal.SecurityIdentifier] $Sid)

    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $digest = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($Sid.Value))
    }
    finally {
        $sha256.Dispose()
    }

    $scope = -join @($digest[0..15] | ForEach-Object { $_.ToString('x2') })
    return "DshWindowsLauncher.SingleInstance.$scope"
}

function New-CurrentUserServer {
    param([Parameter(Mandatory)][string] $PipeName)

    $pipeSecurity = [IO.Pipes.PipeSecurity]::new()
    $pipeSecurity.SetAccessRuleProtection($true, $false)
    $pipeSecurity.SetAccessRule([IO.Pipes.PipeAccessRule]::new(
        $script:CurrentUserSid,
        [IO.Pipes.PipeAccessRights]::FullControl,
        [Security.AccessControl.AccessControlType]::Allow))
    if ($null -ne ('System.IO.Pipes.NamedPipeServerStreamAcl' -as [type])) {
        return [IO.Pipes.NamedPipeServerStreamAcl]::Create(
            $PipeName,
            [IO.Pipes.PipeDirection]::InOut,
            1,
            [IO.Pipes.PipeTransmissionMode]::Byte,
            [IO.Pipes.PipeOptions]::Asynchronous,
            256,
            256,
            $pipeSecurity)
    }

    return [IO.Pipes.NamedPipeServerStream]::new(
        $PipeName,
        [IO.Pipes.PipeDirection]::InOut,
        1,
        [IO.Pipes.PipeTransmissionMode]::Byte,
        [IO.Pipes.PipeOptions]::Asynchronous,
        256,
        256,
        $pipeSecurity)
}

function Read-OneByteWithTimeout {
    param(
        [Parameter(Mandatory)][IO.Stream] $Stream,
        [Parameter(Mandatory)][int] $TimeoutMilliseconds
    )

    $buffer = [byte[]]::new(1)
    $asyncResult = $Stream.BeginRead($buffer, 0, 1, $null, $null)
    if (-not $asyncResult.AsyncWaitHandle.WaitOne($TimeoutMilliseconds)) {
        throw [TimeoutException]::new('The primary instance did not acknowledge maintenance exit.')
    }
    if ($Stream.EndRead($asyncResult) -ne 1) {
        throw [IO.EndOfStreamException]::new('The primary instance closed the pipe without a response.')
    }
    return $buffer[0]
}

function Request-MaintenanceExit {
    param(
        [Parameter(Mandatory)][string] $PipeName,
        [Parameter(Mandatory)][Diagnostics.Stopwatch] $Stopwatch,
        [Parameter(Mandatory)][TimeSpan] $Timeout
    )

    $client = [IO.Pipes.NamedPipeClientStream]::new(
        '.',
        $PipeName,
        [IO.Pipes.PipeDirection]::InOut,
        [IO.Pipes.PipeOptions]::Asynchronous)
    try {
        $remaining = [Math]::Max(1, [int] ($Timeout - $Stopwatch.Elapsed).TotalMilliseconds)
        $client.Connect($remaining)
        $payload = [Text.Encoding]::ASCII.GetBytes('maintenance-exit')
        $header = [BitConverter]::GetBytes([uint16] $payload.Length)
        $client.Write($header, 0, $header.Length)
        $client.Write($payload, 0, $payload.Length)
        $client.Flush()
        $remaining = [Math]::Max(1, [int] ($Timeout - $Stopwatch.Elapsed).TotalMilliseconds)
        try {
            $response = Read-OneByteWithTimeout `
                -Stream $client `
                -TimeoutMilliseconds $remaining
        }
        catch [IO.EndOfStreamException] {
            # The primary can finish shutdown after accepting the request but
            # before the response byte reaches this process. The following
            # exclusive pipe reservation remains the authoritative gate.
            return
        }
        if ($response -ne 1) {
            throw [IO.IOException]::new('The primary instance rejected maintenance exit.')
        }
    }
    finally {
        $client.Dispose()
    }
}

function Test-PipeBusyException {
    param([Parameter(Mandatory)][Exception] $Exception)

    $current = $Exception
    while ($null -ne $current) {
        if ($current -is [IO.IOException] -and
            (($current.HResult -band 0xffff) -eq 231)) {
            return $true
        }

        $current = $current.InnerException
    }

    return $false
}

function Get-SafeFailureCode {
    param([Parameter(Mandatory)][Exception] $Exception)

    $current = $Exception
    while ($null -ne $current.InnerException) {
        $current = $current.InnerException
    }

    return 'failed:{0}:0x{1:X8}' -f $current.GetType().Name, $current.HResult
}

function Wait-ForPipeRelease {
    param(
        [Parameter(Mandatory)][string] $PipeName,
        [Parameter(Mandatory)][Diagnostics.Stopwatch] $Stopwatch,
        [Parameter(Mandatory)][TimeSpan] $Timeout
    )

    while ($Stopwatch.Elapsed -lt $Timeout) {
        try {
            return New-CurrentUserServer -PipeName $PipeName
        }
        catch {
            if (-not (Test-PipeBusyException -Exception $_.Exception)) {
                throw
            }

            Start-Sleep -Milliseconds 50
        }
    }
    throw [TimeoutException]::new('The current-user instance did not release its IPC endpoint.')
}

function Write-StateFile {
    param(
        [Parameter(Mandatory)][string] $Path,
        [Parameter(Mandatory)][string] $Value
    )

    $temporary = "$Path.$([Guid]::NewGuid().ToString('N')).tmp"
    [IO.File]::WriteAllText($temporary, $Value, [Text.Encoding]::ASCII)
    [IO.File]::Move($temporary, $Path)
}

$server = $null
try {
    $script:CurrentUserSid = Get-CurrentUserSid
    $pipeName = Get-CurrentUserPipeName -Sid $script:CurrentUserSid
    try {
        $server = New-CurrentUserServer -PipeName $pipeName
    }
    catch {
        if (-not (Test-PipeBusyException -Exception $_.Exception)) {
            throw
        }

        if ($ProbeOnly) {
            exit 4
        }

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        $timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)
        Request-MaintenanceExit `
            -PipeName $pipeName `
            -Stopwatch $stopwatch `
            -Timeout $timeout
        $server = Wait-ForPipeRelease `
            -PipeName $pipeName `
            -Stopwatch $stopwatch `
            -Timeout $timeout
    }

    if ($ProbeOnly) {
        exit 0
    }

    if ($ParentProcessId -le 0 -or
        [string]::IsNullOrWhiteSpace($ReadyPath) -or
        [string]::IsNullOrWhiteSpace($FailurePath) -or
        [string]::IsNullOrWhiteSpace($StopPath) -or
        [string]::IsNullOrWhiteSpace($ReleasedPath)) {
        throw [ArgumentException]::new('Reservation mode requires parent and state paths.')
    }

    Write-StateFile -Path $ReadyPath -Value 'ready'
    while (-not [IO.File]::Exists($StopPath)) {
        if ($null -eq (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue)) {
            break
        }
        Start-Sleep -Milliseconds 50
    }
}
catch {
    if (-not [string]::IsNullOrWhiteSpace($FailurePath)) {
        try {
            Write-StateFile `
                -Path $FailurePath `
                -Value (Get-SafeFailureCode -Exception $_.Exception)
        }
        catch {
        }
    }
    exit 1
}
finally {
    if ($null -ne $server) {
        $server.Dispose()
    }
}

if (-not $ProbeOnly) {
    Write-StateFile -Path $ReleasedPath -Value 'released'
}
