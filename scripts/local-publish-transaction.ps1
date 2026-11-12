function Assert-LocalPublishDirectory {
    param([string]$ArtifactsRoot, [string]$Directory, [string]$NamePattern)

    $artifactsPath = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $candidatePath = [IO.Path]::GetFullPath($Directory).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($candidatePath), $artifactsPath, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($candidatePath) -cnotmatch $NamePattern) {
        throw 'Local publish directory is outside its owned artifacts scope.'
    }
    for ($ancestor = $candidatePath; $ancestor; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        if (Test-Path -LiteralPath $ancestor -ErrorAction Stop) {
            if (((Get-Item -LiteralPath $ancestor -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Local publish directory contains a linked path.'
            }
        }
    }
    return $candidatePath
}

function Remove-OwnedLocalPublishDirectory {
    param([string]$ArtifactsRoot, [string]$Directory)

    $ownedPath = Assert-LocalPublishDirectory $ArtifactsRoot $Directory '^publish-(next|previous)-[a-f0-9]{32}$'
    if (Test-Path -LiteralPath $ownedPath -ErrorAction Stop) {
        # No operation below can target the active publish directory or a profile.
        $directories = [Collections.Generic.Stack[string]]::new()
        $directories.Push($ownedPath)
        while ($directories.Count -gt 0) {
            foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($directories.Pop())) {
                $attributes = [IO.File]::GetAttributes($entry)
                if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Owned publish cleanup contains a link.' }
                if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { $directories.Push($entry) }
            }
        }
        Remove-Item -LiteralPath $ownedPath -Recurse -Force -ErrorAction Stop
    }
}

function Complete-LocalPublishTransaction {
    param([string]$ArtifactsRoot, [string]$StagingDirectory, [string]$PublishDirectory,
          [scriptblock]$ConfirmCandidate, [scriptblock]$StopCandidate,
          [scriptblock]$RestorePrevious, [switch]$NoStart)

    $stagePath = Assert-LocalPublishDirectory $ArtifactsRoot $StagingDirectory '^publish-next-[a-f0-9]{32}$'
    $publishPath = Assert-LocalPublishDirectory $ArtifactsRoot $PublishDirectory '^publish$'
    if (-not $NoStart -and (-not $ConfirmCandidate -or -not $StopCandidate)) { throw 'A started publication requires readiness and graceful-stop callbacks.' }
    foreach ($name in @('NetLights.exe', 'NetLights.UpdateAgent.exe')) {
        $file = Get-Item -LiteralPath (Join-Path $stagePath $name) -Force -ErrorAction Stop
        if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Invalid staged executable: $name"
        }
    }
    $backupPath = Join-Path ([IO.Path]::GetFullPath($ArtifactsRoot)) ('publish-previous-' + [Guid]::NewGuid().ToString('N'))
    $null = Assert-LocalPublishDirectory $ArtifactsRoot $backupPath '^publish-previous-[a-f0-9]{32}$'
    $oldMoved = $false
    $previousPresent = [IO.Directory]::Exists($publishPath)
    $promoted = $false
    $candidateStarted = $false
    try {
        if ([IO.Directory]::Exists($publishPath)) {
            [IO.Directory]::Move($publishPath, $backupPath)
            $oldMoved = $true
        }
        [IO.Directory]::Move($stagePath, $publishPath)
        $promoted = $true
        if (-not $NoStart) {
            $candidateStarted = $true
            & $ConfirmCandidate $publishPath | Out-Null
        }
    }
    catch {
        $swapFailure = $_
        if ($candidateStarted) {
            try { & $StopCandidate $publishPath | Out-Null }
            catch { throw "Candidate couldn't stop gracefully; no rollback or second writer was started. Previous files remain at $backupPath. Original failure: $($swapFailure.Exception.Message). Stop failure: $($_.Exception.Message)" }
        }
        if ($promoted) {
            if ([IO.Directory]::Exists($stagePath) -or [IO.File]::Exists($stagePath)) { throw "Rollback staging is occupied; previous files remain at $backupPath." }
            [IO.Directory]::Move($publishPath, $stagePath)
        }
        if ($oldMoved) {
            if ([IO.Directory]::Exists($publishPath) -or [IO.File]::Exists($publishPath)) {
                throw "Local publish failed; previous files remain at $backupPath because the destination is occupied."
            }
            try { [IO.Directory]::Move($backupPath, $publishPath) }
            catch { throw "Local publish rollback failed; previous files remain at $backupPath. $($_.Exception.Message)" }
        }
        if ($previousPresent -and $RestorePrevious -and -not $NoStart) {
            try { & $RestorePrevious $publishPath | Out-Null }
            catch { throw "Previous files were retained/restored, but their restart wasn't confirmed. Original failure: $($swapFailure.Exception.Message). Restart failure: $($_.Exception.Message)" }
        }
        throw $swapFailure
    }
    if ($oldMoved -and -not $NoStart) {
        try { Remove-OwnedLocalPublishDirectory $ArtifactsRoot $backupPath }
        catch { Write-Warning "New local publish is complete; old backup retained at $backupPath. $($_.Exception.Message)" }
    }
    return [pscustomobject]@{status=$(if($NoStart){'NOT RUN'}else{'PASS'});healthConfirmed=(-not $NoStart);backupPath=$(if($oldMoved -and $NoStart){$backupPath}else{$null})}
}

