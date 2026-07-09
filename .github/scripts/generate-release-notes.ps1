#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Repo,

    [Parameter(Mandatory = $true)]
    [string]$TagName,

    [string]$PreviousTag = '',

    [Parameter(Mandatory = $true)]
    [string]$TargetSha,

    [Parameter(Mandatory = $true)]
    [string]$TriggerPrNumber,

    [Parameter(Mandatory = $true)]
    [string]$OutputPath,

    [Parameter(Mandatory = $true)]
    [string]$OwnerLogin
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-GhJson([string[]]$Arguments) {
    $output = & gh api @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "gh api failed: gh api $($Arguments -join ' '): $($output -join "`n")"
    }

    $json = ($output -join "`n").Trim()
    if ([string]::IsNullOrWhiteSpace($json)) {
        return @()
    }

    return $json | ConvertFrom-Json -Depth 100
}

function Invoke-Git([string[]]$Arguments) {
    $output = & git @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "git failed: git $($Arguments -join ' '): $($output -join "`n")"
    }

    return @($output | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Test-GitCommitExists([string]$Revision) {
    if ([string]::IsNullOrWhiteSpace($Revision)) {
        return $false
    }

    & git rev-parse --verify "$Revision^{commit}" *> $null
    return $LASTEXITCODE -eq 0
}

function Get-ReleaseCommitShas {
    if (-not (Test-GitCommitExists $TargetSha)) {
        throw "Target commit does not exist locally: $TargetSha"
    }

    $range = $TargetSha
    if (Test-GitCommitExists $PreviousTag) {
        $range = "$PreviousTag..$TargetSha"
    }

    return Invoke-Git @('rev-list', '--first-parent', '--reverse', $range)
}

function Get-PullRequestFiles([int]$Number) {
    $files = New-Object System.Collections.Generic.List[string]
    $page = 1

    do {
        $batch = @(Invoke-GhJson @(
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            "repos/$Repo/pulls/$Number/files?per_page=100&page=$page"
        ))

        foreach ($file in $batch) {
            if ($file.filename) {
                $files.Add([string]$file.filename)
            }
        }

        $page++
    } while ($batch.Count -eq 100)

    return @($files)
}

function Normalize-GitHubLogin([string]$Login) {
    if ([string]::IsNullOrWhiteSpace($Login)) {
        return ''
    }

    $trimmed = $Login.Trim()
    if ($trimmed -match '^[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?$') {
        return $trimmed
    }

    return ''
}

function Select-UniqueLogins([string[]]$Logins) {
    $seen = @{}
    $result = New-Object System.Collections.Generic.List[string]

    foreach ($login in $Logins) {
        $normalized = Normalize-GitHubLogin $login
        if ([string]::IsNullOrWhiteSpace($normalized)) {
            continue
        }

        $key = $normalized.ToLowerInvariant()
        if ($seen.ContainsKey($key)) {
            continue
        }

        $seen[$key] = $true
        $result.Add($normalized)
    }

    return @($result)
}

function Get-WorkflowMetadataValue([string]$Body, [string]$Name) {
    if ([string]::IsNullOrWhiteSpace($Body)) {
        return ''
    }

    $pattern = '<!--\s*' + [regex]::Escape($Name) + ':([^>]*)-->'
    if ($Body -match $pattern) {
        return $Matches[1].Trim()
    }

    return ''
}

function Get-WorkflowContributorLogins([string]$Body) {
    $contributors = Get-WorkflowMetadataValue -Body $Body -Name 'workflow:source-contributors'
    if (-not [string]::IsNullOrWhiteSpace($contributors)) {
        return Select-UniqueLogins @($contributors -split '[,\s]+')
    }

    $sourceActor = Get-WorkflowMetadataValue -Body $Body -Name 'workflow:source-actor'
    return Select-UniqueLogins @($sourceActor)
}

function Get-PullRequestModel([int]$Number) {
    $pull = Invoke-GhJson @(
        '-H', 'Accept: application/vnd.github+json',
        '-H', 'X-GitHub-Api-Version: 2022-11-28',
        "repos/$Repo/pulls/$Number"
    )

    $labels = @()
    if ($pull.labels) {
        $labels = @($pull.labels | ForEach-Object { [string]$_.name })
    }

    $author = [string]$pull.user.login
    $contributors = @(Get-WorkflowContributorLogins -Body ([string]$pull.body))
    if ($contributors.Count -eq 0 -and -not (Test-BotLogin $author)) {
        $contributors = @($author)
    }

    [pscustomobject]@{
        Number       = [int]$pull.number
        Title        = [string]$pull.title
        Author       = $author
        Contributors = @($contributors)
        MergedAt     = [string]$pull.merged_at
        Labels       = $labels
        Files        = @(Get-PullRequestFiles -Number $Number)
    }
}

function Get-AssociatedPullRequestNumbers {
    $numbers = [ordered]@{}

    foreach ($sha in Get-ReleaseCommitShas) {
        $pulls = @(Invoke-GhJson @(
            '-H', 'Accept: application/vnd.github+json',
            '-H', 'X-GitHub-Api-Version: 2022-11-28',
            "repos/$Repo/commits/$sha/pulls"
        ))

        foreach ($pull in $pulls) {
            if ($pull.state -eq 'closed' -and $pull.merged_at -and -not $numbers.Contains([string]$pull.number)) {
                $numbers.Add([string]$pull.number, [int]$pull.number)
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($TriggerPrNumber)) {
        $triggerNumber = [int]$TriggerPrNumber
        if (-not $numbers.Contains([string]$triggerNumber)) {
            $numbers.Add([string]$triggerNumber, $triggerNumber)
        }
    }

    return @($numbers.Values)
}

function Normalize-RepoPath([string]$Path) {
    return (($Path.Replace('\', '/') -replace '^\./', '').ToLowerInvariant())
}

$script:ClassificationPolicy = $null

function Get-PolicyArray($Value) {
    if ($null -eq $Value) {
        return @()
    }

    return @($Value)
}

function Get-ObjectPropertyValue($Object, [string]$Name) {
    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-ClassificationPolicy {
    if ($null -ne $script:ClassificationPolicy) {
        return $script:ClassificationPolicy
    }

    $rulesPath = $env:PR_CLASSIFICATION_RULES
    if ([string]::IsNullOrWhiteSpace($rulesPath)) {
        $rulesPath = Join-Path $PSScriptRoot '..\pr-classification-rules.json'
    }

    $script:ClassificationPolicy = Get-Content -Raw -LiteralPath $rulesPath | ConvertFrom-Json -Depth 100
    return $script:ClassificationPolicy
}

function Test-PolicyPathRule([string]$Path, $Rule) {
    if ($null -eq $Rule) {
        return $false
    }

    $lower = Normalize-RepoPath $Path
    $excludePrefixes = @(Get-PolicyArray (Get-ObjectPropertyValue $Rule 'excludePrefixes') | ForEach-Object { Normalize-RepoPath ([string]$_) })
    $excludeFiles = @(Get-PolicyArray (Get-ObjectPropertyValue $Rule 'excludeFiles') | ForEach-Object { Normalize-RepoPath ([string]$_) })

    foreach ($prefix in $excludePrefixes) {
        if ($lower.StartsWith($prefix)) {
            return $false
        }
    }

    if ($excludeFiles -contains $lower) {
        return $false
    }

    $includePrefixes = @(Get-PolicyArray (Get-ObjectPropertyValue $Rule 'includePrefixes') | ForEach-Object { Normalize-RepoPath ([string]$_) })
    $includeFiles = @(Get-PolicyArray (Get-ObjectPropertyValue $Rule 'includeFiles') | ForEach-Object { Normalize-RepoPath ([string]$_) })

    foreach ($prefix in $includePrefixes) {
        if ($lower.StartsWith($prefix)) {
            return $true
        }
    }

    return $includeFiles -contains $lower
}

function Test-RuntimeReleasePath([string]$Path) {
    $policy = Get-ClassificationPolicy
    return Test-PolicyPathRule -Path $Path -Rule $policy.runtimeRelease
}

function Test-InstallOrPackagePath([string]$Path) {
    $policy = Get-ClassificationPolicy
    return Test-PolicyPathRule -Path $Path -Rule $policy.installOrPackage
}

function Get-ReleaseCategories {
    $policy = Get-ClassificationPolicy
    return @(Get-PolicyArray $policy.releaseCategories)
}

function Test-ReleaseNotesExcluded($PullRequest) {
    $excludedLabels = @('ignore-for-release', 'skip-changelog', 'no-changelog', 'no-release-notes')
    $labels = @($PullRequest.Labels | ForEach-Object { $_.ToLowerInvariant() })
    return @($labels | Where-Object { $excludedLabels -contains $_ }).Count -gt 0
}

function Test-IncludedPullRequest($PullRequest) {
    if (Test-ReleaseNotesExcluded $PullRequest) {
        return $false
    }

    return @($PullRequest.Files | Where-Object { Test-RuntimeReleasePath $_ }).Count -gt 0
}

function Test-BotLogin([string]$Login) {
    if ([string]::IsNullOrWhiteSpace($Login)) {
        return $true
    }

    $lower = $Login.ToLowerInvariant()
    return $lower.EndsWith('[bot]') -or $lower -in @('dependabot', 'github-actions')
}

function Get-ChangeCategory($PullRequest) {
    $labels = @($PullRequest.Labels | ForEach-Object { $_.ToLowerInvariant() })
    $text = (@($PullRequest.Title) + $labels) -join "`n"

    foreach ($category in Get-ReleaseCategories) {
        if ($category.fallback) {
            continue
        }

        $categoryLabels = @(Get-PolicyArray $category.labels | ForEach-Object { $_.ToLowerInvariant() })
        if (@($labels | Where-Object { $categoryLabels -contains $_ }).Count -gt 0) {
            return [string]$category.title
        }

        foreach ($pattern in Get-PolicyArray $category.textPatterns) {
            if ($text -match [string]$pattern) {
                return [string]$category.title
            }
        }

        if ($category.installOrPackage -and @($PullRequest.Files | Where-Object { Test-InstallOrPackagePath $_ }).Count -gt 0) {
            return [string]$category.title
        }
    }

    $fallback = @(Get-ReleaseCategories | Where-Object { $_.fallback } | Select-Object -First 1)
    if ($fallback.Count -gt 0) {
        return [string]$fallback[0].title
    }

    return '其他插件变更'
}

