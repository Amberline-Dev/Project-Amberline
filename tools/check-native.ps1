function Invoke-CheckNative {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true, Position = 0)]
        [string] $FilePath,
        [Parameter(Position = 1)]
        [string[]] $ArgumentList = @(),
        [Parameter(Mandatory = $true)]
        [string] $LogPath
    )

    $previousErrorActionPreference = $ErrorActionPreference
    $exitCode = $null
    try {
        # Native stderr is data for the immutable log, not a terminating
        # PowerShell error. Tee failures remain terminating below.
        $ErrorActionPreference = 'Continue'
        & $FilePath @ArgumentList 2>&1 |
            Tee-Object -FilePath $LogPath -ErrorAction Stop
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }

    if ($exitCode -ne 0) {
        throw "$FilePath failed with exit code $exitCode; see $LogPath"
    }
}