function Start-VerifiedLocalProcess {
    param([string]$Executable, [string]$ExpectedHash, [string]$ExpectedVersion,
          [hashtable]$Ownership, [string]$ProfileDirectory,
          [switch]$Show,
          [ValidateRange(1,60000)][int]$ReadyMilliseconds=30000,
          [ValidateRange(1,10000)][int]$StableMilliseconds=3000)
    $exe=[IO.Path]::GetFullPath($Executable)
    if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ine $ExpectedHash -or
        (([Diagnostics.FileVersionInfo]::GetVersionInfo($exe).ProductVersion -split '\+')[0]) -cne $ExpectedVersion) { throw 'Candidate path/hash/version differs from the prepared image.' }
    $name='Local\NetLights.UpdateHealth.'+[Guid]::NewGuid().ToString('N')
    $ready=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,$name)
    try {
        $start=[Diagnostics.ProcessStartInfo]::new($exe)
        # A surviving GUI process mustn't inherit the verification shell's pipes.
        $start.UseShellExecute=$true
        $start.WindowStyle=if($Show){[Diagnostics.ProcessWindowStyle]::Normal}else{[Diagnostics.ProcessWindowStyle]::Hidden}
        foreach($argument in @('--skip-automatic-updates','--update-health-event',$name)) {$start.ArgumentList.Add($argument)}
        if($Show) {$start.ArgumentList.Add('--show-local')}
        if($ProfileDirectory) {$start.ArgumentList.Add('--profile-directory');$start.ArgumentList.Add([IO.Path]::GetFullPath($ProfileDirectory))}
        $process=[Diagnostics.Process]::Start($start)
        $Ownership.Process=$process; $Ownership.Executable=$exe; $Ownership.ProfileDirectory=$ProfileDirectory
        $deadline=[Diagnostics.Stopwatch]::StartNew()
        $signalled=$false
        while($deadline.ElapsedMilliseconds -lt $ReadyMilliseconds) {
            if($process.HasExited) {throw "Candidate exited before monitor readiness ($($process.ExitCode))."}
            if($ready.WaitOne([Math]::Min(100,[Math]::Max(1,$ReadyMilliseconds-[int]$deadline.ElapsedMilliseconds)))) {$signalled=$true;break}
        }
        if(-not $signalled) {throw 'Candidate did not confirm monitor readiness within the deadline.'}
        if($process.WaitForExit($StableMilliseconds)) {throw "Candidate exited after its readiness signal ($($process.ExitCode))."}
        if(-not[string]::Equals([IO.Path]::GetFullPath($process.Path),$exe,[StringComparison]::OrdinalIgnoreCase) -or
            (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ine $ExpectedHash) {throw 'Running candidate identity changed during readiness verification.'}
        return $process
    } finally {$ready.Dispose()}
}

function Stop-OwnedLocalProcess {
    param([hashtable]$Ownership,[ValidateRange(1,30000)][int]$ExitMilliseconds=20000)
    $process=if($Ownership.ContainsKey('Process')){$Ownership.Process}else{$null}
    if(-not $process -or $process.HasExited) {return}
    $exe=[IO.Path]::GetFullPath([string]$Ownership.Executable)
    if(-not[string]::Equals([IO.Path]::GetFullPath($process.Path),$exe,[StringComparison]::OrdinalIgnoreCase)) {throw 'Graceful exit target no longer matches the owned process.'}
    $start=[Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.ArgumentList.Add('--exit-local')
    if($Ownership.ProfileDirectory) {$start.ArgumentList.Add('--profile-directory');$start.ArgumentList.Add([IO.Path]::GetFullPath([string]$Ownership.ProfileDirectory))}
    $command=[Diagnostics.Process]::Start($start)
    try {
        if(-not $command.WaitForExit([Math]::Min(10000,$ExitMilliseconds)) -or $command.ExitCode -ne 0) {throw 'Owned graceful-exit command failed or timed out.'}
    } finally {
        # --exit-local returns before the application's monitor/data owner starts.
        # Only this exact signal helper may be stopped; never the candidate writer.
        if(-not $command.HasExited) {$command.Kill($true);$command.WaitForExit(5000)|Out-Null}
        $command.Dispose()
    }
    if(-not $process.WaitForExit($ExitMilliseconds)) {throw 'Owned candidate is still running; files and previous backup were retained.'}
}