function Normalize-Title([string]$Title) {
    return ($Title -replace '\s+', ' ').Trim()
}

function New-ContributorLink([string]$Login) {
    $safeLogin = [System.Net.WebUtility]::HtmlEncode($Login)
    return "[$safeLogin](https://github.com/$safeLogin)"
}

function New-ChangeLine($PullRequest) {
    $title = Normalize-Title $PullRequest.Title
    $contributors = @($PullRequest.Contributors)
    if ($contributors.Count -gt 0) {
        $byline = (@($contributors | ForEach-Object { New-ContributorLink $_ }) -join ', ')
    } elseif (-not (Test-BotLogin $PullRequest.Author)) {
        $byline = New-ContributorLink $PullRequest.Author
    } else {
        $byline = 'workflow automation'
    }

    return "- $title by $byline in #$($PullRequest.Number)"
}

function New-ContributorAvatar([string]$Login) {
    $safeLogin = [System.Net.WebUtility]::HtmlEncode($Login)
    return "<a href=`"https://github.com/$safeLogin`" title=`"$safeLogin`"><img src=`"https://github.com/$safeLogin.png?size=64`" width=`"48`" height=`"48`" alt=`"$safeLogin`" /></a>"
}

function New-ReleaseBody($PullRequests) {
    $categoryOrder = @(Get-ReleaseCategories | ForEach-Object { [string]$_.title })
    $categories = [ordered]@{}
    foreach ($category in $categoryOrder) {
        $categories[$category] = New-Object System.Collections.Generic.List[string]
    }

    $contributors = [ordered]@{}
    $ownerLower = $OwnerLogin.ToLowerInvariant()

    foreach ($pr in $PullRequests) {
        $category = Get-ChangeCategory $pr
        $categories[$category].Add((New-ChangeLine $pr))

        foreach ($login in @($pr.Contributors)) {
            $loginLower = $login.ToLowerInvariant()
            if ($loginLower -ne $ownerLower -and -not (Test-BotLogin $login) -and -not $contributors.Contains($loginLower)) {
                $contributors.Add($loginLower, $login)
            }
        }
    }

    $lines = New-Object System.Collections.Generic.List[string]
    $hasChanges = $false
    foreach ($category in $categoryOrder) {
        if ($categories[$category].Count -eq 0) {
            continue
        }

        $hasChanges = $true
        $lines.Add("## $category")
        $lines.Add('')
        foreach ($line in $categories[$category]) {
            $lines.Add($line)
        }
        $lines.Add('')
    }

    if (-not $hasChanges) {
        $lines.Add('## 插件变更')
        $lines.Add('')
        $lines.Add('- 本版本无用户可见运行代码变化。')
        $lines.Add('')
    }

    if ($contributors.Count -gt 0) {
        $lines.Add('## 贡献者')
        $lines.Add('')
        $lines.Add((@($contributors.Values | ForEach-Object { New-ContributorAvatar $_ }) -join ' '))
        $lines.Add('')
    }

    return (($lines | ForEach-Object { [string]$_ }) -join "`n").Trim()
}

