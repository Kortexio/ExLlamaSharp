#!/usr/bin/env pwsh
# Blocks accidental pushes to ExLlamaV3 upstream remotes.
# Install: copy to .git/hooks/pre-push (and into third_party/exllamav3/.git/hooks/pre-push)

$remoteUrl = $args[1]
if (-not $remoteUrl) {
    $remoteUrl = git remote get-url origin 2>$null
}

if ($remoteUrl -match 'turboderp-org/exllamav3' -or $remoteUrl -match 'turboderp/exllamav3') {
    Write-Error "BLOCKED: Do not push to ExLlamaV3 upstream ($remoteUrl). This tree is local-only for ExLlamaSharp."
    exit 1
}

exit 0
