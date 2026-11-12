function Test-HostedRunner {
    $name=([Security.Principal.WindowsIdentity]::GetCurrent().Name -split '\\')[-1]
    return $name -ieq 'runneradmin' -and $env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and $env:CI -ceq 'true'
}

function Assert-HostedRunner {
    if(-not (Test-HostedRunner)) {
        throw 'BLOCKED: this operation requires the actual disposable GitHub-hosted Windows runner account and environment.'
    }
}