$pullRequests = @(
    Get-AssociatedPullRequestNumbers |
        ForEach-Object { Get-PullRequestModel -Number $_ } |
        Where-Object { $_.MergedAt } |
        Sort-Object @{ Expression = { [DateTime]$_.MergedAt } }, Number
)
$includedPullRequests = @($pullRequests | Where-Object { Test-IncludedPullRequest $_ })
$generatedBody = New-ReleaseBody -PullRequests $includedPullRequests

$lines = @(
    '## 下载说明',
    '',
    '| 文件 | 说明 |',
    '| --- | --- |',
    "| **AFR-Deployer_$TagName.exe** | 主安装程序，双击运行并选择 AutoCAD 版本 |",
    "| AFR-DLL_$TagName.zip | 手动 NETLOAD 用插件 DLL 包 |",
    '| Fonts.zip | 字体资源包（用于手动补充或备份） |',
    '',
    "一般用户只需下载：**AFR-Deployer_$TagName.exe**",
    '',
    '------',
    '',
    $generatedBody,
    '',
    '------',
    '',
    '## 升级说明',
    '',
    '- 支持直接覆盖安装',
    '- 无需卸载旧版本',
    '- 已安装字体不会被删除'
)

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$lines | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Generated release notes at $OutputPath from $($includedPullRequests.Count) runtime PR(s)."
