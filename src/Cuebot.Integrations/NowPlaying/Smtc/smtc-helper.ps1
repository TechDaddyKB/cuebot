# Cuebot's Windows "now playing" helper (Windows PowerShell 5.1). Started by Cuebot with -EncodedCommand; not meant to
# be run by hand. Reads the System Media Transport Controls (the media sessions behind the Windows volume flyout):
#   stdout: one JSON object per line - {"type":"sessions","current":"<app id>","sessions":[...]} whenever something
#           changed (checked once a second), {"type":"error","message":"..."}, {"type":"unsupported","message":"..."}.
#   stdin:  one command per line - "<playpause|play|pause|next|previous><TAB><app id>". The helper exits when stdin closes
#           (so it never outlives Cuebot).
$ErrorActionPreference = 'Stop'
$utf8 = New-Object System.Text.UTF8Encoding($false)
[Console]::OutputEncoding = $utf8
[Console]::InputEncoding = $utf8

function Write-Line($value) {
    [Console]::Out.WriteLine((ConvertTo-Json -InputObject $value -Compress -Depth 5))
    [Console]::Out.Flush()
}

try {
    Add-Type -AssemblyName System.Runtime.WindowsRuntime
    $managerType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager, Windows.Media.Control, ContentType = WindowsRuntime]
    $propertiesType = [Windows.Media.Control.GlobalSystemMediaTransportControlsSessionMediaProperties, Windows.Media.Control, ContentType = WindowsRuntime]
    $asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.IsGenericMethod -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    } | Select-Object -First 1
} catch {
    Write-Line ([ordered]@{ type = 'unsupported'; message = $_.Exception.Message })
    exit 2
}

function Wait-Operation($operation, [Type]$resultType) {
    $task = $asTask.MakeGenericMethod($resultType).Invoke($null, @($operation))
    if (-not $task.Wait(5000)) { throw 'Windows did not answer in time.' }
    return $task.Result
}

try {
    $manager = Wait-Operation ($managerType::RequestAsync()) $managerType
} catch {
    Write-Line ([ordered]@{ type = 'unsupported'; message = $_.Exception.Message })
    exit 2
}

$stdin = [Console]::In
$pending = $stdin.ReadLineAsync()
$last = ''
$nextPoll = [DateTime]::MinValue

while ($true) {
    if ($pending.IsCompleted) {
        $line = $null
        try { $line = $pending.Result } catch { exit 0 }
        if ($null -eq $line) { exit 0 }
        $parts = $line.Split([char]9, 2)
        try {
            $target = $null
            if ($parts.Count -gt 1 -and $parts[1].Length -gt 0) {
                foreach ($s in $manager.GetSessions()) { if ($s.SourceAppUserModelId -eq $parts[1]) { $target = $s; break } }
            }
            if ($null -eq $target) { $target = $manager.GetCurrentSession() }
            if ($null -ne $target) {
                $operation = $null
                switch ($parts[0]) {
                    'playpause' { $operation = $target.TryTogglePlayPauseAsync() }
                    'play' { $operation = $target.TryPlayAsync() }
                    'pause' { $operation = $target.TryPauseAsync() }
                    'next' { $operation = $target.TrySkipNextAsync() }
                    'previous' { $operation = $target.TrySkipPreviousAsync() }
                }
                if ($null -ne $operation) { $null = Wait-Operation $operation ([bool]) }
            }
        } catch {
            Write-Line ([ordered]@{ type = 'error'; message = $_.Exception.Message })
        }
        $pending = $stdin.ReadLineAsync()
        $nextPoll = [DateTime]::MinValue
    }

    if ([DateTime]::UtcNow -ge $nextPoll) {
        $nextPoll = [DateTime]::UtcNow.AddSeconds(1)
        try {
            $list = New-Object System.Collections.ArrayList
            foreach ($s in $manager.GetSessions()) {
                $title = ''; $artist = ''; $album = ''; $albumArtist = ''
                try {
                    $p = Wait-Operation ($s.TryGetMediaPropertiesAsync()) $propertiesType
                    if ($null -ne $p) { $title = $p.Title; $artist = $p.Artist; $album = $p.AlbumTitle; $albumArtist = $p.AlbumArtist }
                } catch { }
                $status = 0
                try { $status = [int]$s.GetPlaybackInfo().PlaybackStatus } catch { }
                $durationMs = 0
                try {
                    $timeline = $s.GetTimelineProperties()
                    $durationMs = [long]($timeline.EndTime - $timeline.StartTime).TotalMilliseconds
                } catch { }
                $null = $list.Add([ordered]@{
                    id = $s.SourceAppUserModelId; status = $status; title = $title; artist = $artist
                    album = $album; albumArtist = $albumArtist; durationMs = $durationMs
                })
            }
            $currentId = ''
            $current = $manager.GetCurrentSession()
            if ($null -ne $current) { $currentId = $current.SourceAppUserModelId }
            $json = ConvertTo-Json -InputObject ([ordered]@{ type = 'sessions'; current = $currentId; sessions = $list.ToArray() }) -Compress -Depth 5
            if ($json -ne $last) {
                $last = $json
                [Console]::Out.WriteLine($json)
                [Console]::Out.Flush()
            }
        } catch {
            Write-Line ([ordered]@{ type = 'error'; message = $_.Exception.Message })
        }
    }
    Start-Sleep -Milliseconds 100
}
